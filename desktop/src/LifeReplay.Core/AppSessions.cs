namespace LifeReplay.Core;

/// <summary>How sure a session's start and end are.</summary>
public enum SessionConfidence
{
    /// <summary>Both an "opened" and a "closed" event were received.</summary>
    Complete,
    /// <summary>No "closed" event: ended when the next app opened (iPhone shows one app at a time).</summary>
    EndInferred,
    /// <summary>No "opened" event: started at the previous event, at most <see cref="AppSessions.MaxInferMs"/> earlier.</summary>
    StartInferred,
    /// <summary>Only one side is known and nothing nearby bounds the other: the duration is unknown.</summary>
    Unknown,
}

public sealed record AppSession(
    string DeviceId, string App, long StartUtcMs, long EndUtcMs, SessionConfidence Confidence, string Source, string? Note)
{
    public long DurationMs => Math.Max(0, EndUtcMs - StartUtcMs);
    /// <summary>Durations of Unknown sessions aren't real; they're drawn as a marker only.</summary>
    public bool HasDuration => Confidence != SessionConfidence.Unknown;
}

public sealed record AppTotal(string App, long KnownMs, long InferredMs, int Sessions, int UnknownSessions)
{
    public long TotalMs => KnownMs + InferredMs;
}

/// <summary>
/// Rebuilds app sessions from open/close events. Deterministic: sorted by phone timestamp, then by event type
/// (close before open at the same instant) and app, so the same events always give the same sessions.
/// </summary>
public static class AppSessions
{
    /// <summary>The longest gap a missing open/close may be inferred across. Beyond it the duration is reported unknown.</summary>
    public const long MaxInferMs = 30 * 60_000;
    /// <summary>Repeated events for the same app and type closer together than this are one event (double triggers).</summary>
    public const long DuplicateWindowMs = 2_000;
    /// <summary>Phone clock this far ahead of when the event reached the PC is noted on the session.</summary>
    public const long ClockSkewNoteMs = 2 * 60_000;

    public static List<AppSession> Reconstruct(IEnumerable<AppEvent> events)
    {
        var result = new List<AppSession>();
        foreach (var device in events.GroupBy(e => e.DeviceId))
            result.AddRange(ReconstructDevice(device.ToList()));
        return result.OrderBy(s => s.StartUtcMs).ThenBy(s => s.App, StringComparer.Ordinal).ToList();
    }

    private static List<AppSession> ReconstructDevice(List<AppEvent> raw)
    {
        // Sort, then collapse double triggers (same app + type within 2 s).
        var sorted = raw
            .OrderBy(e => e.TsUtcMs)
            .ThenBy(e => e.Type == AppEvents.Closed ? 0 : 1)
            .ThenBy(e => e.App, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.EventId, StringComparer.Ordinal)
            .ToList();
        var events = new List<AppEvent>();
        foreach (var e in sorted)
        {
            var dup = events.LastOrDefault(x => x.Type == e.Type && string.Equals(x.App, e.App, StringComparison.OrdinalIgnoreCase));
            if (dup != null && e.TsUtcMs - dup.TsUtcMs < DuplicateWindowMs && ReferenceEquals(dup, events[^1])) continue;
            events.Add(e);
        }

        var sessions = new List<AppSession>();
        AppEvent? open = null;          // the currently open app's "opened" event
        bool duplicateOpen = false;
        long? previousTs = null;        // timestamp of the event before the current one
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            string? skew = e.TsUtcMs - e.ReceivedUtcMs > ClockSkewNoteMs ? "phone clock ahead of PC" : null;
            if (e.Type == AppEvents.Opened)
            {
                if (open != null)
                {
                    if (string.Equals(open.App, e.App, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicateOpen = true; // opened twice without a close: keep the first start
                        previousTs = e.TsUtcMs;
                        continue;
                    }
                    // Another app came to the front without a close event for the first one.
                    sessions.Add(CloseByNext(open, e.TsUtcMs, duplicateOpen));
                }
                open = e;
                duplicateOpen = false;
            }
            else // closed
            {
                if (open != null && string.Equals(open.App, e.App, StringComparison.OrdinalIgnoreCase))
                {
                    sessions.Add(new AppSession(e.DeviceId, open.App, open.TsUtcMs, e.TsUtcMs, SessionConfidence.Complete, open.Source,
                        Join(duplicateOpen ? "duplicate open event ignored" : null, skew)));
                    open = null;
                    duplicateOpen = false;
                }
                else
                {
                    if (open != null)
                    {
                        // A different app was open: it ended at the latest when this one was closed.
                        sessions.Add(CloseByNext(open, e.TsUtcMs, duplicateOpen));
                        open = null;
                        duplicateOpen = false;
                    }
                    // Close without a matching open: bounded by the previous event if it's close enough.
                    long? start = previousTs is { } p && e.TsUtcMs - p <= MaxInferMs ? p : null;
                    sessions.Add(start is { } s
                        ? new AppSession(e.DeviceId, e.App, s, e.TsUtcMs, SessionConfidence.StartInferred, e.Source, Join("no \"opened\" event; started at the previous event", skew))
                        : new AppSession(e.DeviceId, e.App, e.TsUtcMs, e.TsUtcMs, SessionConfidence.Unknown, e.Source, Join("\"closed\" without \"opened\"; duration unknown", skew)));
                }
            }
            previousTs = e.TsUtcMs;
        }
        if (open != null)
        {
            // Still open at the end of the data: either in use right now, or the close event never arrived.
            sessions.Add(new AppSession(open.DeviceId, open.App, open.TsUtcMs, open.TsUtcMs, SessionConfidence.Unknown, open.Source,
                duplicateOpen ? "no \"closed\" event yet (duplicate open ignored)" : "no \"closed\" event yet"));
        }
        return sessions;
    }

