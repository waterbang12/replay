using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LifeReplay.Coach;

/// <summary>
/// Checks a model answer before anything is shown. Schema validation alone isn't enough, so it also rejects:
/// unknown evidence or task ids, numbers that don't appear in the context, out-of-range minutes, more than 3
/// actions, an empty or copy-paste counterpoint, and "insufficient evidence" contradictions. Free checks, no model.
/// </summary>
public static partial class SuggestionValidator
{
    public const int MaxMessageLength = 600;
    public const int MaxActions = 3;

    public static (Suggestion? Suggestion, List<string> Errors) Validate(string text, CoachContext ctx)
    {
        var errors = new List<string>();
        JsonElement root;
        try { root = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { return (null, ["answer is not valid JSON"]); }
        if (root.ValueKind != JsonValueKind.Object) return (null, ["answer is not a JSON object"]);

        string Str(string name)
        {
            if (root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String) return p.GetString()!.Trim();
            errors.Add($"{name} missing or not a string");
            return "";
        }

        var message = Str("message");
        var counterpoint = Str("counterpoint");
        var confidence = Str("confidence");
        bool insufficient = root.TryGetProperty("insufficient_evidence", out var ie) && ie.ValueKind is JsonValueKind.True;
        if (!root.TryGetProperty("insufficient_evidence", out ie) || ie.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            errors.Add("insufficient_evidence missing or not a boolean");

        if (message.Length == 0) errors.Add("message is empty");
        if (message.Length > MaxMessageLength) errors.Add($"message longer than {MaxMessageLength} characters");
        if (confidence is not ("low" or "medium" or "high")) errors.Add("confidence must be low, medium or high");
        if (counterpoint.Length < 10) errors.Add("counterpoint is missing or too short");
        else if (Normalize(counterpoint) == Normalize(message)) errors.Add("counterpoint repeats the message");

        var evidence = new List<string>();
        if (root.TryGetProperty("evidence_ids", out var ev) && ev.ValueKind == JsonValueKind.Array)
            foreach (var e in ev.EnumerateArray())
            {
                var id = e.ValueKind == JsonValueKind.String ? e.GetString()! : "";
                if (!ctx.Evidence.ContainsKey(id)) errors.Add($"evidence id '{id}' is not in the context");
                else if (!evidence.Contains(id)) evidence.Add(id);
            }
        else errors.Add("evidence_ids missing");
        if (evidence.Count == 0 && !insufficient) errors.Add("no evidence ids cited (cite evidence or set insufficient_evidence)");

        var actions = new List<SuggestedAction>();
        if (root.TryGetProperty("actions", out var acts) && acts.ValueKind == JsonValueKind.Array)
        {
            if (acts.GetArrayLength() > MaxActions) errors.Add($"more than {MaxActions} actions");
            foreach (var a in acts.EnumerateArray().Take(MaxActions))
            {
                string type = a.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
                string task = a.TryGetProperty("task_id", out var ti) && ti.ValueKind == JsonValueKind.String ? ti.GetString()!.Trim() : "";
                int minutes = a.TryGetProperty("minutes", out var mi) && mi.ValueKind == JsonValueKind.Number && mi.TryGetInt32(out var mv) ? mv : -1;
                string detail = a.TryGetProperty("detail", out var de) && de.ValueKind == JsonValueKind.String ? de.GetString()!.Trim() : "";
                if (!ActionTypes.All.Contains(type)) { errors.Add($"unknown action type '{type}'"); continue; }
                if (task.Length > 0 && !ctx.TaskIds.Contains(task)) errors.Add($"action refers to unknown task '{task}'");
                if (task.Length == 0 && ActionTypes.NeedsTask(type)) errors.Add($"action '{type}' needs a task_id");
                int maxMinutes = Math.Max(ctx.RemainingWorkMinutes, 240);
                if (minutes < 0 || minutes > maxMinutes) errors.Add($"action minutes {minutes} out of range 0–{maxMinutes}");
                if (detail.Length > 300) errors.Add("action detail too long");
                actions.Add(new SuggestedAction(type, task, Math.Max(0, minutes), detail));
            }
        }
        else errors.Add("actions missing");

        // Numbers: only what the context contains (or the minutes the answer itself proposes).
        var allowed = new HashSet<decimal>(ctx.Numbers);
        foreach (var a in actions) { allowed.Add(a.Minutes); allowed.Add(Math.Round(a.Minutes / 60m, 1)); }
        foreach (var n in NumbersIn(message + " " + counterpoint))
            if (n >= 2 && !allowed.Contains(n)) errors.Add($"number {n.ToString(CultureInfo.InvariantCulture)} does not appear in the context");

        return errors.Count == 0
            ? (new Suggestion(message, actions, evidence, counterpoint, confidence, insufficient), errors)
            : (null, errors);
    }

    [GeneratedRegex(@"(?<![A-Za-z_\d.])\d+(?:\.\d+)?")] private static partial Regex Number();

    private static IEnumerable<decimal> NumbersIn(string text)
    {
        foreach (Match m in Number().Matches(text))
            if (decimal.TryParse(m.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) yield return n;
    }

    private static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
