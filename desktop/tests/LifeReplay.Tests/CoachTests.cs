using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LifeReplay.Analysis;
using LifeReplay.Coach;
using LifeReplay.Core;

namespace LifeReplay.Tests;

/// <summary>Synthetic activity streams: a todo plan + window log on a fixed day, evaluated at a chosen time.</summary>
internal static class Synth
{
    public static readonly DateOnly Day = new(2026, 10, 29);
    public static long At(int h, int m = 0) => TimeUtil.LocalToUtcMs(Day.ToDateTime(TimeOnly.MinValue).AddHours(h).AddMinutes(m));

    public static FocusRecord F(string title, int h0, int m0, int h1, int m1, string process = "chrome.exe") =>
        new() { DeviceId = "pc", Process = process, Title = title, StartUtcMs = At(h0, m0), EndUtcMs = At(h1, m1) };

    public static DayEvaluation Eval(string md, IEnumerable<FocusRecord> focus, int nowH, int nowM = 0, Dictionary<int, GitActivity>? git = null)
    {
        var plan = TodoPlan.Parse(md, Day);
        var (ds, de) = TimeUtil.LocalDayRange(Day);
        var f = focus.ToList();
        long now = At(nowH, nowM);
        var zero = new float[4]; // keyword matching only: no model in tests
        var status = DayAnalysis.Match(plan, f, ds, de, now, new(9, 0, 0), new(18, 0, 0), _ => zero, new Dictionary<string, string>());
        return ScheduleEvaluator.Evaluate(new EvaluationInput
        {
            Day = Day, NowMs = now, Plan = plan, Status = status, Focus = f, DefaultStart = new(9, 0, 0), DefaultEnd = new(18, 0, 0),
            Git = git ?? [], Goals = Goals.Parse(md),
        });
    }

    public const string TwoTasks = """
        hours: 09:00-18:00
        - [ ] Read domain theory paper ~2h
          match: domain theory
          progress: {0}/10 pages
        - [ ] pwn.college Program Misuse ~2h
          match: pwn.college
          progress: {1}/10 levels
        """;

    public static string Plan(int a, int b) => string.Format(TwoTasks, a, b);
}

public class ScheduleEvaluatorTests
{
    [Fact]
    public void AheadWhenVerifiedProgressBeatsTheClock()
    {
        var ev = Synth.Eval(Synth.Plan(8, 4), [Synth.F("Domain Theory notes.pdf", 9, 0, 10, 30)], 12);
        Assert.Equal(ScheduleState.Ahead, ev.State);
        Assert.Equal(ScheduleState.Ahead, ev.Tasks[0].State);           // 80% vs 33% of the day
        Assert.Equal(ScheduleState.OnTrack, ev.Tasks[1].State);         // 40% vs 33%: inside the 15-point band
        Assert.Equal((long)(2.4 * 3_600_000), ev.VerifiedDoneMs);
        Assert.Equal(90 * 60_000, ev.Tasks[0].FocusMs);                 // time is reported, but isn't what made it "ahead"
    }

    [Fact]
    public void BehindWhenProgressLagsTheWorkingDay()
    {
        var ev = Synth.Eval(Synth.Plan(0, 1), [Synth.F("pwn.college - level 1", 9, 0, 14, 0)], 15);
        Assert.Equal(ScheduleState.Behind, ev.State);
        Assert.All(ev.Tasks, t => Assert.Equal(ScheduleState.Behind, t.State));
        Assert.Equal(5 * 3_600_000, ev.Tasks[1].FocusMs); // lots of time, little output: still behind
    }

    [Fact]
    public void IdleDayWithNoActivityIsBehindNotOnTrack()
    {
        var ev = Synth.Eval(Synth.Plan(0, 0), [], 16);
        Assert.Equal(ScheduleState.Behind, ev.State);
        Assert.Equal(0, ev.FocusOnPlanMs);
    }

    [Fact]
    public void TimeWithoutOutputIsInsufficientEvidence()
    {
        const string md = """
            - [ ] Read domain theory paper ~2h
              match: domain theory
            - [ ] Write report ~2h
              match: report
            """;
        var ev = Synth.Eval(md, [Synth.F("Domain theory.pdf", 9, 0, 12, 0), Synth.F("report.docx - Word", 12, 0, 13, 0, "WINWORD.EXE")], 14);
        Assert.Equal(ScheduleState.InsufficientEvidence, ev.State);
        Assert.All(ev.Tasks, t => Assert.Equal(ScheduleState.InsufficientEvidence, t.State));
        Assert.Contains("time isn't progress", ev.Tasks[0].Reason);
        Assert.Equal(3 * 3_600_000, ev.Tasks[0].FocusMs);
    }