    private static AppSession CloseByNext(AppEvent open, long nextTs, bool duplicateOpen)
    {
        if (nextTs - open.TsUtcMs <= MaxInferMs)
            return new AppSession(open.DeviceId, open.App, open.TsUtcMs, nextTs, SessionConfidence.EndInferred, open.Source,
                Join("no \"closed\" event; ended when the next app opened", duplicateOpen ? "duplicate open event ignored" : null));
        return new AppSession(open.DeviceId, open.App, open.TsUtcMs, open.TsUtcMs, SessionConfidence.Unknown, open.Source,
            $"no \"closed\" event and the next event came {(nextTs - open.TsUtcMs) / 60_000} min later; duration unknown");
    }

    private static string? Join(string? a, string? b) => a == null ? b : b == null ? a : a + "; " + b;

    /// <summary>Per-app totals inside [fromMs, toMs): sessions crossing midnight count only their part in the day.</summary>
    public static List<AppTotal> Totals(IEnumerable<AppSession> sessions, long fromMs, long toMs)
    {
        return sessions
            .Where(s => s.EndUtcMs >= fromMs && s.StartUtcMs < toMs)
            .GroupBy(s => s.App, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                long known = 0, inferred = 0;
                int unknown = 0;
                foreach (var s in g)
                {
                    if (!s.HasDuration) { unknown++; continue; }
                    long ms = Math.Max(0, Math.Min(s.EndUtcMs, toMs) - Math.Max(s.StartUtcMs, fromMs));
                    if (s.Confidence == SessionConfidence.Complete) known += ms; else inferred += ms;
                }
                return new AppTotal(g.First().App, known, inferred, g.Count(), unknown);
            })
            .OrderByDescending(t => t.TotalMs)
            .ToList();
    }

    /// <summary>Time inside [fromMs, toMs) when an iPhone app session and Windows activity overlap.</summary>
    public static long OverlapMs(IEnumerable<AppSession> sessions, IEnumerable<FocusRecord> windowsFocus, long fromMs, long toMs)
    {
        var a = Merge(sessions.Where(s => s.HasDuration).Select(s => (s.StartUtcMs, s.EndUtcMs)), fromMs, toMs);
        var b = Merge(windowsFocus.Select(f => (f.StartUtcMs, f.EndUtcMs)), fromMs, toMs);
        long total = 0;
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            long s = Math.Max(a[i].Start, b[j].Start), e = Math.Min(a[i].End, b[j].End);
            if (e > s) total += e - s;
            if (a[i].End < b[j].End) i++; else j++;
        }
        return total;
    }

    private static List<(long Start, long End)> Merge(IEnumerable<(long Start, long End)> spans, long fromMs, long toMs)
    {
        var list = spans.Select(s => (Start: Math.Max(s.Start, fromMs), End: Math.Min(s.End, toMs)))
            .Where(s => s.End > s.Start).OrderBy(s => s.Start).ToList();
        var merged = new List<(long Start, long End)>();
        foreach (var s in list)
        {
            if (merged.Count > 0 && s.Start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, s.End));
            else merged.Add(s);
        }
        return merged;
    }
}
