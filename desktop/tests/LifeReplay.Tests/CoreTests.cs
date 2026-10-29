using LifeReplay.Core;

namespace LifeReplay.Tests;

public class MigrationTests
{
    [Fact]
    public void FreshDatabaseIsMigratedToLatestVersion()
    {
        using var t = new TempData();
        using var c = t.Store.Open();
        Assert.Equal(Migrations.LatestVersion, Migrations.GetVersion(c));
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        using var r = cmd.ExecuteReader();
        var tables = new List<string>();
        while (r.Read()) tables.Add(r.GetString(0));
        foreach (var name in new[] { "devices", "displays", "sessions", "segments", "gaps", "thumbnails" })
            Assert.Contains(name, tables);
    }

    [Fact]
    public void MigratingTwiceIsHarmless()
    {
        using var t = new TempData();
        t.Store.Migrate();
        t.AddSegment("d1", 1000, 2000);
        t.Store.Migrate();
        Assert.Single(t.Store.GetSegmentsInRange(0, 10_000));
    }

    [Fact]
    public void JournalModeIsWal()
    {
        using var t = new TempData();
        using var c = t.Store.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string)cmd.ExecuteScalar()!);
    }
}

public class MetadataStoreTests
{
    [Fact]
    public void RangeQueryReturnsOverlappingSegmentsOnly()
    {
        using var t = new TempData();
        t.AddSegment("d1", 0, 100);
        var b = t.AddSegment("d1", 100, 200);
        var c = t.AddSegment("d1", 200, 300);
        t.AddSegment("d1", 300, 400);
        var result = t.Store.GetSegmentsInRange(150, 250);
        Assert.Equal([b.Id, c.Id], result.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void UpdateSegmentPersistsFinalState()
    {
        using var t = new TempData();
        var s = t.AddSegment("d1", 1000, 2000, SegmentStatus.Recording);
        s.Status = SegmentStatus.Finalized;
        s.EndUtcMs = 5000;
        s.DurationMs = 4000;
        s.Activity = "1,0,3";
        s.FrameCount = 42;
        t.Store.UpdateSegment(s);
        var back = t.Store.GetSegment(s.Id)!;
        Assert.Equal(SegmentStatus.Finalized, back.Status);
        Assert.Equal(5000, back.EndUtcMs);
        Assert.Equal("1,0,3", back.Activity);
        Assert.Equal(42, back.FrameCount);
    }

    [Fact]
    public void GapsOpenAndClose()
    {
        using var t = new TempData();
        long id = t.Store.InsertGap(new GapRecord { DeviceId = "dev1", StartUtcMs = 1000, Reason = GapReasons.Paused });
        Assert.Null(t.Store.GetGapsInRange(0, 5000).Single().EndUtcMs);
        t.Store.CloseGap(id, 3000);
        Assert.Equal(3000, t.Store.GetGapsInRange(0, 5000).Single().EndUtcMs);
        t.Store.CloseGap(id, 9000); // already closed: unchanged
        Assert.Equal(3000, t.Store.GetGapsInRange(0, 5000).Single().EndUtcMs);
    }

    [Fact]
    public void DeleteSegmentRowsRemovesThumbnailsAndEmptySessions()
    {
        using var t = new TempData();
        var s = t.AddSegment("d1", 0, 10);
        t.Store.InsertThumbnail(new ThumbnailRecord { SegmentId = s.Id, TsUtcMs = 5, FilePath = "x.jpg" });
        t.Store.DeleteSegmentRows([s.Id]);
        Assert.Null(t.Store.GetSegment(s.Id));
        Assert.Empty(t.Store.GetThumbnails([s.Id]));
        Assert.Empty(t.Store.GetSessions());
    }
}

public class Mp4InspectorTests
{
    private static Mp4Info Inspect(byte[] data) => Mp4Inspector.Inspect(new MemoryStream(data));

    [Fact]
    public void ValidFileReportsDurationAndSize()
    {
        var info = Inspect(Mp4Builder.Build(300_000, 1728, 1080));
        Assert.True(info.IsValid, info.Error);
        Assert.Equal(300_000, info.DurationMs);
        Assert.Equal(1728, info.Width);
        Assert.Equal(1080, info.Height);
        Assert.Equal(1, info.VideoTracks);
    }

    [Fact]
    public void FragmentedFileUsesMehdDuration()
    {
        // ftyp, moov(mvhd duration 0, mvex/mehd 45 s, trak), moof, mdat — as written by AVAssetWriter with fragments.
        var normal = Mp4Builder.Build(0, 886, 1920, includeMoov: false, includeMdat: false);
        var mvhd = new byte[100];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(12), 600);
        var mehd = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(mehd.AsSpan(4), 45 * 600);
        var tkhd = new byte[84];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(76), 886u << 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(80), 1920u << 16);
        var hdlr = new byte[24];
        System.Text.Encoding.ASCII.GetBytes("vide").CopyTo(hdlr, 8);
        var moov = Mp4Builder.Box("moov", Mp4Builder.Box("mvhd", mvhd), Mp4Builder.Box("mvex", Mp4Builder.Box("mehd", mehd)),
            Mp4Builder.Box("trak", Mp4Builder.Box("tkhd", tkhd), Mp4Builder.Box("mdia", Mp4Builder.Box("hdlr", hdlr))));
        var file = normal.Concat(moov).Concat(Mp4Builder.Box("moof", new byte[16])).Concat(Mp4Builder.Box("mdat", new byte[64])).ToArray();
        var info = Inspect(file);
        Assert.True(info.IsValid, info.Error);
        Assert.True(info.Fragmented);
        Assert.Equal(45_000, info.DurationMs);
        Assert.Equal(886, info.Width);
    }

