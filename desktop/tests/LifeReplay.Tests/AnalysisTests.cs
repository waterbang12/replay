using LifeReplay.Analysis;
using LifeReplay.Core;

namespace LifeReplay.Tests;

public class TodoPlanTests
{
    private const string File = """
        # Every day
        hours: 09:30-18:00
        - [ ] Email ~15m

        # 2026-10-30
        - [ ] pwn.college: Program Misuse ~2h
          match: pwn.college, program-misuse
          progress: 3/10 levels
        - [x] Read domain theory paper ~1h30m
        - [ ] Free thinking

        # 2026-10-31
        - [ ] Other day ~1h

        <!--
        - [ ] not a task
          match: hidden
        -->
        """;

    [Fact]
    public void ParsesTheDaysSectionPlusEveryDay()
    {
        var p = TodoPlan.Parse(File, new DateOnly(2026, 10, 30));
        Assert.Equal(["Email", "pwn.college: Program Misuse", "Read domain theory paper", "Free thinking"], p.Tasks.Select(t => t.Title).ToArray());
        Assert.Equal(15 * 60_000L, p.Tasks[0].PlannedMs);
        var pwn = p.Tasks[1];
        Assert.Equal(7_200_000, pwn.PlannedMs);
        Assert.Equal(["pwn.college", "program-misuse"], pwn.Match.ToArray());
        Assert.Equal((3, 10, "levels"), (pwn.ProgressDone, pwn.ProgressTotal, pwn.ProgressUnit));
        Assert.True(p.Tasks[2].Done);
        Assert.Equal(5_400_000, p.Tasks[2].PlannedMs);
        Assert.Null(p.Tasks[3].PlannedMs);
        Assert.Equal(new TimeSpan(9, 30, 0), p.DayStart);
        Assert.DoesNotContain(p.Tasks, t => t.Title.Contains("not a task"));
    }

    [Fact]
    public void FileWithoutDatesAppliesEveryDay()
    {
        var p = TodoPlan.Parse("- [ ] Thing ~1.5h\n- [ ] Other ~90min", new DateOnly(2030, 1, 1));
        Assert.Equal([5_400_000L, 5_400_000L], p.Tasks.Select(t => t.PlannedMs!.Value).ToArray());
    }

    [Fact]
    public void TemplateParsesCleanly()
    {
        var today = new DateOnly(2026, 10, 30);
        var p = TodoPlan.Parse(TodoPlan.Template(today), today);
        Assert.Equal(3, p.Tasks.Count);
        Assert.Equal(["pwn.college"], p.Tasks[0].Match.ToArray());
    }
}

public class DayAnalysisTests
{
    // Fake 3-D "meaning" vectors: x = hacking, y = math, z = coding.
    private static readonly Dictionary<string, float[]> Space = new()
    {
        ["pwn.college: Program Misuse pwn.college"] = V(1, 0, 0),
        ["Read domain theory paper"] = V(0, 1, 0),
        ["Code LifeReplay"] = V(0, 0, 1),
        ["Dojos (Google Chrome)"] = V(0.9f, 0.1f, 0),
        ["Abramsky - Domain Theory.pdf"] = V(0.05f, 0.95f, 0.1f),
        ["hacker@practice"] = V(0.3f, 0.3f, 0.3f),
        ["MainWindow.xaml.cs - life_replay"] = V(0, 0.1f, 0.95f),
        ["YouTube (Google Chrome)"] = V(0.33f, 0.33f, 0.33f),
    };

    private static float[] V(float x, float y, float z)
    {
        var n = MathF.Sqrt(x * x + y * y + z * z);
        return [x / n, y / n, z / n];
    }

    private static FocusRecord F(int startMin, int endMin, string process, string title) =>
        new() { DeviceId = "d", StartUtcMs = startMin * 60_000L, EndUtcMs = endMin * 60_000L, Process = process, Title = title };

    private static TodoPlan Plan() => TodoPlan.Parse("""
        - [ ] pwn.college: Program Misuse ~2h
          match: pwn.college
          progress: 2/4 levels
        - [ ] Read domain theory paper ~1h
        - [ ] Code LifeReplay ~1h
        """, new DateOnly(2026, 1, 1));

