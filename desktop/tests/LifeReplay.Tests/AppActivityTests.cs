using LifeReplay.Core;

namespace LifeReplay.Tests;

public class AppEventParsingTests
{
    private static readonly long Now = DateTimeOffset.Parse("2026-10-29T15:00:00+09:00").ToUnixTimeMilliseconds();

    private static AppEventIngestResult Ingest(TempData t, string body) => AppEvents.Ingest(t.Store, body, Now);

    [Theory]
    [InlineData("2026-10-29T14:00:00+09:00", "2026-10-29T05:00:00Z", 540)]
    [InlineData("2026-10-29T05:00:00Z", "2026-10-29T05:00:00Z", 0)]
    [InlineData("2026-10-29T14:00:00+0900", "2026-10-29T05:00:00Z", 540)]
    [InlineData("2026-10-29T14:00:00.123+09:00", "2026-10-29T05:00:00.123Z", 540)]
    public void TimestampsAreNormalizedToUtc(string input, string expectedUtc, int offset)
    {
        Assert.True(AppEvents.TryParseTimestamp(input, out var ms, out var off));
        Assert.Equal(DateTimeOffset.Parse(expectedUtc).ToUnixTimeMilliseconds(), ms);
        Assert.Equal(offset, off);
    }

    [Theory]
    [InlineData("2026-10-29T14:00:00")]      // no offset: ambiguous, refused
    [InlineData("14:00")]
    [InlineData("")]
    [InlineData("yesterday")]
    public void TimestampsWithoutOffsetAreRejected(string input) => Assert.False(AppEvents.TryParseTimestamp(input, out _, out _));

    [Fact]
    public void AcceptsObjectArrayAndJsonLinesAndSkipsDuplicates()
    {
        using var t = new TempData();
        var e1 = """{"device_id":"iphone","app_name":"Instagram","event_type":"opened","timestamp":"2026-10-29T14:00:00+09:00","event_id":"a"}""";
        var e2 = """{"device_id":"iphone","app_name":"Instagram","event_type":"closed","timestamp":"2026-10-29T14:24:00+09:00","event_id":"b"}""";
        Assert.Equal(1, Ingest(t, e1).Accepted);
        var r = Ingest(t, $"[{e1},{e2}]");
        Assert.Equal((1, 1), (r.Accepted, r.Duplicates));
        r = Ingest(t, e1 + "\r\n" + e2 + "\n");
        Assert.Equal((0, 2), (r.Accepted, r.Duplicates));
        Assert.Equal(2, t.Store.GetAppEvents(0, long.MaxValue).Count);
    }

    [Fact]
    public void MissingEventIdFallsBackToAppTypeAndSecond()
    {
        using var t = new TempData();
        var noId = """{"device_id":"iphone","app_name":"KakaoTalk","event_type":"opened","timestamp":"2026-10-29T14:24:00+09:00"}""";
        Assert.Equal(1, Ingest(t, noId).Accepted);
        Assert.Equal(0, Ingest(t, noId).Accepted); // re-sent from the queue: same derived id
        var stored = t.Store.GetAppEvents(0, long.MaxValue).Single();
        Assert.True(stored.EventIdDerived);
        Assert.Equal(AppEvents.DerivedId("iphone", "KakaoTalk", "opened", stored.TsUtcMs), stored.EventId);
    }

    [Fact]
    public void InvalidEventsAreReportedAndValidOnesStillStored()
    {
        using var t = new TempData();
        var body = string.Join("\n",
            """{"device_id":"iphone","app_name":"YouTube","event_type":"opened","timestamp":"2026-10-29T14:00:00+09:00"}""",
            """{"device_id":"../etc","app_name":"x","event_type":"opened","timestamp":"2026-10-29T14:00:00+09:00"}""",
            """{"device_id":"iphone","app_name":"x","event_type":"paused","timestamp":"2026-10-29T14:00:00+09:00"}""",
            """{"device_id":"iphone","app_name":"x","event_type":"opened","timestamp":"2026-10-29T16:00:00+09:00"}""", // 1 h in the future
            """{"device_id":"iphone","app_name":"x","event_type":"opened","timestamp":"2026-09-01T14:00:00+09:00"}""", // too old
            "not json");
        var r = Ingest(t, body);
        Assert.Equal(1, r.Accepted);
        Assert.Equal(5, r.Errors.Count);
        Assert.Contains(r.Errors, e => e.Contains("device_id"));
        Assert.Contains(r.Errors, e => e.Contains("event_type"));
        Assert.Contains(r.Errors, e => e.Contains("future"));
        Assert.Contains(r.Errors, e => e.Contains("30 days"));
        Assert.Contains(r.Errors, e => e.Contains("not valid JSON"));
    }

