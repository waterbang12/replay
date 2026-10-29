using System.Security.Cryptography;

namespace LifeReplay.Core;

/// <summary>Metadata the iPhone app sends with each segment. Matches shared/segment-metadata.schema.json.</summary>
public sealed class IngestSegmentMetadata
{
    public string SegmentId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "iPhone";
    public long StartUtcMs { get; set; }
    public long EndUtcMs { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public int FrameCount { get; set; }
    public string Sha256 { get; set; } = "";
    /// <summary>"finalized" normally; "interrupted" if the extension was stopped mid-segment but the file is still readable.</summary>
    public string? EndReason { get; set; }
}

public sealed record IngestGap(long StartUtcMs, long EndUtcMs, string? Detail);

public enum IngestOutcome { Created, AlreadyExists, Rejected }

public sealed record IngestResult(IngestOutcome Outcome, string Message, string? SegmentId = null);

/// <summary>Validates an uploaded iPhone segment and files it under recordings/YYYY-MM-DD/iphone/.</summary>
public sealed class IngestService
{
    private readonly MetadataStore _store;
    private readonly AppPaths _paths;
    private static readonly object WriteGate = new();
    public const long MaxSegmentDurationMs = 2 * 3600_000L;

    public IngestService(MetadataStore store, AppPaths paths)
    {
        _store = store;
        _paths = paths;
    }

    public static string IosDisplayId(string deviceId) => "ios-" + deviceId;

    public static string? Validate(IngestSegmentMetadata m, long nowMs)
    {
        // GUIDs only: they end up in file names, so this also blocks path tricks.
        if (!Guid.TryParse(m.SegmentId, out _)) return "segmentId must be a GUID";
        if (!Guid.TryParse(m.SessionId, out _)) return "sessionId must be a GUID";
        if (!Guid.TryParse(m.DeviceId, out _)) return "deviceId must be a GUID";
        if (m.StartUtcMs < 946684800000L) return "startUtcMs is before 2000-01-01";
        if (m.StartUtcMs > nowMs + 10 * 60_000) return "startUtcMs is in the future";
        if (m.EndUtcMs <= m.StartUtcMs) return "endUtcMs must be after startUtcMs";
        if (m.EndUtcMs - m.StartUtcMs > MaxSegmentDurationMs) return "segment longer than 2 hours";
        if (m.Width <= 0 || m.Height <= 0 || m.Width > 8192 || m.Height > 8192) return "invalid dimensions";
        if (m.Sha256.Length != 64 || !m.Sha256.All(Uri.IsHexDigit)) return "sha256 must be 64 hex characters";
        if (m.DeviceName.Length > 100) m.DeviceName = m.DeviceName[..100];
        return null;
    }