    [Fact]
    public void UnrelatedTabsDontChangeProgressButAreCounted()
    {
        var baseline = Synth.Eval(Synth.Plan(5, 5), [], 13, 30);
        var withTabs = Synth.Eval(Synth.Plan(5, 5), [Synth.F("lofi beats - YouTube", 12, 0, 13, 30), Synth.F("Inbox (3) - Gmail", 11, 0, 11, 20)], 13, 30);
        Assert.Equal(baseline.State, withTabs.State);
        Assert.Equal(baseline.VerifiedDoneMs, withTabs.VerifiedDoneMs);
        Assert.Equal(90 * 60_000, withTabs.DistractionMs);
        Assert.Equal(60 * 60_000, withTabs.DistractionLastHourMs);
        Assert.Equal(110 * 60_000, withTabs.UnplannedMs);
    }

    [Fact]
    public void TaskCompletedEarlyIsAhead()
    {
        const string md = """
            - [x] Fix viewer bug ~1h
            - [ ] Read paper ~1h
              progress: 2/10 pages
            """;
        var ev = Synth.Eval(md, [], 11);
        Assert.Equal(ScheduleState.Ahead, ev.Tasks[0].State);
        Assert.Equal(1.0, ev.Tasks[0].VerifiedFraction);
        Assert.Equal(ScheduleState.Ahead, ev.State); // 1h12m verified vs ~27 min expected
    }

    [Fact]
    public void ConflictingPrioritiesThatDontFitAreFlaggedOvercommitted()
    {
        const string md = """
            - [ ] Assignment A ~4h
              progress: 0/4 problems
            - [ ] Reproduce paper ~4h
              progress: 0/3 runs
            - [ ] Exam prep ~3h
              progress: 0/5 chapters
            """;
        var ev = Synth.Eval(md, [], 14);
        Assert.True(ev.Overcommitted);
        Assert.Equal(11 * 3_600_000, ev.RemainingPlannedMs);
        Assert.Equal(4 * 3_600_000, ev.RemainingWorkMs);
    }

    [Fact]
    public void CommitsAreEvidenceButNotAFraction()
    {
        const string md = """
            - [ ] LifeReplay coach ~3h
              repo: C:\code\life_replay
            """;
        var ev = Synth.Eval(md, [], 12, git: new() { [0] = new GitActivity(3, ["Add evaluator", "Add tests", "Docs"]) });
        var t = Assert.Single(ev.Tasks);
        Assert.Contains(t.Outputs, o => o.Kind == "commits" && o.Detail == "3 commits today");
        Assert.Null(t.VerifiedFraction);
        Assert.Equal(ScheduleState.InsufficientEvidence, t.State);
        Assert.Equal(@"C:\code\life_replay", TodoPlan.Parse(md, Synth.Day).Tasks[0].Repo);
    }

    [Fact]
    public void NoPlannedTimesIsInsufficientEvidence()
    {
        var ev = Synth.Eval("- [ ] Something\n  progress: 1/2", [], 12);
        Assert.Equal(ScheduleState.InsufficientEvidence, ev.State);
        Assert.Contains("planned times", ev.Reason);
    }

    [Fact]
    public void GoalsAreParsed()
    {
        var g = Goals.Parse("goal: Land a research internship by 2026-12-01\n# 2026-10-29\ngoal: Ship LifeReplay coach");
        Assert.Equal(2, g.Count);
        Assert.Equal(new DateOnly(2026, 12, 1), g[0].Deadline);
        Assert.Null(g[1].Deadline);
    }
}

public class CoachPrivacyTests
{
    [Fact]
    public void WindowTitlesUrlsAndPathsNeverReachThePayload()
    {
        const string md = """
            - [ ] Read paper https://arxiv.org/abs/1234.5678 with alice@example.com ~2h
              match: paper
              progress: 2/10 pages
            - [ ] CTF pwn.college{AbCdEf123456_secretflag} password: hunter2 ~1h
              progress: 0/3 levels
            - [ ] Fix C:\Users\lee\private\notes.txt ~1h
              progress: 0/1 fix
            """;
        var focus = new[]
        {
            Synth.F("paper draft - Confidential Bank Statement - https://bank.example/login", 9, 0, 10, 0),
            Synth.F("(private)", 10, 0, 10, 30, "KeePass.exe"),
        };
        var ev = Synth.Eval(md, focus, 12);
        var ctx = ContextBuilder.Build(CoachKind.Check, ev, [], null, new CoachConfig());
        foreach (var forbidden in new[] { "Bank Statement", "bank.example", "arxiv.org", "alice@example.com", "AbCdEf123456", "hunter2", @"C:\Users", "KeePass", "(private)" })
            Assert.DoesNotContain(forbidden, ctx.InputJson);
        Assert.Contains("[link]", ctx.InputJson);
        Assert.Contains("[flag]", ctx.InputJson);
        Assert.Contains("[path]", ctx.InputJson);
    }

