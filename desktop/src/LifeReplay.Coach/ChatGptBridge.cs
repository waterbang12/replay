using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LifeReplay.Core;

namespace LifeReplay.Coach;

public sealed record ProposedTask(string Title, int Minutes, int? ProgressTotal, string? ProgressUnit, string? Match);
public sealed record PlanProposal(DateOnly Day, List<ProposedTask> Tasks);

/// <summary>
/// Refining plans in the ChatGPT website, with no API credits: copy a context pack → discuss in ChatGPT →
/// paste ChatGPT's final ```lifereplay-plan block back → validated → appended to the todo list only if you click.
/// No browser automation (against OpenAI's terms and fragile).
/// </summary>
public static partial class ChatGptBridge
{
    public const string BlockName = "lifereplay-plan";

    public static string BuildPack(CoachContext ctx, DayEvaluation ev)
    {
        var pretty = JsonNode.Parse(ctx.InputJson)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var tomorrow = ev.Day.AddDays(1).ToString("yyyy-MM-dd");
        return $$"""
            I track my work with LifeReplay. Below is today's measured state (computed on my PC; "verified" means
            ticked tasks, progress lines or commits, not just time in an app). Help me refine my plan.
            Be critical: point out where the plan doesn't fit my measured pace (estimate_ratio_14d) or my goals.

            ```json
            {{pretty}}
            ```

            When we agree on a plan, end your reply with exactly one block in this format (minutes = planned time;
            progress_total/progress_unit/match are optional):

            ```{{BlockName}}
            {"day": "{{tomorrow}}", "tasks": [
              {"title": "Reproduce Table 2 baseline", "minutes": 120, "progress_total": 3, "progress_unit": "runs", "match": "baseline"}
            ]}
            ```
            """;
    }

    [GeneratedRegex(@"```\s*lifereplay-plan\s*\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex Block();

    /// <summary>Parses and checks the pasted block. Warnings don't block; errors do.</summary>
    public static (PlanProposal? Plan, List<string> Errors, List<string> Warnings) Parse(string pasted, DateOnly today, long workingWindowMs, double? estimateRatio)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var matches = Block().Matches(pasted);
        if (matches.Count == 0) return (null, ["No ```lifereplay-plan block found in the pasted text."], warnings);
        if (matches.Count > 1) warnings.Add("More than one plan block; using the last one.");
        JsonElement root;
        try { root = JsonDocument.Parse(matches[^1].Groups[1].Value).RootElement.Clone(); }
        catch (JsonException ex) { return (null, ["The plan block is not valid JSON: " + ex.Message], warnings); }

        DateOnly day = today;
        if (root.TryGetProperty("day", out var d) && d.ValueKind == JsonValueKind.String)
        {
            if (!DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) errors.Add("\"day\" must be yyyy-MM-dd.");
            else if (day < today) errors.Add($"\"day\" {day:yyyy-MM-dd} is in the past.");
        }
        else errors.Add("\"day\" is missing.");

        var tasks = new List<ProposedTask>();
        if (root.TryGetProperty("tasks", out var ts) && ts.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in ts.EnumerateArray())
            {
                var title = t.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String ? Clean(ti.GetString()!) : "";
                int minutes = t.TryGetProperty("minutes", out var mi) && mi.ValueKind == JsonValueKind.Number && mi.TryGetInt32(out var mv) ? mv : -1;
                int? total = t.TryGetProperty("progress_total", out var pt) && pt.ValueKind == JsonValueKind.Number && pt.TryGetInt32(out var pv) ? pv : null;
                string? unit = t.TryGetProperty("progress_unit", out var pu) && pu.ValueKind == JsonValueKind.String ? Clean(pu.GetString()!) : null;
                string? match = t.TryGetProperty("match", out var ma) && ma.ValueKind == JsonValueKind.String ? Clean(ma.GetString()!) : null;
                if (title.Length == 0 || title.Length > 120) { errors.Add("Each task needs a title of 1–120 characters."); continue; }
                if (minutes is < 5 or > 600) { errors.Add($"\"{title}\": minutes must be 5–600."); continue; }
                if (total is <= 0 or > 10_000) { warnings.Add($"\"{title}\": progress_total ignored."); total = null; }
                tasks.Add(new ProposedTask(title, minutes, total, string.IsNullOrWhiteSpace(unit) ? null : unit, string.IsNullOrWhiteSpace(match) ? null : match));
            }
        }
        else errors.Add("\"tasks\" is missing.");
        if (tasks.Count == 0 && errors.Count == 0) errors.Add("The plan has no tasks.");
        if (tasks.Count > 15) errors.Add("More than 15 tasks for one day.");

        long totalMs = tasks.Sum(t => t.Minutes * 60_000L);
        if (workingWindowMs > 0)
        {
            double ratio = estimateRatio ?? 1;
            long realistic = (long)(totalMs * ratio);
            if (realistic > workingWindowMs)
                warnings.Add($"Planned {TimeUtil.FormatDuration(totalMs)}" + (estimateRatio is { } r ? $" (≈{TimeUtil.FormatDuration(realistic)} at your measured {r:0.0}× pace)" : "") +
                             $" is more than the {TimeUtil.FormatDuration(workingWindowMs)} working window.");
        }
        return errors.Count > 0 ? (null, errors, warnings) : (new PlanProposal(day, tasks), errors, warnings);
    }

    public static string ToMarkdown(PlanProposal p)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(p.Day.ToString("yyyy-MM-dd")).Append('\n');
        foreach (var t in p.Tasks)
        {
            sb.Append("- [ ] ").Append(t.Title).Append(" ~").Append(Duration(t.Minutes)).Append('\n');
            if (t.ProgressTotal is { } total) sb.Append("  progress: 0/").Append(total).Append(t.ProgressUnit is { } u ? " " + u : "").Append('\n');
            if (t.Match is { } m) sb.Append("  match: ").Append(m).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Appends the plan to the todo file after a backup (todo.md.bak). Only called on an explicit click.</summary>
    public static void AppendToTodo(string todoPath, string markdown)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(todoPath)!);
        if (File.Exists(todoPath)) File.Copy(todoPath, todoPath + ".bak", overwrite: true);
        var existing = File.Exists(todoPath) ? File.ReadAllText(todoPath) : "";
        File.WriteAllText(todoPath, existing.TrimEnd() + (existing.Length > 0 ? "\n\n" : "") + markdown);
    }

    private static string Duration(int minutes) => minutes % 60 == 0 ? $"{minutes / 60}h" : minutes > 60 ? $"{minutes / 60}h{minutes % 60}m" : $"{minutes}m";

    private static string Clean(string s) => Regex.Replace(s.Replace('\n', ' ').Replace('\r', ' '), @"\s{2,}", " ").Trim().TrimStart('#', '-', '*', ' ');
}
