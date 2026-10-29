namespace LifeReplay.Coach;

/// <summary>
/// Schedule state from verified outputs only. Time spent in an app is never proof of progress, so a plan whose
/// tasks have no checkable output (ticked box, "progress: x/y", commits) is <see cref="InsufficientEvidence"/>.
/// </summary>
public enum ScheduleState { OnTrack, Ahead, Behind, InsufficientEvidence }

public enum CoachKind { Morning, Check, Review }

public static class CoachNames
{
    public static string Of(ScheduleState s) => s switch
    {
        ScheduleState.OnTrack => "on_track",
        ScheduleState.Ahead => "ahead",
        ScheduleState.Behind => "behind",
        _ => "insufficient_evidence",
    };

    public static string Of(CoachKind k) => k switch { CoachKind.Morning => "morning", CoachKind.Review => "review", _ => "check" };

    public static string Label(ScheduleState s) => s switch
    {
        ScheduleState.OnTrack => "On track",
        ScheduleState.Ahead => "Ahead",
        ScheduleState.Behind => "Behind",
        _ => "Not enough evidence",
    };
}

/// <summary>A longer-term aim from the todo file: "goal: Land a lab internship by 2026-12-01".</summary>
public sealed record Goal(string Title, DateOnly? Deadline);

/// <summary>Something checkable that shows progress: a ticked task, a "progress: x/y" line, commits.</summary>
public sealed record VerifiedOutput(string Kind, string Detail, double? Fraction);

public sealed record TaskEvaluation(
    string Id, string Title, long? PlannedMs, long FocusMs, double? VerifiedFraction,
    IReadOnlyList<VerifiedOutput> Outputs, ScheduleState State, double Confidence, string Reason);

public sealed record DayEvaluation(
    DateOnly Day, long NowMs, long WindowStartMs, long WindowEndMs, double DayFraction, long RemainingWorkMs,
    ScheduleState State, double Confidence,
    long PlannedMs, long PlannedWithEvidenceMs, long VerifiedDoneMs, long ExpectedByNowMs,
    long FocusOnPlanMs, long UnplannedMs, long DistractionMs, long DistractionLastHourMs,
    long RemainingPlannedMs, bool Overcommitted,
    IReadOnlyList<TaskEvaluation> Tasks, IReadOnlyList<Goal> Goals, string Reason);

/// <summary>One proposed step. Proposals only: nothing is applied without the user.</summary>
public sealed record SuggestedAction(string Type, string TaskId, int Minutes, string Detail);

public static class ActionTypes
{
    public const string FocusTask = "focus_task", Reorder = "reorder", Reschedule = "reschedule", SplitTask = "split_task",
        TakeBreak = "take_break", AddEvidence = "add_evidence", None = "none";
    public static readonly string[] All = [FocusTask, Reorder, Reschedule, SplitTask, TakeBreak, AddEvidence, None];
    public static bool NeedsTask(string t) => t is FocusTask or SplitTask or Reschedule or AddEvidence;
}

/// <summary>A validated model answer (or a deterministic fallback with the same shape).</summary>
public sealed record Suggestion(
    string Message, IReadOnlyList<SuggestedAction> Actions, IReadOnlyList<string> EvidenceIds,
    string Counterpoint, string Confidence, bool InsufficientEvidence);

public sealed record Intervention(
    long Id, long CreatedUtcMs, string Day, string Kind, string Source, string State, string Message,
    IReadOnlyList<SuggestedAction> Actions, IReadOnlyList<string> Evidence, string? Counterpoint, string? Confidence,
    string? TaskId, long? RequestId, string? Response, InterventionOutcome? Outcome);

/// <summary>Measured afterwards from the activity log: did focus go to the suggested task?</summary>
public sealed record InterventionOutcome(long InterventionId, long MeasuredUtcMs, long WindowMs, string? TaskId, long FocusMs, bool Followed);

public static class InterventionSources
{
    /// <summary>Validated model answer.</summary>
    public const string Model = "model";
    /// <summary>Deterministic text used when the model is off, over budget, offline, or its answer failed validation.</summary>
    public const string Fallback = "fallback";
    /// <summary>Deterministic notification from <see cref="InterventionPolicy"/>.</summary>
    public const string Rule = "rule";
    /// <summary>A choice you made (e.g. "quiet for today"), stored so the rules can respect it.</summary>
    public const string User = "user";
}
