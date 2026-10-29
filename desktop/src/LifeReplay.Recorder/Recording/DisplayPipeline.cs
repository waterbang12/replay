using LifeReplay.Core;
using LifeReplay.Recorder.Capture;
using System.Drawing.Imaging;



namespace LifeReplay.Recorder.Recording;

internal sealed record RecorderContext(MetadataStore Store, AppPaths Paths, string DeviceId);

/// <summary>
/// Records one monitor: owns the D3D device and capture session, and a chain of SegmentEncoders.
/// Each segment is written to "*.mp4.part", checked, renamed to "*.mp4", then marked finalized in SQLite.
/// </summary>
internal sealed class DisplayPipeline
{
    private readonly RecorderContext _ctx;
    private readonly RecorderConfig _cfg;
    private readonly string _sessionId;
    private readonly bool _hardware;
    private readonly D3DContext _d3d;
    private readonly MonitorCapture _capture;
    private readonly object _gate = new();
    private readonly List<Task> _finalizers = [];
    private SegmentEncoder? _encoder;
    private long _nextThumbTs;
    private int _thumbBusy;
    private bool _stopped;

    public MonitorInfo Monitor { get; }
    public EncodingPlan Plan { get; }
    public string DisplayId => Monitor.Id;
    public bool Hardware => _hardware;
    public string? CurrentSegmentId { get; private set; }
    /// <summary>Raised when encoding or capture failed and the controller should restart this display.</summary>
    public event Action<DisplayPipeline, string, bool>? Faulted; // pipeline, message, likelyEncoderProblem

    public MonitorCapture Capture => _capture;

    private DisplayPipeline(RecorderContext ctx, RecorderConfig cfg, string sessionId, MonitorInfo monitor, bool hardware)
    {
        _ctx = ctx;
        _cfg = cfg;
        _sessionId = sessionId;
        _hardware = hardware;
        Monitor = monitor;
        _d3d = new D3DContext();
        try
        {
            _capture = new MonitorCapture(_d3d, monitor, cfg.CaptureCursor, cfg.ShowCaptureBorder, cfg.Fps);
        }
        catch
        {
            _d3d.Dispose();
            throw;
        }
        Plan = EncodingPlan.Compute(_capture.Width, _capture.Height, cfg);
        _capture.Lost += (_, reason) => Faulted?.Invoke(this, reason, false);
        if (_d3d.IsWarp) Log.Warn("No hardware Direct3D device; using WARP (software); expect higher CPU usage");
    }

    public static DisplayPipeline Start(RecorderContext ctx, RecorderConfig cfg, string sessionId, MonitorInfo monitor, bool hardware, bool blanked)
    {
        var p = new DisplayPipeline(ctx, cfg, sessionId, monitor, hardware);
        p._capture.Blanked = blanked;
        ctx.Store.UpsertDisplay(new DisplayRecord
        {
            Id = monitor.Id, DeviceId = ctx.DeviceId, Name = monitor.FriendlyName, Width = p._capture.Width, Height = p._capture.Height,
            Left = monitor.Left, Top = monitor.Top, IsPrimary = monitor.IsPrimary, FirstSeenUtcMs = TimeUtil.NowMs(), LastSeenUtcMs = TimeUtil.NowMs(),
        });
        Log.Info($"Recording {monitor.DeviceName} ({monitor.FriendlyName}) {p._capture.Width}x{p._capture.Height} -> {p.Plan.Width}x{p.Plan.Height} " +
                 $"@ up to {p.Plan.Fps} fps, {p.Plan.BitrateBps / 1000} kbps, encoder {(hardware ? "hardware-preferred" : "software")}");
        p.StartSegment(0, 0);
        return p;
    }

