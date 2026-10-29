using LifeReplay.Core;
using LifeReplay.Recorder.Recording;

namespace LifeReplay.Recorder.UI;

/// <summary>
/// Logs which app and window title is in front, as intervals in focus_events. Samples once a second and writes
/// only when the window changes (plus a 15 s refresh of the end time). Logs only while actually recording, so
/// pauses, lock, sleep and "outside recording hours" are never logged. Idle time (no input and no screen
/// change for IdleMinutes) isn't counted. Excluded windows are stored as "(private)".
/// </summary>
internal sealed class FocusTracker : IDisposable
{
    private readonly RecorderHost _host;
    private readonly System.Windows.Forms.Timer _timer;
    private long? _currentId;
    private string? _currentKey;
    private long _lastWriteMs;
    private long _lastFrames = -1;
    private long _lastScreenChangeMs = TimeUtil.NowMs();

    public FocusTracker(RecorderHost host)
    {
        _host = host;
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

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
                Close(idle ? now - Math.Min(inputIdleMs, now - _lastScreenChangeMs) : now);
                return;
            }

            var (process, title) = Native.ForegroundWindowInfo();
            if (string.IsNullOrEmpty(process)) process = "unknown";
            bool isPrivate = controller.Blanked || ExclusionWatcher.Match(cfg, process, title) != null;
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
