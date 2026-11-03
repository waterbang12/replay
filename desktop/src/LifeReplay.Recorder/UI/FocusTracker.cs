using LifeReplay.Core;
using LifeReplay.Recorder.Recording;

namespace LifeReplay.Recorder.UI;

/// <summary>
/// Logs which app and window title is in front, as intervals in focus_events. Samples once a second and writes
/// only when the window changes (plus a 15 s refresh of the end time). Logs only while actually recording, so
/// pauses, lock, sleep and "outside recording hours" are never logged. Idle time (no input and no screen
/// change for IdleMinutes) isn't counted. Excluded windows are stored as "(private)".
/// Also logs waits on AI tools (ai_waits): no input for AiWait.IdleSeconds while an AI chat is in front, or while
/// Claude Code is working and an editor/terminal is in front. A wait starts at the last input.
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
    private readonly ClaudeCodeActivity _claude = new();
    private long? _waitId;
    private string? _waitTool;
    private long _waitWriteMs;

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
                long end = idle ? now - Math.Min(inputIdleMs, now - _lastScreenChangeMs) : now;
                Close(end);
                CloseWait(end);
                return;
            }

            var (process, title) = Native.ForegroundWindowInfo();
            if (string.IsNullOrEmpty(process)) process = "unknown";
            bool isPrivate = controller.Blanked || ExclusionWatcher.Match(cfg, process, title) != null;
            TrackWait(cfg, now, inputIdleMs, isPrivate ? null : AiTools.Match(process, title)
                ?? (AiTools.IsWorkWindow(process) && _claude.Busy ? AiTools.ClaudeCode : null));
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

    private void TrackWait(RecorderConfig cfg, long now, long inputIdleMs, string? tool)
    {
        long threshold = cfg.AiWait.IdleSeconds * 1000L;
        bool waiting = cfg.AiWait.Enabled && tool != null && inputIdleMs >= threshold;
        if (waiting && _waitId != null && tool == _waitTool)
        {
            if (now - _waitWriteMs >= 15_000) { _host.Store.UpdateAiWaitEnd(_waitId.Value, now); _waitWriteMs = now; }
            return;
        }
        // Input came back: the wait ended at that input. Otherwise (the tool finished or another window came to the
        // front) it ended now.
        var previous = _waitTool;
        CloseWait(inputIdleMs < threshold ? now - inputIdleMs : now);
        if (!waiting) return;
        long start = previous == null ? now - inputIdleMs : now; // a fresh wait began at the last input; a switch of tool, now
        _waitId = _host.Store.InsertAiWait(start, now, tool!);
        _waitTool = tool;
        _waitWriteMs = now;
    }

    private void CloseWait(long endMs)
    {
        if (_waitId is { } id) _host.Store.UpdateAiWaitEnd(id, endMs);
        _waitId = null;
        _waitTool = null;
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
        try { Close(TimeUtil.NowMs()); CloseWait(TimeUtil.NowMs()); } catch { }
        _claude.Dispose();
    }
}
