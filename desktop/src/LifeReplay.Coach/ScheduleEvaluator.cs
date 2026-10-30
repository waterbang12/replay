using System.Globalization;
using System.Text.RegularExpressions;
using LifeReplay.Analysis;
using LifeReplay.Core;

namespace LifeReplay.Coach;

/// <summary>Today's commits in a task's repository (from <see cref="GitEvidence"/>).</summary>
public sealed record GitActivity(int Commits, IReadOnlyList<string> Subjects);

public sealed class EvaluationInput
{
    public required DateOnly Day { get; init; }
    public required long NowMs { get; init; }
    public required TodoPlan Plan { get; init; }
    /// <summary>Time per task from the existing matcher (<see cref="DayAnalysis.Match"/>).</summary>
    public required PlanStatus Status { get; init; }
    /// <summary>The day's window log (only used for "not progress" time; titles never leave the PC).</summary>
    public required IReadOnlyList<FocusRecord> Focus { get; init; }
    public required TimeSpan DefaultStart { get; init; }
    public required TimeSpan DefaultEnd { get; init; }
    /// <summary>Commits per task index (only tasks with "repo:").</summary>
    public IReadOnlyDictionary<int, GitActivity> Git { get; init; } = new Dictionary<int, GitActivity>();
    public IReadOnlyList<Goal> Goals { get; init; } = [];
    /// <summary>Your progress / not-progress choices from the Topics view (exact window text).</summary>
    public IReadOnlyDictionary<string, string> TopicKinds { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Deterministic schedule evaluation. Same input → same output; no model involved.
///
/// Progress counts only from verified outputs: a ticked task (100%), "progress: x/y" (x/y). Commits are
/// recorded as evidence but give no fraction. Expected progress = the share of the working window that has passed.
/// A task is ahead/behind when its verified fraction differs from that by more than <see cref="TaskTolerance"/>.
/// The day is evaluated over tasks that have a planned time and verifiable output; if those cover less than
/// <see cref="MinCoverage"/> of the planned time, the day is "insufficient_evidence".
/// </summary>
public static class ScheduleEvaluator
{
    public const double TaskTolerance = 0.15;
    public const double MinCoverage = 0.5;
    public const long MinDayToleranceMs = 15 * 60_000;
    public const double DayToleranceShare = 0.10;

    public static string TaskId(int index) => $"t{index + 1}";

    public static (long StartMs, long EndMs) Window(DateOnly day, TodoPlan plan, TimeSpan defaultStart, TimeSpan defaultEnd)
    {
        var date = day.ToDateTime(TimeOnly.MinValue);
        long s = TimeUtil.LocalToUtcMs(date + (plan.DayStart ?? defaultStart));
        long e = TimeUtil.LocalToUtcMs(date + (plan.DayEnd ?? defaultEnd));
        return (s, Math.Max(s, e));
    }

    public static DayEvaluation Evaluate(EvaluationInput input)
    {
        var (winStart, winEnd) = Window(input.Day, input.Plan, input.DefaultStart, input.DefaultEnd);
        long now = input.NowMs;
        double dayFraction = winEnd <= winStart ? 1 : Math.Clamp((now - winStart) / (double)(winEnd - winStart), 0, 1);
        long remainingWork = Math.Max(0, winEnd - Math.Max(now, winStart));

        var tasks = new List<TaskEvaluation>();
        for (int i = 0; i < input.Status.Tasks.Count; i++)
        {
            var ts = input.Status.Tasks[i];
            var t = ts.Task;
            var outputs = new List<VerifiedOutput>();
            double? fraction = null;
            double conf = 0;
            if (t.Done) { outputs.Add(new("done", "ticked as done in the todo list", 1)); fraction = 1; conf = 1.0; }
            else if (t.ProgressTotal is > 0 && t.ProgressDone is { } pd)
            {
                fraction = Math.Clamp(pd / (double)t.ProgressTotal.Value, 0, 1);
                outputs.Add(new("progress", $"{pd}/{t.ProgressTotal} {t.ProgressUnit ?? "done"}", fraction));
                conf = 0.7; // only as current as you keep the line
            }
            if (input.Git.TryGetValue(i, out var g) && g.Commits > 0)
            {
                outputs.Add(new("commits", $"{g.Commits} commit{(g.Commits == 1 ? "" : "s")} today", null));
                conf = Math.Max(conf, 0.3);
            }

            ScheduleState state;
            string reason;
            if (fraction is not { } f)
            {
                state = ScheduleState.InsufficientEvidence;
                reason = outputs.Count > 0
                    ? $"{TimeUtil.FormatDuration(ts.SpentMs)} focused and {outputs[0].Detail}, but no measurable fraction. Add \"progress: x/y\" or tick it when done."
                    : ts.SpentMs > 0
                        ? $"{TimeUtil.FormatDuration(ts.SpentMs)} focused, but time isn't progress. Add \"progress: x/y\", \"repo:\", or tick it when done."
                        : "No time and no verifiable output yet.";
            }
            else if (f >= 1)
            {
                state = dayFraction < 1 ? ScheduleState.Ahead : ScheduleState.OnTrack;
                reason = dayFraction < 1 ? "Done before the end of the working day." : "Done.";
            }
            else
            {
                double diff = f - dayFraction;
                state = diff > TaskTolerance ? ScheduleState.Ahead : diff < -TaskTolerance ? ScheduleState.Behind : ScheduleState.OnTrack;
                reason = $"{f:P0} done vs {dayFraction:P0} of the working day passed.";
            }
            tasks.Add(new TaskEvaluation(TaskId(i), t.Title, t.PlannedMs, ts.SpentMs, fraction, outputs, state, conf, reason));
        }

        long planned = tasks.Sum(t => t.PlannedMs ?? 0);
        var known = tasks.Where(t => t.PlannedMs is > 0 && t.VerifiedFraction != null).ToList();
        long knownPlanned = known.Sum(t => t.PlannedMs!.Value);
        long verifiedDone = (long)known.Sum(t => t.PlannedMs!.Value * t.VerifiedFraction!.Value);
        long expected = (long)(knownPlanned * dayFraction);
        long remainingPlanned = (long)tasks.Where(t => t.PlannedMs is > 0).Sum(t => t.PlannedMs!.Value * (1 - (t.VerifiedFraction ?? 0)));
        bool overcommitted = remainingPlanned > remainingWork && remainingPlanned > 0;

        ScheduleState dayState;
        double dayConf;
        string dayReason;
        double coverage = planned > 0 ? knownPlanned / (double)planned : 0;
        if (planned == 0)
        {
            dayState = ScheduleState.InsufficientEvidence;
            dayConf = 0;
            dayReason = tasks.Count == 0 ? "No tasks for today." : "No planned times (\"~1h\") on today's tasks.";
        }
        else if (coverage < MinCoverage)
        {
            dayState = ScheduleState.InsufficientEvidence;
            dayConf = coverage;
            dayReason = $"Only {coverage:P0} of the planned time has checkable output (progress lines, ticks). Time alone isn't progress.";
        }
        else
        {
            long tol = Math.Max(MinDayToleranceMs, (long)(knownPlanned * DayToleranceShare));
            long diff = verifiedDone - expected;
            dayState = diff > tol ? ScheduleState.Ahead : diff < -tol ? ScheduleState.Behind : ScheduleState.OnTrack;
            dayConf = Math.Round(coverage * known.Average(t => t.Confidence), 2);
            dayReason = $"{TimeUtil.FormatDuration(verifiedDone)} verified of {TimeUtil.FormatDuration(knownPlanned)} planned (with evidence); " +
                        $"{TimeUtil.FormatDuration(expected)} expected by now ({dayFraction:P0} of the working day).";
        }

        long unplanned = input.Status.Unassigned.Sum(u => u.Ms);
        var (dayStart, dayEnd) = TimeUtil.LocalDayRange(input.Day);
        long distraction = DistractionMs(input.Focus, dayStart, Math.Min(dayEnd, now), input.TopicKinds);
        long distractionHour = DistractionMs(input.Focus, now - 3_600_000, now, input.TopicKinds);
        return new DayEvaluation(input.Day, now, winStart, winEnd, dayFraction, remainingWork, dayState, dayConf,
            planned, knownPlanned, verifiedDone, expected, input.Status.SpentOnPlanMs, unplanned, distraction, distractionHour,
            remainingPlanned, overcommitted, tasks, input.Goals, dayReason);
    }

    /// <summary>"Not progress" time (videos, games, chat; rules plus your Topics choices) inside [from, to).</summary>
    public static long DistractionMs(IReadOnlyList<FocusRecord> focus, long fromMs, long toMs, IReadOnlyDictionary<string, string>? kinds = null)
    {
        if (toMs <= fromMs) return 0;
        return DayAnalysis.Items(focus, fromMs, toMs).Where(i => ProgressKinds.Of(i, kinds) == ProgressKinds.NotProgress).Sum(i => i.DurationMs);
    }
}

/// <summary>"goal: Land a research internship by 2026-12-01" lines anywhere in the todo file.</summary>
public static partial class Goals
{
    [GeneratedRegex(@"^\s*goal\s*:\s*(.+?)(?:\s+(?:by|until|~)\s*(\d{4}-\d{2}-\d{2}))?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex GoalLine();

    public static List<Goal> Parse(string markdown) =>
        GoalLine().Matches(markdown.Replace("\r", ""))
            .Select(m => new Goal(m.Groups[1].Value.Trim(),
                m.Groups[2].Success && DateOnly.TryParseExact(m.Groups[2].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null))
            .Where(g => g.Title.Length > 0).ToList();
}

/// <summary>Reads commit counts from local repositories with the git command line (no network).</summary>
public static class GitEvidence
{
    public static GitActivity? Collect(string repo, long sinceMs, long untilMs, int timeoutMs = 5000)
    {
        try
        {
            if (!Directory.Exists(repo)) return null;
            var since = DateTimeOffset.FromUnixTimeMilliseconds(sinceMs).ToString("yyyy-MM-ddTHH:mm:sszzz");
            var until = DateTimeOffset.FromUnixTimeMilliseconds(untilMs).ToString("yyyy-MM-ddTHH:mm:sszzz");
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var a in new[] { "-C", repo, "log", "--all", $"--since={since}", $"--until={until}", "--pretty=format:%s" }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return null; }
            if (p.ExitCode != 0) return null;
            var subjects = output.Result.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            return new GitActivity(subjects.Count, subjects);
        }
        catch (Exception ex)
        {
            Log.Warn($"Coach: git log failed for {repo}", ex);
            return null;
        }
    }
}

/// <summary>
/// Plan matching without loading the language model: manual labels, keywords, and vectors already cached in the
/// database (the viewer caches every title it has seen). Unseen titles simply don't match by meaning.
/// Used by the background notifier so it stays small; the viewer uses the full matcher.
/// </summary>
public static class CachedPlanMatcher
{
    public static PlanStatus Match(MetadataStore store, TodoPlan plan, IReadOnlyList<FocusRecord> focus, long fromMs, long toMs, long nowMs,
        TimeSpan defaultStart, TimeSpan defaultEnd)
    {
        var labels = store.GetTitleLabels();
        var items = DayAnalysis.Items(focus, fromMs, toMs);
        var texts = items.Select(i => i.Text)
            .Concat(plan.Tasks.Select(t => t.Title + (t.Match.Count > 0 ? " " + string.Join(" ", t.Match) : "")))
            .Concat(labels.Keys).Distinct().ToList();
        var cached = store.GetEmbeddings(ModelStore.ModelName, texts);
        int dim = cached.Values.FirstOrDefault()?.Length ?? 384;
        var zero = new float[dim];
        return DayAnalysis.Match(plan, focus, fromMs, toMs, nowMs, defaultStart, defaultEnd,
            t => cached.TryGetValue(t, out var v) && v.Length == dim ? v : zero, labels);
    }
}
