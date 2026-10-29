using System.Text.Json;
using System.Text.Json.Nodes;
using LifeReplay.Analysis;
using LifeReplay.Core;

namespace LifeReplay.Coach;

/// <summary>Result of one coaching request. On any failure <see cref="Suggestion"/> is the deterministic fallback.</summary>
public sealed record CoachResult(string Status, Suggestion Suggestion, long InterventionId, long? RequestId, IReadOnlyList<string> Errors, int Credits)
{
    public bool FromModel => Status == "ok";
}

public sealed record BudgetStatus(int CreditsUsedThisMonth, int MonthlyCap, int RequestsToday, int DailyCap, int CreditsPerRequest)
{
    public bool Allows(int extraCredits = 0) => CreditsUsedThisMonth + extraCredits + CreditsPerRequest <= MonthlyCap && RequestsToday < DailyCap;
    public string Describe() => $"{CreditsUsedThisMonth}/{MonthlyCap} credits this month · {RequestsToday}/{DailyCap} requests today";
}

/// <summary>
/// The MVP loop: deterministic evaluation → sanitized request (only after you approve the preview) → validated,
/// typed suggestion → opt-in notification → feedback and measured outcome. Every step is logged in the coach tables.
/// Any failure (disabled, no key, over budget, offline, timeout, invalid answer) yields the deterministic fallback.
/// </summary>
public sealed class CoachService
{
    public const long OutcomeWindowMs = 60 * 60_000;
    public const long FollowedThresholdMs = 15 * 60_000;
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ICoachingProvider? _provider;
    private readonly Func<long> _now;

    public CoachStore Store { get; }
    public CoachConfig Config { get; }
    public ICoachingProvider? Provider => _provider;

    public CoachService(CoachStore store, CoachConfig config, ICoachingProvider? provider, Func<long>? clock = null)
    {
        Store = store;
        Config = config;
        _provider = provider;
        _now = clock ?? TimeUtil.NowMs;
    }

    /// <summary>The real provider from coach.json and the API key, or null when there's no key.</summary>
    public static ICoachingProvider? CreateProvider(CoachConfig cfg, AppPaths paths)
    {
        var key = ApiKeys.Find(paths);
        return key == null ? null : new OpenAiResponsesProvider(cfg.BaseUrl, key, cfg.Model, TimeSpan.FromSeconds(cfg.TimeoutSeconds), cfg.ReasoningEffort);
    }

    // ------------------------------------------------------------------ budget

    public BudgetStatus Budget()
    {
        var local = TimeUtil.ToLocal(_now());
        long monthStart = TimeUtil.LocalToUtcMs(new DateTime(local.Year, local.Month, 1));
        long dayStart = TimeUtil.LocalToUtcMs(local.Date);
        return new BudgetStatus(Store.CreditsSince(monthStart), Config.MonthlyCreditCap, Store.AttemptsSince(dayStart), Config.DailyRequestCap, Config.CreditsPerRequest);
    }

    // ------------------------------------------------------------------ requests

    public CoachContext Prepare(CoachKind kind, DayEvaluation ev, IReadOnlyDictionary<string, GitActivity>? git = null) =>
        ContextBuilder.Build(kind, ev, Store.Interventions(ev.Day), EstimateRatio(ev.Day), Config, git);

    /// <summary>Exactly what <see cref="SendAsync"/> will transmit, for the approval preview.</summary>
    public string Preview(CoachContext ctx)
    {
        var input = JsonNode.Parse(ctx.InputJson)!.ToJsonString(Pretty);
        return $"POST {_provider?.Endpoint ?? Config.BaseUrl + "/responses"}\nmodel: {_provider?.Model ?? Config.Model}\n\n" +
               $"── instructions ──\n{ctx.Instructions}\n\n── input ──\n{input}\n\n" +
               "Not sent: video, screenshots, window titles, URLs, file paths, the window log.";
    }

    private static string Payload(CoachContext ctx) =>
        new JsonObject { ["instructions"] = ctx.Instructions, ["input"] = ctx.InputJson, ["schema"] = "coach_suggestion" }.ToJsonString();