    [Fact]
    public void TaskTitlesCanBeRedactedAndCommitMessagesAreOptIn()
    {
        const string md = "- [ ] Secret project X ~2h\n  repo: C:\\x";
        var ev = Synth.Eval(md, [], 12, git: new() { [0] = new GitActivity(2, ["Add stealth mode", "Fix"]) });
        var git = new Dictionary<string, GitActivity> { ["t1"] = new(2, ["Add stealth mode", "Fix"]) };
        var plain = ContextBuilder.Build(CoachKind.Check, ev, [], null, new CoachConfig(), git);
        Assert.Contains("Secret project X", plain.InputJson);
        Assert.DoesNotContain("stealth", plain.InputJson);
        Assert.Contains("2 commits today", plain.InputJson);

        var redacted = ContextBuilder.Build(CoachKind.Check, ev, [], null, new CoachConfig { RedactTaskTitles = true, IncludeCommitMessages = true }, git);
        Assert.DoesNotContain("Secret project", redacted.InputJson);
        Assert.Contains("Task 1", redacted.InputJson);
        Assert.Contains("stealth", redacted.InputJson);
    }

    [Theory]
    [InlineData("flag is pwn.college{xyzXYZ0123}", "flag is [flag]")]
    [InlineData("api_key=sk-abcdefghijklmnopqrstuvwxyz0123", "api_key: [removed]")]
    [InlineData("call 01012345678", "call [number]")]
    [InlineData("see www.example.com/page", "see [link]")]
    [InlineData("Domain theory ch. 3", "Domain theory ch. 3")]
    public void Sanitize(string input, string expected) => Assert.Equal(expected, ContextBuilder.Sanitize(input, 200));
}

public class SuggestionValidatorTests
{
    private static CoachContext Ctx() => ContextBuilder.Build(CoachKind.Check, Synth.Eval(Synth.Plan(2, 1), [Synth.F("Domain theory.pdf", 9, 0, 10, 0)], 15), [], null, new CoachConfig());

    private static string Answer(string message = "Behind on t1: 2 of 10 pages done. Do the next pages now.", string evidence = "\"ev2\"",
        string actions = """[{"type":"focus_task","task_id":"t1","minutes":45,"detail":"next pages"}]""",
        string counter = "Pages differ in difficulty, so page count may overstate the gap.", bool insufficient = false) =>
        $$"""{"message":{{JsonSerializer.Serialize(message)}},"actions":{{actions}},"evidence_ids":[{{evidence}}],"counterpoint":{{JsonSerializer.Serialize(counter)}},"confidence":"medium","insufficient_evidence":{{(insufficient ? "true" : "false")}}}""";

    [Fact]
    public void ValidAnswerPasses()
    {
        var (s, errors) = SuggestionValidator.Validate(Answer(), Ctx());
        Assert.Empty(errors);
        Assert.Equal("t1", s!.Actions[0].TaskId);
    }

    [Theory]
    [InlineData("evidence", "\"ev99\"", "evidence id 'ev99'")]
    [InlineData("task", "", "unknown task 't7'")]
    [InlineData("number", "", "number 95")]
    [InlineData("counter", "", "counterpoint")]
    [InlineData("minutes", "", "out of range")]
    [InlineData("too many", "", "more than 3 actions")]
    [InlineData("no evidence", "", "no evidence ids")]
    public void HallucinationsAreRejected(string kind, string evidence, string expectedError)
    {
        string answer = kind switch
        {
            "evidence" => Answer(evidence: evidence),
            "task" => Answer(actions: """[{"type":"focus_task","task_id":"t7","minutes":30,"detail":"x"}]"""),
            "number" => Answer(message: "You spent 95 minutes on t1 today."),
            "counter" => Answer(counter: "ok"),
            "minutes" => Answer(actions: """[{"type":"focus_task","task_id":"t1","minutes":5000,"detail":"x"}]"""),
            "too many" => Answer(actions: "[" + string.Join(",", Enumerable.Repeat("""{"type":"none","task_id":"","minutes":0,"detail":""}""", 4)) + "]"),
            _ => Answer(evidence: ""),
        };
        var (s, errors) = SuggestionValidator.Validate(answer, Ctx());
        Assert.Null(s);
        Assert.Contains(errors, e => e.Contains(expectedError));
    }

