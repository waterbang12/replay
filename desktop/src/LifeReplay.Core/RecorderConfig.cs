using System.Text.Json;
using System.Text.Json.Serialization;

namespace LifeReplay.Core;

/// <summary>User-editable settings, stored as config.json in the data directory.</summary>
public sealed class RecorderConfig
{
    /// <summary>Length of each video file. Boundaries are aligned to the wall clock (e.g. :00, :05, :10).</summary>
    public int SegmentSeconds { get; set; } = 300;
    /// <summary>Maximum frames per second. Frames are only encoded when the screen changes.</summary>
    public int Fps { get; set; } = 10;
    /// <summary>Downscale so the output is at most this tall. 0 = native resolution.</summary>
    public int MaxOutputHeight { get; set; } = 1080;
    /// <summary>low | medium | high. Ignored when BitrateKbps &gt; 0.</summary>
    public string Quality { get; set; } = "medium";
    public int BitrateKbps { get; set; } = 0;
    public bool CaptureCursor { get; set; } = true;
    /// <summary>Windows draws a yellow border around captured displays unless this is false (Windows 11 only).</summary>
    public bool ShowCaptureBorder { get; set; } = false;
    public bool HardwareEncoding { get; set; } = true;
    public bool RecordOnLaunch { get; set; } = true;
    /// <summary>Ask Windows to relaunch the recorder if it crashes (RegisterApplicationRestart).</summary>
    public bool AutoRestartAfterCrash { get; set; } = true;
    public int RetentionDays { get; set; } = 30;
    public double MaxStorageGB { get; set; } = 100;
    /// <summary>Pause recording if free disk space drops below this.</summary>
    public double MinFreeDiskGB { get; set; } = 5;
    public int ThumbnailIntervalSeconds { get; set; } = 30;
    /// <summary>Windows-only: while one of these processes owns the foreground window, video is blanked.</summary>
    public List<string> ExcludedProcesses { get; set; } = ["KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe"];
    /// <summary>Case-insensitive substrings of foreground window titles that blank the video.</summary>
    public List<string> ExcludedWindowTitles { get; set; } = ["InPrivate", "Incognito", "Private Browsing"];
    public string PauseHotkey { get; set; } = "Ctrl+Alt+Shift+P";
    /// <summary>Log which app and window title is in front (for "time per app / tab" summaries).</summary>
    public bool LogWindowTitles { get; set; } = true;
    /// <summary>No input and no screen change for this long counts as idle (not added to app time).</summary>
    public int IdleMinutes { get; set; } = 5;
    /// <summary>Optional daily recording hours; outside them the recorder stays in the tray but doesn't record.</summary>
    public RecordingHours Hours { get; set; } = new();
    public SyncSettings Sync { get; set; } = new();

    [JsonIgnore] public string? LoadWarning { get; private set; }

    public void Normalize()
    {
        SegmentSeconds = Math.Clamp(SegmentSeconds, 10, 3600);
        Fps = Math.Clamp(Fps, 1, 30);
        if (MaxOutputHeight < 0) MaxOutputHeight = 0;
        if (MaxOutputHeight is > 0 and < 240) MaxOutputHeight = 240;
        Quality = (Quality ?? "medium").Trim().ToLowerInvariant();
        if (Quality is not ("low" or "medium" or "high")) Quality = "medium";
        BitrateKbps = Math.Clamp(BitrateKbps, 0, 100_000);
        RetentionDays = Math.Max(0, RetentionDays);
        MaxStorageGB = Math.Max(0, MaxStorageGB);
        MinFreeDiskGB = Math.Max(0, MinFreeDiskGB);
        ThumbnailIntervalSeconds = Math.Clamp(ThumbnailIntervalSeconds, 5, 600);
        IdleMinutes = Math.Clamp(IdleMinutes, 1, 120);
        ExcludedProcesses ??= [];
        ExcludedWindowTitles ??= [];
        Sync ??= new SyncSettings();
        Hours ??= new RecordingHours();
        Hours.Normalize();
        Sync.Port = Math.Clamp(Sync.Port, 1024, 65535);
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads config; creates it with defaults if missing. A broken file is kept as config.json.bad.</summary>
    public static RecorderConfig Load(string path)
    {
        RecorderConfig cfg;
        string? warning = null;
        if (File.Exists(path))
        {
            try
            {
                cfg = JsonSerializer.Deserialize<RecorderConfig>(File.ReadAllText(path), JsonOptions) ?? new RecorderConfig();
            }
            catch (Exception ex)
            {
                warning = $"config.json could not be parsed ({ex.Message}); using defaults. The broken file was saved as config.json.bad";
                try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* best effort */ }
                cfg = new RecorderConfig();
                cfg.Normalize();
                cfg.Save(path);
            }
        }
        else
        {
            cfg = new RecorderConfig();
            cfg.Normalize();
            cfg.Save(path);
        }
        cfg.Normalize();
        cfg.LoadWarning = warning;
        return cfg;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>"Only record between Start and End" (local time). End before Start means the window crosses midnight.</summary>
public sealed class RecordingHours
{
    public bool Enabled { get; set; } = false;
    public string Start { get; set; } = "09:00";
    public string End { get; set; } = "18:00";
    public bool WeekdaysOnly { get; set; } = false;

    public static bool TryParse(string? text, out TimeSpan time)
    {
        time = default;
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m) || h is < 0 or > 23 || m is < 0 or > 59)
            return false;
        time = new TimeSpan(h, m, 0);
        return true;
    }

    public void Normalize()
    {
        Start = TryParse(Start, out var s) ? $"{s.Hours:D2}:{s.Minutes:D2}" : "09:00";
        End = TryParse(End, out var e) ? $"{e.Hours:D2}:{e.Minutes:D2}" : "18:00";
    }

    /// <summary>True if recording is allowed at this local time (always true when the schedule is off).</summary>
    public bool Allows(DateTime local)
    {
        if (!Enabled) return true;
        if (!TryParse(Start, out var s) || !TryParse(End, out var e) || s == e) return true;
        // For a window crossing midnight, the early-morning part belongs to the previous day's window.
        var dayOfWindow = local.TimeOfDay < s && s > e ? local.AddDays(-1).DayOfWeek : local.DayOfWeek;
        if (WeekdaysOnly && dayOfWindow is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var t = local.TimeOfDay;
        return s < e ? t >= s && t < e : t >= s || t < e;
    }

    public string Describe() => Enabled ? $"{Start}–{End}{(WeekdaysOnly ? ", Mon–Fri" : "")}" : "all day";
}

public sealed class SyncSettings
{
    /// <summary>Off by default. When on, an HTTPS endpoint (token auth) accepts iPhone app-usage events from Shortcuts.</summary>
    public bool Enabled { get; set; } = false;
    public int Port { get; set; } = 47800;
    /// <summary>
    /// Address to listen on. 127.0.0.1 (default) = reachable only through Tailscale Serve on this PC, which is
    /// the supported setup; 0.0.0.0 = all networks (the PC's firewall still applies).
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";
}