    [Fact]
    public void FileWithoutMoovIsInvalid()
    {
        var info = Inspect(Mp4Builder.Build(1000, 100, 100, includeMoov: false));
        Assert.False(info.IsValid);
        Assert.Contains("moov", info.Error);
    }

    [Fact]
    public void TruncatedFileIsInvalid()
    {
        var full = Mp4Builder.Build(1000, 100, 100);
        var info = Inspect(full[..(full.Length - 20)]);
        Assert.False(info.IsValid);
        Assert.Contains("truncated", info.Error);
    }

    [Fact]
    public void GarbageIsInvalid()
    {
        Assert.False(Inspect(new byte[4096]).IsValid);
        Assert.False(Inspect([1, 2, 3]).IsValid);
    }

    [Fact]
    public void FileMustStartWithFtyp()
    {
        var bad = Mp4Builder.Box("mdat", new byte[32]).Concat(Mp4Builder.Build(1000, 10, 10)).ToArray();
        Assert.False(Inspect(bad).IsValid);
    }
}

public class TimeUtilTests
{
    [Theory]
    [InlineData(0, 300_000, 300_000)]
    [InlineData(299_000, 300_000, 600_000)]      // 1 s before a boundary: skip to the next one
    [InlineData(150_000, 300_000, 300_000)]
    [InlineData(300_000, 300_000, 600_000)]
    public void SegmentBoundariesAlignToWallClock(long now, long seg, long expected)
    {
        Assert.Equal(expected, TimeUtil.NextSegmentBoundaryMs(now, seg, 30_000));
    }

    [Fact]
    public void DstDaysHaveCorrectLength()
    {
        var ny = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var (s1, e1) = TimeUtil.LocalDayRange(new DateOnly(2026, 3, 8), ny);   // spring forward
        var (s2, e2) = TimeUtil.LocalDayRange(new DateOnly(2026, 11, 1), ny);  // fall back
        var (s3, e3) = TimeUtil.LocalDayRange(new DateOnly(2026, 6, 1), ny);
        Assert.Equal(23 * 3600_000L, e1 - s1);
        Assert.Equal(25 * 3600_000L, e2 - s2);
        Assert.Equal(24 * 3600_000L, e3 - s3);
    }

    [Fact]
    public void LocalAndUtcRoundTrip()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        long utc = TimeUtil.LocalToUtcMs(new DateTime(2026, 10, 27, 9, 30, 0), tz);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 0, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), utc);
        Assert.Equal(new DateTime(2026, 10, 27, 9, 30, 0), TimeUtil.ToLocal(utc, tz));
    }
}