    [Fact]
    public void NumbersFromTheContextInDerivedFormsAreAllowed()
    {
        // 15:00 = 67% of 09:00–18:00; 120 planned min = 2 h.
        var (s, errors) = SuggestionValidator.Validate(Answer(message: "67% of the day is gone and t1 had 2 h planned."), Ctx());
        Assert.Empty(errors);
        Assert.NotNull(s);
    }

    [Fact]
    public void NotJsonIsRejected() => Assert.Contains("not valid JSON", SuggestionValidator.Validate("Sure! Here's my advice", Ctx()).Errors[0]);
}

/// <summary>Scripted provider: returns or throws the queued items in order and records what it was sent.</summary>
internal sealed class FakeProvider(params object[] script) : ICoachingProvider
{
    private readonly Queue<object> _script = new(script);
    public List<ProviderRequest> Requests { get; } = [];
    public string Name => "fake";
    public string Model => "fake-model";
    public string Endpoint => "https://fake.invalid/v1/responses";

    public Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        var next = _script.Count > 0 ? _script.Dequeue() : new ProviderException(ProviderFailure.Offline, "script empty");
        return next switch
        {
            Exception ex => Task.FromException<ProviderResponse>(ex),
            string text => Task.FromResult(new ProviderResponse(text, 100, 50)),
            _ => throw new InvalidOperationException(),
        };
    }
}

public class CoachServiceTests
{
    private const string Good = """{"message":"Behind on t1: 2 of 10 pages done. Do the next pages now.","actions":[{"type":"focus_task","task_id":"t1","minutes":45,"detail":"next pages"}],"evidence_ids":["ev2"],"counterpoint":"Page counts can overstate the gap if later pages are easier.","confidence":"medium","insufficient_evidence":false}""";
    private const string Hallucinated = """{"message":"You finished 7 papers this week!","actions":[],"evidence_ids":["ev42"],"counterpoint":"none really","confidence":"high","insufficient_evidence":false}""";

    private static (CoachService Svc, DayEvaluation Ev, CoachStore Store) Setup(TempData t, ICoachingProvider? p, Action<CoachConfig>? cfg = null, long? now = null)
    {
        var c = new CoachConfig { Enabled = true, Retries = 1, CreditsPerRequest = 5, MonthlyCreditCap = 600, DailyRequestCap = 8 };
        cfg?.Invoke(c);
        var store = new CoachStore(t.Store);
        var ev = Synth.Eval(Synth.Plan(2, 1), [Synth.F("Domain theory.pdf", 9, 0, 10, 0)], 15);
        return (new CoachService(store, c, p, () => now ?? Synth.At(15)), ev, store);
    }

    [Fact]
    public async Task ValidAnswerIsStoredWithItsRequestAndAuditPayload()
    {
        using var t = new TempData();
        var fake = new FakeProvider(Good);
        var (svc, ev, store) = Setup(t, fake);
        var ctx = svc.Prepare(CoachKind.Check, ev);
        var r = await svc.SendAsync(ctx, ev);
        Assert.True(r.FromModel);
        Assert.Equal(5, r.Credits);
        var req = Assert.Single(store.RecentRequests(10));
        Assert.Equal(("ok", 1, 5), (req.Status, req.Attempts, req.Credits));
        Assert.Contains(fake.Requests[0].Input, req.Payload.Replace("\\u0022", "\"")); // audit log holds exactly what was sent
        Assert.DoesNotContain("Domain theory.pdf", req.Payload);                       // window title stayed local
        var i = Assert.Single(store.Interventions(Synth.Day));
        Assert.Equal((InterventionSources.Model, "t1"), (i.Source, i.TaskId));
    }

    [Fact]
    public async Task HallucinationIsRetriedOnceWithTheReasonsThenAccepted()
    {
        using var t = new TempData();
        var fake = new FakeProvider(Hallucinated, Good);
        var (svc, ev, store) = Setup(t, fake);
        var r = await svc.SendAsync(svc.Prepare(CoachKind.Check, ev), ev);
        Assert.True(r.FromModel);
        Assert.Equal(2, fake.Requests.Count);
        Assert.Contains("rejected by the validator", fake.Requests[1].Input);
        Assert.Contains("ev42", fake.Requests[1].Input);
        Assert.Equal(10, store.RecentRequests(1)[0].Credits);
    }

