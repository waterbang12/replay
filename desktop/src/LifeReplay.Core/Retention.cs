namespace LifeReplay.Core;

public sealed record RetentionItem(string Id, long StartUtcMs, long SizeBytes, string Status);

public static class Retention
{
    /// <summary>
    /// Picks segments to delete: first everything older than the retention period, then the oldest remaining
    /// segments until total size fits the quota. Segments still being written are never selected.
    /// retentionDays = 0 or maxBytes = 0 disables that rule.
    /// </summary>
    public static List<string> Plan(IEnumerable<RetentionItem> items, long nowMs, int retentionDays, long maxBytes)
    {
        var all = items.ToList();
        var delete = new List<string>();
        var deleted = new HashSet<string>();
        long total = all.Sum(i => Math.Max(0, i.SizeBytes));

        var candidates = all.Where(i => i.Status != SegmentStatus.Recording).OrderBy(i => i.StartUtcMs).ToList();
        if (retentionDays > 0)
        {
            long cutoff = nowMs - retentionDays * 86_400_000L;
            foreach (var i in candidates.Where(i => i.StartUtcMs < cutoff))
            {
                delete.Add(i.Id); deleted.Add(i.Id); total -= Math.Max(0, i.SizeBytes);
            }
        }
        if (maxBytes > 0)
        {
            foreach (var i in candidates)
            {
                if (total <= maxBytes) break;
                if (deleted.Contains(i.Id)) continue;
                delete.Add(i.Id); deleted.Add(i.Id); total -= Math.Max(0, i.SizeBytes);
            }
        }
        return delete;
    }
}

/// <summary>Deletes recordings from disk and database. Used by retention, the viewer and the sync ingest.</summary>
public static class RecordingDeleter
{
    public sealed record Result(int Deleted, int Skipped, long BytesFreed);

    public static Result DeleteSegments(MetadataStore store, AppPaths paths, IEnumerable<string> segmentIds)
    {
        var ids = segmentIds.Distinct().ToList();
        var segments = ids.Select(store.GetSegment).Where(s => s != null).Cast<SegmentRecord>().ToList();
        var thumbs = store.GetThumbnails(ids).ToLookup(t => t.SegmentId);
        var removed = new List<string>();
        long freed = 0;
        int skipped = 0;
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seg in segments)
        {
            if (seg.Status == SegmentStatus.Recording) { skipped++; continue; }
            var full = paths.ToFull(seg.FilePath);
            try
            {
                if (File.Exists(full)) { freed += new FileInfo(full).Length; File.Delete(full); }
                if (File.Exists(full + ".part")) File.Delete(full + ".part");
                foreach (var t in thumbs[seg.Id])
                {
                    var tp = paths.ToFull(t.FilePath);
                    if (File.Exists(tp)) File.Delete(tp);
                    dirs.Add(Path.GetDirectoryName(tp)!);
                }
                dirs.Add(Path.GetDirectoryName(full)!);
                removed.Add(seg.Id);
            }
            catch (Exception ex)
            {
                // Usually the viewer has the file open. It will be retried next time.
                Log.Warn($"Could not delete segment file {full}", ex);
                skipped++;
            }
        }
        if (removed.Count > 0) store.DeleteSegmentRows(removed);
        foreach (var d in dirs) RemoveEmptyDirs(d, paths.RecordingsDir);
        return new Result(removed.Count, skipped, freed);
    }

    private static void RemoveEmptyDirs(string dir, string stopAt)
    {
        try
        {
            var stop = Path.GetFullPath(stopAt).TrimEnd('\\', '/');
            var current = Path.GetFullPath(dir).TrimEnd('\\', '/');
            while (current.Length > stop.Length && current.StartsWith(stop, StringComparison.OrdinalIgnoreCase)
                   && Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any())
            {
                Directory.Delete(current);
                current = Path.GetDirectoryName(current)!;
            }
        }
        catch { /* best effort */ }
    }

    public static Result ApplyRetention(MetadataStore store, AppPaths paths, RecorderConfig cfg, long nowMs)
    {
        var items = store.GetSegmentsWhere("1 = 1").Select(s => new RetentionItem(s.Id, s.StartUtcMs, s.FileSize ?? 0, s.Status));
        var plan = Retention.Plan(items, nowMs, cfg.RetentionDays, (long)(cfg.MaxStorageGB * 1024 * 1024 * 1024));
        if (cfg.RetentionDays > 0)
        {
            store.DeleteGapsBefore(nowMs - cfg.RetentionDays * 86_400_000L);
            store.DeleteFocusBefore(nowMs - cfg.RetentionDays * 86_400_000L);
        }
        if (plan.Count == 0) return new Result(0, 0, 0);
        var r = DeleteSegments(store, paths, plan);
        Log.Info($"Retention: deleted {r.Deleted} segment(s), freed {TimeUtil.FormatBytes(r.BytesFreed)}, skipped {r.Skipped}");
        return r;
    }
}