public class FocusTests
{
    [Theory]
    [InlineData("chrome.exe", "pwn.college - Program Misuse - Google Chrome", "pwn.college - Program Misuse")]
    [InlineData("msedge.exe", "paper.pdf - Microsoft​ Edge", "paper.pdf")]
    [InlineData("Code.exe", "● exploit.py - pwn-notes - Visual Studio Code", "exploit.py - pwn-notes")]
    [InlineData("chrome.exe", "(3) Inbox - Google Chrome", "Inbox")]
    [InlineData("KakaoTalk.exe", "", "KakaoTalk")]
    [InlineData("chrome.exe", "통합검색 | 잡코리아 - Chrome", "통합검색 | 잡코리아")]
    [InlineData("chrome.exe", "a - b - c", "a - b - c")] // dashes that aren't the app name stay
    public void ItemNamesDropAppSuffixes(string process, string title, string expected) =>
        Assert.Equal(expected, FocusSummary.ItemName(process, title));

    [Fact]
    public void SummaryGroupsByAppAndItemAndClipsToRange()
    {
        FocusRecord F(long s, long e, string p, string t) => new() { DeviceId = "d", StartUtcMs = s, EndUtcMs = e, Process = p, Title = t };
        var records = new[]
        {
            F(0, 60_000, "chrome.exe", "pwn.college - Google Chrome"),
            F(60_000, 90_000, "Code.exe", "a.py - proj - Visual Studio Code"),
            F(90_000, 150_000, "chrome.exe", "pwn.college - Google Chrome"),
            F(150_000, 400_000, "chrome.exe", "YouTube - Google Chrome"), // clipped at 200 s
        };
        var apps = FocusSummary.Summarize(records, 0, 200_000);
        Assert.Equal("Google Chrome", apps[0].App);
        Assert.Equal(170_000, apps[0].DurationMs);
        Assert.Equal(("pwn.college", 120_000L), (apps[0].Items[0].Item, apps[0].Items[0].DurationMs));
        Assert.Equal(("VS Code", 30_000L), (apps[1].App, apps[1].DurationMs));
    }

    [Fact]
    public void FocusEventsRoundTripAndFocusAt()
    {
        using var t = new TempData();
        long id = t.Store.InsertFocus(new FocusRecord { DeviceId = "d", StartUtcMs = 1000, EndUtcMs = 1000, Process = "chrome.exe", Title = "x" });
        t.Store.UpdateFocusEnd(id, 5000);
        t.Store.InsertFocus(new FocusRecord { DeviceId = "d", StartUtcMs = 6000, EndUtcMs = 9000, Process = "Code.exe", Title = "y" });
        var all = t.Store.GetFocusInRange(0, 10_000);
        Assert.Equal(2, all.Count);
        Assert.Equal(4000, all[0].DurationMs);
        var tl = new DayTimeline(0, 10_000, 10_000, [], []) { Focus = all };
        Assert.Equal("chrome.exe", tl.FocusAt(3000)?.Process);
        Assert.Null(tl.FocusAt(5500));
        Assert.Equal("Code.exe", tl.FocusAt(8999)?.Process);
        t.Store.DeleteFocusInRange(0, 5500);
        Assert.Single(t.Store.GetFocusInRange(0, 10_000));
    }
}

public class RecordingHoursTests
{
    private static RecordingHours H(string s, string e, bool weekdays = false) => new() { Enabled = true, Start = s, End = e, WeekdaysOnly = weekdays };

    [Theory]
    [InlineData("09:00", "18:00", "2026-10-28 08:59", false)]
    [InlineData("09:00", "18:00", "2026-10-28 09:00", true)]
    [InlineData("09:00", "18:00", "2026-10-28 17:59", true)]
    [InlineData("09:00", "18:00", "2026-10-28 18:00", false)]
    [InlineData("22:00", "02:00", "2026-10-28 23:30", true)]   // crosses midnight
    [InlineData("22:00", "02:00", "2026-10-29 01:30", true)]
    [InlineData("22:00", "02:00", "2026-10-29 12:00", false)]
    public void WindowBoundaries(string s, string e, string at, bool expected) =>
        Assert.Equal(expected, H(s, e).Allows(DateTime.Parse(at)));

