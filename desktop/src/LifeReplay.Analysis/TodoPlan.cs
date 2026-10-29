using System.Globalization;
using System.Text.RegularExpressions;

namespace LifeReplay.Analysis;

public sealed class PlanTask
{
    public required string Title { get; init; }
    public long? PlannedMs { get; init; }
    public List<string> Match { get; init; } = [];
    public int? ProgressDone { get; init; }
    public int? ProgressTotal { get; init; }
    public string? ProgressUnit { get; init; }
    public bool Done { get; init; }
}

/// <summary>
/// A day's plan from a Markdown todo file.
///
///   # 2026-10-30                 ← tasks for that date (optional; without dated headings, the whole file applies every day)
///   hours: 09:00-18:00           ← optional working window, used for ahead/behind
///   - [ ] Read domain theory paper ~1h30m
///     match: domain theory, abramsky
///     progress: 12/40 pages
///   - [x] pwn.college Program Misuse ~2h
///
/// A "# Every day" (or "# Daily") section is added to every date.
/// </summary>
public sealed partial class TodoPlan
{
    public List<PlanTask> Tasks { get; } = [];
    public TimeSpan? DayStart { get; private set; }
    public TimeSpan? DayEnd { get; private set; }

    [GeneratedRegex(@"^\s*#{1,3}\s*(.+?)\s*$")] private static partial Regex Heading();
    [GeneratedRegex(@"^\s*[-*]\s*\[( |x|X)\]\s*(.+?)\s*$")] private static partial Regex Checkbox();
    [GeneratedRegex(@"\s~\s*((?:\d+(?:\.\d+)?\s*h)?\s*(?:\d+\s*m(?:in)?)?)\s*$", RegexOptions.IgnoreCase)] private static partial Regex Estimate();
    [GeneratedRegex(@"^\s*(match|progress|hours)\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase)] private static partial Regex Property();
    [GeneratedRegex(@"^(\d+)\s*/\s*(\d+)\s*(.*)$")] private static partial Regex Progress();
    [GeneratedRegex(@"^(\d{1,2}:\d{2})\s*[-–~]\s*(\d{1,2}:\d{2})$")] private static partial Regex HoursRange();

    public static TodoPlan Parse(string markdown, DateOnly day)
    {
        var plan = new TodoPlan();
        var lines = markdown.Replace("\r", "").Split('\n');
        bool hasDated = lines.Any(l => Heading().Match(l) is { Success: true } m && TryDate(m.Groups[1].Value, out _));
        bool active = !hasDated; // no dated headings: everything applies
        Builder? current = null;
        var builders = new List<Builder>();
        bool inComment = false;
        foreach (var raw in lines)
        {
            // Skip <!-- HTML comments --> (the template's help text lives in one).
            var line = raw;
            if (inComment)
            {
                int close = line.IndexOf("-->", StringComparison.Ordinal);
                if (close < 0) continue;
                inComment = false;
                line = line[(close + 3)..];
            }
            int open = line.IndexOf("<!--", StringComparison.Ordinal);
            if (open >= 0)
            {
                int close = line.IndexOf("-->", open, StringComparison.Ordinal);
                if (close < 0) { inComment = true; line = line[..open]; }
                else line = line[..open] + line[(close + 3)..];
            }
            if (line.Trim().Length == 0) continue;
            var h = Heading().Match(line);
            if (h.Success)
            {
                var text = h.Groups[1].Value;
                if (TryDate(text, out var d)) active = d == day;
                else if (Regex.IsMatch(text, @"^(every\s*day|daily|매일)$", RegexOptions.IgnoreCase)) active = true;
                else if (hasDated) active = false; // other headings in a dated file are ignored
                current = null;
                continue;
            }
            if (!active) continue;
            var cb = Checkbox().Match(line);
            if (cb.Success)
            {
                current = new Builder { Done = cb.Groups[1].Value is "x" or "X" };
                var title = cb.Groups[2].Value;
                var est = Estimate().Match(" " + title);
                if (est.Success && est.Groups[1].Value.Trim().Length > 0)
                {
                    current.PlannedMs = ParseDuration(est.Groups[1].Value);
                    title = (" " + title)[..est.Index].Trim();
                }
                current.Title = title;
                builders.Add(current);
                continue;
            }
            var p = Property().Match(line);
            if (!p.Success) continue;
            var key = p.Groups[1].Value.ToLowerInvariant();
            var value = p.Groups[2].Value;
            if (key == "hours" && HoursRange().Match(value) is { Success: true } hr)
            {
                if (TimeSpan.TryParse(hr.Groups[1].Value, CultureInfo.InvariantCulture, out var s)) plan.DayStart = s;
                if (TimeSpan.TryParse(hr.Groups[2].Value, CultureInfo.InvariantCulture, out var e)) plan.DayEnd = e;
            }
            else if (current != null && key == "match")
                current.Match.AddRange(value.Split(',').Select(x => x.Trim().Trim('"', '\'')).Where(x => x.Length > 0));
            else if (current != null && key == "progress" && Progress().Match(value) is { Success: true } pr)
            {
                current.ProgressDone = int.Parse(pr.Groups[1].Value);
                current.ProgressTotal = int.Parse(pr.Groups[2].Value);
                current.ProgressUnit = pr.Groups[3].Value.Trim() is { Length: > 0 } u ? u : null;
            }
        }
        foreach (var b in builders.Where(b => b.Title.Length > 0))
            plan.Tasks.Add(new PlanTask
            {
                Title = b.Title, PlannedMs = b.PlannedMs, Match = b.Match, Done = b.Done,
                ProgressDone = b.ProgressDone, ProgressTotal = b.ProgressTotal, ProgressUnit = b.ProgressUnit,
            });
        return plan;
    }

    private static bool TryDate(string s, out DateOnly d) =>
        DateOnly.TryParseExact(s.Trim(), ["yyyy-MM-dd", "yyyy.MM.dd", "yyyy/MM/dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    /// <summary>"2h", "45m", "1h30m", "1.5h", "90min".</summary>
    public static long? ParseDuration(string s)
    {
        var m = Regex.Match(s.Trim(), @"^(?:(\d+(?:\.\d+)?)\s*h)?\s*(?:(\d+)\s*m(?:in)?)?$", RegexOptions.IgnoreCase);
        if (!m.Success || (m.Groups[1].Length == 0 && m.Groups[2].Length == 0)) return null;
        double hours = m.Groups[1].Length > 0 ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        int minutes = m.Groups[2].Length > 0 ? int.Parse(m.Groups[2].Value) : 0;
        return (long)(hours * 3_600_000) + minutes * 60_000L;
    }

    /// <summary>Starter file shown the first time the todo list is opened.</summary>
    public static string Template(DateOnly today) => $"""
        # Every day
        hours: 09:00-18:00

        # {today:yyyy-MM-dd}
        - [ ] pwn.college: Program Misuse ~2h
          match: pwn.college
          progress: 0/10 levels
        - [ ] Read a domain theory paper ~1h
        - [ ] Coursework PDF ~1h30m

        <!--
        One "# yyyy-MM-dd" section per day. Tasks under "# Every day" apply to all days.
        "- [ ]" open, "- [x]" done. "~1h30m" = planned time (used for ahead/behind).
        Optional lines under a task:
          match: words, that, identify, it     (exact keywords; otherwise matched by meaning)
          progress: 3/10 levels                (lets LifeReplay estimate the time left at your pace)
        -->
        """;

    private sealed class Builder
    {
        public string Title = "";
        public long? PlannedMs;
        public List<string> Match = [];
        public int? ProgressDone, ProgressTotal;
        public string? ProgressUnit;
        public bool Done;
    }
}
