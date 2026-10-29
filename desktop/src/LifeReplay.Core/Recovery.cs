namespace LifeReplay.Core;

public sealed record RecoveryReport(int Recovered, int Lost, int SessionsClosed, int GapsClosed);

/// <summary>
/// Runs at recorder startup. Anything still marked "recording" belongs to a previous run that ended without
/// a clean shutdown (crash, power loss, kill). Finalized segments are never touched.
/// </summary>
public static class Recovery
{
    public static RecoveryReport Run(MetadataStore store, AppPaths paths, string deviceId, long nowMs)
    {
        var device = store.GetDevice(deviceId);
        long lastSeen = Math.Min(device?.LastSeenUtcMs ?? nowMs, nowMs);
        int recovered = 0, lost = 0;
        long? lostFrom = null;

        foreach (var seg in store.GetSegmentsWhere("device_id = $d AND status = $s", ("$d", deviceId), ("$s", SegmentStatus.Recording)))
        {
            var full = paths.ToFull(seg.FilePath);
            var part = full + ".part";
            Mp4Info? info = null;
            string? usedPath = null;
            if (File.Exists(full)) { info = Mp4Inspector.Inspect(full); usedPath = full; }
            else if (File.Exists(part)) { info = Mp4Inspector.Inspect(part); usedPath = part; }

            if (info is { IsValid: true } && info.DurationMs > 0 && usedPath != null)
            {
                // The file was completed but the DB update didn't happen (crash between the two steps).
                if (usedPath == part) File.Move(part, full, overwrite: false);
                seg.Status = SegmentStatus.Finalized;
                seg.DurationMs = info.DurationMs;
                seg.EndUtcMs = seg.StartUtcMs + info.DurationMs;
                seg.FileSize = new FileInfo(full).Length;
                seg.FinalizedUtcMs = nowMs;
                seg.Error = "recovered after unclean shutdown";
                store.UpdateSegment(seg);
                recovered++;
                Log.Info($"Recovery: segment {seg.Id} was complete on disk, marked finalized");
            }
            else
            {
                // MP4 written by Media Foundation has its index (moov) at the end, so a cut-off file is unplayable.
                // The encoder writes continuously, so the file's last write time is a good estimate of the crash time.
                long end = Math.Max(seg.StartUtcMs, lastSeen);
                if (usedPath != null)
                {
                    try
                    {
                        long lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(usedPath)).ToUnixTimeMilliseconds();
                        if (lastWrite > end && lastWrite <= nowMs) end = lastWrite;
                        File.Delete(usedPath);
                    }
                    catch (Exception ex) { Log.Warn($"Recovery: could not delete {usedPath}", ex); }
                }
                lastSeen = Math.Max(lastSeen, end);
                seg.Status = SegmentStatus.Lost;
                seg.EndUtcMs = end;
                seg.DurationMs = seg.EndUtcMs - seg.StartUtcMs;
                seg.FileSize = 0;
                seg.Error = info?.Error ?? "file missing after unclean shutdown";
                store.UpdateSegment(seg);
                lost++;
                lostFrom = lostFrom is null ? seg.StartUtcMs : Math.Min(lostFrom.Value, seg.StartUtcMs);
                Log.Warn($"Recovery: segment {seg.Id} could not be recovered ({seg.Error})");
            }
        }

        int sessionsClosed = 0;
        foreach (var s in store.GetSessions(SessionStatus.Active).Where(s => s.DeviceId == deviceId))
        {
            store.EndSession(s.Id, Math.Max(s.StartUtcMs, lastSeen), SessionStatus.Interrupted, "unclean shutdown");
            sessionsClosed++;
        }

        int gapsClosed = store.CloseOpenGaps(deviceId, lastSeen);

        if (sessionsClosed > 0 || lost > 0)
        {
            long from = lostFrom ?? lastSeen;
            if (nowMs - from > 1000)
                store.InsertGap(new GapRecord { DeviceId = deviceId, StartUtcMs = from, EndUtcMs = nowMs, Reason = GapReasons.Crash, Detail = $"{lost} unfinished segment(s) lost" });
        }
        return new RecoveryReport(recovered, lost, sessionsClosed, gapsClosed);
    }
}