    [Fact]
    public void WeekdaysOnlySkipsWeekends()
    {
        var h = H("09:00", "18:00", weekdays: true);
        Assert.True(h.Allows(new DateTime(2026, 10, 30, 10, 0, 0)));   // Friday
        Assert.False(h.Allows(new DateTime(2026, 10, 31, 10, 0, 0)));  // Saturday
        // Friday-night window that runs into Saturday morning still belongs to Friday.
        Assert.True(H("22:00", "02:00", weekdays: true).Allows(new DateTime(2026, 10, 31, 1, 0, 0)));
    }

    [Fact]
    public void DisabledAllowsEverything() =>
        Assert.True(new RecordingHours { Enabled = false, Start = "09:00", End = "10:00" }.Allows(new DateTime(2026, 10, 28, 3, 0, 0)));

    [Fact]
    public void BadTimesAreNormalized()
    {
        var h = new RecordingHours { Start = "25:99", End = "evening" };
        h.Normalize();
        Assert.Equal("09:00", h.Start);
        Assert.Equal("18:00", h.End);
        var ok = new RecordingHours { Start = "7:05", End = "19:30" };
        ok.Normalize();
        Assert.Equal("07:05", ok.Start);
    }
}

public class EncodingPlanTests
{
    [Fact]
    public void DownscalesToMaxHeightWithEvenDimensions()
    {
        var cfg = new RecorderConfig { MaxOutputHeight = 1080, Fps = 10 };
        var p = EncodingPlan.Compute(1920, 1200, cfg);
        Assert.Equal(1728, p.Width);
        Assert.Equal(1080, p.Height);
        Assert.Equal(0, p.Width % 2);
    }

    [Fact]
    public void NativeResolutionWhenSmallerThanLimit()
    {
        var p = EncodingPlan.Compute(1366, 767, new RecorderConfig { MaxOutputHeight = 1080 });
        Assert.Equal(1366, p.Width);
        Assert.Equal(766, p.Height); // odd height rounded down to even
    }

    [Fact]
    public void BitrateOverrideAndEstimate()
    {
        var p = EncodingPlan.Compute(1920, 1080, new RecorderConfig { BitrateKbps = 2000 });
        Assert.Equal(2_000_000, p.BitrateBps);
        Assert.Equal(900_000_000, p.MaxBytesPerHour); // 2 Mbit/s = 900 MB per hour
    }

    [Fact]
    public void QualityPresetsAreOrdered()
    {
        int Br(string q) => EncodingPlan.Compute(1920, 1080, new RecorderConfig { Quality = q }).BitrateBps;
        Assert.True(Br("low") < Br("medium"));
        Assert.True(Br("medium") < Br("high"));
    }
}

public class ConfigTests
{
    [Fact]
    public void MissingConfigIsCreatedWithDefaults()
    {
        using var t = new TempData();
        var cfg = RecorderConfig.Load(t.Paths.ConfigPath);
        Assert.True(File.Exists(t.Paths.ConfigPath));
        Assert.Equal(300, cfg.SegmentSeconds);
        Assert.Equal(10, cfg.Fps);
        Assert.False(cfg.Sync.Enabled);
    }

    [Fact]
    public void BrokenConfigFallsBackAndIsKept()
    {
        using var t = new TempData();
        File.WriteAllText(t.Paths.ConfigPath, "{ this is not json");
        var cfg = RecorderConfig.Load(t.Paths.ConfigPath);
        Assert.NotNull(cfg.LoadWarning);
        Assert.True(File.Exists(t.Paths.ConfigPath + ".bad"));
        Assert.Equal(300, cfg.SegmentSeconds);
    }

    [Fact]
    public void ValuesAreClamped()
    {
        using var t = new TempData();
        File.WriteAllText(t.Paths.ConfigPath, "{ \"fps\": 500, \"segmentSeconds\": 1, \"quality\": \"ultra\", // comment allowed\n }");
        var cfg = RecorderConfig.Load(t.Paths.ConfigPath);
        Assert.Equal(30, cfg.Fps);
        Assert.Equal(10, cfg.SegmentSeconds);
        Assert.Equal("medium", cfg.Quality);
    }
}