    [Fact]
    public async Task RepeatedHallucinationFallsBackToRules()
    {
        using var t = new TempData();
        var (svc, ev, store) = Setup(t, new FakeProvider(Hallucinated, Hallucinated));
        var r = await svc.SendAsync(svc.Prepare(CoachKind.Check, ev), ev);
        Assert.Equal("invalid", r.Status);
        Assert.False(r.FromModel);
        Assert.Contains("Behind", r.Suggestion.Message);
        Assert.Equal(InterventionSources.Fallback, store.Interventions(Synth.Day).Single().Source);
    }

    [Fact]
    public async Task ApiTimeoutRetriesThenFallsBack()
    {
        using var t = new TempData();
        var fake = new FakeProvider(new ProviderException(ProviderFailure.Timeout, "slow"), new ProviderException(ProviderFailure.Timeout, "slow"));
        var (svc, ev, store) = Setup(t, fake);
        var r = await svc.SendAsync(svc.Prepare(CoachKind.Check, ev), ev);
        Assert.Equal("timeout", r.Status);
        Assert.Equal(2, fake.Requests.Count);       // 1 retry
        Assert.Equal(10, store.RecentRequests(1)[0].Credits);
    }

    [Fact]
    public async Task NoConnectivityFallsBack()
    {
        using var t = new TempData();
        var (svc, ev, _) = Setup(t, new FakeProvider(new ProviderException(ProviderFailure.Offline, "no route"), new ProviderException(ProviderFailure.Offline, "no route")));
        var r = await svc.SendAsync(svc.Prepare(CoachKind.Review, ev), ev);
        Assert.Equal("offline", r.Status);
        Assert.NotEmpty(r.Suggestion.Message);
    }

    [Fact]
    public async Task RejectedRequestIsNotRetried()
    {
        using var t = new TempData();
        var fake = new FakeProvider(new ProviderException(ProviderFailure.Rejected, "401"));
        var (svc, ev, _) = Setup(t, fake);
        Assert.Equal("rejected", (await svc.SendAsync(svc.Prepare(CoachKind.Check, ev), ev)).Status);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task DisabledOrNoKeyNeverCallsOut()
    {
        using var t = new TempData();
        var fake = new FakeProvider(Good);
        var (svc, ev, store) = Setup(t, fake, c => c.Enabled = false);
        var r = await svc.SendAsync(svc.Prepare(CoachKind.Check, ev), ev);
        Assert.Equal("disabled", r.Status);
        Assert.Empty(fake.Requests);
        Assert.Empty(store.RecentRequests(10));

        var (noKey, ev2, _) = Setup(t, null);
        Assert.Equal("disabled", (await noKey.SendAsync(noKey.Prepare(CoachKind.Check, ev2), ev2)).Status);
    }

    [Fact]
    public async Task BudgetCapStopsRequests()
    {
        using var t = new TempData();
        var fake = new FakeProvider(Good, Good);
        var (svc, ev, _) = Setup(t, fake, c => c.MonthlyCreditCap = 5);
        Assert.True((await svc.SendAsync(svc.Prepare(CoachKind.Check, ev), ev)).FromModel);
        var second = await svc.SendAsync(svc.Prepare(CoachKind.Check, ev), ev);
        Assert.Equal("budget", second.Status);
        Assert.Single(fake.Requests);
        Assert.Equal(5, svc.Budget().CreditsUsedThisMonth);
    }

    [Fact]
    public async Task DailyRequestCapCountsAttempts()
    {
        using var t = new TempData();
        var fake = new FakeProvider(new ProviderException(ProviderFailure.Server, "502"), Good, Good);
        var (svc, ev, _) = Setup(t, fake, c => c.DailyRequestCap = 2);
        Assert.True((await svc.SendAsync(svc.Prepare(CoachKind.Check, ev), ev)).FromModel); // 2 attempts
        Assert.Equal("budget", (await svc.SendAsync(svc.Prepare(CoachKind.Check, ev), ev)).Status);
    }

    [Fact]
    public void OutcomeIsMeasuredFromActivityAfterAnHour()
    {
        using var t = new TempData();
        long clock = Synth.At(10);
        var store = new CoachStore(t.Store);
        var svc = new CoachService(store, new CoachConfig(), null, () => clock);
        var s = new Suggestion("Do t1", [new(ActionTypes.FocusTask, "t1", 30, "")], ["ev1"], "counter point text", "medium", false);
        long id = store.InsertIntervention(clock, Synth.Day, "check", InterventionSources.Model, ScheduleState.Behind, s, null);
        Assert.Equal(0, svc.MeasureOutcomes((_, _) => new Dictionary<string, long> { ["t1"] = 20 * 60_000 })); // too early
        clock = Synth.At(11, 30);
        Assert.Equal(1, svc.MeasureOutcomes((from, to) =>
        {
            Assert.Equal((Synth.At(10), Synth.At(11)), (from, to));
            return new Dictionary<string, long> { ["t1"] = 20 * 60_000 };
        }));
        var o = store.Interventions(Synth.Day).Single(i => i.Id == id).Outcome!;
        Assert.True(o.Followed);
        Assert.Equal(20 * 60_000, o.FocusMs);
    }

    [Fact]
    public void CalibrationAndLedgerFromFinishedTasks()
    {
        using var t = new TempData();
        var store = new CoachStore(t.Store);
        var svc = new CoachService(store, new CoachConfig(), null, () => Synth.At(18));
        Assert.Null(svc.EstimateRatio(Synth.Day));
        for (int i = 0; i < 3; i++) store.UpsertLedger(Synth.Day.AddDays(-i), $"task {i}", 60 * 60_000, 90 * 60_000, "[]", 0);
        Assert.Equal(1.5, svc.EstimateRatio(Synth.Day)!.Value, 3);

        var ev = Synth.Eval("- [x] Done thing ~1h\n- [ ] Open thing ~1h\n  progress: 1/2", [], 17);
        svc.Record(ev);
        var row = Assert.Single(store.Ledger(Synth.Day, Synth.Day), r => r.Task == "Done thing");
        Assert.False(row.Showable); // private unless switched on
        store.SetShowable(row.Id, true);
        svc.Record(ev); // re-recording keeps the switch
        Assert.True(store.Ledger(Synth.Day, Synth.Day).Single(r => r.Task == "Done thing").Showable);
    }
}

public class InterventionPolicyTests
{
    private static DayEvaluation Behind() => Synth.Eval(Synth.Plan(0, 1), [], 15);
    private static CoachConfig On() => new() { Notifications = true, QuietHours = "22:00-08:00", CooldownMinutes = 60, MaxNotificationsPerDay = 3 };

