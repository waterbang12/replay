namespace LifeReplay.Core;

/// <summary>All timestamps are stored as UTC Unix milliseconds; only the UI converts to local time.</summary>
public static class TimeUtil
{
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    public static DateTime ToLocal(long utcMs, TimeZoneInfo? tz = null) =>
        TimeZoneInfo.ConvertTimeFromUtc(FromMs(utcMs).UtcDateTime, tz ?? TimeZoneInfo.Local);

    public static DateOnly LocalDate(long utcMs, TimeZoneInfo? tz = null) => DateOnly.FromDateTime(ToLocal(utcMs, tz));

    /// <summary>UTC range [start, end) of a local calendar day. Handles 23/25-hour DST days.</summary>
    public static (long StartMs, long EndMs) LocalDayRange(DateOnly day, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return (LocalToUtcMs(start, tz), LocalToUtcMs(start.AddDays(1), tz));
    }

    public static long LocalToUtcMs(DateTime local, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // A local time that doesn't exist (DST spring-forward) maps to the first valid time after it.
        int guard = 0;
        while (tz.IsInvalidTime(local) && guard++ < 8) local = local.AddMinutes(15);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, tz), TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Next wall-clock-aligned segment boundary after now. A boundary closer than minSegmentMs is skipped so
    /// a recording started at 12:04:58 doesn't produce a 2-second file.
    /// </summary>
    public static long NextSegmentBoundaryMs(long nowMs, long segmentMs, long minSegmentMs)
    {
        if (segmentMs <= 0) throw new ArgumentOutOfRangeException(nameof(segmentMs));
        long next = (nowMs / segmentMs + 1) * segmentMs;
        if (next - nowMs < minSegmentMs) next += segmentMs;
        return next;
    }

    public static string FormatDuration(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes:D2}m" : ts.TotalMinutes >= 1 ? $"{ts.Minutes}m {ts.Seconds:D2}s" : $"{ts.Seconds}s";
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.##} {units[u]}";
    }
}
