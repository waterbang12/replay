using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LifeReplay.Core;

/// <summary>An "app opened / app closed" event written by an iPhone Shortcuts automation.</summary>
public sealed class AppEvent
{
    public long Id { get; init; }
    public required string DeviceId { get; init; }
    public required string Source { get; init; }
    public required string App { get; init; }
    /// <summary>"opened" or "closed".</summary>
    public required string Type { get; init; }
    /// <summary>When it happened on the phone (UTC ms), from the phone's own timestamp.</summary>
    public long TsUtcMs { get; init; }
    /// <summary>The phone's UTC offset when it happened, in minutes (for display and midnight handling).</summary>
    public int TzOffsetMinutes { get; init; }
    public required string EventId { get; init; }
    /// <summary>When LifeReplay read it (UTC ms). Large differences mean delayed sync or a wrong phone clock.</summary>
    public long ReceivedUtcMs { get; init; }
    public bool EventIdDerived { get; init; }
}

/// <summary>
/// iPhone app events from the Shortcuts log file in iCloud Drive (see <see cref="ParseLog"/>). Ids are derived from
/// app + event + second, so reading the same lines again only finds duplicates.
/// </summary>
public static partial class AppEvents
{
    public const string Opened = "opened";
    public const string Closed = "closed";
    /// <summary>Phone timestamps more than this far ahead of the PC clock are rejected as a broken clock.</summary>
    public const long MaxFutureMs = 10 * 60_000;
    /// <summary>Events older than this are rejected (a stale queue file, or a wrong clock).</summary>
    public const long MaxAgeMs = 30L * 86_400_000;

    [GeneratedRegex(@"^\s*(?<app>.+?)\s+(?<ev>opened|closed|open|close|열림|닫힘)\s+(?<ts>\S.*?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LogLine();

    /// <summary>
    /// The iCloud log the Shortcuts automations append to, one line per event: "YouTube opened 2026-11-03T16:21:50+09:00".
    /// The date may also be the phone's localized format ("2026. 11. 3. 오후 4:21:50"), read as this PC's local time.
    /// Ids are derived from app, event and second, so reading the whole file again only finds duplicates.
    /// </summary>
    public static List<AppEvent> ParseLog(string text, long nowMs, List<string> errors)
    {
        const string device = "iphone";
        var list = new List<AppEvent>();
        int n = 0;
        foreach (var raw in text.Split('\n'))
        {
            n++;
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;
            var m = LogLine().Match(line);
            if (!m.Success) { errors.Add($"line {n}: expected \"<app> opened|closed <date>\""); continue; }
            var app = NormalizeApp(m.Groups["app"].Value);
            var type = NormalizeType(m.Groups["ev"].Value);
            if (app == null || type == null) { errors.Add($"line {n}: app name must be 1-80 characters"); continue; }
            if (!TryParseLogTime(m.Groups["ts"].Value, out var ts, out var offset)) { errors.Add($"line {n}: can't read the date \"{m.Groups["ts"].Value}\""); continue; }
            if (ts > nowMs + MaxFutureMs || ts < nowMs - MaxAgeMs) continue; // wrong clock or very old: skip quietly
            list.Add(new AppEvent
            {
                DeviceId = device, Source = "ios_icloud_log", App = app, Type = type, TsUtcMs = ts, TzOffsetMinutes = offset,
                EventId = DerivedId(device, app, type, ts), EventIdDerived = true, ReceivedUtcMs = nowMs,
            });
        }
        return list;
    }

    private static bool TryParseLogTime(string text, out long utcMs, out int offsetMinutes)
    {
        if (TryParseTimestamp(text, out utcMs, out offsetMinutes)) return true;
        foreach (var culture in new[] { "ko-KR", "en-US" })
            if (DateTime.TryParse(text, CultureInfo.GetCultureInfo(culture), DateTimeStyles.AssumeLocal, out var local))
            {
                var dto = new DateTimeOffset(local);
                utcMs = dto.ToUnixTimeMilliseconds();
                offsetMinutes = (int)dto.Offset.TotalMinutes;
                return true;
            }
        return false;
    }

    public static string DerivedId(string device, string app, string type, long tsUtcMs)
    {
        var key = $"{device}|{app.ToLowerInvariant()}|{type}|{tsUtcMs / 1000}";
        return "d-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
    }

    private static string? NormalizeApp(string? app)
    {
        if (app == null) return null;
        var a = new string(app.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return a.Length is >= 1 and <= 80 ? a : null;
    }

    private static string? NormalizeType(string? t) => (t ?? "").Trim().ToLowerInvariant() switch
    {
        "opened" or "open" or "is opened" or "열림" => Opened,
        "closed" or "close" or "is closed" or "닫힘" => Closed,
        _ => null,
    };

    /// <summary>ISO 8601 with an explicit offset ("+09:00", "+0900" or "Z"). Local times without an offset are refused.</summary>
    public static bool TryParseTimestamp(string? text, out long utcMs, out int offsetMinutes)
    {
        utcMs = 0;
        offsetMinutes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        // Require an explicit zone: "Z" or a trailing ±hh:mm / ±hhmm.
        if (!(s.EndsWith('Z') || s.EndsWith('z') || Regex.IsMatch(s, @"[+-]\d{2}:?\d{2}$"))) return false;
        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)) return false;
        utcMs = dto.ToUnixTimeMilliseconds();
        offsetMinutes = (int)dto.Offset.TotalMinutes;
        return true;
    }
}
