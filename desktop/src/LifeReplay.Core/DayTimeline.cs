namespace LifeReplay.Core;

/// <summary>One row on the timeline: a single Windows display or an iPhone screen.</summary>
public sealed class TimelineLane
{
    public required string DisplayId { get; init; }
    public required string DeviceId { get; init; }
    public required string Platform { get; init; }
    public required string Label { get; init; }
    public int SortLeft { get; init; }
    public int SortTop { get; init; }
    public List<SegmentRecord> Segments { get; } = [];

    /// <summary>Playable segment covering t, or null (binary search; segments sorted by start).</summary>
    public SegmentRecord? SegmentAt(long t, long nowMs)
    {
        int lo = 0, hi = Segments.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Segments[mid].StartUtcMs <= t) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        // Walk back a little in case of overlaps (e.g. a finalized segment overlapping a lost one).
        for (int i = found; i >= 0 && i >= found - 3; i--)
        {
            var s = Segments[i];
            if (s.IsPlayable && s.StartUtcMs <= t && t < s.EffectiveEnd(nowMs)) return s;
        }
        return null;
    }

    /// <summary>Ranges in [from, to) not covered by a playable segment.</summary>
    public List<(long Start, long End)> Uncovered(long from, long to, long nowMs)
    {
        var result = new List<(long, long)>();
        long cursor = from;
        foreach (var s in Segments.Where(s => s.IsPlayable).OrderBy(s => s.StartUtcMs))
        {
            long end = s.EffectiveEnd(nowMs);
            if (end <= cursor) continue;
            if (s.StartUtcMs >= to) break;
            if (s.StartUtcMs > cursor) result.Add((cursor, Math.Min(s.StartUtcMs, to)));
            cursor = Math.Max(cursor, end);
            if (cursor >= to) break;
        }
        if (cursor < to) result.Add((cursor, to));
        // Ignore sub-second seams between consecutive segments.
        return result.Where(r => r.Item2 - r.Item1 >= 1000).ToList();
    }
}

/// <summary>Everything the viewer needs for one local day: lanes of segments plus explicit gap reasons.</summary>
public sealed class DayTimeline
{
    public long DayStartMs { get; }
    public long DayEndMs { get; }
    public long NowMs { get; }
    public List<TimelineLane> Lanes { get; }
    public List<GapRecord> Gaps { get; }
    /// <summary>Which app/window was in front (window-title log), sorted by start.</summary>
    public List<FocusRecord> Focus { get; set; } = [];

    public FocusRecord? FocusAt(long t)
    {
        int lo = 0, hi = Focus.Count - 1, found = -1;
        while (lo <= hi) { int mid = (lo + hi) / 2; if (Focus[mid].StartUtcMs <= t) { found = mid; lo = mid + 1; } else hi = mid - 1; }
        return found >= 0 && t < Focus[found].EndUtcMs ? Focus[found] : null;
    }

    public DayTimeline(long dayStartMs, long dayEndMs, long nowMs, List<TimelineLane> lanes, List<GapRecord> gaps)
    {
        DayStartMs = dayStartMs; DayEndMs = dayEndMs; NowMs = nowMs; Lanes = lanes; Gaps = gaps;
    }

