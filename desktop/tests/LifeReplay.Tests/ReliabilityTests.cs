using LifeReplay.Core;

namespace LifeReplay.Tests;

public class RetentionTests
{
    private const long Day = 86_400_000;

    [Fact]
    public void DeletesSegmentsOlderThanRetention()
    {
        long now = 100 * Day;
        var items = new[]
        {
            new RetentionItem("old", now - 40 * Day, 10, SegmentStatus.Finalized),
            new RetentionItem("recent", now - 5 * Day, 10, SegmentStatus.Finalized),
        };
        Assert.Equal(["old"], Retention.Plan(items, now, 30, 0));
    }

    [Fact]
    public void QuotaDeletesOldestFirst()
    {
        long now = 100 * Day;
        var items = new[]
        {
            new RetentionItem("c", now - 1 * Day, 40, SegmentStatus.Finalized),
            new RetentionItem("a", now - 3 * Day, 40, SegmentStatus.Finalized),
            new RetentionItem("b", now - 2 * Day, 40, SegmentStatus.Finalized),
        };
        Assert.Equal(["a", "b"], Retention.Plan(items, now, 0, 50));
    }

    [Fact]
    public void NeverDeletesSegmentBeingRecorded()
    {
        long now = 100 * Day;
        var items = new[]
        {
            new RetentionItem("live", now - 90 * Day, 1000, SegmentStatus.Recording),
            new RetentionItem("done", now - 1 * Day, 10, SegmentStatus.Finalized),
        };
        var plan = Retention.Plan(items, now, 30, 5);
        Assert.DoesNotContain("live", plan);
        Assert.Contains("done", plan);
    }

    [Fact]
    public void ApplyRetentionRemovesFilesAndRows()
    {
        using var t = new TempData();
        long now = TimeUtil.NowMs();
        var old = t.AddSegment("d1", now - 40 * Day, now - 40 * Day + 1000, writeFile: true);
        var keep = t.AddSegment("d1", now - 1000, now, writeFile: true);
        var r = RecordingDeleter.ApplyRetention(t.Store, t.Paths, new RecorderConfig { RetentionDays = 30, MaxStorageGB = 0 }, now);
        Assert.Equal(1, r.Deleted);
        Assert.False(File.Exists(t.Paths.ToFull(old.FilePath)));
        Assert.True(File.Exists(t.Paths.ToFull(keep.FilePath)));
        Assert.Null(t.Store.GetSegment(old.Id));
        Assert.NotNull(t.Store.GetSegment(keep.Id));
    }
}

public class RecoveryTests
{
    [Fact]
    public void UnfinishedSegmentIsMarkedLostAndGapRecorded()
    {
        using var t = new TempData();
        long now = TimeUtil.NowMs();
        t.Store.UpsertDevice(new DeviceRecord { Id = "dev1", Name = "pc", Platform = Platforms.Windows, CreatedUtcMs = now - 100_000, LastSeenUtcMs = now - 20_000 });
        t.Store.InsertSession(new SessionRecord { Id = "live-session", DeviceId = "dev1", StartUtcMs = now - 60_000, Status = SessionStatus.Active });
        var done = t.AddSegment("d1", now - 60_000, now - 30_000, writeFile: true, sessionId: "live-session");
        var live = t.AddSegment("d1", now - 30_000, now, SegmentStatus.Recording, sessionId: "live-session");
        // The crash left a half-written file without its index.
        var part = t.Paths.ToFull(live.FilePath) + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        File.WriteAllBytes(part, Mp4Builder.Build(30_000, 100, 100, includeMoov: false));
        File.SetLastWriteTimeUtc(part, DateTimeOffset.FromUnixTimeMilliseconds(now - 10_000).UtcDateTime);

        var report = Recovery.Run(t.Store, t.Paths, "dev1", now);

        Assert.Equal(1, report.Lost);
        Assert.Equal(1, report.SessionsClosed);
        Assert.Equal(SegmentStatus.Lost, t.Store.GetSegment(live.Id)!.Status);
        // Ends at the partial file's last write (later than the last heartbeat at now - 20 s).
        Assert.Equal(now - 10_000, t.Store.GetSegment(live.Id)!.EndUtcMs);
        Assert.Equal(now - 10_000, t.Store.GetSessions().Single(s => s.Id == "live-session").EndUtcMs);
        Assert.False(File.Exists(part));
        // Finalized recordings from before the crash are untouched.
        Assert.Equal(SegmentStatus.Finalized, t.Store.GetSegment(done.Id)!.Status);
        Assert.True(File.Exists(t.Paths.ToFull(done.FilePath)));
        Assert.Equal(SessionStatus.Interrupted, t.Store.GetSessions().Single(s => s.Id == "live-session").Status);
        Assert.Contains(t.Store.GetGapsInRange(0, long.MaxValue / 2), g => g.Reason == GapReasons.Crash);
    }

