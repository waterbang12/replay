using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LifeReplay.Core;

/// <summary>An "app opened / app closed" event sent by an iPhone Shortcuts automation.</summary>
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
    /// <summary>When LifeReplay received it (UTC ms). Large differences mean queued delivery or a wrong phone clock.</summary>
    public long ReceivedUtcMs { get; init; }
    public bool EventIdDerived { get; init; }
}

public sealed record AppEventIngestResult(int Accepted, int Duplicates, List<string> Errors);

/// <summary>
/// Parses, validates and stores app events. Accepts a single JSON object, a JSON array, or JSON Lines (one object
/// per line), which is what the Shortcuts offline queue sends. Duplicates are ignored, so re-sending is always safe.
/// </summary>
public static partial class AppEvents
{
    public const string Opened = "opened";
    public const string Closed = "closed";
    /// <summary>Phone timestamps more than this far ahead of the PC clock are rejected as a broken clock.</summary>
    public const long MaxFutureMs = 10 * 60_000;
    /// <summary>Events older than this are rejected (a stale queue file, or a wrong clock).</summary>
    public const long MaxAgeMs = 30L * 86_400_000;
    public const int MaxBatch = 5000;

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")] private static partial Regex SafeId();

    public static AppEventIngestResult Ingest(MetadataStore store, string body, long nowMs)
    {
        var errors = new List<string>();
        var parsed = new List<AppEvent>();
        int index = 0;
        foreach (var element in Elements(body, errors))
        {
            index++;
            if (parsed.Count >= MaxBatch) { errors.Add($"#{index}: batch limit of {MaxBatch} events reached; send the rest separately"); break; }
            var e = Parse(element, nowMs, out var error);
            if (e != null) parsed.Add(e); else errors.Add($"#{index}: {error}");
        }
        int accepted = parsed.Count == 0 ? 0 : store.InsertAppEvents(parsed);
        return new AppEventIngestResult(accepted, parsed.Count - accepted, errors);
    }

    /// <summary>Object, array, or one object per line.</summary>
    private static IEnumerable<JsonElement> Elements(string body, List<string> errors)
    {
        var text = (body ?? "").Trim().TrimStart('﻿');
        if (text.Length == 0) yield break;
        if (text[0] == '[')
        {
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(text); }
            catch (JsonException ex) { errors.Add("invalid JSON array: " + ex.Message); }
            if (doc == null) yield break;
            using (doc)
                foreach (var el in doc.RootElement.EnumerateArray()) yield return el.Clone();
            yield break;
        }
        // JSON Lines (a single object is just one line). Shortcuts may join lines with \r or \n.
        int lineNo = 0;
        foreach (var raw in text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            lineNo++;
            var line = raw.Trim().TrimEnd(',');
            if (line.Length == 0) continue;
            JsonElement el;
            try { using var d = JsonDocument.Parse(line); el = d.RootElement.Clone(); }
            catch (JsonException) { errors.Add($"line {lineNo}: not valid JSON"); continue; }
            yield return el;
        }
    }

    private static string? Str(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }

    public static AppEvent? Parse(JsonElement e, long nowMs, out string? error)
    {
        error = null;
        if (e.ValueKind != JsonValueKind.Object) { error = "not a JSON object"; return null; }
        var device = Str(e, "device_id", "deviceId")?.Trim() ?? "";
        if (!SafeId().IsMatch(device)) { error = "device_id must be 1-64 letters, digits, '.', '_' or '-'"; return null; }
        var source = Str(e, "source")?.Trim();
        if (string.IsNullOrEmpty(source)) source = "ios_shortcuts";
        if (!SafeId().IsMatch(source)) { error = "source must be 1-64 letters, digits, '.', '_' or '-'"; return null; }
        var app = NormalizeApp(Str(e, "app_name", "appName", "app"));
        if (app == null) { error = "app_name is required (1-80 characters)"; return null; }
        var type = NormalizeType(Str(e, "event_type", "eventType", "event"));
        if (type == null) { error = "event_type must be \"opened\" or \"closed\""; return null; }
        var tsText = Str(e, "timestamp", "ts");
        if (!TryParseTimestamp(tsText, out var ts, out var offset)) { error = "timestamp must be ISO 8601 with a UTC offset, e.g. 2026-10-29T14:00:00+09:00"; return null; }
        if (ts > nowMs + MaxFutureMs) { error = $"timestamp is {(ts - nowMs) / 60_000} min in the future (check the phone's clock)"; return null; }
        if (ts < nowMs - MaxAgeMs) { error = "timestamp is older than 30 days"; return null; }
        var id = Str(e, "event_id", "eventId", "id")?.Trim();
        bool derived = false;
        if (string.IsNullOrEmpty(id))
        {
            // Fallback when Shortcuts can't make an id: the same app, type and second is the same event.
            id = DerivedId(device, app, type, ts);
            derived = true;
        }
        else if (id.Length > 100 || id.Any(char.IsControl)) { error = "event_id must be at most 100 printable characters"; return null; }
        return new AppEvent
        {
            DeviceId = device, Source = source, App = app, Type = type, TsUtcMs = ts, TzOffsetMinutes = offset,
            EventId = id, ReceivedUtcMs = nowMs, EventIdDerived = derived,
        };
    }

    /// <summary>
    /// The one-action Shortcuts setup: the automation only opens a URL with the app and event in it, so the time
    /// is when the PC receives it (no queue: nothing is recorded while the PC can't be reached).
    /// </summary>
    public static AppEvent? FromUrl(string? app, string? ev, long nowMs, out string? error)
    {
        error = null;
        var a = NormalizeApp(app);
        if (a == null) { error = "app is required (1-80 characters)"; return null; }
        var t = NormalizeType(ev);
        if (t == null) { error = "ev must be \"opened\" or \"closed\""; return null; }
        const string device = "iphone";
        return new AppEvent
        {
            DeviceId = device, Source = "ios_shortcuts_url", App = a, Type = t, TsUtcMs = nowMs,
            TzOffsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime).TotalMinutes,
            EventId = DerivedId(device, a, t, nowMs), EventIdDerived = true, ReceivedUtcMs = nowMs,
        };
    }

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