    [Fact]
    public void MatchesByKeywordMeaningAndContext()
    {
        var focus = new[]
        {
            F(0, 30, "chrome.exe", "pwn.college - Program Misuse - Google Chrome"), // keyword
            F(30, 40, "WindowsTerminal.exe", "hacker@practice"),                     // ambiguous; between two pwn windows → context
            F(40, 60, "chrome.exe", "Dojos - Google Chrome"),                        // meaning (0.9 hacking)
            F(60, 90, "msedge.exe", "Abramsky - Domain Theory.pdf - Microsoft Edge"),// meaning
            F(90, 100, "chrome.exe", "YouTube - Google Chrome"),                     // ambiguous, not sandwiched → unassigned
            F(100, 130, "Code.exe", "MainWindow.xaml.cs - life_replay - Visual Studio Code"),
        };
        var s = DayAnalysis.Match(Plan(), focus, 0, 86_400_000, 0, TimeSpan.Zero, TimeSpan.FromHours(24),
            t => Space.TryGetValue(t, out var v) ? v : V(0.33f, 0.33f, 0.34f), new Dictionary<string, string>());

        var pwn = s.Tasks[0];
        Assert.Equal(60 * 60_000L, pwn.SpentMs);
        Assert.Contains(pwn.Items, i => i.How == MatchHow.Keyword);
        Assert.Contains(pwn.Items, i => i.How == MatchHow.Context && i.Item.App == "Windows Terminal");
        Assert.Contains(pwn.Items, i => i.How == MatchHow.Meaning && i.Item.Item == "Dojos");
        Assert.Equal(30 * 60_000L, s.Tasks[1].SpentMs);
        Assert.Equal(30 * 60_000L, s.Tasks[2].SpentMs);
        Assert.Single(s.Unassigned);
        Assert.Equal("YouTube", s.Unassigned[0].Item.Item);
        // 2 of 4 levels in 60 min → about 60 min left at that pace.
        Assert.Equal(60 * 60_000L, pwn.RemainingMs);
    }

    [Fact]
    public void ManualLabelsWinAndAheadBehindUsesTheWorkingDay()
    {
        long midnight = TimeUtil.LocalToUtcMs(new DateTime(2026, 1, 1)); // local midnight in whatever time zone runs the test
        var f = F(0, 60, "chrome.exe", "YouTube - Google Chrome");
        var focus = new[] { new FocusRecord { DeviceId = "d", StartUtcMs = midnight + f.StartUtcMs, EndUtcMs = midnight + f.EndUtcMs, Process = f.Process, Title = f.Title } };
        var labels = new Dictionary<string, string> { ["YouTube (Google Chrome)"] = "Code LifeReplay" };
        // Working day 00:00-04:00, now = 02:00 → half the day passed → expected 2h of the 4h planned.
        var s = DayAnalysis.Match(Plan(), focus, midnight, midnight + 86_400_000, midnight + 2 * 3_600_000, TimeSpan.Zero, TimeSpan.FromHours(4),
            t => Space.TryGetValue(t, out var v) ? v : V(0.33f, 0.33f, 0.34f), labels);
        Assert.Equal(MatchHow.Manual, s.Tasks[2].Items.Single().How);
        Assert.Equal(4 * 3_600_000L, s.PlannedTotalMs);
        Assert.Equal(2 * 3_600_000L, s.ExpectedByNowMs);
        Assert.Equal(-1 * 3_600_000L, s.AheadMs); // 1h spent on plan vs 2h expected → 1h behind
    }

    [Fact]
    public void TopicsGroupSimilarWindows()
    {
        var items = new List<TitleItem>
        {
            new("Chrome", "pwn A", 30, 0), new("Chrome", "pwn B", 20, 0), new("Edge", "math", 25, 0), new("Code", "code", 5, 0),
        };
        var vecs = new List<float[]> { V(1, 0, 0), V(0.95f, 0.05f, 0), V(0, 1, 0), V(0, 0, 1) };
        var topics = DayAnalysis.FindTopics(items, vecs);
        Assert.Equal(3, topics.Count);
        Assert.Equal("pwn A", topics[0].Label);
        Assert.Equal(50, topics[0].DurationMs);
    }
}
