using LifeReplay.Core;
using LifeReplay.Recorder.Recording;
using LifeReplay.Recorder.UI;

namespace LifeReplay.Recorder.Features.WindowLog;

/// <summary>
/// Logs which app and window title is in front, as intervals in focus_events. Samples once a second and writes
/// only when the window changes (plus a 15 s refresh of the end time). Logs only while actually recording, so
/// pauses, lock, sleep and "outside recording hours" are never logged. Idle time (no input and no screen
/// change for IdleMinutes) isn't counted. Excluded windows are stored as "(private)".
/// Each second's observation is published as <see cref="Sampled"/> for other features.
/// </summary>
internal sealed class WindowLogFeature : IRecorderFeature
{
    private readonly RecorderHost _host;
    private readonly System.Windows.Forms.Timer _timer;
    private long? _currentId;
    private string? _currentKey;
    private long _lastWriteMs;
    private long _lastFrames = -1;
    private long _lastScreenChangeMs = TimeUtil.NowMs();

    public string Name => "window log";

    /// <summary>Raised once a second on the UI thread.</summary>
    public event Action<ActivitySample>? Sampled;

    public WindowLogFeature(RecorderHost host)
    {
        _host = host;
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();

    private void Tick()
    {
        try
        {
            var cfg = _host.Config;
            var controller = _host.Controller;
            long now = TimeUtil.NowMs();

            long frames = controller.TotalFramesArrived;
            if (frames != _lastFrames) { _lastFrames = frames; _lastScreenChangeMs = now; }
            long inputIdleMs = (long)Native.IdleTime().TotalMilliseconds;
            long idleLimit = cfg.IdleMinutes * 60_000L;
            bool idle = inputIdleMs > idleLimit && now - _lastScreenChangeMs > idleLimit;

            if (!cfg.LogWindowTitles || controller.State != RecState.Recording || idle)
            {
                // Idle: the interval ended when activity stopped, not now.
                long end = idle ? now - Math.Min(inputIdleMs, now - _lastScreenChangeMs) : now;
                Close(end);
                Publish(new ActivitySample(now, inputIdleMs, false, end, null, null, false));
                return;
            }

            var (process, title) = Native.ForegroundWindowInfo();
            if (string.IsNullOrEmpty(process)) process = "unknown";
            bool isPrivate = controller.Blanked || ExclusionWatcher.Match(cfg, process, title) != null;
            Publish(new ActivitySample(now, inputIdleMs, true, now, process, title, isPrivate));
            if (isPrivate) title = "(private)";
            var key = process + "\n" + title;

            if (key != _currentKey)
            {
                Close(now);
                _currentId = _host.Store.InsertFocus(new FocusRecord { DeviceId = _host.DeviceId, StartUtcMs = now, EndUtcMs = now, Process = process, Title = title });
                _currentKey = key;
                _lastWriteMs = now;
            }
            else if (now - _lastWriteMs >= 15_000 && _currentId is { } id)
            {
                _host.Store.UpdateFocusEnd(id, now);
                _lastWriteMs = now;
            }
        }
        catch (Exception ex) { Log.Warn("Focus tracking failed", ex); }
    }

    /// <summary>A subscriber's failure must not stop the window log.</summary>
    private void Publish(ActivitySample sample)
    {
        if (Sampled == null) return;
        foreach (var handler in Sampled.GetInvocationList().Cast<Action<ActivitySample>>())
            try { handler(sample); }
            catch (Exception ex) { Log.Warn("Activity subscriber failed", ex); }
    }

    private void Close(long endMs)
    {
        if (_currentId is { } id) _host.Store.UpdateFocusEnd(id, endMs);
        _currentId = null;
        _currentKey = null;
    }

    public void Dispose()
    {
        _timer.Dispose();
        try { Close(TimeUtil.NowMs()); } catch { }
    }
}