    [Fact]
    public void ShortcutsStyleValuesAreNormalized()
    {
        using var t = new TempData();
        // Shortcuts' own wording ("Is Opened") and camelCase keys are accepted; control characters are stripped.
        var r = Ingest(t, "{\"deviceId\":\"iphone\",\"appName\":\" Safari\\u0007 \",\"eventType\":\"Is Opened\",\"timestamp\":\"2026-10-29T14:00:00+09:00\"}");
        Assert.Equal(1, r.Accepted);
        var e = t.Store.GetAppEvents(0, long.MaxValue).Single();
        Assert.Equal(("Safari", "opened", "ios_shortcuts"), (e.App, e.Type, e.Source));
    }
}

public class AppSessionTests
{
    private static readonly DateTimeOffset Day = DateTimeOffset.Parse("2026-10-29T00:00:00+09:00");
    private static int _n;

    private static AppEvent E(string app, string type, string hhmm, string device = "iphone", long? receivedOffsetMs = null)
    {
        var parts = hhmm.Split(':');
        var at = Day.AddHours(int.Parse(parts[0])).AddMinutes(int.Parse(parts[1])).AddSeconds(parts.Length > 2 ? int.Parse(parts[2]) : 0);
        long ts = at.ToUnixTimeMilliseconds();
        return new AppEvent
        {
            DeviceId = device, Source = "ios_shortcuts", App = app, Type = type, TsUtcMs = ts, TzOffsetMinutes = 540,
            EventId = "e" + Interlocked.Increment(ref _n), ReceivedUtcMs = ts + (receivedOffsetMs ?? 1000),
        };
    }

    private static string Show(AppSession s) =>
        $"{s.App} {DateTimeOffset.FromUnixTimeMilliseconds(s.StartUtcMs).ToOffset(TimeSpan.FromHours(9)):HH:mm}-" +
        $"{DateTimeOffset.FromUnixTimeMilliseconds(s.EndUtcMs).ToOffset(TimeSpan.FromHours(9)):HH:mm} {s.Confidence}";

    [Fact]
    public void NormalOpenCloseAndSwitching()
    {
        var s = AppSessions.Reconstruct([
            E("Instagram", "opened", "14:00"), E("Instagram", "closed", "14:24"),
            E("KakaoTalk", "opened", "14:24"), E("KakaoTalk", "closed", "14:37"),
        ]);
        Assert.Equal(["Instagram 14:00-14:24 Complete", "KakaoTalk 14:24-14:37 Complete"], s.Select(Show).ToArray());
        Assert.Equal(24 * 60_000, s[0].DurationMs);
    }

    [Fact]
    public void OutOfOrderDeliveryGivesTheSameResult()
    {
        AppEvent[] events = [E("Instagram", "opened", "14:00"), E("Instagram", "closed", "14:24"), E("KakaoTalk", "opened", "14:24"), E("KakaoTalk", "closed", "14:37")];
        var inOrder = AppSessions.Reconstruct(events).Select(Show);
        var shuffled = AppSessions.Reconstruct([events[3], events[1], events[2], events[0]]).Select(Show);
        Assert.Equal(inOrder, shuffled);
    }

    [Fact]
    public void ClosedAndOpenedAtTheSameSecondStillPair()
    {
        // Rapid switching: Instagram closes and KakaoTalk opens at the same timestamp; close is processed first.
        var s = AppSessions.Reconstruct([E("KakaoTalk", "opened", "14:24"), E("Instagram", "closed", "14:24"), E("Instagram", "opened", "14:20"), E("KakaoTalk", "closed", "14:25")]);
        Assert.Equal(["Instagram 14:20-14:24 Complete", "KakaoTalk 14:24-14:25 Complete"], s.Select(Show).ToArray());
    }

    [Fact]
    public void MissingCloseEndsAtNextAppIfCloseEnough()
    {
        var s = AppSessions.Reconstruct([E("YouTube", "opened", "10:00"), E("Safari", "opened", "10:20"), E("Safari", "closed", "10:25")]);
        Assert.Equal("YouTube 10:00-10:20 EndInferred", Show(s[0]));
        Assert.Contains("no \"closed\"", s[0].Note);
        Assert.Equal(SessionConfidence.Complete, s[1].Confidence);
    }

    [Fact]
    public void MissingCloseWithLongGapIsUnknownNotFabricated()
    {
        var s = AppSessions.Reconstruct([E("YouTube", "opened", "10:00"), E("Safari", "opened", "13:00"), E("Safari", "closed", "13:05")]);
        Assert.Equal(SessionConfidence.Unknown, s[0].Confidence);
        Assert.False(s[0].HasDuration);
        Assert.Equal(0, s[0].DurationMs);
        Assert.Contains("unknown", s[0].Note);
    }