    private void StartSegment(long seedTs, long seedUtcMs)
    {
        var id = Guid.NewGuid().ToString();
        long startUtc = seedUtcMs > 0 ? seedUtcMs : TimeUtil.NowMs();
        var local = TimeUtil.ToLocal(startUtc);
        var dir = Path.Combine(_ctx.Paths.DayPlatformDir(local, "windows"), Monitor.Id);
        Directory.CreateDirectory(dir);
        var final = Path.Combine(dir, $"{local:HH-mm-ss}_{id[..8]}.mp4");
        var encoder = new SegmentEncoder(_capture, Plan, _hardware, final + ".part", seedTs, seedUtcMs);
        var row = new SegmentRecord
        {
            Id = id, SessionId = _sessionId, DeviceId = _ctx.DeviceId, DisplayId = Monitor.Id, StartUtcMs = startUtc,
            FilePath = _ctx.Paths.ToRelative(final), Width = Plan.Width, Height = Plan.Height, SourceWidth = Plan.SourceWidth,
            SourceHeight = Plan.SourceHeight, Fps = Plan.Fps, Bitrate = Plan.BitrateBps, Encoder = encoder.EncoderName,
            Status = SegmentStatus.Recording, CreatedUtcMs = TimeUtil.NowMs(),
        };
        // Insert first so a crash mid-segment leaves a record for recovery to resolve.
        _ctx.Store.InsertSegment(row);
        encoder.FrameWritten += f => OnFrameWritten(row, encoder, f);
        lock (_gate)
        {
            _encoder = encoder;
            CurrentSegmentId = id;
            var run = Task.Run(encoder.RunAsync);
            _finalizers.Add(run.ContinueWith(t => FinalizeSegment(row, encoder, final, t.Exception?.GetBaseException()), TaskScheduler.Default));
        }
    }

    /// <summary>Closes the current file at <paramref name="ts"/> and continues seamlessly in a new one.</summary>
    public void Rotate(long ts, long utcMs)
    {
        lock (_gate)
        {
            if (_stopped) return;
            _encoder?.RequestStop(ts, utcMs);
            _finalizers.RemoveAll(t => t.IsCompleted);
        }
        StartSegment(ts, utcMs);
    }