    private static Intervention Rule(int h, int m, string? response = null, string kind = "behind", string? task = "t1") =>
        new(1, Synth.At(h, m), "2026-10-29", kind, InterventionSources.Rule, "behind", "msg", [], [], "", "low", task, null, response, null);

    [Fact]
    public void BehindNotifiesAndNamesTheMostBehindTask()
    {
        var d = InterventionPolicy.Decide(Behind(), On(), [], Synth.At(15));
        Assert.True(d.Notify);
        Assert.Equal("t1", d.TaskId); // 0% beats 10%
        Assert.Contains("Behind plan", d.Message);
    }

    [Fact]
    public void RulesSayNoInOrder()
    {
        var ev = Behind();
        Assert.Equal("notifications are off", InterventionPolicy.Decide(ev, new CoachConfig { Notifications = false }, [], Synth.At(15)).Reason);
        Assert.Equal("quiet hours", InterventionPolicy.Decide(ev, On(), [], Synth.At(23)).Reason);
        Assert.Equal("outside the working window", InterventionPolicy.Decide(ev, On(), [], Synth.At(8, 30)).Reason);
        Assert.Contains("daily maximum", InterventionPolicy.Decide(ev, On(), [Rule(10, 0), Rule(12, 0), Rule(13, 0)], Synth.At(15)).Reason);
        Assert.Equal("cooldown", InterventionPolicy.Decide(ev, On(), [Rule(14, 30)], Synth.At(15)).Reason);
        Assert.Equal("snoozed", InterventionPolicy.Decide(ev, Tap(On(), x => x.CooldownMinutes = 5), [Rule(14, 30, "snoozed")], Synth.At(15)).Reason);
        var quiet = new Intervention(2, Synth.At(9), "2026-10-29", InterventionPolicy.QuietTodayNote, InterventionSources.User, "x", "Quiet for today", [], [], "", "low", null, null, null, null);
        Assert.Equal("quiet for today", InterventionPolicy.Decide(ev, On(), [quiet], Synth.At(15)).Reason);
    }

    private static CoachConfig Tap(CoachConfig c, Action<CoachConfig> a) { a(c); return c; }