    public static string Sha256Hex(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>Moves <paramref name="tempFile"/> into the recordings folder if valid; the temp file is consumed either way.</summary>
    public IngestResult IngestSegment(IngestSegmentMetadata m, string tempFile)
    {
        try
        {
            long now = TimeUtil.NowMs();
            var error = Validate(m, now);
            if (error != null) return new IngestResult(IngestOutcome.Rejected, error);

            var existing = _store.GetSegment(m.SegmentId);
            if (existing is { Status: SegmentStatus.Finalized })
                return new IngestResult(IngestOutcome.AlreadyExists, "segment already stored", m.SegmentId);

            var hash = Sha256Hex(tempFile);
            if (!string.Equals(hash, m.Sha256, StringComparison.OrdinalIgnoreCase))
                return new IngestResult(IngestOutcome.Rejected, "sha256 mismatch (upload corrupted)");

            var info = Mp4Inspector.Inspect(tempFile);
            if (!info.IsValid) return new IngestResult(IngestOutcome.Rejected, "not a valid MP4: " + info.Error);

            var local = TimeUtil.ToLocal(m.StartUtcMs);
            var dir = Path.Combine(_paths.DayPlatformDir(local, "iphone"), m.DeviceId[..8]);
            Directory.CreateDirectory(dir);
            var full = Path.Combine(dir, $"{local:HH-mm-ss}_{m.SegmentId[..8]}.mp4");

            lock (WriteGate)
            {
                if (File.Exists(full)) File.Delete(full); // leftover from an earlier failed attempt
                File.Move(tempFile, full);
                long fileSize = new FileInfo(full).Length;
                long duration = m.EndUtcMs - m.StartUtcMs;

                using var c = _store.Open();
                using var tx = c.BeginTransaction();
                void Run(string sql, params (string, object?)[] args)
                {
                    using var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = sql;
                    foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                Run("""
                    INSERT INTO devices(id, name, platform, created_utc, last_seen_utc) VALUES($id, $name, 'ios', $now, $now)
                    ON CONFLICT(id) DO UPDATE SET name = excluded.name, last_seen_utc = excluded.last_seen_utc
                    """, ("$id", m.DeviceId), ("$name", m.DeviceName), ("$now", now));
                Run("""
                    INSERT INTO displays(id, device_id, name, width, height, left_px, top_px, is_primary, first_seen_utc, last_seen_utc)
                    VALUES($id, $dev, $name, $w, $h, 0, 0, 1, $now, $now)
                    ON CONFLICT(id) DO UPDATE SET width = excluded.width, height = excluded.height, last_seen_utc = excluded.last_seen_utc
                    """, ("$id", IosDisplayId(m.DeviceId)), ("$dev", m.DeviceId), ("$name", m.DeviceName + " screen"), ("$w", m.Width), ("$h", m.Height), ("$now", now));
                Run("""
                    INSERT INTO sessions(id, device_id, start_utc, end_utc, status, end_reason, app_version) VALUES($id, $dev, $start, $end, 'ended', $reason, 'ios')
                    ON CONFLICT(id) DO UPDATE SET start_utc = MIN(sessions.start_utc, excluded.start_utc), end_utc = MAX(COALESCE(sessions.end_utc, 0), excluded.end_utc)
                    """, ("$id", m.SessionId), ("$dev", m.DeviceId), ("$start", m.StartUtcMs), ("$end", m.EndUtcMs), ("$reason", m.EndReason));
                Run("DELETE FROM segments WHERE id = $id", ("$id", m.SegmentId));
                MetadataStore.InsertSegment(c, tx, new SegmentRecord
                {
                    Id = m.SegmentId, SessionId = m.SessionId, DeviceId = m.DeviceId, DisplayId = IosDisplayId(m.DeviceId),
                    StartUtcMs = m.StartUtcMs, EndUtcMs = m.EndUtcMs, DurationMs = duration, FilePath = _paths.ToRelative(full),
                    FileSize = fileSize, Width = m.Width, Height = m.Height, SourceWidth = m.Width, SourceHeight = m.Height,
                    Fps = m.Fps, Bitrate = duration > 0 ? (int)Math.Min(int.MaxValue, fileSize * 8000 / duration) : 0,
                    Encoder = "ios-avassetwriter", Status = SegmentStatus.Finalized, FrameCount = m.FrameCount,
                    Error = m.EndReason == "interrupted" ? "iPhone broadcast ended mid-segment" : null,
                    CreatedUtcMs = now, FinalizedUtcMs = now,
                });
                tx.Commit();
            }
            Log.Info($"Ingest: stored iPhone segment {m.SegmentId} ({TimeUtil.FormatDuration(m.EndUtcMs - m.StartUtcMs)})");
            return new IngestResult(IngestOutcome.Created, "stored", m.SegmentId);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    public IngestResult IngestThumbnail(string segmentId, long tsUtcMs, byte[] jpeg)
    {
        if (!Guid.TryParse(segmentId, out _)) return new IngestResult(IngestOutcome.Rejected, "segmentId must be a GUID");
        if (jpeg.Length < 4 || jpeg.Length > 2_000_000 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return new IngestResult(IngestOutcome.Rejected, "not a JPEG (or larger than 2 MB)");
        var seg = _store.GetSegment(segmentId);
        if (seg == null) return new IngestResult(IngestOutcome.Rejected, "unknown segment; upload the video first");
        var dir = Path.Combine(Path.GetDirectoryName(_paths.ToFull(seg.FilePath))!, "thumbs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{segmentId[..8]}_{Math.Max(0, tsUtcMs - seg.StartUtcMs) / 1000:D5}.jpg");
        File.WriteAllBytes(path, jpeg);
        _store.InsertThumbnail(new ThumbnailRecord { SegmentId = segmentId, TsUtcMs = tsUtcMs, FilePath = _paths.ToRelative(path) });
        return new IngestResult(IngestOutcome.Created, "stored", segmentId);
    }

    public IngestResult IngestGaps(string deviceId, IEnumerable<IngestGap> gaps)
    {
        if (!Guid.TryParse(deviceId, out _)) return new IngestResult(IngestOutcome.Rejected, "deviceId must be a GUID");
        int n = 0;
        var existing = _store.GetGapsInRange(0, long.MaxValue / 2).Where(g => g.DeviceId == deviceId)
            .Select(g => (g.StartUtcMs, g.EndUtcMs)).ToHashSet();
        foreach (var g in gaps.Take(1000))
        {
            if (g.EndUtcMs <= g.StartUtcMs) continue;
            if (existing.Contains((g.StartUtcMs, g.EndUtcMs))) continue;
            _store.InsertGap(new GapRecord { DeviceId = deviceId, DisplayId = IosDisplayId(deviceId), StartUtcMs = g.StartUtcMs, EndUtcMs = g.EndUtcMs, Reason = GapReasons.IosInterrupted, Detail = g.Detail?.Length > 200 ? g.Detail[..200] : g.Detail });
            n++;
        }
        return new IngestResult(IngestOutcome.Created, $"{n} gap(s) stored");
    }
}
