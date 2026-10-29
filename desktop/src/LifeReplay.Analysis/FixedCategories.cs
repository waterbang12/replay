namespace LifeReplay.Analysis;

/// <summary>
/// Three fixed buckets on top of the automatic topics: YouTube, Social, and Other (everything else, which is
/// then grouped by topic). Decided from the app and window title only.
/// </summary>
public static class FixedCategories
{
    public const string YouTube = "YouTube";
    public const string Social = "Social";
    public const string Other = "Other";

    private static readonly HashSet<string> SocialApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "KakaoTalk", "Discord", "Telegram", "WhatsApp", "Messenger", "LINE", "Signal",
    };

    /// <summary>Words that identify social / messaging sites in a browser tab title.</summary>
    private static readonly string[] SocialTitleWords =
    [
        "Instagram", "Facebook", "Reddit", "Threads", "LinkedIn", "Discord", "Telegram", "WhatsApp", "Messenger",
        "KakaoTalk", "카카오톡", "에브리타임", "Everytime", "디시인사이드", "DCInside", "Twitter", "Bluesky", "Mastodon",
    ];

    public static string Of(TitleItem item)
    {
        if (SocialApps.Contains(item.App)) return Social;
        var t = item.Item;
        if (t.Contains("YouTube", StringComparison.OrdinalIgnoreCase)) return YouTube;
        foreach (var w in SocialTitleWords)
            if (t.Contains(w, StringComparison.OrdinalIgnoreCase)) return Social;
        // X (Twitter) tab titles end with " / X", e.g. "Home / X".
        if (t.EndsWith(" / X", StringComparison.Ordinal) || t.Contains(" on X: ", StringComparison.Ordinal)) return Social;
        return Other;
    }
}