    [Fact]
    public void DismissedTaskIsNotNamedAgainToday()
    {
        var d = InterventionPolicy.Decide(Behind(), On(), [Rule(10, 0, "dismissed", task: "t1")], Synth.At(15));
        Assert.True(d.Notify);
        Assert.Equal("t2", d.TaskId);
    }

    [Fact]
    public void DriftTriggersOnceWhenNotBehind()
    {
        var ev = Synth.Eval(Synth.Plan(6, 6), [Synth.F("music - YouTube", 13, 50, 14, 50)], 14, 50);
        Assert.NotEqual(ScheduleState.Behind, ev.State);
        var d = InterventionPolicy.Decide(ev, On(), [], Synth.At(14, 50));
        Assert.Equal("drift", d.Trigger);
        Assert.False(InterventionPolicy.Decide(ev, On(), [Rule(9, 0, kind: "drift")], Synth.At(14, 50)).Notify);
    }

    [Fact]
    public async Task ServiceLogsNotificationsAndRespectsCooldown()
    {
        using var t = new TempData();
        long clock = Synth.At(15);
        var svc = new CoachService(new CoachStore(t.Store), On(), null, () => clock);
        var ev = Behind();
        Assert.True(svc.CheckNotification(ev).Notify);
        Assert.False(svc.CheckNotification(ev).Notify); // cooldown
        clock = Synth.At(16, 5);
        Assert.True(svc.CheckNotification(ev).Notify);
        Assert.Equal(2, svc.Store.Interventions(Synth.Day).Count(i => i.Source == InterventionSources.Rule));
        await Task.CompletedTask;
    }
}

public class ResponsesProviderTests
{
    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            ct.ThrowIfCancellationRequested();
            return await f(request);
        }
    }

    private static ProviderRequest Req() => new("rules", "{\"x\":1}", "coach_suggestion", ContextBuilder.Schema(), 500);

    private const string Ok = """{"status":"completed","output":[{"type":"reasoning","summary":[]},{"type":"message","content":[{"type":"output_text","text":"{\"a\":1}"}]}],"usage":{"input_tokens":70,"output_tokens":20}}""";

    [Fact]
    public async Task SendsStrictJsonSchemaAndBearerToken()
    {
        var stub = new Stub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Ok) }));
        using var p = new OpenAiResponsesProvider("https://gw.example/v1/gateway/", "k-123", "gpt-6-astra", TimeSpan.FromSeconds(5), "low", stub);
        var r = await p.CompleteAsync(Req(), default);
        Assert.Equal("{\"a\":1}", r.Text);
        Assert.Equal(70, r.InputTokens);
        Assert.Equal("https://gw.example/v1/gateway/responses", stub.Last!.RequestUri!.ToString());
        Assert.Equal("Bearer k-123", stub.Last.Headers.Authorization!.ToString());
        var body = JsonNode.Parse(stub.Body!)!;
        Assert.Equal("json_schema", (string?)body["text"]!["format"]!["type"]);
        Assert.True((bool)body["text"]!["format"]!["strict"]!);
        Assert.Equal("low", (string?)body["reasoning"]!["effort"]);
        Assert.False((bool)body["store"]!);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, ProviderFailure.Server, true)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderFailure.RateLimited, true)]
    [InlineData(HttpStatusCode.Unauthorized, ProviderFailure.Rejected, false)]
    public async Task HttpErrorsAreClassified(HttpStatusCode code, ProviderFailure failure, bool retryable)
    {
        var stub = new Stub(_ => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("{}") }));
        using var p = new OpenAiResponsesProvider("https://gw.example/v1", "k", "m", TimeSpan.FromSeconds(5), "", stub);
        var ex = await Assert.ThrowsAsync<ProviderException>(() => p.CompleteAsync(Req(), default));
        Assert.Equal((failure, retryable), (ex.Failure, ex.Retryable));
    }

    [Fact]
    public async Task TimeoutAndNetworkErrors()
    {
        var slow = new Stub(async _ => { await Task.Delay(5000); return new HttpResponseMessage(HttpStatusCode.OK); });
        using (var p = new OpenAiResponsesProvider("https://gw.example/v1", "k", "m", TimeSpan.FromMilliseconds(200), "", slow))
            Assert.Equal(ProviderFailure.Timeout, (await Assert.ThrowsAsync<ProviderException>(() => p.CompleteAsync(Req(), default))).Failure);
        var down = new Stub(_ => throw new HttpRequestException("No such host"));
        using (var p = new OpenAiResponsesProvider("https://gw.example/v1", "k", "m", TimeSpan.FromSeconds(5), "", down))
            Assert.Equal(ProviderFailure.Offline, (await Assert.ThrowsAsync<ProviderException>(() => p.CompleteAsync(Req(), default))).Failure);
    }

    [Fact]
    public void IncompleteOrRefusedResponsesAreErrors()
    {
        Assert.Equal(ProviderFailure.BadResponse, Assert.Throws<ProviderException>(() => OpenAiResponsesProvider.Parse("""{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"}}""")).Failure);
        Assert.Contains("refused", Assert.Throws<ProviderException>(() => OpenAiResponsesProvider.Parse("""{"status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"no"}]}]}""")).Message);
    }

    [Fact]
    public void KeyFilesAcceptJsonOrBareKey()
    {
        Assert.Equal("baze_abcdefghijklmnopqrstuvwxyz", ApiKeys.FromText("""{ "bazeApiKey": "baze_abcdefghijklmnopqrstuvwxyz" }"""));
        Assert.Equal("baze_abcdefghijklmnopqrstuvwxyz", ApiKeys.FromText("  baze_abcdefghijklmnopqrstuvwxyz\n"));
        Assert.Null(ApiKeys.FromText("short"));
    }
}

