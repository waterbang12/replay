using LifeReplay.Core;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace LifeReplay.Recorder.Capture;

/// <summary>A GPU texture we own, wrapped once as a WinRT surface, reference counted so it can be reused.</summary>
internal sealed class PooledTexture
{
    public required ID3D11Texture2D Texture { get; init; }
    public required IDirect3DSurface Surface { get; init; }
    public int Refs;
    public bool Permanent;
    /// <summary>Bumped when a texture is forcibly reclaimed, so late releases from old holders are ignored.</summary>
    public int Generation;
    public long LastAcquiredTs;
}

/// <summary>A frame handed to the encoder. Holds one reference on <see cref="Entry"/>; give it back with MonitorCapture.Release.</summary>
internal sealed record CapturedFrame(PooledTexture Entry, long Ts, bool IsReal, int Gen);

/// <summary>
/// Windows.Graphics.Capture session for one monitor. Windows only delivers a frame when something on screen
/// changed; we keep the most recent one and let the encoder pull frames at its own pace (up to the target FPS).
/// </summary>
internal sealed class MonitorCapture : IDisposable
{
    private const int PoolSoftLimit = 12;
    /// <summary>
    /// Hard cap on GPU textures per display (~9 MB each at 1920×1200). If the pipeline never hands textures
    /// back, the least recently used one is reused rather than letting memory grow without bound.
    /// </summary>
    private const int PoolHardLimit = 24;

    private readonly D3DContext _d3d;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly object _gate = new();
    private readonly AutoResetEvent _frameEvent = new(false);
    private readonly ManualResetEvent _closedEvent = new(false);
    private readonly List<PooledTexture> _textures = [];
    private Direct3D11CaptureFrame? _pending;
    private PooledTexture? _last;
    private PooledTexture? _blank;
    private volatile bool _blanked;
    private volatile bool _blankPending;
    private bool _warnedPool;
    private int _disposed;

    public MonitorInfo Monitor { get; }
    public int Width { get; }
    public int Height { get; }
    public long FramesArrived;
    public string? LostReason { get; private set; }
    /// <summary>Raised once when the capture can't continue (monitor unplugged, resolution changed).</summary>
    public event Action<MonitorCapture, string>? Lost;