    public async Task StopAsync(long ts, long utcMs)
    {
        Task[] pending;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _encoder?.RequestStop(ts, utcMs);
            pending = _finalizers.ToArray();
        }
        var all = Task.WhenAll(pending);
        if (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(20))) != all)
        {
            // Never pull the device out from under a running encoder; release it once the encoder is done.
            Log.Error($"{Monitor.DeviceName}: segment did not finish within 20 s; it is finished in the background (or resolved by recovery on next start)");
            _ = all.ContinueWith(_ => { _capture.Dispose(); _d3d.Dispose(); }, TaskScheduler.Default);
            return;
        }
        _capture.Dispose();
        _d3d.Dispose();
    }

    private void FinalizeSegment(SegmentRecord row, SegmentEncoder enc, string final, Exception? error)
    {
        var part = final + ".part";
        try
        {
            if (enc.FrameCount == 0 || enc.FirstTs < 0)
            {
                if (File.Exists(part)) File.Delete(part);
                _ctx.Store.DeleteSegmentRows([row.Id]);
                if (error != null) Log.Error($"{Monitor.DeviceName}: encoder failed before the first frame", error);
                return;
            }
            var info = Mp4Inspector.Inspect(part);
            row.StartUtcMs = enc.FirstUtcMs;
            row.EndUtcMs = Math.Max(enc.FirstUtcMs, enc.EndUtcMs);
            row.DurationMs = row.EndUtcMs - row.StartUtcMs;
            row.FrameCount = enc.FrameCount;
            row.Activity = enc.Activity;
            row.FinalizedUtcMs = TimeUtil.NowMs();
            if (info.IsValid)
            {
                File.Move(part, final, overwrite: false);
                row.Status = SegmentStatus.Finalized;
                row.FileSize = new FileInfo(final).Length;
                row.Error = error?.Message;
                if (Math.Abs(info.DurationMs - row.DurationMs.Value) > 1000 / Math.Max(1, Plan.Fps) + 250)
                    Log.Warn($"Segment {row.Id}: file duration {info.DurationMs} ms vs clock {row.DurationMs} ms");
            }
            else
            {
                row.Status = SegmentStatus.Corrupt;
                row.FileSize = File.Exists(part) ? new FileInfo(part).Length : 0;
                row.FilePath = _ctx.Paths.ToRelative(part);
                row.Error = $"integrity check failed: {info.Error}" + (error != null ? $"; encoder error: {error.Message}" : "");
                Log.Error($"Segment {row.Id} failed integrity check: {row.Error}");
            }
            _ctx.Store.UpdateSegment(row);
            Log.Info($"Segment {row.Status}: {Path.GetFileName(final)} {TimeUtil.FormatDuration(row.DurationMs ?? 0)}, " +
                     $"{enc.RealFrameCount}/{enc.FrameCount} frames, {TimeUtil.FormatBytes(row.FileSize ?? 0)}, pool {_capture.PoolSize}");
        }
        catch (Exception ex)
        {
            Log.Error($"Finalizing segment {row.Id} failed", ex);
        }
        // The finished segment's Media Foundation pipeline (encoder, buffers, samples) is only released when
        // its WinRT wrappers are collected. The managed heap barely grows, so the GC would otherwise wait
        // far too long; collect once per segment.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        bool isStopped;
        lock (_gate) isStopped = _stopped;
        if (error == null && enc.Discontinuity && !isStopped)
        {
            // The encoder closed the segment itself because time jumped; nothing is recording now.
            Faulted?.Invoke(this, "recording interrupted by sleep or standby", false);
        }
        if (error != null)
        {
            Log.Error($"{Monitor.DeviceName}: encoder stopped with an error", error);
            bool stopped;
            lock (_gate) stopped = _stopped;
            if (!stopped) Faulted?.Invoke(this, error.Message, true);
        }
    }

    // ------------------------------------------------------------------ thumbnails

    private void OnFrameWritten(SegmentRecord row, SegmentEncoder enc, CapturedFrame frame)
    {
        if (frame.Ts < _nextThumbTs) return;
        if (Interlocked.Exchange(ref _thumbBusy, 1) == 1) return;
        _nextThumbTs = frame.Ts + _cfg.ThumbnailIntervalSeconds * 10_000_000L;
        var entry = frame.Entry;
        int gen = _capture.AddRef(frame);
        long tsUtc = QpcClock.ToUtcMs(frame.Ts);
        long offsetSec = Math.Max(0, tsUtc - enc.FirstUtcMs) / 1000;
        var dir = Path.Combine(Path.GetDirectoryName(_ctx.Paths.ToFull(row.FilePath))!, "thumbs");
        var path = Path.Combine(dir, $"{row.Id[..8]}_{offsetSec:D5}.jpg");
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(dir);
                SaveThumbnail(entry, path);
                _ctx.Store.InsertThumbnail(new ThumbnailRecord { SegmentId = row.Id, TsUtcMs = tsUtc, FilePath = _ctx.Paths.ToRelative(path) });
            }
            catch (Exception ex) { Log.Warn("Thumbnail failed", ex); }
            finally
            {
                _capture.Release(entry, gen);
                Interlocked.Exchange(ref _thumbBusy, 0);
            }
        });
    }

    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>320-px JPEG preview. Downscaled from a GPU staging copy; no full-size bitmap is ever allocated.</summary>
    private void SaveThumbnail(PooledTexture entry, string path)
    {
        const int width = 320;
        var pixels = _d3d.ReadDownscaled(entry.Texture, _capture.Width, _capture.Height, width, out int height);
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
        }
        finally { bmp.UnlockBits(data); }
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
        bmp.Save(path, JpegCodec, parameters);
    }
}
