namespace LifeReplay.Core;

public sealed record FocusItem(string Item, long DurationMs);
public sealed record FocusApp(string App, long DurationMs, List<FocusItem> Items);

/// <summary>Groups window-title intervals into "time per app" and "time per tab / document / file".</summary>
public static class FocusSummary
{
    private static readonly Dictionary<string, string> AppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome.exe"] = "Google Chrome", ["msedge.exe"] = "Microsoft Edge", ["firefox.exe"] = "Firefox", ["whale.exe"] = "Naver Whale",
        ["Code.exe"] = "VS Code", ["devenv.exe"] = "Visual Studio", ["WindowsTerminal.exe"] = "Windows Terminal",
        ["explorer.exe"] = "File Explorer", ["notepad.exe"] = "Notepad", ["Acrobat.exe"] = "Adobe Acrobat",
        ["AcroRd32.exe"] = "Adobe Reader", ["SumatraPDF.exe"] = "SumatraPDF", ["WINWORD.EXE"] = "Word", ["EXCEL.EXE"] = "Excel",
        ["POWERPNT.EXE"] = "PowerPoint", ["Discord.exe"] = "Discord", ["KakaoTalk.exe"] = "KakaoTalk", ["ChatGPT.exe"] = "ChatGPT",
        ["Notion.exe"] = "Notion", ["Obsidian.exe"] = "Obsidian", ["steam.exe"] = "Steam", ["claude.exe"] = "Claude",
    };

    /// <summary>Suffixes apps append to every title; removing them leaves the tab / document name.</summary>
    private static readonly string[] Suffixes =
    [
        " - Google Chrome", " - Microsoft Edge", " - Microsoft​ Edge", " — Mozilla Firefox", " - Mozilla Firefox",
        " - Visual Studio Code", " - Notepad", " - Adobe Acrobat Reader", " - Adobe Acrobat", " - SumatraPDF", " - Whale",
        " - Word", " - Excel", " - PowerPoint", " - Discord", " - Notion", " - Obsidian",
    ];

    public static string AppName(string process)
    {
        if (AppNames.TryGetValue(process, out var name)) return name;
        var n = process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process[..^4] : process;
        return n.Length == 0 ? "Unknown" : n;
    }

    /// <summary>The tab / document / file part of a window title, without the app's suffix.</summary>
    public static string ItemName(string process, string title)
    {
        var t = (title ?? "").Trim();
        // Strip tab counters like "(3) " that chat and mail sites prepend.
        if (t.StartsWith('(') && t.IndexOf(") ", StringComparison.Ordinal) is int close and > 0 and < 6 && t[1..close].All(char.IsDigit))
            t = t[(close + 2)..];
        bool stripped = false;
        foreach (var s in Suffixes)
            if (t.EndsWith(s, StringComparison.OrdinalIgnoreCase)) { t = t[..^s.Length].TrimEnd(); stripped = true; break; }
        if (!stripped)
        {
            // Generic: drop a trailing " - <app name>" (e.g. newer Chrome uses " - Chrome").
            int dash = Math.Max(t.LastIndexOf(" - ", StringComparison.Ordinal), t.LastIndexOf(" — ", StringComparison.Ordinal));
            if (dash > 0)
            {
                var tail = t[(dash + 3)..].Trim().Replace("​", "");
                var exe = process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process[..^4] : process;
                var app = AppName(process);
                if (tail.Equals(exe, StringComparison.OrdinalIgnoreCase) || tail.Equals(app, StringComparison.OrdinalIgnoreCase)
                    || app.EndsWith(" " + tail, StringComparison.OrdinalIgnoreCase))
                    t = t[..dash].TrimEnd();
            }
        }
        // VS Code: "● file.py - folder" → "file.py - folder" (the dot just means unsaved).
        t = t.TrimStart('●', ' ');
        if (t.Length == 0) t = AppName(process);
        return t.Length > 120 ? t[..120] + "…" : t;
    }

    /// <summary>Time per app (largest first), each with its tabs/documents (largest first).</summary>
    public static List<FocusApp> Summarize(IEnumerable<FocusRecord> records, long fromMs, long toMs)
    {
        var perItem = new Dictionary<(string App, string Item), long>();
        foreach (var r in records)
        {
            long d = Math.Min(r.EndUtcMs, toMs) - Math.Max(r.StartUtcMs, fromMs);
            if (d <= 0) continue;
            var key = (AppName(r.Process), ItemName(r.Process, r.Title));
            perItem[key] = perItem.GetValueOrDefault(key) + d;
        }
        return perItem.GroupBy(kv => kv.Key.App)
            .Select(g => new FocusApp(g.Key, g.Sum(kv => kv.Value),
                g.Select(kv => new FocusItem(kv.Key.Item, kv.Value)).OrderByDescending(i => i.DurationMs).ToList()))
            .OrderByDescending(a => a.DurationMs)
            .ToList();
    }
}
