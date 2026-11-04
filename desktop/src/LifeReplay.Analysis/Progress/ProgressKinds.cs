namespace LifeReplay.Analysis;

/// <summary>
/// Sorts windows into Progress (work that moves something forward), Not progress (videos, games, chat, feeds)
/// and Unsorted. Order of trust: your choice for that exact window, then similarity to a window you sorted
/// (see <see cref="DayAnalysis.Categorize"/>), then the rules in <see cref="ProgressRules"/>, else Unsorted.
/// </summary>
public static class ProgressKinds
{
    public const string Progress = "progress";
    public const string NotProgress = "not_progress";
    public const string Unsorted = "unsorted";

    public static readonly string[] All = [Progress, NotProgress, Unsorted];

    public static string Label(string kind) => kind switch
    {
        Progress => "Progress",
        NotProgress => "Not progress",
        _ => "Unsorted",
    };

    public static bool IsKind(string? s) => s is Progress or NotProgress or Unsorted;

    /// <summary>The built-in rule for a window (<see cref="ProgressRules"/>), or Unsorted.</summary>
    public static string Rule(TitleItem item) => ProgressRules.Classify(item);

    /// <summary>Your exact choice for this window, else the rule (no model needed).</summary>
    public static string Of(TitleItem item, IReadOnlyDictionary<string, string>? manual = null) =>
        manual != null && manual.TryGetValue(item.Text, out var k) && IsKind(k) ? k : Rule(item);
}