    public MonitorCapture(D3DContext d3d, MonitorInfo monitor, bool captureCursor, bool showBorder, int fps)
    {
        _d3d = d3d;
        Monitor = monitor;
        _item = Monitors.CreateCaptureItem(monitor.Handle);
        Width = _item.Size.Width;
        Height = _item.Size.Height;
        _item.Closed += (_, _) => SignalLost("display was disconnected");
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(d3d.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        const string sessionType = "Windows.Graphics.Capture.GraphicsCaptureSession";
        try { if (ApiInformation.IsPropertyPresent(sessionType, "IsCursorCaptureEnabled")) _session.IsCursorCaptureEnabled = captureCursor; }
        catch (Exception ex) { Log.Warn("Could not set cursor capture", ex); }
        try { if (!showBorder && ApiInformation.IsPropertyPresent(sessionType, "IsBorderRequired")) _session.IsBorderRequired = false; }
        catch (Exception ex) { Log.Warn("Could not disable the capture border (Windows draws a yellow frame instead)", ex); }
        try
        {
            // Windows 11 24H2+: don't wake us more often than needed.
            if (ApiInformation.IsPropertyPresent(sessionType, "MinUpdateInterval"))
                _session.MinUpdateInterval = TimeSpan.FromMilliseconds(Math.Max(15, 500.0 / fps));
        }
        catch { /* optional */ }
        _session.StartCapture();
    }

    public bool IsClosed => _closedEvent.WaitOne(0);

    /// <summary>While blanked, real frames are dropped and the encoder receives a black frame instead.</summary>
    public bool Blanked
    {
        get => _blanked;
        set
        {
            if (_blanked == value) return;
            _blanked = value;
            if (value)
            {
                Direct3D11CaptureFrame? old;
                lock (_gate) { old = _pending; _pending = null; }
                old?.Dispose();
                _blankPending = true;
                _frameEvent.Set();
            }
        }
    }

    private void SignalLost(string reason)
    {
        if (LostReason != null) return;
        LostReason = reason;
        Log.Warn($"Capture of {Monitor.DeviceName} stopped: {reason}");
        _closedEvent.Set();
        try { Lost?.Invoke(this, reason); } catch (Exception ex) { Log.Error("Lost handler failed", ex); }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame;
        try { frame = sender.TryGetNextFrame(); }
        catch (Exception ex) { Log.Warn("TryGetNextFrame failed", ex); return; }
        if (frame == null) return;
        Interlocked.Increment(ref FramesArrived);
        if (frame.ContentSize.Width != Width || frame.ContentSize.Height != Height)
        {
            frame.Dispose();
            SignalLost($"resolution changed to {frame.ContentSize.Width}x{frame.ContentSize.Height}");
            return;
        }
        if (_blanked) { frame.Dispose(); return; }
        Direct3D11CaptureFrame? old;
        lock (_gate) { old = _pending; _pending = frame; }
        old?.Dispose();
        _frameEvent.Set();
    }

    // ------------------------------------------------------------------ texture pool

    /// <summary>Returns an entry with Refs = 1 owned by the caller (read its Generation under the same lock).</summary>
    private PooledTexture Acquire(out int gen)
    {
        long now = QpcClock.Now100ns();
        lock (_gate)
        {
            foreach (var t in _textures)
                if (t.Refs == 0 && !t.Permanent) { t.Refs = 1; t.LastAcquiredTs = now; gen = t.Generation; return t; }
            if (_textures.Count >= PoolSoftLimit && !_warnedPool)
            {
                _warnedPool = true;
                Log.Warn($"Texture pool for {Monitor.DeviceName} reached {_textures.Count}; the encoder is holding frames longer than expected");
            }
            if (_textures.Count >= PoolHardLimit)
            {
                var victim = _textures.Where(t => !t.Permanent && t != _last).MinBy(t => t.LastAcquiredTs);
                if (victim != null)
                {
                    victim.Generation++;
                    victim.Refs = 1;
                    victim.LastAcquiredTs = now;
                    ReclaimedTextures++;
                    if (ReclaimedTextures == 1 || ReclaimedTextures % 100 == 0)
                        Log.Warn($"Texture pool for {Monitor.DeviceName} is full; reclaimed a texture the encoder never returned (total {ReclaimedTextures})");
                    gen = victim.Generation;
                    return victim;
                }
            }
        }
        var tex = _d3d.CreateTexture(Width, Height);
        var entry = new PooledTexture { Texture = tex, Surface = D3DContext.SurfaceFromTexture(tex), Refs = 1, LastAcquiredTs = now };
        lock (_gate) _textures.Add(entry);
        gen = 0;
        return entry;
    }

    public long ReclaimedTextures { get; private set; }

    /// <summary>Takes another reference on the frame's texture; returns the generation to pass to Release.</summary>
    public int AddRef(CapturedFrame frame)
    {
        lock (_gate)
        {
            if (frame.Entry.Generation == frame.Gen) frame.Entry.Refs++;
            return frame.Gen;
        }
    }

    public void Release(PooledTexture entry, int gen)
    {
        lock (_gate) { if (entry.Generation == gen && entry.Refs > 0) entry.Refs--; }
    }

    public void Release(CapturedFrame frame) => Release(frame.Entry, frame.Gen);

    /// <summary>New frame referencing an entry (caller holds _gate).</summary>
    private CapturedFrame RefFrame(PooledTexture entry, long ts, bool real)
    {
        entry.Refs++;
        return new CapturedFrame(entry, ts, real, entry.Generation);
    }

    private void SetLast(PooledTexture entry)
    {
        lock (_gate)
        {
            entry.Refs++;
            if (_last != null) _last.Refs--;
            _last = entry;
        }
    }

    public int PoolSize { get { lock (_gate) return _textures.Count; } }

    private PooledTexture BlankTexture()
    {
        lock (_gate)
        {
            if (_blank != null) return _blank;
        }
        var tex = _d3d.CreateTexture(Width, Height);
        _d3d.Lock();
        try { _d3d.Clear(tex); }
        finally { _d3d.Unlock(); }
        var entry = new PooledTexture { Texture = tex, Surface = D3DContext.SurfaceFromTexture(tex), Refs = 1, Permanent = true };
        lock (_gate) { _blank ??= entry; _textures.Add(entry); return _blank; }
    }

    private CapturedFrame CopyToPool(Direct3D11CaptureFrame frame, long ts)
    {
        var entry = Acquire(out int gen);
        try
        {
            using var src = D3DContext.TextureFromSurface(frame.Surface);
            _d3d.Lock();
            try
            {
                _d3d.Context.CopySubresourceRegion(entry.Texture, 0, 0, 0, 0, src, 0, new Box(0, 0, 0, Width, Height, 1));
            }
            finally { _d3d.Unlock(); }
        }
        catch
        {
            Release(entry, gen);
            throw;
        }
        SetLast(entry);
        return new CapturedFrame(entry, ts, true, gen);
    }

    // ------------------------------------------------------------------ frame access for the encoder

    /// <summary>
    /// The picture for frame slot <paramref name="slotTs"/>. Waits until the slot's time, then returns the newest
    /// captured frame if one arrived by the end of the slot, otherwise the previous picture again (or black
    /// while blanked). Every slot gets a frame, so the encoder never has to fill gaps itself.
    /// Returns null when <paramref name="cancel"/> is signalled, the capture is lost, or nothing was captured yet.
    /// </summary>
    public CapturedFrame? FrameForSlot(long slotTs, long intervalTicks, WaitHandle cancel)
    {
        WaitHandle[] stopHandles = [cancel, _closedEvent];
        while (true)
        {
            if (cancel.WaitOne(0) || _closedEvent.WaitOne(0)) return null;
            long now = QpcClock.Now100ns();
            if (now < slotTs)
            {
                WaitHandle.WaitAny(stopHandles, (int)Math.Min(1000, (slotTs - now) / 10_000 + 1));
                continue;
            }
            if (_blankPending)
            {
                _blankPending = false;
                var b = BlankTexture();
                SetLast(b);
                lock (_gate) return RefFrame(b, slotTs, false);
            }
            Direct3D11CaptureFrame? f = null;
            lock (_gate)
            {
                // A frame captured after this slot ends belongs to a later slot (happens while catching up).
                if (_pending != null && _pending.SystemRelativeTime.Ticks <= slotTs + intervalTicks) { f = _pending; _pending = null; }
            }
            if (f != null)
            {
                using (f)
                {
                    if (!_blanked) return CopyToPool(f, slotTs);
                }
            }
            return Repeat(slotTs);
        }
    }

    /// <summary>The current screen content stamped with <paramref name="ts"/> (used to start a new segment seamlessly).</summary>
    public CapturedFrame? SeedFrame(long ts, WaitHandle cancel, TimeSpan timeout)
    {
        Direct3D11CaptureFrame? f;
        lock (_gate) { f = _pending; _pending = null; }
        if (f != null)
        {
            using (f)
            {
                if (!_blanked) return CopyToPool(f, ts);
            }
        }
        var repeat = Repeat(ts);
        if (repeat != null) return repeat;
        // Nothing captured yet: Windows sends the first frame right after StartCapture.
        var deadline = QpcClock.Now100ns() + timeout.Ticks;
        while (QpcClock.Now100ns() < deadline)
        {
            var first = WaitForFrameWithTimeout(cancel, 250);
            if (first != null) return first;
            if (cancel.WaitOne(0) || IsClosed) return null;
        }
        return null;
    }

    private CapturedFrame? WaitForFrameWithTimeout(WaitHandle cancel, int ms)
    {
        WaitHandle.WaitAny([_frameEvent, cancel, _closedEvent], ms);
        Direct3D11CaptureFrame? f;
        lock (_gate) { f = _pending; _pending = null; }
        if (f == null) return _blanked ? Repeat(QpcClock.Now100ns()) : null;
        using (f) return CopyToPool(f, f.SystemRelativeTime.Ticks);
    }

    /// <summary>Last frame again (or black when blanked) stamped with <paramref name="ts"/>; null if nothing was captured yet.</summary>
    public CapturedFrame? Repeat(long ts)
    {
        if (_blanked)
        {
            var b = BlankTexture();
            lock (_gate) return RefFrame(b, ts, false);
        }
        lock (_gate)
        {
            return _last == null ? null : RefFrame(_last, ts, false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _closedEvent.Set();
        try { _session.Dispose(); } catch { }
        try { _pool.FrameArrived -= OnFrameArrived; _pool.Dispose(); } catch { }
        lock (_gate)
        {
            _pending?.Dispose();
            _pending = null;
            foreach (var t in _textures)
            {
                try { t.Texture.Dispose(); } catch { }
            }
            _textures.Clear();
            _last = null;
            _blank = null;
        }
    }
}
