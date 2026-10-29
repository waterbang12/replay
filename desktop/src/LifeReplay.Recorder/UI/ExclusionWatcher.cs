using LifeReplay.Core;

namespace LifeReplay.Recorder.UI;

/// <summary>
/// Watches the foreground window (WinEvent hook + 1 s poll as a safety net) and reports when it belongs to an
/// excluded process or has an excluded title. Must be created on a thread with a message loop.
/// Limitation: only the foreground window is checked; an excluded window that is visible but not focused
/// (e.g. on another monitor) is still recorded.
/// </summary>
internal sealed class ExclusionWatcher : IDisposable
{
    private readonly Native.WinEventDelegate _callback; // keep alive for the hook
    private readonly IntPtr _foregroundHook;
    private readonly IntPtr _nameHook;
    private readonly System.Windows.Forms.Timer _poll;
    private Func<RecorderConfig> _config;
    private string? _current;

    /// <summary>Raised with the matching rule (e.g. "KeePass.exe") or null when nothing excluded is in front.</summary>
    public event Action<string?>? Changed;

    public ExclusionWatcher(Func<RecorderConfig> config)
    {
        _config = config;
        _callback = (_, _, hwnd, idObject, _, _, _) =>
        {
            if (idObject == 0) Check();
        };
        _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        _nameHook = Native.SetWinEventHook(Native.EVENT_OBJECT_NAMECHANGE, Native.EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        _poll = new System.Windows.Forms.Timer { Interval = 1000 };
        _poll.Tick += (_, _) => Check();
        _poll.Start();
        _recheck = new System.Windows.Forms.Timer { Interval = 800 };
        _recheck.Tick += (_, _) => { _recheck.Stop(); Check(); };
    }

    private readonly System.Windows.Forms.Timer _recheck;

    public static string? Match(RecorderConfig cfg, string processName, string title)
    {
        foreach (var p in cfg.ExcludedProcesses)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var want = p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : p + ".exe";
            if (string.Equals(processName, want, StringComparison.OrdinalIgnoreCase)) return p;
        }
        foreach (var t in cfg.ExcludedWindowTitles)
        {
            if (!string.IsNullOrWhiteSpace(t) && title.Contains(t, StringComparison.OrdinalIgnoreCase)) return $"title contains \"{t}\"";
        }
        return null;
    }

    private long _clearSinceMs;

    public void Check()
    {
        try
        {
            var (proc, title) = Native.ForegroundWindowInfo();
            var match = Match(_config(), proc, title);
            if (match != null)
            {
                _clearSinceMs = 0;
                if (match == _current) return;
                _current = match;
                Changed?.Invoke(match); // blank immediately
                return;
            }
            if (_current == null) return;
            // Un-blank only after nothing excluded has been in front for a moment: focus flickers while
            // windows open and close, and staying blank a little longer is the safe side.
            long now = Environment.TickCount64;
            if (_clearSinceMs == 0) { _clearSinceMs = now; _recheck.Start(); return; }
            if (now - _clearSinceMs < 750) return;
            _clearSinceMs = 0;
            _current = null;
            Changed?.Invoke(null);
        }
        catch (Exception ex) { Log.Warn("Exclusion check failed", ex); }
    }

    public void Dispose()
    {
        _poll.Dispose();
        _recheck.Dispose();
        if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
        if (_nameHook != IntPtr.Zero) Native.UnhookWinEvent(_nameHook);
    }
}