    public static DayTimeline Build(long dayStartMs, long dayEndMs, long nowMs, IEnumerable<SegmentRecord> segments,
        IEnumerable<GapRecord> gaps, IEnumerable<DisplayRecord> displays, IEnumerable<DeviceRecord> devices)
    {
        var displayById = displays.ToDictionary(d => d.Id);
        var deviceById = devices.ToDictionary(d => d.Id);
        var lanes = new Dictionary<string, TimelineLane>();
        foreach (var seg in segments.OrderBy(s => s.StartUtcMs))
        {
            if (!lanes.TryGetValue(seg.DisplayId, out var lane))
            {
                displayById.TryGetValue(seg.DisplayId, out var disp);
                deviceById.TryGetValue(seg.DeviceId, out var dev);
                var platform = dev?.Platform ?? (seg.DisplayId.StartsWith("ios-") ? Platforms.Ios : Platforms.Windows);
                var label = platform == Platforms.Ios
                    ? (dev?.Name ?? "iPhone")
                    : $"{disp?.Name ?? seg.DisplayId}" + (disp != null ? $" ({disp.Width}×{disp.Height})" : "");
                lane = new TimelineLane
                {
                    DisplayId = seg.DisplayId, DeviceId = seg.DeviceId, Platform = platform, Label = label,
                    SortLeft = disp?.Left ?? 0, SortTop = disp?.Top ?? 0,
                };
                lanes[seg.DisplayId] = lane;
            }
            lane.Segments.Add(seg);
        }
        var ordered = lanes.Values
            .OrderBy(l => l.Platform == Platforms.Windows ? 0 : 1)
            .ThenBy(l => l.SortLeft).ThenBy(l => l.SortTop).ThenBy(l => l.Label)
            .ToList();
        return new DayTimeline(dayStartMs, dayEndMs, nowMs, ordered, gaps.ToList());
    }

    public bool AnyCoverage(long t) => Lanes.Any(l => l.SegmentAt(t, NowMs) != null);

    /// <summary>First playable segment start strictly after t (any lane), or null.</summary>
    public long? NextSegmentStart(long t) => Lanes.SelectMany(l => l.Segments).Where(s => s.IsPlayable && s.StartUtcMs > t)
        .Select(s => (long?)s.StartUtcMs).Min();

    public long? PreviousSegmentStart(long t) => Lanes.SelectMany(l => l.Segments).Where(s => s.IsPlayable && s.StartUtcMs < t - 1500)
        .Select(s => (long?)s.StartUtcMs).Max();

    public long? FirstRecordingStart() => Lanes.SelectMany(l => l.Segments).Where(s => s.IsPlayable).Select(s => (long?)s.StartUtcMs).Min();

    /// <summary>
    /// True if something changed on any screen in the activity bucket containing t. Segments without activity
    /// data (e.g. iPhone) count as active.
    /// </summary>
    public bool IsActive(long t)
    {
        foreach (var lane in Lanes)
        {
            var s = lane.SegmentAt(t, NowMs);
            if (s == null) continue;
            var buckets = Activity.Decode(s.Activity);
            if (buckets.Length == 0) return true;
            int i = (int)((t - s.StartUtcMs) / Activity.BucketMs);
            if (i < 0 || i >= buckets.Length || buckets[i] > 0) return true;
        }
        return false;
    }

    /// <summary>Next time ≥ t that is covered and active, stepping bucket by bucket (and jumping over gaps).</summary>
    public long? NextActiveTime(long t, long limit)
    {
        long cursor = t;
        int guard = 0;
        while (cursor < limit && guard++ < 100_000)
        {
            if (!AnyCoverage(cursor))
            {
                var next = NextSegmentStart(cursor);
                if (next == null || next >= limit) return null;
                cursor = next.Value;
                continue;
            }
            if (IsActive(cursor)) return cursor;
            // Jump to the start of the next 10 s bucket of the earliest covering segment.
            long step = Activity.BucketMs;
            foreach (var lane in Lanes)
            {
                var s = lane.SegmentAt(cursor, NowMs);
                if (s == null) continue;
                long into = (cursor - s.StartUtcMs) % Activity.BucketMs;
                step = Math.Min(step, Activity.BucketMs - into);
            }
            cursor += Math.Max(1, step);
        }
        return null;
    }

    /// <summary>Explicit gap (paused, locked, ...) of the lane's device that covers t, if any.</summary>
    public GapRecord? GapAt(TimelineLane lane, long t) =>
        Gaps.LastOrDefault(g => g.DeviceId == lane.DeviceId && (g.DisplayId == null || g.DisplayId == lane.DisplayId)
                                && g.StartUtcMs <= t && t < (g.EndUtcMs ?? NowMs));
}
