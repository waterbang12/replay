namespace LifeReplay.Analysis;

/// <summary>
/// Sorts windows into Progress (work that moves something forward), Not progress (videos, games, chat, feeds)
/// and Unsorted. Order of trust: your choice for that exact window, then similarity to a window you sorted
/// (see <see cref="DayAnalysis.Categorize"/>), then the rules below, else Unsorted.
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

    private static readonly HashSet<string> ProgressApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "VS Code", "Visual Studio", "Windows Terminal", "Cursor", "WindowsTerminal", "powershell", "pwsh", "cmd", "wsl",
        "idea64", "pycharm64", "clion64", "rider64", "Obsidian", "Notion", "Word", "Excel", "PowerPoint", "Adobe Acrobat",
        "Adobe Reader", "SumatraPDF", "ghidra", "ida64", "x64dbg", "Wireshark", "Zotero", "Typora", "Overleaf",
    };

    private static readonly HashSet<string> GameApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Steam", "EpicGamesLauncher", "Battle.net", "RiotClientServices", "LeagueClient", "League of Legends", "VALORANT",
        "cod", "BlackOps6", "Minecraft", "javaw", "MapleStory", "Overwatch", "GenshinImpact", "StarRail",
    };

    /// <summary>Title words for learning or making things (also turn a YouTube video into progress).</summary>
    private static readonly string[] ProgressWords =
    [
        "GitHub", "pwn.college", "arXiv", "Stack Overflow", "Overleaf", "LeetCode", "Codeforces", "백준", "Baekjoon",
        "Google Scholar", "Semantic Scholar", ".pdf", "lecture", "tutorial", "course", "강의", "강좌", "논문", " paper",
        "CTF", "exploit", "algorithm", "알고리즘", "USENIX", "DEF CON", "Black Hat", "documentation", "docs.",
    ];

    /// <summary>Title words for entertainment: video and streaming sites, games, feeds.</summary>
    private static readonly string[] NotProgressWords =
    [
        "Netflix", "Twitch", "치지직", "CHZZK", "SOOP", "아프리카TV", "TikTok", "Shorts", "웹툰", "Webtoon", "Disney+", "TVING",
        "티빙", "Watcha", "왓챠", "Coupang Play", "쿠팡플레이", "Laftel", "라프텔", "gameplay", "playthrough", "cutscene", "Cutscenes",
        "게임", "Call of Duty", "콜 오브 듀티", "Steam", "op.gg", "나무위키",
    ];

    /// <summary>The built-in rule for a window, or Unsorted.</summary>
    public static string Rule(TitleItem item)
    {
        var t = item.Item;
        if (GameApps.Contains(item.App)) return NotProgress;
        if (FixedCategories.Of(item) == FixedCategories.Social) return NotProgress;
        bool learning = ProgressWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (FixedCategories.Of(item) == FixedCategories.YouTube) return learning ? Progress : NotProgress;
        if (NotProgressWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase))) return NotProgress;
        if (learning || ProgressApps.Contains(item.App)) return Progress;
        return Unsorted;
    }

    /// <summary>Your exact choice for this window, else the rule (no model needed).</summary>
    public static string Of(TitleItem item, IReadOnlyDictionary<string, string>? manual = null) =>
        manual != null && manual.TryGetValue(item.Text, out var k) && IsKind(k) ? k : Rule(item);
}
