namespace LifeReplay.Analysis;

/// <summary>
/// <b>The built-in rules for Progress / Not progress.</b> Checked from top to bottom; the first rule that matches a
/// window decides. A window that matches none is Unsorted. Your own choices (Topics → Move to) and windows similar
/// to them come first and override these rules (see <see cref="ProgressKinds.Of"/> and DayAnalysis.Categorize).
///
/// To change what counts as what, edit the word and app lists below. "App" is the friendly app name
/// (FocusSummary.AppName, e.g. "VS Code", "Google Chrome"); "title" is the tab/document part of the window title.
/// All comparisons ignore upper/lower case; title words match anywhere in the title.
/// </summary>
public static class ProgressRules
{
    public sealed record Rule(string Name, string Kind, Func<TitleItem, bool> Matches);

    /// <summary>The rules, in the order they're checked.</summary>
    public static readonly IReadOnlyList<Rule> All =
    [
        new("waiting on an AI tool", ProgressKinds.NotProgress, i => i.App == "Waiting on AI"),
        new("game app", ProgressKinds.NotProgress, i => GameApps.Contains(i.App)),
        new("chat / social app", ProgressKinds.NotProgress, i => SocialApps.Contains(i.App)),
        new("YouTube with a learning word", ProgressKinds.Progress, i => IsYouTube(i) && HasAny(i.Item, LearningWords)),
        new("YouTube", ProgressKinds.NotProgress, IsYouTube),
        new("social site in the title", ProgressKinds.NotProgress, i => HasAny(i.Item, SocialTitleWords) || IsXTitle(i.Item)),
        new("entertainment word in the title", ProgressKinds.NotProgress, i => HasAny(i.Item, EntertainmentWords)),
        new("learning / work word in the title", ProgressKinds.Progress, i => HasAny(i.Item, LearningWords)),
        new("work app", ProgressKinds.Progress, i => WorkApps.Contains(i.App)),
    ];

    /// <summary>The kind from the first matching rule, or Unsorted.</summary>
    public static string Classify(TitleItem item) => Match(item)?.Kind ?? ProgressKinds.Unsorted;

    /// <summary>The rule that decides this window (for "why is this here?"), or null when none matches.</summary>
    public static Rule? Match(TitleItem item) => All.FirstOrDefault(r => r.Matches(item));

    // ------------------------------------------------------------------ the lists

    /// <summary>Apps where you make things.</summary>
    public static readonly HashSet<string> WorkApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "VS Code", "Visual Studio", "Windows Terminal", "Cursor", "WindowsTerminal", "powershell", "pwsh", "cmd", "wsl",
        "idea64", "pycharm64", "clion64", "rider64", "Obsidian", "Notion", "Word", "Excel", "PowerPoint", "Adobe Acrobat",
        "Adobe Reader", "SumatraPDF", "ghidra", "ida64", "x64dbg", "Wireshark", "Zotero", "Typora", "Overleaf",
    };

    public static readonly HashSet<string> GameApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Steam", "EpicGamesLauncher", "Battle.net", "RiotClientServices", "LeagueClient", "League of Legends", "VALORANT",
        "cod", "BlackOps6", "Minecraft", "javaw", "MapleStory", "Overwatch", "GenshinImpact", "StarRail",
    };

    /// <summary>Chat and social apps on Windows.</summary>
    public static readonly HashSet<string> SocialApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "KakaoTalk", "Discord", "Telegram", "WhatsApp", "Messenger", "LINE", "Signal",
    };

    /// <summary>Social / messaging sites in a browser tab title.</summary>
    public static readonly string[] SocialTitleWords =
    [
        "Instagram", "Facebook", "Reddit", "Threads", "LinkedIn", "Discord", "Telegram", "WhatsApp", "Messenger",
        "KakaoTalk", "카카오톡", "에브리타임", "Everytime", "디시인사이드", "DCInside", "Twitter", "Bluesky", "Mastodon",
    ];

    /// <summary>Learning or making things. Also turns a YouTube video into progress.</summary>
    public static readonly string[] LearningWords =
    [
        "GitHub", "pwn.college", "arXiv", "Stack Overflow", "Overleaf", "LeetCode", "Codeforces", "백준", "Baekjoon",
        "Google Scholar", "Semantic Scholar", ".pdf", "lecture", "tutorial", "course", "강의", "강좌", "논문", " paper",
        "CTF", "exploit", "algorithm", "알고리즘", "USENIX", "DEF CON", "Black Hat", "documentation", "docs.",
    ];

    /// <summary>Video and streaming sites, games, feeds.</summary>
    public static readonly string[] EntertainmentWords =
    [
        "Netflix", "Twitch", "치지직", "CHZZK", "SOOP", "아프리카TV", "TikTok", "Shorts", "웹툰", "Webtoon", "Disney+", "TVING",
        "티빙", "Watcha", "왓챠", "Coupang Play", "쿠팡플레이", "Laftel", "라프텔", "gameplay", "playthrough", "cutscene", "Cutscenes",
        "게임", "Call of Duty", "콜 오브 듀티", "Steam", "op.gg", "나무위키",
    ];

    // ------------------------------------------------------------------ helpers

    private static bool HasAny(string title, string[] words) => words.Any(w => title.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static bool IsYouTube(TitleItem i) => i.Item.Contains("YouTube", StringComparison.OrdinalIgnoreCase);

    /// <summary>X (Twitter) tab titles end with " / X", e.g. "Home / X".</summary>
    private static bool IsXTitle(string t) => t.EndsWith(" / X", StringComparison.Ordinal) || t.Contains(" on X: ", StringComparison.Ordinal);
}
