using LifeReplay.Core;
using LifeReplay.Recorder.Capture;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LifeReplay.Recorder.Recording;

/// <summary>
/// Encodes one MP4 segment from a MonitorCapture with Media Foundation (MediaTranscoder + MediaStreamSource).
/// Media Foundation picks the GPU's H.264 encoder when HardwareAccelerationEnabled is set and falls back to
/// its software encoder otherwise. Frames are timestamped with QPC, so the file timeline is real time even
/// though frames only arrive when the screen changes.
/// </summary>
internal sealed class SegmentEncoder
{
    // MF_MT_MAX_KEYFRAME_SPACING: a keyframe at least every 2 s keeps seeking fast.
    private static readonly Guid MaxKeyframeSpacing = new("c16eb52b-73a1-476f-8d62-839d6a020652");
    /// <summary>
    /// Falling this far behind the frame clock means the process was frozen (sleep, Modern Standby, debugger).
    /// Filling that time would mean hours of repeated frames after a night of standby, so the segment ends instead.
    /// </summary>
    private const long DiscontinuityTicks = 90 * 10_000_000L;

    private readonly MonitorCapture _capture;
    private readonly EncodingPlan _plan;
    private readonly bool _hardware;
    private readonly string _outputPath;
    private readonly long _seedTs;
    private readonly long _seedUtcMs;
    private readonly long _intervalTicks;
    private readonly ManualResetEvent _stop = new(false);
    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<int> _activity = [];
    private CapturedFrame? _first;
    private volatile bool _stopRequested;
    private bool _finalSent;
    private long _stopTs;

    public long FirstTs { get; private set; } = -1;
    public long LastTs { get; private set; } = -1;
    public long FirstUtcMs { get; private set; }
    public long StopUtcMs { get; private set; }
    public int FrameCount { get; private set; }
    public int RealFrameCount { get; private set; }
    public bool FinalFrameWritten { get; private set; }
    /// <summary>True if the segment was cut short because time jumped (system sleep / standby).</summary>
    public bool Discontinuity { get; private set; }
    public string EncoderName => _hardware ? "mf-h264-hw" : "mf-h264-sw";
    public Task<bool> Started => _started.Task;
    public string Activity => Core.Activity.Encode(_activity);

    /// <summary>Raised on the encoder thread for each frame written (not for the closing frame).</summary>
    public event Action<CapturedFrame>? FrameWritten;

    /// <param name="seedTs">QPC time this segment starts at when continuing from a previous one; 0 for the first segment.</param>
    public SegmentEncoder(MonitorCapture capture, EncodingPlan plan, bool hardware, string outputPath, long seedTs, long seedUtcMs)
    {
        _capture = capture;
        _plan = plan;
        _hardware = hardware;
        _outputPath = outputPath;
        _seedTs = seedTs;
        _seedUtcMs = seedUtcMs;
        _intervalTicks = 10_000_000L / Math.Max(1, plan.Fps);
    }

    /// <summary>Ends the segment at <paramref name="stopTs"/>: the last frame is repeated up to that time, then the file is closed.</summary>
    public void RequestStop(long stopTs, long stopUtcMs)
    {
        if (_stopRequested) return;
        _stopTs = stopTs;
        StopUtcMs = stopUtcMs;
        _stopRequested = true;
        _stop.Set();
    }

    /// <summary>A stopped segment ends exactly at the stop time, so consecutive segments share the boundary.</summary>
    public long EndUtcMs => _stopRequested && StopUtcMs > 0 ? Math.Max(StopUtcMs, FirstUtcMs)
        : FirstTs < 0 ? FirstUtcMs : FirstUtcMs + (LastTs - FirstTs) / 10_000;

    public async Task RunAsync()
    {
        var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_capture.Width, (uint)_capture.Height);
        var source = new MediaStreamSource(new VideoStreamDescriptor(input)) { BufferTime = TimeSpan.Zero, CanSeek = false };
        source.Starting += OnStarting;
        source.SampleRequested += OnSampleRequested;

