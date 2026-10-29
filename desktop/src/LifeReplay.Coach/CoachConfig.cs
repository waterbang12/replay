using System.Text.Json;
using System.Text.RegularExpressions;
using LifeReplay.Core;

namespace LifeReplay.Coach;

/// <summary>
/// Coach settings, in coach.json next to config.json. Everything that talks to the network is off by default.
/// The API key is never stored here (see <see cref="ApiKeys"/>).
/// </summary>
public sealed class CoachConfig
{
    /// <summary>Send requests to the model at all. Off = deterministic evaluation and notifications only.</summary>
    public bool Enabled { get; set; } = false;
    /// <summary>OpenAI-compatible base URL; requests go to {BaseUrl}/responses. Default: BAZE API Gateway.</summary>
    public string BaseUrl { get; set; } = "https://api.baze.co/v1/gateway";
    public string Model { get; set; } = "gpt-6-astra";
    /// <summary>Reasoning effort sent to reasoning models (low keeps hidden reasoning tokens down). Empty = don't send.</summary>
    public string ReasoningEffort { get; set; } = "low";
    public int TimeoutSeconds { get; set; } = 60;
    /// <summary>Extra attempts after a timeout, network error, 429 or 5xx. Each attempt counts against the budget.</summary>
    public int Retries { get; set; } = 1;
    public int MaxOutputTokens { get; set; } = 1200;

    // Budget (BAZE bills per request in credits; set CreditsPerRequest to what your gateway charges).
    public int CreditsPerRequest { get; set; } = 5;
    public int MonthlyCreditCap { get; set; } = 600;
    public int DailyRequestCap { get; set; } = 8;

    // Notifications (deterministic, no model call).
    public bool Notifications { get; set; } = false;
    public string QuietHours { get; set; } = "22:00-08:00";
    public int CooldownMinutes { get; set; } = 60;
    public int MaxNotificationsPerDay { get; set; } = 3;
    public int SnoozeMinutes { get; set; } = 60;

    // Privacy. Window titles, URLs, screenshots and video are never sent; these control the rest.
    /// <summary>Send "Task 1" instead of your todo titles.</summary>
    public bool RedactTaskTitles { get; set; } = false;
    /// <summary>Include today's commit subject lines (otherwise only the count is sent).</summary>
    public bool IncludeCommitMessages { get; set; } = false;

    public static string PathFor(AppPaths paths) => Path.Combine(paths.DataDir, "coach.json");

    public static CoachConfig Load(AppPaths paths)
    {
        var path = PathFor(paths);
        CoachConfig cfg;
        try { cfg = File.Exists(path) ? JsonSerializer.Deserialize<CoachConfig>(File.ReadAllText(path), RecorderConfig.JsonOptions) ?? new() : new(); }
        catch (Exception ex) { Log.Warn("coach.json unreadable; using defaults (coach off)", ex); cfg = new(); cfg.Enabled = false; }
        cfg.Normalize();
        return cfg;
    }

    public void Save(AppPaths paths)
    {
        var path = PathFor(paths);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, RecorderConfig.JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public void Normalize()
    {
        BaseUrl = (BaseUrl ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) { BaseUrl = "https://api.baze.co/v1/gateway"; }
        Model = string.IsNullOrWhiteSpace(Model) ? "gpt-6-astra" : Model.Trim();
        ReasoningEffort = (ReasoningEffort ?? "").Trim().ToLowerInvariant();
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 300);
        Retries = Math.Clamp(Retries, 0, 3);
        MaxOutputTokens = Math.Clamp(MaxOutputTokens, 200, 8000);
        CreditsPerRequest = Math.Max(0, CreditsPerRequest);
        MonthlyCreditCap = Math.Max(0, MonthlyCreditCap);
        DailyRequestCap = Math.Max(0, DailyRequestCap);
        CooldownMinutes = Math.Clamp(CooldownMinutes, 5, 24 * 60);
        MaxNotificationsPerDay = Math.Clamp(MaxNotificationsPerDay, 0, 50);
        SnoozeMinutes = Math.Clamp(SnoozeMinutes, 5, 24 * 60);
        if (!TryParseRange(QuietHours, out _, out _)) QuietHours = "22:00-08:00";
    }

    /// <summary>"22:00-08:00" (may cross midnight). Empty string = no quiet hours.</summary>
    public static bool TryParseRange(string? text, out TimeSpan start, out TimeSpan end)
    {
        start = end = default;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var parts = text.Split('-', '–');
        return parts.Length == 2 && RecordingHours.TryParse(parts[0], out start) && RecordingHours.TryParse(parts[1], out end);
    }
}

/// <summary>
/// Finds the API key without ever writing it anywhere: BAZE_API_KEY, then OPENAI_API_KEY, then
/// secrets.json in the data folder (outside the repository). secrets.json may be {"bazeApiKey": "..."},
/// {"apiKey": "..."} or just the key on its own.
/// </summary>
public static partial class ApiKeys
{
    public static string SecretsPath(AppPaths paths) => Path.Combine(paths.DataDir, "secrets.json");

    public static string? Find(AppPaths paths)
    {
        foreach (var name in new[] { "BAZE_API_KEY", "OPENAI_API_KEY" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v) return v.Trim();
        try
        {
            var path = SecretsPath(paths);
            return File.Exists(path) ? FromText(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    public static string? FromText(string text)
    {
        text = text.Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var name in new[] { "bazeApiKey", "apiKey", "openaiApiKey", "key" })
                    if (doc.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } k)
                        return k.Trim();
            }
            catch (JsonException) { /* fall through: maybe a key with braces around it */ }
        }
        var m = KeyLike().Match(text);
        return m.Success ? m.Value : null;
    }

    [GeneratedRegex(@"[A-Za-z0-9][A-Za-z0-9_\-\.]{19,}")] private static partial Regex KeyLike();
}
