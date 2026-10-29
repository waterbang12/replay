using LifeReplay.Core;

namespace LifeReplay.Coach;

public sealed record NotificationDecision(bool Notify, string Reason, string? Message = null, string? TaskId = null, string? Trigger = null);

/// <summary>
/// Deterministic rules for when the coach may interrupt. Checked in order; the first rule that says no wins:
/// notifications off → quiet hours → outside the working window → daily maximum → cooldown since the last
/// notification → snoozed → "quiet for today" → low confidence. Then the triggers, most important first:
/// behind schedule, more than 30 min of YouTube/social in the last hour, plan doesn't fit the time left.
/// A task you dismissed isn't named again that day.
/// </summary>
public static class InterventionPolicy
{
    public const long DriftThresholdMs = 30 * 60_000;
    public const double MinConfidence = 0.5;
    public const string QuietTodayNote = "quiet-today";

    public static NotificationDecision Decide(DayEvaluation ev, CoachConfig cfg, IReadOnlyList<Intervention> today, long nowMs)
    {
        if (!cfg.Notifications) return new(false, "notifications are off");
        var local = TimeUtil.ToLocal(nowMs).TimeOfDay;
        if (InQuietHours(cfg.QuietHours, local)) return new(false, "quiet hours");
        if (nowMs < ev.WindowStartMs || nowMs >= ev.WindowEndMs) return new(false, "outside the working window");

        var rules = today.Where(i => i.Source == InterventionSources.Rule).OrderBy(i => i.CreatedUtcMs).ToList();
        if (rules.Count >= cfg.MaxNotificationsPerDay) return new(false, $"daily maximum ({cfg.MaxNotificationsPerDay}) reached");
        if (rules.Count > 0 && nowMs - rules[^1].CreatedUtcMs < cfg.CooldownMinutes * 60_000L) return new(false, "cooldown");
        if (rules.Count > 0 && rules[^1].Response == "snoozed" && nowMs - rules[^1].CreatedUtcMs < cfg.SnoozeMinutes * 60_000L) return new(false, "snoozed");
        if (today.Any(i => i.Kind == QuietTodayNote)) return new(false, "quiet for today");

        var dismissedTasks = today.Where(i => i.Response == "dismissed" && i.TaskId != null).Select(i => i.TaskId!).ToHashSet();
        var usedTriggers = rules.Select(r => r.Kind).ToHashSet();

        if (ev.State == ScheduleState.Behind && ev.Confidence >= MinConfidence)
        {
            var worst = ev.Tasks.Where(t => t.State == ScheduleState.Behind && !dismissedTasks.Contains(t.Id))
                .OrderBy(t => (t.VerifiedFraction ?? 0) - ev.DayFraction).FirstOrDefault();
            string msg = $"Behind plan: {TimeUtil.FormatDuration(ev.VerifiedDoneMs)} verified of {TimeUtil.FormatDuration(ev.ExpectedByNowMs)} expected by now.";
            if (worst != null) msg += $" Most behind: {worst.Title} ({worst.VerifiedFraction:P0} done, {ev.DayFraction:P0} of the day passed).";
            if (worst != null || dismissedTasks.Count == 0) return new(true, "behind", msg, worst?.Id, "behind");
        }
        if (ev.DistractionLastHourMs >= DriftThresholdMs && !usedTriggers.Contains("drift"))
            return new(true, "drift", $"{TimeUtil.FormatDuration(ev.DistractionLastHourMs)} of YouTube/social in the last hour, during working time.", null, "drift");
        if (ev.Overcommitted && !usedTriggers.Contains("overcommitted"))
            return new(true, "overcommitted",
                $"The rest of today's plan ({TimeUtil.FormatDuration(ev.RemainingPlannedMs)}) doesn't fit the working time left ({TimeUtil.FormatDuration(ev.RemainingWorkMs)}). Consider moving something.",
                null, "overcommitted");
        if (ev.State == ScheduleState.Behind) return new(false, "behind, but evidence confidence is low");
        return new(false, "nothing to report");
    }

    public static bool InQuietHours(string quietHours, TimeSpan local)
    {
        if (!CoachConfig.TryParseRange(quietHours, out var s, out var e) || string.IsNullOrWhiteSpace(quietHours) || s == e) return false;
        return s < e ? local >= s && local < e : local >= s || local < e;
    }
}

/// <summary>Deterministic suggestion used whenever the model isn't (or can't be) asked.</summary>
public static class FallbackSuggestion
{
    public static Suggestion Build(CoachKind kind, DayEvaluation ev, string why)
    {
        var behind = ev.Tasks.Where(t => t.State == ScheduleState.Behind).OrderBy(t => (t.VerifiedFraction ?? 0) - ev.DayFraction).FirstOrDefault();
        var noEvidence = ev.Tasks.FirstOrDefault(t => t.State == ScheduleState.InsufficientEvidence && t.PlannedMs is > 0 && t.VerifiedFraction == null);
        var actions = new List<SuggestedAction>();
        string msg;
        switch (ev.State)
        {
            case ScheduleState.Behind when behind != null:
                msg = $"Behind: {TimeUtil.FormatDuration(ev.VerifiedDoneMs)} verified of {TimeUtil.FormatDuration(ev.ExpectedByNowMs)} expected. Next: {behind.Title}.";
                actions.Add(new(ActionTypes.FocusTask, behind.Id, 0, "most behind of the plan"));
                break;
            case ScheduleState.InsufficientEvidence:
                msg = "Not enough evidence to judge the day. " + ev.Reason;
                if (noEvidence != null) actions.Add(new(ActionTypes.AddEvidence, noEvidence.Id, 0, "add \"progress: x/y\" or \"repo:\" under this task"));
                break;
            default:
                msg = $"{CoachNames.Label(ev.State)}: {ev.Reason}";
                break;
        }
        if (ev.Overcommitted)
            msg += $" Remaining plan {TimeUtil.FormatDuration(ev.RemainingPlannedMs)} > time left {TimeUtil.FormatDuration(ev.RemainingWorkMs)}.";
        return new Suggestion(msg, actions, [], $"Rule-based, not reviewed by a model ({why}). Progress numbers are only as current as your todo list.",
            "low", ev.State == ScheduleState.InsufficientEvidence);
    }
}
