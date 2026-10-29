using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LifeReplay.Core;

namespace LifeReplay.Coach;

/// <summary>What one request will send, and what the answer is allowed to refer to.</summary>
public sealed class CoachContext
{
    public required CoachKind Kind { get; init; }
    public required string Instructions { get; init; }
    /// <summary>The exact JSON sent as the model input.</summary>
    public required string InputJson { get; init; }
    public required IReadOnlyDictionary<string, string> Evidence { get; init; }
    public required IReadOnlySet<string> TaskIds { get; init; }
    /// <summary>Every number that appears in the context (plus %/hour forms): the only numbers the answer may state.</summary>
    public required IReadOnlySet<decimal> Numbers { get; init; }
    public required int RemainingWorkMinutes { get; init; }
}

/// <summary>
/// Builds the compact, sanitized context for a model call. Never included: video, screenshots, window titles,
/// URLs, file paths, or the window log itself. Included: today's plan (task titles unless redacted), minutes,
/// verified outputs, the deterministic evaluation, goals, and recent suggestions with your responses.
/// Free text that does go out (task titles, goals, optional commit subjects) passes through <see cref="Sanitize"/>.
/// </summary>
public static partial class ContextBuilder
{
    public static CoachContext Build(CoachKind kind, DayEvaluation ev, IReadOnlyList<Intervention> recent, double? estimateRatio,
        CoachConfig cfg, IReadOnlyDictionary<string, GitActivity>? gitByTask = null)
    {
        var evidence = new Dictionary<string, string>();
        string Add(string text) { var id = $"ev{evidence.Count + 1}"; evidence[id] = text; return id; }
        static int Min(long ms) => (int)Math.Round(ms / 60_000.0);

        var tasks = new JsonArray();
        foreach (var t in ev.Tasks)
        {
            var ids = new JsonArray();
            if (t.FocusMs > 0) ids.Add(Add($"{t.Id}: {Min(t.FocusMs)} min focused today (window log)"));
            foreach (var o in t.Outputs)
            {
                string detail = o.Detail;
                if (o.Kind == "commits" && cfg.IncludeCommitMessages && gitByTask != null && gitByTask.TryGetValue(t.Id, out var g))
                    detail += ": " + string.Join("; ", g.Subjects.Take(8).Select(s => Sanitize(s, 100)));
                ids.Add(Add($"{t.Id}: {detail} ({o.Kind})"));
            }
            tasks.Add(new JsonObject
            {
                ["id"] = t.Id,
                ["title"] = cfg.RedactTaskTitles ? $"Task {t.Id[1..]}" : Sanitize(t.Title, 120),
                ["planned_min"] = t.PlannedMs is { } p ? Min(p) : null,
                ["focus_min"] = Min(t.FocusMs),
                ["verified_fraction"] = t.VerifiedFraction is { } f ? Math.Round(f, 2) : null,
                ["state"] = CoachNames.Of(t.State),
                ["evidence_ids"] = ids,
            });
        }

        var dayIds = new JsonArray
        {
            Add($"day: {Min(ev.VerifiedDoneMs)} min verified of {Min(ev.PlannedWithEvidenceMs)} planned with evidence; {Min(ev.ExpectedByNowMs)} min expected by now"),
        };
        if (ev.UnplannedMs > 0) dayIds.Add(Add($"day: {Min(ev.UnplannedMs)} min on windows not matched to any task"));
        if (ev.DistractionMs > 0) dayIds.Add(Add($"day: {Min(ev.DistractionMs)} min on YouTube/social today, {Min(ev.DistractionLastHourMs)} min in the last hour"));
        if (ev.Overcommitted) dayIds.Add(Add($"day: {Min(ev.RemainingPlannedMs)} min of planned work left but only {Min(ev.RemainingWorkMs)} min of working time"));
        if (estimateRatio is { } r) dayIds.Add(Add($"calibration: finished tasks took {r:0.0}x their planned time over the last 14 days"));

        var goals = new JsonArray();
        foreach (var g in ev.Goals.Take(5))
        {
            int? daysLeft = g.Deadline is { } d ? d.DayNumber - ev.Day.DayNumber : null;
            goals.Add(new JsonObject
            {
                ["goal"] = Sanitize(g.Title, 120),
                ["days_left"] = daysLeft,
                ["evidence_id"] = Add($"goal: {Sanitize(g.Title, 120)}" + (daysLeft is { } dl ? $", {dl} days left" : "")),
            });
        }

        var history = new JsonArray();
        foreach (var i in recent.Where(i => i.Source != InterventionSources.Rule).TakeLast(4))
            history.Add(new JsonObject
            {
                ["minutes_ago"] = Min(ev.NowMs - i.CreatedUtcMs),
                ["kind"] = i.Kind,
                ["message"] = Sanitize(i.Message, 300),
                ["your_response"] = i.Response ?? "none",
                ["followed"] = i.Outcome?.Followed,
            });

        var local = TimeUtil.ToLocal(ev.NowMs);
        var root = new JsonObject
        {
            ["request"] = CoachNames.Of(kind),
            ["date"] = ev.Day.ToString("yyyy-MM-dd"),
            ["local_time"] = local.ToString("HH:mm"),
            ["working_window"] = $"{TimeUtil.ToLocal(ev.WindowStartMs):HH:mm}-{TimeUtil.ToLocal(ev.WindowEndMs):HH:mm}",
            ["day_fraction_passed"] = Math.Round(ev.DayFraction, 2),
            ["remaining_work_min"] = Min(ev.RemainingWorkMs),
            ["evaluation"] = new JsonObject
            {
                ["state"] = CoachNames.Of(ev.State),
                ["confidence"] = Math.Round(ev.Confidence, 2),
                ["planned_min"] = Min(ev.PlannedMs),
                ["planned_with_evidence_min"] = Min(ev.PlannedWithEvidenceMs),
                ["verified_done_min"] = Min(ev.VerifiedDoneMs),
                ["expected_by_now_min"] = Min(ev.ExpectedByNowMs),
                ["remaining_planned_min"] = Min(ev.RemainingPlannedMs),
                ["overcommitted"] = ev.Overcommitted,
                ["unplanned_min"] = Min(ev.UnplannedMs),
                ["youtube_social_min"] = Min(ev.DistractionMs),
                ["evidence_ids"] = dayIds,
            },
            ["tasks"] = tasks,
            ["goals"] = goals,
            ["estimate_ratio_14d"] = estimateRatio is { } er ? Math.Round(er, 1) : null,
            ["recent_suggestions"] = history,
            ["evidence"] = new JsonObject(evidence.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
        };
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        return new CoachContext
        {
            Kind = kind,
            Instructions = Instructions(kind),
            InputJson = json,
            Evidence = evidence,
            TaskIds = ev.Tasks.Select(t => t.Id).ToHashSet(),
            Numbers = NumbersIn(json),
            RemainingWorkMinutes = Min(ev.RemainingWorkMs),
        };
    }

    public static string Instructions(CoachKind kind)
    {
        const string common = """
            You are the coach inside LifeReplay, a personal activity tracker. Use ONLY the JSON context you are given.
            Rules:
            - Time spent in an app is not progress. Only verified outputs (ticked tasks, progress lines, commits) are.
            - Every statement must be backed by evidence ids from context.evidence; list them in evidence_ids.
            - Do not invent numbers. Any number in message or counterpoint must appear in the context. Minutes in actions are allowed.
            - If the evidence is too thin to judge, say so plainly and set insufficient_evidence to true.
            - counterpoint: the strongest honest reason your advice could be wrong or not worth it. Do not agree just to please.
            - Propose only. Never tell the user to submit, delete, or change calendars; they decide.
            - Use task ids (t1, t2, ...) in actions. At most 3 actions. message: at most 2 short sentences, concrete, no praise.
            - Reply in English unless most task titles are Korean, then reply in Korean.
            """;
        string specific = kind switch
        {
            CoachKind.Morning => "Request: morning planning. Propose the order of today's tasks and flag if the plan doesn't fit remaining_work_min, using estimate_ratio_14d if present.",
            CoachKind.Review => "Request: end-of-day review. State what was verified, what slipped, and what to carry over to tomorrow. No moralizing.",
            _ => "Request: check-in now. Give the single most useful next step for the rest of the working day.",
        };
        return common + "\n" + specific;
    }

    /// <summary>Strict JSON schema for the answer (OpenAI structured outputs: all fields required, no extras).</summary>
    public static JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("message", "actions", "evidence_ids", "counterpoint", "confidence", "insufficient_evidence"),
        ["properties"] = new JsonObject
        {
            ["message"] = new JsonObject { ["type"] = "string" },
            ["actions"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("type", "task_id", "minutes", "detail"),
                    ["properties"] = new JsonObject
                    {
                        ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(ActionTypes.All.Select(a => (JsonNode)a).ToArray()) },
                        ["task_id"] = new JsonObject { ["type"] = "string" },
                        ["minutes"] = new JsonObject { ["type"] = "integer" },
                        ["detail"] = new JsonObject { ["type"] = "string" },
                    },
                },
            },
            ["evidence_ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["counterpoint"] = new JsonObject { ["type"] = "string" },
            ["confidence"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("low", "medium", "high") },
            ["insufficient_evidence"] = new JsonObject { ["type"] = "boolean" },
        },
    };

    // ------------------------------------------------------------------ sanitizing

    [GeneratedRegex(@"(?i)\b(?:https?://|www\.)\S+")] private static partial Regex Url();
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")] private static partial Regex Email();
    [GeneratedRegex(@"(?i)\b[a-z0-9_.\-]*\{[^}\s]{4,}\}")] private static partial Regex Flag();
    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|token|secret|api[_ \-]?key|비밀번호)\s*[:=]\s*\S+")] private static partial Regex Credential();
    [GeneratedRegex(@"(?i)(?:[a-z]:\\|\\\\)[^\s""'<>|]+|(?:~|/(?:home|users|mnt|tmp))/[^\s""'<>|]+")] private static partial Regex FilePath();
    [GeneratedRegex(@"\b(?=[A-Za-z0-9_\-]*\d)(?=[A-Za-z0-9_\-]*[A-Za-z])[A-Za-z0-9_\-]{28,}\b")] private static partial Regex LongToken();
    [GeneratedRegex(@"\b\d{6,}\b")] private static partial Regex LongNumber();
    [GeneratedRegex(@"[\p{Cc}\p{Cf}]")] private static partial Regex Control();

    /// <summary>Removes URLs, e-mail addresses, CTF flags, credentials, file paths, key-like tokens and long numbers.</summary>
    public static string Sanitize(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = Control().Replace(text, " ");
        s = Credential().Replace(s, "$1: [removed]");
        s = Url().Replace(s, "[link]");
        s = Email().Replace(s, "[email]");
        s = Flag().Replace(s, "[flag]");
        s = FilePath().Replace(s, "[path]");
        s = LongToken().Replace(s, "[removed]");
        s = LongNumber().Replace(s, "[number]");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return s.Length <= maxLength ? s : s[..maxLength].TrimEnd() + "…";
    }

    [GeneratedRegex(@"(?<![A-Za-z_])\d+(?:\.\d+)?")] private static partial Regex Number();

    public static HashSet<decimal> NumbersIn(string text)
    {
        var set = new HashSet<decimal>();
        foreach (Match m in Number().Matches(text))
        {
            if (!decimal.TryParse(m.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) continue;
            set.Add(n);
            if (n is > 0 and <= 1 && m.Value.Contains('.')) set.Add(Math.Round(n * 100)); // 0.58 → 58 (%)
            if (n >= 30) { set.Add(Math.Round(n / 60m, 1)); set.Add(Math.Round(n / 60m)); }   // minutes → hours
        }
        return set;
    }
}