    [Fact]
    public void MissingOpenUsesPreviousEventOnlyWhenNear()
    {
        var near = AppSessions.Reconstruct([E("Safari", "closed", "10:05"), E("KakaoTalk", "closed", "10:10")]);
        Assert.Equal(SessionConfidence.Unknown, near.Single(x => x.App == "Safari").Confidence);              // nothing before the first close
        Assert.Equal("KakaoTalk 10:05-10:10 StartInferred", Show(near.Single(x => x.App == "KakaoTalk")));  // bounded by the previous event
        var far = AppSessions.Reconstruct([E("Safari", "closed", "08:00"), E("KakaoTalk", "closed", "10:10")]);
        Assert.Equal(SessionConfidence.Unknown, far.Single(x => x.App == "KakaoTalk").Confidence);
    }

    [Fact]
    public void DuplicateOpensAndDoubleTriggersCollapse()
    {
        var s = AppSessions.Reconstruct([
            E("Instagram", "opened", "14:00:00"), E("Instagram", "opened", "14:00:01"), // double trigger
            E("Instagram", "opened", "14:05:00"),                                       // re-open without close
            E("Instagram", "closed", "14:10:00"),
        ]);
        var only = Assert.Single(s);
        Assert.Equal("Instagram 14:00-14:10 Complete", Show(only));
        Assert.Contains("duplicate open", only.Note);
    }

    [Fact]
    public void StillOpenAtEndIsUnknown()
    {
        var s = AppSessions.Reconstruct([E("YouTube", "opened", "23:50")]);
        Assert.Equal(SessionConfidence.Unknown, Assert.Single(s).Confidence);
    }

    [Fact]
    public void CrossMidnightSessionIsSplitBetweenDaysInTotals()
    {
        var s = AppSessions.Reconstruct([E("YouTube", "opened", "23:50"), E("YouTube", "closed", "24:10")]);
        long dayStart = Day.ToUnixTimeMilliseconds(), dayEnd = Day.AddDays(1).ToUnixTimeMilliseconds();
        Assert.Equal(10 * 60_000, AppSessions.Totals(s, dayStart, dayEnd).Single().KnownMs);
        Assert.Equal(10 * 60_000, AppSessions.Totals(s, dayEnd, dayEnd + 86_400_000).Single().KnownMs);
    }

    [Fact]
    public void TotalsSeparateKnownInferredAndUnknown()
    {
        var s = AppSessions.Reconstruct([
            E("YouTube", "opened", "10:00"), E("YouTube", "closed", "10:30"),      // 30 known
            E("YouTube", "opened", "11:00"), E("Safari", "opened", "11:10"),       // 10 inferred
            E("Safari", "closed", "11:15"),
            E("YouTube", "opened", "12:00"),                                       // unknown (no close)
        ]);
        var yt = AppSessions.Totals(s, 0, long.MaxValue).Single(x => x.App == "YouTube");
        Assert.Equal((30 * 60_000L, 10 * 60_000L, 3, 1), (yt.KnownMs, yt.InferredMs, yt.Sessions, yt.UnknownSessions));
    }

    [Fact]
    public void DevicesAreReconstructedSeparately()
    {
        var s = AppSessions.Reconstruct([E("YouTube", "opened", "10:00", "phoneA"), E("Safari", "opened", "10:05", "phoneB"), E("YouTube", "closed", "10:20", "phoneA")]);
        Assert.Contains(s, x => x.DeviceId == "phoneA" && x.Confidence == SessionConfidence.Complete && x.DurationMs == 20 * 60_000);
    }

    [Fact]
    public void PhoneClockAheadIsNoted()
    {
        var s = AppSessions.Reconstruct([E("YouTube", "opened", "10:00"), E("YouTube", "closed", "10:05", receivedOffsetMs: -5 * 60_000)]);
        Assert.Contains("clock", Assert.Single(s).Note);
    }

    [Fact]
    public void OverlapWithWindowsActivity()
    {
        var s = AppSessions.Reconstruct([E("YouTube", "opened", "10:00"), E("YouTube", "closed", "10:30")]);
        long t0 = Day.AddHours(10).ToUnixTimeMilliseconds();
        var focus = new[] { new FocusRecord { DeviceId = "pc", StartUtcMs = t0 + 20 * 60_000, EndUtcMs = t0 + 60 * 60_000, Process = "Code.exe", Title = "x" } };
        Assert.Equal(10 * 60_000, AppSessions.OverlapMs(s, focus, 0, long.MaxValue));
    }
}
