using System.Text.Json;

namespace LifeReplay.Core;

/// <summary>A stretch of time spent waiting on an AI tool: no input while it was in front or while it was working.</summary>
public sealed record AiWait(long Id, long StartUtcMs, long EndUtcMs, string Tool)
{
    public long DurationMs => Math.Max(0, EndUtcMs - StartUtcMs);
}

/// <summary>
/// Waiting on AI tools. The recorder logs a wait when there's no keyboard/mouse input for
/// <see cref="AiWaitSettings.IdleSeconds"/> and either an AI chat window/tab is in front, or Claude Code is working
/// while an editor/terminal is in front. Waits are cut out of the window log and shown as the app
/// "Waiting on AI", so they count as not progress everywhere (Apps, Topics, Plan, coach).
/// </summary>
public static class AiTools
{
    /// <summary>Process name used for waits spliced into the window log; <see cref="FocusSummary.AppName"/> shows it as "Waiting on AI".</summary>
    public const string WaitProcess = "ai-wait";
    public const string ClaudeCode = "Claude Code";

    private static readonly Dictionary<string, string> Apps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ChatGPT.exe"] = "ChatGPT", ["claude.exe"] = "Claude", ["Perplexity.exe"] = "Perplexity",
    };

    /// <summary>Browser tab titles of AI chat sites (the tab title usually ends with or contains the product name).</summary>
    private static readonly (string Word, string Tool)[] Titles =
    [
        ("ChatGPT", "ChatGPT"), ("Claude", "Claude"), ("Gemini", "Gemini"), ("Perplexity", "Perplexity"),
        ("Copilot", "Copilot"), ("DeepSeek", "DeepSeek"), ("Grok", "Grok"), ("Le Chat", "Mistral"), ("NotebookLM", "NotebookLM"),
    ];

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "whale.exe", "brave.exe", "opera.exe", "arc.exe",
    };

    /// <summary>Where coding agents run: editors and terminals.</summary>
    private static readonly HashSet<string> WorkWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code.exe", "Cursor.exe", "WindowsTerminal.exe", "powershell.exe", "pwsh.exe", "cmd.exe", "wt.exe",
        "devenv.exe", "idea64.exe", "pycharm64.exe", "claude.exe", "Windsurf.exe",
    };

    /// <summary>The AI chat tool in this window, or null.</summary>
    public static string? Match(string process, string title)
    {
        if (Apps.TryGetValue(process, out var app)) return app;
        if (!Browsers.Contains(process)) return null;
        foreach (var (word, tool) in Titles)
            if (title.Contains(word, StringComparison.OrdinalIgnoreCase)) return tool;
        return null;
    }

    public static bool IsWorkWindow(string process) => WorkWindows.Contains(process);

    /// <summary>
    /// From the end of a Claude Code transcript (JSON Lines): true = still working (the last message is a prompt,
    /// a tool result or a tool call), false = finished (the last assistant message ended its turn), null = unknown.
    /// Only the "type" and "stop_reason" fields are read.
    /// </summary>
    public static bool? TranscriptBusy(string tail)
    {
        var lines = tail.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "user") return true;
                if (type != "assistant") continue;
                var stop = root.TryGetProperty("message", out var m) && m.TryGetProperty("stop_reason", out var s) ? s.GetString() : null;
                return stop != "end_turn";
            }
            catch (JsonException) { /* the first line of the tail is usually cut off */ }
        }
        return null;
    }

    /// <summary>
    /// The window log with waits cut in: each wait replaces whatever window was in front during it, as a record
    /// with process <see cref="WaitProcess"/> and the tool as title. Sorted by start.
    /// </summary>
    public static List<FocusRecord> Splice(IReadOnlyList<FocusRecord> focus, IReadOnlyList<AiWait> waits)
    {
        if (waits.Count == 0) return focus.ToList();
        var w = waits.Where(x => x.EndUtcMs > x.StartUtcMs).OrderBy(x => x.StartUtcMs).ToList();
        var result = new List<FocusRecord>();
        foreach (var f in focus)
        {
            long cursor = f.StartUtcMs;
            foreach (var x in w)
            {
                if (x.EndUtcMs <= cursor || x.StartUtcMs >= f.EndUtcMs) continue;
                if (x.StartUtcMs > cursor) result.Add(Piece(f, cursor, x.StartUtcMs));
                cursor = Math.Max(cursor, x.EndUtcMs);
                if (cursor >= f.EndUtcMs) break;
            }
            if (cursor < f.EndUtcMs) result.Add(Piece(f, cursor, f.EndUtcMs));
        }
        foreach (var x in w)
            result.Add(new FocusRecord { DeviceId = focus.FirstOrDefault()?.DeviceId ?? "pc", StartUtcMs = x.StartUtcMs, EndUtcMs = x.EndUtcMs, Process = WaitProcess, Title = x.Tool });
        return result.OrderBy(r => r.StartUtcMs).ThenBy(r => r.EndUtcMs).ToList();
    }

    private static FocusRecord Piece(FocusRecord f, long start, long end) =>
        new() { Id = f.Id, DeviceId = f.DeviceId, StartUtcMs = start, EndUtcMs = end, Process = f.Process, Title = f.Title };
}

/// <summary>Logs time waiting on AI tools as "Waiting on AI" (not progress). See <see cref="AiTools"/>.</summary>
public sealed class AiWaitSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>No keyboard/mouse input for this long (while an AI tool is in front or working) starts a wait.</summary>
    public int IdleSeconds { get; set; } = 20;
}