    /// <summary>Sends an approved request. Call only after the user has seen <see cref="Preview"/>.</summary>
    public async Task<CoachResult> SendAsync(CoachContext ctx, DayEvaluation ev, CancellationToken ct = default)
    {
        if (!Config.Enabled) return Fallback(ctx, ev, "disabled", "AI suggestions are turned off", null, 0, []);
        if (_provider == null) return Fallback(ctx, ev, "disabled", "no API key found", null, 0, []);
        var budget = Budget();
        if (!budget.Allows()) return Fallback(ctx, ev, "budget", "budget cap reached: " + budget.Describe(), null, 0, []);

        long reqId = Store.InsertRequest(_now(), CoachNames.Of(ctx.Kind), _provider.Model, _provider.Endpoint, Payload(ctx));
        var errors = new List<string>();
        int attempts = 0, credits = 0, transportRetries = Config.Retries;
        bool validationRetryUsed = false;
        string input = ctx.InputJson, status = "error";
        string? lastText = null;
        while (true)
        {
            if (attempts > 0 && !budget.Allows(credits)) { status = "budget"; errors.Add("budget cap reached during retries"); break; }
            attempts++;
            credits += Config.CreditsPerRequest;
            try
            {
                var resp = await _provider.CompleteAsync(new ProviderRequest(ctx.Instructions, input, "coach_suggestion", ContextBuilder.Schema(), Config.MaxOutputTokens), ct);
                lastText = resp.Text;
                var (s, errs) = SuggestionValidator.Validate(resp.Text, ctx);
                if (s != null)
                {
                    Store.FinishRequest(reqId, "ok", attempts, credits, resp.Text, errors.Count > 0 ? string.Join("\n", errors) : null);
                    long id = Store.InsertIntervention(_now(), ev.Day, CoachNames.Of(ctx.Kind), InterventionSources.Model, ev.State, s, reqId,
                        s.EvidenceIds.Select(e => $"{e}: {ctx.Evidence[e]}").ToList());
                    Log.Info($"Coach: {CoachNames.Of(ctx.Kind)} suggestion from {_provider.Model} after {attempts} attempt(s), {credits} credits");
                    return new CoachResult("ok", s, id, reqId, errors, credits);
                }
                errors.AddRange(errs.Select(e => $"attempt {attempts}: {e}"));
                if (validationRetryUsed) { status = "invalid"; break; }
                validationRetryUsed = true;
                input = ctx.InputJson + "\n\nYour previous answer was rejected by the validator: " + string.Join("; ", errs.Take(6)) +
                        ". Answer again and follow every rule.";
            }
            catch (ProviderException ex)
            {
                errors.Add($"attempt {attempts}: {ex.Message}");
                if (ex.Retryable && transportRetries-- > 0)
                {
                    try { await Task.Delay(1500 * attempts, ct); } catch (OperationCanceledException) { status = "cancelled"; break; }
                    continue;
                }
                status = ex.Failure switch
                {
                    ProviderFailure.Timeout => "timeout", ProviderFailure.Offline => "offline", ProviderFailure.RateLimited => "rate_limited",
                    ProviderFailure.Server => "server_error", ProviderFailure.Rejected => "rejected", _ => "invalid",
                };
                break;
            }
            catch (OperationCanceledException) { status = "cancelled"; errors.Add("cancelled"); break; }
        }
        Store.FinishRequest(reqId, status, attempts, credits, lastText, string.Join("\n", errors));
        Log.Warn($"Coach: request {reqId} failed ({status}); showing the rule-based fallback. {errors.LastOrDefault()}");
        return Fallback(ctx, ev, status, errors.LastOrDefault() ?? status, reqId, credits, errors);
    }