        var video = VideoEncodingProperties.CreateH264();
        video.Width = (uint)_plan.Width;
        video.Height = (uint)_plan.Height;
        video.Bitrate = (uint)_plan.BitrateBps;
        video.FrameRate.Numerator = (uint)_plan.Fps;
        video.FrameRate.Denominator = 1;
        video.PixelAspectRatio.Numerator = 1;
        video.PixelAspectRatio.Denominator = 1;
        video.ProfileId = H264ProfileIds.High;
        try { video.Properties[MaxKeyframeSpacing] = (uint)Math.Max(1, _plan.Fps * 2); } catch { /* encoder default */ }
        var profile = new MediaEncodingProfile
        {
            Container = new ContainerEncodingProperties { Subtype = MediaEncodingSubtypes.Mpeg4 },
            Video = video,
            Audio = null, // LifeReplay never records audio
        };

        IRandomAccessStream? stream = null;
        try
        {
            stream = await FileRandomAccessStream.OpenAsync(_outputPath, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateNew);
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = _hardware };
            var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile);
            if (!prepared.CanTranscode)
                throw new InvalidOperationException($"Media Foundation cannot encode this format: {prepared.FailureReason}");
            await prepared.TranscodeAsync();
            await stream.FlushAsync();
        }
        finally
        {
            stream?.Dispose();
            if (_first != null) { _capture.Release(_first); _first = null; }
            // Media Foundation doesn't raise Processed for the last samples when the pipeline shuts down. The
            // file is closed now, so nothing reads those textures any more: hand them back to the pool.
            int leftover = 0;
            foreach (var key in _inFlight.Keys)
            {
                if (_inFlight.TryRemove(key, out var f)) { _capture.Release(f); leftover++; }
            }
            if (leftover > 0) Log.Info($"{_capture.Monitor.DeviceName}: returned {leftover} texture(s) still held at end of segment");
            _started.TrySetResult(false);
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, CapturedFrame> _inFlight = new();
    private long _sampleSeq;

    private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
    {
        try
        {
            // Continuing segment: current picture stamped at the boundary. First segment: wait for Windows' first frame.
            var first = _capture.SeedFrame(_seedTs > 0 ? _seedTs : QpcClock.Now100ns(), _stop, TimeSpan.FromSeconds(10));
            if (first == null)
            {
                Log.Warn($"{_capture.Monitor.DeviceName}: no frame received to start the segment");
                args.Request.SetActualStartPosition(TimeSpan.FromTicks(QpcClock.Now100ns()));
                _stopRequested = true;
                _finalSent = true;
                return;
            }
            _first = first;
            FirstTs = first.Ts;
            FirstUtcMs = _seedTs > 0 && _seedUtcMs > 0 ? _seedUtcMs : QpcClock.ToUtcMs(first.Ts);
            args.Request.SetActualStartPosition(TimeSpan.FromTicks(first.Ts));
            _started.TrySetResult(true);
        }
        catch (Exception ex)
        {
            Log.Error("Encoder start failed", ex);
            _stopRequested = true;
            _finalSent = true;
            args.Request.SetActualStartPosition(TimeSpan.FromTicks(QpcClock.Now100ns()));
        }
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        try
        {
            if (_first != null)
            {
                var f = _first;
                _first = null;
                Emit(args, f, notify: true);
                return;
            }
            while (true)
            {
                if (_stopRequested)
                {
                    if (!_finalSent)
                    {
                        _finalSent = true;
                        long stopTs = _stopTs;
                        if (FirstTs >= 0 && stopTs - LastTs > DiscontinuityTicks)
                        {
                            // Stopping after a freeze (e.g. resume from standby): don't stretch the last picture
                            // across the time the PC was asleep. End one frame after the last real one.
                            Discontinuity = true;
                            stopTs = LastTs + _intervalTicks;
                            StopUtcMs = FirstUtcMs + (stopTs - FirstTs) / 10_000;
                        }
                        // Hold the last picture until the stop time so the file covers the full segment.
                        // The last grid slot at or before the stop time: the file then ends within one frame of it.
                        long finalTs = FirstTs >= 0 ? FirstTs + (stopTs - FirstTs) / _intervalTicks * _intervalTicks : -1;
                        if (FirstTs >= 0 && finalTs > LastTs && _capture.Repeat(finalTs) is { } last)
                        {
                            Emit(args, last, notify: false, exactTs: true);
                            FinalFrameWritten = true;
                            return;
                        }
                    }
                    args.Request.Sample = null; // end of stream -> MediaTranscoder finalizes the MP4
                    return;
                }
                // One sample per frame slot. Media Foundation fills gaps with the *next* frame, which would
                // show future screen content (and defeat privacy blanking), so we never leave gaps.
                long slot = LastTs + _intervalTicks;
                long lag = QpcClock.Now100ns() - slot;
                if (lag > DiscontinuityTicks)
                {
                    // We were frozen (sleep / Modern Standby): end the file right after the last frame instead of
                    // stretching it across the time the PC was asleep.
                    Log.Warn($"{_capture.Monitor.DeviceName}: clock jumped {lag / 10_000_000} s (sleep/standby?); closing the segment at the last frame");
                    Discontinuity = true;
                    RequestStop(slot, FirstUtcMs + (slot - FirstTs) / 10_000);
                    continue;
                }
                var frame = _capture.FrameForSlot(slot, _intervalTicks, _stop);
                if (frame != null)
                {
                    Emit(args, frame, notify: true);
                    return;
                }
                if (!_stopRequested && _capture.IsClosed)
                {
                    // Monitor went away: close the file at the current time.
                    RequestStop(QpcClock.Now100ns(), TimeUtil.NowMs());
                }
                else if (!_stopRequested)
                {
                    _stop.WaitOne(50); // nothing captured yet (should not happen after the first frame)
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"{_capture.Monitor.DeviceName}: sample request failed; closing segment", ex);
            args.Request.Sample = null;
        }
    }

    /// <summary>
    /// Media Foundation writes a constant-frame-rate file (every sample lasts one frame interval). Samples are
    /// therefore stamped exactly on the frame grid (first frame + k × interval), so position k in the file is
    /// exactly k intervals of real time after the segment start.
    /// </summary>
    private void Emit(MediaStreamSourceSampleRequestedEventArgs args, CapturedFrame frame, bool notify, bool exactTs = true)
    {
        long ts = LastTs >= 0 && frame.Ts <= LastTs ? LastTs + _intervalTicks : frame.Ts;
        if (FirstTs < 0) FirstTs = ts;
        MediaStreamSample sample;
        try
        {
            sample = MediaStreamSample.CreateFromDirect3D11Surface(frame.Entry.Surface, TimeSpan.FromTicks(ts));
        }
        catch
        {
            _capture.Release(frame);
            throw;
        }
        sample.Duration = TimeSpan.FromTicks(_intervalTicks);
        long seq = Interlocked.Increment(ref _sampleSeq);
        _inFlight[seq] = frame;
        // The pipeline is done with the texture once the sample is processed; return it to the pool.
        sample.Processed += (_, _) => { if (_inFlight.TryRemove(seq, out var f)) _capture.Release(f); };
        args.Request.Sample = sample;
        LastTs = ts;
        FrameCount++;
        if (frame.IsReal)
        {
            RealFrameCount++;
            int bucket = (int)((ts - FirstTs) / (Core.Activity.BucketMs * 10_000L));
            while (_activity.Count <= bucket) _activity.Add(0);
            _activity[bucket]++;
        }
        else
        {
            int bucket = (int)((ts - FirstTs) / (Core.Activity.BucketMs * 10_000L));
            while (_activity.Count <= bucket) _activity.Add(0);
        }
        if (notify)
        {
            try { FrameWritten?.Invoke(frame); } catch (Exception ex) { Log.Warn("FrameWritten handler failed", ex); }
        }
    }
}