    [Fact]
    public void CompletedButUnrecordedFileIsRecovered()
    {
        using var t = new TempData();
        long now = TimeUtil.NowMs();
        var seg = t.AddSegment("d1", now - 30_000, now, SegmentStatus.Recording);
        // Crash happened after the file was closed but before the database update.
        var part = t.Paths.ToFull(seg.FilePath) + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        File.WriteAllBytes(part, Mp4Builder.Build(29_500, 100, 100));

        var report = Recovery.Run(t.Store, t.Paths, "dev1", now);

        Assert.Equal(1, report.Recovered);
        var back = t.Store.GetSegment(seg.Id)!;
        Assert.Equal(SegmentStatus.Finalized, back.Status);
        Assert.Equal(29_500, back.DurationMs);
        Assert.True(File.Exists(t.Paths.ToFull(seg.FilePath)));
        Assert.False(File.Exists(part));
    }

    [Fact]
    public void RecoveryIsIdempotent()
    {
        using var t = new TempData();
        long now = TimeUtil.NowMs();
        t.AddSegment("d1", now - 30_000, now, SegmentStatus.Recording);
        Recovery.Run(t.Store, t.Paths, "dev1", now);
        var second = Recovery.Run(t.Store, t.Paths, "dev1", now + 1000);
        Assert.Equal(0, second.Lost + second.Recovered + second.SessionsClosed);
    }
}

public class DayTimelineTests
{
    private static DayTimeline Build(TempData t, long dayStart, long dayEnd, long now)
    {
        return DayTimeline.Build(dayStart, dayEnd, now, t.Store.GetSegmentsInRange(dayStart, dayEnd), t.Store.GetGapsInRange(dayStart, dayEnd),
            t.Store.GetDisplays(), t.Store.GetDevices());
    }

    [Fact]
    public void FindsSegmentsAndGaps()
    {
        using var t = new TempData();
        t.AddSegment("d1", 1_000_000, 1_300_000);
        t.AddSegment("d1", 1_300_000, 1_600_000);
        t.AddSegment("d1", 2_000_000, 2_300_000);
        t.Store.InsertGap(new GapRecord { DeviceId = "dev1", StartUtcMs = 1_600_000, EndUtcMs = 2_000_000, Reason = GapReasons.Paused });
        var tl = Build(t, 0, 3_000_000, 3_000_000);
        var lane = Assert.Single(tl.Lanes);

        Assert.NotNull(lane.SegmentAt(1_299_999, tl.NowMs));
        Assert.NotNull(lane.SegmentAt(1_300_000, tl.NowMs));
        Assert.Null(lane.SegmentAt(1_700_000, tl.NowMs));
        Assert.Equal(2_000_000, tl.NextSegmentStart(1_700_000));
        Assert.Equal(GapReasons.Paused, tl.GapAt(lane, 1_700_000)?.Reason);

        var gaps = lane.Uncovered(0, 3_000_000, tl.NowMs);
        Assert.Equal([(0L, 1_000_000L), (1_600_000L, 2_000_000L), (2_300_000L, 3_000_000L)], gaps);
    }

    [Fact]
    public void LostSegmentsAreNotPlayable()
    {
        using var t = new TempData();
        t.AddSegment("d1", 1000, 300_000, SegmentStatus.Lost);
        var tl = Build(t, 0, 400_000, 400_000);
        Assert.Null(tl.Lanes[0].SegmentAt(5000, tl.NowMs));
        Assert.False(tl.AnyCoverage(5000));
    }

    [Fact]
    public void SkipsInactiveBuckets()
    {
        using var t = new TempData();
        // 6 buckets of 10 s: active, idle, idle, idle, active, active
        t.AddSegment("d1", 0, 60_000, activity: "5,0,0,0,2,1");
        var tl = Build(t, 0, 100_000, 100_000);
        Assert.True(tl.IsActive(5_000));
        Assert.False(tl.IsActive(15_000));
        Assert.Equal(40_000, tl.NextActiveTime(12_000, 100_000));
    }

    [Fact]
    public void ActivityOnAnyScreenCountsAsActive()
    {
        using var t = new TempData();
        t.AddSegment("d1", 0, 30_000, activity: "0,0,0");
        t.AddSegment("d2", 0, 30_000, activity: "0,4,0");
        var tl = Build(t, 0, 100_000, 100_000);
        Assert.Equal(2, tl.Lanes.Count);
        Assert.False(tl.IsActive(5_000));
        Assert.True(tl.IsActive(15_000));
        Assert.Equal(10_000, tl.NextActiveTime(0, 100_000));
    }

    [Fact]
    public void NextActiveTimeJumpsOverGaps()
    {
        using var t = new TempData();
        t.AddSegment("d1", 0, 20_000, activity: "0,0");
        t.AddSegment("d1", 100_000, 120_000, activity: "0,3");
        var tl = Build(t, 0, 200_000, 200_000);
        Assert.Equal(110_000, tl.NextActiveTime(0, 200_000));
    }

    [Fact]
    public void WindowsLanesComeBeforeIphone()
    {
        using var t = new TempData();
        t.Store.UpsertDevice(new DeviceRecord { Id = "phone", Name = "Lee's iPhone", Platform = Platforms.Ios, CreatedUtcMs = 0 });
        t.AddSegment("ios-phone", 0, 1000, deviceId: "phone");
        t.AddSegment("d1", 0, 1000);
        var tl = Build(t, 0, 10_000, 10_000);
        Assert.Equal(["d1", "ios-phone"], tl.Lanes.Select(l => l.DisplayId).ToArray());
        Assert.Equal(Platforms.Ios, tl.Lanes[1].Platform);
    }
}
