using LifeReplay.Analysis;
using LifeReplay.Core;

namespace LifeReplay.Tests;

public class AiWaitTests
{
    [Theory]
    [InlineData("chrome.exe", "ChatGPT - Google Chrome", "ChatGPT")]
    [InlineData("msedge.exe", "New chat - Claude", "Claude")]
    [InlineData("ChatGPT.exe", "ChatGPT", "ChatGPT")]
    [InlineData("chrome.exe", "lofi beats - YouTube", null)]
    [InlineData("Code.exe", "Program.cs - life_replay - Visual Studio Code", null)] // the editor itself isn't a chat
    public void RecognizesAiChatWindows(string process, string title, string? tool) => Assert.Equal(tool, AiTools.Match(process, title));

    [Fact]
    public void ReadsWhetherClaudeCodeIsWorkingFromTheTranscriptEnd()
    {
        const string working = """
            {"type":"user","message":{"role":"user","content":"fix it"}}
            {"type":"assistant","message":{"role":"assistant","stop_reason":"tool_use"}}
            {"type":"ai-title","title":"x"}
            """;
        const string done = """
            {"type":"assistant","message":{"role":"assistant","stop_reason":"tool_use"}}
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result"}]}}
            {"type":"assistant","message":{"role":"assistant","stop_reason":"end_turn"}}
            {"type":"last-prompt"}
            """;
        Assert.True(AiTools.TranscriptBusy(working));
        Assert.False(AiTools.TranscriptBusy(done));
        Assert.True(AiTools.TranscriptBusy("ut off line\n" + """{"type":"user","message":{}}"""));  // a cut-off first line is skipped
        Assert.Null(AiTools.TranscriptBusy("""{"type":"mode"}"""));
    }

    private static FocusRecord F(long s, long e, string p = "Code.exe", string t = "a.cs") =>
        new() { DeviceId = "pc", StartUtcMs = s, EndUtcMs = e, Process = p, Title = t };

    [Fact]
    public void WaitsAreCutOutOfTheWindowLog()
    {
        var focus = new[] { F(0, 100), F(100, 200, "chrome.exe", "ChatGPT") };
        var waits = new[] { new AiWait(1, 50, 150, "Claude Code") };
        var spliced = AiTools.Splice(focus, waits);
        Assert.Equal([(0L, 50L, "Code.exe"), (50L, 150L, AiTools.WaitProcess), (150L, 200L, "chrome.exe")],
            spliced.Select(f => (f.StartUtcMs, f.EndUtcMs, f.Process)));
        Assert.Equal(200, spliced.Sum(f => f.DurationMs)); // nothing counted twice
    }

    [Fact]
    public void WaitingCountsAsNotProgress()
    {
        var item = DayAnalysis.Items(AiTools.Splice([F(0, 60_000)], [new AiWait(1, 0, 60_000, "Claude Code")]), 0, 60_000).Single();
        Assert.Equal(("Waiting on AI", "Claude Code"), (item.App, item.Item));
        Assert.Equal(ProgressKinds.NotProgress, ProgressKinds.Rule(item));
    }

    [Fact]
    public void StoredWaitsComeBackInTheActivityLog()
    {
        using var t = new TempData();
        t.Store.InsertFocus(F(0, 100_000));
        long id = t.Store.InsertAiWait(10_000, 20_000, "ChatGPT");
        t.Store.UpdateAiWaitEnd(id, 40_000);
        var log = t.Store.GetActivityInRange(0, 100_000);
        Assert.Equal(30_000, log.Where(f => f.Process == AiTools.WaitProcess).Sum(f => f.DurationMs));
        Assert.Equal(100_000, log.Sum(f => f.DurationMs));
        t.Store.DeleteAiWaitsBefore(50_000);
        Assert.Empty(t.Store.GetAiWaits(0, 100_000));
    }
}