public class ChatGptBridgeTests
{
    private const string Reply = """
        Here's the refined plan.
        ```lifereplay-plan
        {"day": "2026-10-30", "tasks": [
          {"title": "Reproduce Table 2 baseline", "minutes": 150, "progress_total": 3, "progress_unit": "runs", "match": "baseline"},
          {"title": "# Read section 4", "minutes": 45}
        ]}
        ```
        """;

    [Fact]
    public void ValidBlockBecomesTodoMarkdownThatParsesBack()
    {
        var (plan, errors, warnings) = ChatGptBridge.Parse(Reply, Synth.Day, 9 * 3_600_000, null);
        Assert.Empty(errors);
        Assert.Empty(warnings);
        var md = ChatGptBridge.ToMarkdown(plan!);
        Assert.StartsWith("# 2026-10-30\n- [ ] Reproduce Table 2 baseline ~2h30m\n  progress: 0/3 runs\n  match: baseline\n- [ ] Read section 4 ~45m", md);
        var parsed = TodoPlan.Parse(md, new DateOnly(2026, 10, 30));
        Assert.Equal(150 * 60_000, parsed.Tasks[0].PlannedMs);
        Assert.Equal(3, parsed.Tasks[0].ProgressTotal);
    }

    [Fact]
    public void BadBlocksAreRejectedAndOverloadWarned()
    {
        Assert.Contains("No ```lifereplay-plan", ChatGptBridge.Parse("just chat", Synth.Day, 0, null).Errors[0]);
        Assert.Contains(ChatGptBridge.Parse(Reply.Replace("2026-10-30", "2026-10-01"), Synth.Day, 0, null).Errors, e => e.Contains("in the past"));
        Assert.Contains(ChatGptBridge.Parse(Reply.Replace("150", "9000"), Synth.Day, 0, null).Errors, e => e.Contains("5–600"));
        var (_, _, warnings) = ChatGptBridge.Parse(Reply, Synth.Day, 3 * 3_600_000, 1.5);
        Assert.Contains(warnings, w => w.Contains("1.5×"));
    }

    [Fact]
    public void AppendKeepsABackup()
    {
        using var t = new TempData();
        var path = Path.Combine(t.Paths.DataDir, "todo.md");
        File.WriteAllText(path, "# 2026-10-29\n- [ ] a ~1h\n");
        ChatGptBridge.AppendToTodo(path, "# 2026-10-30\n- [ ] b ~1h\n");
        Assert.Equal("# 2026-10-29\n- [ ] a ~1h\n", File.ReadAllText(path + ".bak"));
        Assert.Contains("# 2026-10-30", File.ReadAllText(path));
        Assert.Contains("- [ ] a ~1h", File.ReadAllText(path));
    }
}

public class CoachMigrationTests
{
    [Fact]
    public void SchemaV5HasTheCoachTables()
    {
        using var t = new TempData();
        using var c = t.Store.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'coach_%' ORDER BY name";
        var names = new List<string>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) names.Add(r.GetString(0));
        Assert.Equal(["coach_evaluations", "coach_feedback", "coach_interventions", "coach_ledger", "coach_outcomes", "coach_requests"], names);
        Assert.Equal(5, Migrations.GetVersion(c));
    }
}
