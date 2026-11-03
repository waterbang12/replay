namespace LifeReplay.Core;

/// <summary>A DNS lookup from a phone that belongs to a known app (only these are stored).</summary>
public sealed record DnsHit(string DeviceId, long TsUtcMs, string Domain, string App);

/// <summary>
/// Which app a domain belongs to. Only domains that clearly belong to one app are listed; shared ones
/// (googleapis.com, fbcdn.net, akamai…) are left out, because they would credit the wrong app.
/// </summary>
public static class NetworkApps
{
    /// <summary>App name → domain suffixes. Names match what you'd type in Shortcuts, so both sources line up.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Apps = new Dictionary<string, string[]>
    {
        ["YouTube"] = ["youtube.com", "googlevideo.com", "ytimg.com", "youtube-nocookie.com", "youtubei.googleapis.com", "yt3.ggpht.com"],
        ["Instagram"] = ["instagram.com", "cdninstagram.com"],
        ["Threads"] = ["threads.net", "threads.com"],
        ["Facebook"] = ["facebook.com", "facebook.net"],
        ["TikTok"] = ["tiktok.com", "tiktokv.com", "tiktokcdn.com", "tiktokcdn-us.com", "ibytedtos.com", "byteoversea.com"],
        ["KakaoTalk"] = ["kakao.com", "kakaocdn.net", "kakao.co.kr"],
        ["Discord"] = ["discord.com", "discordapp.com", "discordapp.net", "discord.gg", "discord.media"],
        ["X"] = ["twitter.com", "x.com", "twimg.com"],
        ["Reddit"] = ["reddit.com", "redd.it", "redditmedia.com", "redditstatic.com"],
        ["Netflix"] = ["netflix.com", "nflxvideo.net", "nflxso.net", "nflxext.com", "nflximg.net"],
        ["Twitch"] = ["twitch.tv", "ttvnw.net", "jtvnw.net"],
        ["치지직"] = ["chzzk.naver.com"],
        ["SOOP"] = ["sooplive.co.kr", "afreecatv.com"],
        ["네이버 웹툰"] = ["comic.naver.com", "webtoons.com"],
        ["에브리타임"] = ["everytime.kr"],
        ["Pinterest"] = ["pinterest.com", "pinimg.com"],
        ["Disney+"] = ["disneyplus.com", "bamgrid.com"],
        ["TVING"] = ["tving.com"],
        ["Coupang Play"] = ["coupangplay.com"],
        ["Laftel"] = ["laftel.net"],
        ["Wavve"] = ["wavve.com"],
        ["ChatGPT"] = ["chatgpt.com", "openai.com", "oaistatic.com", "oaiusercontent.com"],
        ["GitHub"] = ["github.com", "githubusercontent.com"],
    };

    private static readonly List<(string Suffix, string App)> Suffixes =
        Apps.SelectMany(a => a.Value.Select(d => (d, a.Key))).OrderByDescending(x => x.d.Length).ToList();

    /// <summary>The app for a domain ("rr3---sn-abc.googlevideo.com" → YouTube), or null. Matches whole labels only.</summary>
    public static string? Match(string domain)
    {
        var d = domain.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (var (suffix, app) in Suffixes)
            if (d == suffix || d.EndsWith("." + suffix, StringComparison.Ordinal)) return app;
        return null;
    }
}

/// <summary>
/// Turns lookups into estimated app sessions. An app in use makes many lookups (images, video chunks, feeds);
/// a single notification makes one or two. So: lookups for the same app at most <see cref="GapMs"/> apart form a
/// session, a session needs at least <see cref="MinHits"/> lookups (in different seconds), and it lasts from the
/// first lookup to <see cref="TailMs"/> after the last one. These are estimates: the phone also caches answers.
/// </summary>
public static class NetworkSessions
{
    public const long GapMs = 3 * 60_000;
    public const long TailMs = 60_000;
    public const int MinHits = 3;
    public const string Source = "network";

    public static List<AppSession> Build(IEnumerable<DnsHit> hits)
    {
        var result = new List<AppSession>();
        foreach (var g in hits.GroupBy(h => (h.DeviceId, h.App)))
        {
            // A and AAAA lookups for the same name arrive together: count one per name per second.
            var times = g.Select(h => (h.TsUtcMs, h.Domain)).Distinct().Select(x => x.TsUtcMs / 1000 * 1000)
                .Distinct().OrderBy(t => t).ToList();
            int start = 0;
            for (int i = 1; i <= times.Count; i++)
            {
                if (i < times.Count && times[i] - times[i - 1] <= GapMs) continue;
                int n = i - start;
                if (n >= MinHits)
                    result.Add(new AppSession(g.Key.DeviceId, g.Key.App, times[start], times[i - 1] + TailMs, SessionConfidence.Estimated, Source,
                        $"estimated from {n} network lookups"));
                start = i;
            }
        }
        return result.OrderBy(s => s.StartUtcMs).ThenBy(s => s.App, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Shortcuts sessions plus network sessions, without counting an app twice: for an app that has Shortcuts
    /// sessions in the same range, the exact Shortcuts sessions win.
    /// </summary>
    public static List<AppSession> Combine(IReadOnlyList<AppSession> shortcuts, IReadOnlyList<AppSession> network)
    {
        var exact = shortcuts.Select(s => s.App.ToLowerInvariant()).ToHashSet();
        return shortcuts.Concat(network.Where(n => !exact.Contains(n.App.ToLowerInvariant())))
            .OrderBy(s => s.StartUtcMs).ThenBy(s => s.App, StringComparer.Ordinal).ToList();
    }
}
