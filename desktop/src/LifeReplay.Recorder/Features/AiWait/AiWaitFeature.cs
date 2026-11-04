using LifeReplay.Core;
using LifeReplay.Recorder.Features.WindowLog;

namespace LifeReplay.Recorder.Features.AiWait;

/// <summary>
/// Logs waits on AI tools (ai_waits) from the window log's samples: no input for AiWait.IdleSeconds while an AI chat
/// is in front, or while Claude Code is working and an editor/terminal is in front. A wait starts at the last input.
/// The viewer and coach cut waits into the window log as "Waiting on AI" (see <see cref="AiTools"/>).
/// </summary>
internal sealed class AiWaitFeature : IRecorderFeature
{
    private readonly RecorderHost _host;
    private readonly WindowLogFeature _windowLog;
    private readonly ClaudeCodeActivity _claude = new();
    private long? _waitId;
    private string? _waitTool;
    private long _waitWriteMs;

    public string Name => "waiting on AI";

    public AiWaitFeature(RecorderHost host, WindowLogFeature windowLog)
    {
        _host = host;
        _windowLog = windowLog;
    }

    public void Start() => _windowLog.Sampled += OnSample;

    private void OnSample(ActivitySample s)
    {
        if (!s.Active) { CloseWait(s.EndUtcMs); return; }
        var tool = s.IsPrivate ? null : AiTools.Match(s.Process!, s.Title ?? "")
            ?? (AiTools.IsWorkWindow(s.Process!) && _claude.Busy ? AiTools.ClaudeCode : null);
        TrackWait(_host.Config, s.NowUtcMs, s.InputIdleMs, tool);
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

    public void Dispose()
    {
        _windowLog.Sampled -= OnSample;
        try { CloseWait(TimeUtil.NowMs()); } catch { }
        _claude.Dispose();
    }
}
