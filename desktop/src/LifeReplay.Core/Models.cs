namespace LifeReplay.Core;

public static class Platforms
{
    public const string Windows = "windows";
    public const string Ios = "ios";
}

public static class SegmentStatus
{
    /// <summary>File is being written (or the recorder died while writing it).</summary>
    public const string Recording = "recording";
    /// <summary>File was closed cleanly and passed the integrity check.</summary>
    public const string Finalized = "finalized";
    /// <summary>File exists but failed the integrity check.</summary>
    public const string Corrupt = "corrupt";
    /// <summary>Recorder was interrupted before the file could be finalized; the video is not recoverable.</summary>
    public const string Lost = "lost";
}

public static class SessionStatus
{
    public const string Active = "active";
    public const string Ended = "ended";
    public const string Interrupted = "interrupted";
}

public static class GapReasons
{
    public const string Paused = "paused";
    public const string Locked = "locked";
    public const string Sleep = "sleep";
    public const string DisplayOff = "display-off";
    public const string OutsideHours = "outside-hours";
    public const string Excluded = "excluded";
    public const string Stopped = "stopped";
    public const string Crash = "crash";
    public const string DiskFull = "disk-full";
    public const string Error = "error";
    public const string IosInterrupted = "ios-interrupted";

    public static string Describe(string reason) => reason switch
    {
        Paused => "Paused",
        Locked => "Screen locked",
        Sleep => "Sleep / standby",
        DisplayOff => "Display off",
        OutsideHours => "Outside recording hours",
        Excluded => "Private app (blanked)",
        Stopped => "Recorder not running",
        Crash => "Recorder crashed",
        DiskFull => "Low disk space",
        Error => "Capture error",
        IosInterrupted => "iPhone broadcast interrupted",
        _ => reason,
    };
}

public sealed class DeviceRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Platform { get; init; }
    public long CreatedUtcMs { get; init; }
    public long? LastSeenUtcMs { get; init; }
}

public sealed class DisplayRecord
{
    public required string Id { get; init; }
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Left { get; init; }
    public int Top { get; init; }
    public bool IsPrimary { get; init; }
    public long FirstSeenUtcMs { get; init; }
    public long LastSeenUtcMs { get; init; }
}

public sealed class SessionRecord
{
    public required string Id { get; init; }
    public required string DeviceId { get; init; }
    public long StartUtcMs { get; init; }
    public long? EndUtcMs { get; init; }
    public string Status { get; init; } = SessionStatus.Active;
    public string? EndReason { get; init; }
    public string? AppVersion { get; init; }
}

public sealed class SegmentRecord
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public required string DeviceId { get; init; }
    public required string DisplayId { get; init; }
    public long StartUtcMs { get; set; }
    public long? EndUtcMs { get; set; }
    public long? DurationMs { get; set; }
    /// <summary>Relative to the data directory, forward slashes.</summary>
    public required string FilePath { get; set; }
    public long? FileSize { get; set; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public double Fps { get; init; }
    public int Bitrate { get; init; }
    public string Encoder { get; init; } = "";
    public string Status { get; set; } = SegmentStatus.Recording;
    public int FrameCount { get; set; }
    /// <summary>Frames captured per 10-second bucket, comma separated. Empty bucket = nothing changed on screen.</summary>
    public string? Activity { get; set; }
    public string? Error { get; set; }
    public long CreatedUtcMs { get; init; }
    public long? FinalizedUtcMs { get; set; }

    /// <summary>End time, treating a still-recording segment as running until <paramref name="nowMs"/>.</summary>
    public long EffectiveEnd(long nowMs) => EndUtcMs ?? Math.Max(StartUtcMs, nowMs);
    public bool IsPlayable => Status == SegmentStatus.Finalized;
}

public sealed class GapRecord
{
    public long Id { get; init; }
    public required string DeviceId { get; init; }
    public string? DisplayId { get; init; }
    public long StartUtcMs { get; init; }
    public long? EndUtcMs { get; init; }
    public required string Reason { get; init; }
    public string? Detail { get; init; }
}

public sealed class ThumbnailRecord
{
    public required string SegmentId { get; init; }
    public long TsUtcMs { get; init; }
    public required string FilePath { get; init; }
}

/// <summary>A stretch of time during which one window (app + title) was in front and in use.</summary>
public sealed class FocusRecord
{
    public long Id { get; init; }
    public required string DeviceId { get; init; }
    public long StartUtcMs { get; init; }
    public long EndUtcMs { get; set; }
    /// <summary>Executable name, e.g. "chrome.exe".</summary>
    public required string Process { get; init; }
    /// <summary>Window title, or "(private)" for excluded windows.</summary>
    public required string Title { get; init; }
    public long DurationMs => Math.Max(0, EndUtcMs - StartUtcMs);
}

public static class Activity
{
    public const int BucketMs = 10_000;

    public static string Encode(IReadOnlyList<int> buckets) => string.Join(',', buckets);

    public static int[] Decode(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return [];
        var parts = s.Split(',');
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++) int.TryParse(parts[i], out result[i]);
        return result;
    }
}