    private CoachResult Fallback(CoachContext ctx, DayEvaluation ev, string status, string why, long? reqId, int credits, List<string> errors)
    {
        var s = FallbackSuggestion.Build(ctx.Kind, ev, why);
        long id = Store.InsertIntervention(_now(), ev.Day, CoachNames.Of(ctx.Kind), InterventionSources.Fallback, ev.State, s, reqId);
        return new CoachResult(status, s, id, reqId, errors.Count > 0 ? errors : [why], credits);
    }

    // ------------------------------------------------------------------ notifications and feedback

    /// <summary>Applies the notification rules; when one fires it is logged and returned for display.</summary>
    public NotificationDecision CheckNotification(DayEvaluation ev)
    {
        var decision = InterventionPolicy.Decide(ev, Config, Store.Interventions(ev.Day), _now());
        if (decision.Notify)
        {
            var actions = decision.TaskId != null ? new List<SuggestedAction> { new(ActionTypes.FocusTask, decision.TaskId, 0, "named in the notification") } : [];
            Store.InsertIntervention(_now(), ev.Day, decision.Trigger ?? "notify", InterventionSources.Rule, ev.State,
                new Suggestion(decision.Message!, actions, [], "", "low", false), null);
        }
        return decision;
    }

    public void Feedback(long interventionId, string response, string? note = null) => Store.AddFeedback(interventionId, response, _now(), note);

    public void QuietToday(DateOnly day) =>
        Store.InsertIntervention(_now(), day, InterventionPolicy.QuietTodayNote, InterventionSources.User, ScheduleState.InsufficientEvidence,
            new Suggestion("Quiet for today", [], [], "", "low", false), null);

    // ------------------------------------------------------------------ records, outcomes, calibration

    /// <summary>Stores an evaluation snapshot (at most every 10 min) and finished tasks in the private ledger.</summary>
    public void Record(DayEvaluation ev)
    {
        long now = _now();
        if (Store.LastEvaluationUtc(ev.Day) is not { } last || now - last >= 10 * 60_000) Store.InsertEvaluation(now, ev);
        foreach (var t in ev.Tasks.Where(t => t.VerifiedFraction >= 1))
            Store.UpsertLedger(ev.Day, t.Title, t.PlannedMs, t.FocusMs, JsonSerializer.Serialize(t.Outputs.Select(o => new { o.Kind, o.Detail })), now);
    }

    /// <summary>
    /// For "focus on task X" suggestions at least an hour old: how much of the next hour actually went to X?
    /// <paramref name="focusByTask"/> returns focus ms per task id for a time range (from the window log).
    /// </summary>
    public int MeasureOutcomes(Func<long, long, IReadOnlyDictionary<string, long>> focusByTask)
    {
        long now = _now();
        int n = 0;
        foreach (var i in Store.InterventionsSince(now - 2 * 86_400_000))
        {
            if (i.Outcome != null || i.Source == InterventionSources.User || i.CreatedUtcMs + OutcomeWindowMs > now) continue;
            var task = i.Actions.FirstOrDefault(a => a.Type == ActionTypes.FocusTask && a.TaskId.Length > 0)?.TaskId;
            if (task == null) continue;
            long focus = focusByTask(i.CreatedUtcMs, i.CreatedUtcMs + OutcomeWindowMs).GetValueOrDefault(task);
            Store.InsertOutcome(new InterventionOutcome(i.Id, now, OutcomeWindowMs, task, focus, focus >= FollowedThresholdMs));
            n++;
        }
        return n;
    }

    /// <summary>Focus time ÷ planned time of finished tasks over the last 14 days (needs at least 3). Weekly calibration.</summary>
    public double? EstimateRatio(DateOnly day)
    {
        var rows = Store.Ledger(day.AddDays(-14), day).Where(r => r.PlannedMs is > 0 && r.FocusMs > 0).ToList();
        if (rows.Count < 3) return null;
        return rows.Sum(r => r.FocusMs) / (double)rows.Sum(r => r.PlannedMs!.Value);
    }
}

/// <summary>Everything the coach needs for one day, loaded the same way by the viewer and the notifier.</summary>
public sealed record DaySnapshot(TodoPlan Plan, string Markdown, PlanStatus Status, IReadOnlyList<FocusRecord> Focus,
    DayEvaluation Evaluation, IReadOnlyDictionary<string, GitActivity> Git);

public static class CoachDay
{
    /// <summary>Same file the viewer's Plan view uses (todo-path.txt in the data folder, else Documents\LifeReplay\todo.md).</summary>
    public static string TodoPath(AppPaths paths)
    {
        try
        {
            var f = Path.Combine(paths.DataDir, "todo-path.txt");
            if (File.Exists(f) && File.ReadAllText(f).Trim() is { Length: > 0 } p) return p;
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LifeReplay", "todo.md");
    }

    /// <summary>The todo file's "hours:", else recording hours, else 09:00–18:00 (as in the Plan view).</summary>
    public static (TimeSpan Start, TimeSpan End) DefaultHours(AppPaths paths)
    {
        try
        {
            var cfg = RecorderConfig.Load(paths.ConfigPath);
            if (cfg.Hours.Enabled && RecordingHours.TryParse(cfg.Hours.Start, out var s) && RecordingHours.TryParse(cfg.Hours.End, out var e) && s < e) return (s, e);
        }
        catch { }
        return (new TimeSpan(9, 0, 0), new TimeSpan(18, 0, 0));
    }

    /// <summary>
    /// Loads the plan and window log, runs the matcher (the full one if given, else <see cref="CachedPlanMatcher"/>),
    /// reads today's commits for tasks with "repo:", and evaluates. Null when there's no todo file.
    /// </summary>
    public static DaySnapshot? Load(MetadataStore store, AppPaths paths, DateOnly day, long nowMs,
        Func<TodoPlan, IReadOnlyList<FocusRecord>, long, long, PlanStatus>? matcher = null, bool collectGit = true)
    {
        var todo = TodoPath(paths);
        if (!File.Exists(todo)) return null;
        var markdown = File.ReadAllText(todo);
        var plan = TodoPlan.Parse(markdown, day);
        var (dayStart, dayEnd) = TimeUtil.LocalDayRange(day);
        var focus = store.GetFocusInRange(dayStart, dayEnd);
        var (defStart, defEnd) = DefaultHours(paths);
        var status = matcher != null ? matcher(plan, focus, dayStart, dayEnd)
            : CachedPlanMatcher.Match(store, plan, focus, dayStart, dayEnd, nowMs, defStart, defEnd);
        var gitByIndex = new Dictionary<int, GitActivity>();
        var gitById = new Dictionary<string, GitActivity>();
        if (collectGit)
            for (int i = 0; i < plan.Tasks.Count; i++)
                if (plan.Tasks[i].Repo is { Length: > 0 } repo && GitEvidence.Collect(repo, dayStart, Math.Min(dayEnd, nowMs)) is { } g)
                {
                    gitByIndex[i] = g;
                    gitById[ScheduleEvaluator.TaskId(i)] = g;
                }
        var ev = ScheduleEvaluator.Evaluate(new EvaluationInput
        {
            Day = day, NowMs = nowMs, Plan = plan, Status = status, Focus = focus, DefaultStart = defStart, DefaultEnd = defEnd,
            Git = gitByIndex, Goals = Goals.Parse(markdown),
        });
        return new DaySnapshot(plan, markdown, status, focus, ev, gitById);
    }

    /// <summary>Focus ms per task id in [from, to), for outcome measurement.</summary>
    public static IReadOnlyDictionary<string, long> FocusByTask(MetadataStore store, AppPaths paths, TodoPlan plan, long fromMs, long toMs, long nowMs)
    {
        var focus = store.GetFocusInRange(fromMs, toMs);
        var (s, e) = DefaultHours(paths);
        var status = CachedPlanMatcher.Match(store, plan, focus, fromMs, toMs, nowMs, s, e);
        return status.Tasks.Select((t, i) => (Id: ScheduleEvaluator.TaskId(i), t.SpentMs)).ToDictionary(x => x.Id, x => x.SpentMs);
    }
}
