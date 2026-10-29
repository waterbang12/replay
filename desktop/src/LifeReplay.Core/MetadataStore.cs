using Microsoft.Data.Sqlite;

namespace LifeReplay.Core;

/// <summary>
/// SQLite access for recording metadata. Opens a pooled connection per call, so it is safe to use from any
/// thread and from the recorder and viewer processes at the same time (WAL mode, busy retries).
/// </summary>
public sealed class MetadataStore
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public MetadataStore(string databasePath)
    {
        DatabasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30, // seconds to retry while the other process holds a write lock
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    public void Migrate()
    {
        using var c = Open();
        Migrations.Apply(c);
    }

    // ---------------------------------------------------------------- helpers

    private static SqliteCommand Cmd(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private int Exec(string sql, params (string, object?)[] args)
    {
        using var c = Open();
        using var cmd = Cmd(c, sql, null, args);
        return cmd.ExecuteNonQuery();
    }

    private static long? NLong(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static string? NStr(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // ---------------------------------------------------------------- devices & displays

    public void UpsertDevice(DeviceRecord d) => Exec("""
        INSERT INTO devices(id, name, platform, created_utc, last_seen_utc) VALUES($id, $name, $platform, $created, $seen)
        ON CONFLICT(id) DO UPDATE SET name = excluded.name, last_seen_utc = COALESCE(excluded.last_seen_utc, devices.last_seen_utc)
        """, ("$id", d.Id), ("$name", d.Name), ("$platform", d.Platform), ("$created", d.CreatedUtcMs), ("$seen", d.LastSeenUtcMs));

    public void Heartbeat(string deviceId, long nowMs) =>
        Exec("UPDATE devices SET last_seen_utc = $t WHERE id = $id", ("$t", nowMs), ("$id", deviceId));

    public DeviceRecord? GetDevice(string id) => GetDevices().FirstOrDefault(d => d.Id == id);

    public List<DeviceRecord> GetDevices()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT id, name, platform, created_utc, last_seen_utc FROM devices ORDER BY platform, name");
        using var r = cmd.ExecuteReader();
        var list = new List<DeviceRecord>();
        while (r.Read())
            list.Add(new DeviceRecord { Id = r.GetString(0), Name = r.GetString(1), Platform = r.GetString(2), CreatedUtcMs = r.GetInt64(3), LastSeenUtcMs = NLong(r, 4) });
        return list;
    }

    public void UpsertDisplay(DisplayRecord d) => Exec("""
        INSERT INTO displays(id, device_id, name, width, height, left_px, top_px, is_primary, first_seen_utc, last_seen_utc)
        VALUES($id, $dev, $name, $w, $h, $l, $t, $p, $first, $last)
        ON CONFLICT(id) DO UPDATE SET name = excluded.name, width = excluded.width, height = excluded.height,
            left_px = excluded.left_px, top_px = excluded.top_px, is_primary = excluded.is_primary, last_seen_utc = excluded.last_seen_utc
        """, ("$id", d.Id), ("$dev", d.DeviceId), ("$name", d.Name), ("$w", d.Width), ("$h", d.Height), ("$l", d.Left), ("$t", d.Top),
        ("$p", d.IsPrimary ? 1 : 0), ("$first", d.FirstSeenUtcMs), ("$last", d.LastSeenUtcMs));

    public List<DisplayRecord> GetDisplays()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT id, device_id, name, width, height, left_px, top_px, is_primary, first_seen_utc, last_seen_utc FROM displays");
        using var r = cmd.ExecuteReader();
        var list = new List<DisplayRecord>();
        while (r.Read())
            list.Add(new DisplayRecord
            {
                Id = r.GetString(0), DeviceId = r.GetString(1), Name = r.GetString(2), Width = r.GetInt32(3), Height = r.GetInt32(4),
                Left = r.GetInt32(5), Top = r.GetInt32(6), IsPrimary = r.GetInt32(7) != 0, FirstSeenUtcMs = r.GetInt64(8), LastSeenUtcMs = r.GetInt64(9),
            });
        return list;
    }

    // ---------------------------------------------------------------- sessions

    public void InsertSession(SessionRecord s) => Exec("""
        INSERT INTO sessions(id, device_id, start_utc, end_utc, status, end_reason, app_version)
        VALUES($id, $dev, $start, $end, $status, $reason, $ver)
        """, ("$id", s.Id), ("$dev", s.DeviceId), ("$start", s.StartUtcMs), ("$end", s.EndUtcMs), ("$status", s.Status), ("$reason", s.EndReason), ("$ver", s.AppVersion));

    public void EndSession(string id, long endMs, string status, string? reason) =>
        Exec("UPDATE sessions SET end_utc = $end, status = $status, end_reason = $reason WHERE id = $id",
            ("$end", endMs), ("$status", status), ("$reason", reason), ("$id", id));

    public List<SessionRecord> GetSessions(string? status = null)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT id, device_id, start_utc, end_utc, status, end_reason, app_version FROM sessions" +
                               (status != null ? " WHERE status = $s" : "") + " ORDER BY start_utc", null, ("$s", status));
        using var r = cmd.ExecuteReader();
        var list = new List<SessionRecord>();
        while (r.Read())
            list.Add(new SessionRecord { Id = r.GetString(0), DeviceId = r.GetString(1), StartUtcMs = r.GetInt64(2), EndUtcMs = NLong(r, 3), Status = r.GetString(4), EndReason = NStr(r, 5), AppVersion = NStr(r, 6) });
        return list;
    }

    // ---------------------------------------------------------------- segments

    private const string SegmentColumns = "id, session_id, device_id, display_id, start_utc, end_utc, duration_ms, file_path, file_size, width, height, source_width, source_height, fps, bitrate, encoder, status, frame_count, activity, error, created_utc, finalized_utc";

    private static SegmentRecord ReadSegment(SqliteDataReader r) => new()
    {
        Id = r.GetString(0), SessionId = r.GetString(1), DeviceId = r.GetString(2), DisplayId = r.GetString(3),
        StartUtcMs = r.GetInt64(4), EndUtcMs = NLong(r, 5), DurationMs = NLong(r, 6), FilePath = r.GetString(7), FileSize = NLong(r, 8),
        Width = r.GetInt32(9), Height = r.GetInt32(10), SourceWidth = r.GetInt32(11), SourceHeight = r.GetInt32(12), Fps = r.GetDouble(13),
        Bitrate = r.GetInt32(14), Encoder = r.GetString(15), Status = r.GetString(16), FrameCount = r.GetInt32(17), Activity = NStr(r, 18),
        Error = NStr(r, 19), CreatedUtcMs = r.GetInt64(20), FinalizedUtcMs = NLong(r, 21),
    };

    public void InsertSegment(SegmentRecord s)
    {
        using var c = Open();
        InsertSegment(c, null, s);
    }

    public static void InsertSegment(SqliteConnection c, SqliteTransaction? tx, SegmentRecord s)
    {
        using var cmd = Cmd(c, $"""
            INSERT INTO segments({SegmentColumns}) VALUES($id, $ses, $dev, $disp, $start, $end, $dur, $path, $size, $w, $h, $sw, $sh, $fps, $br, $enc, $status, $fc, $act, $err, $created, $fin)
            """, tx,
            ("$id", s.Id), ("$ses", s.SessionId), ("$dev", s.DeviceId), ("$disp", s.DisplayId), ("$start", s.StartUtcMs), ("$end", s.EndUtcMs),
            ("$dur", s.DurationMs), ("$path", s.FilePath), ("$size", s.FileSize), ("$w", s.Width), ("$h", s.Height), ("$sw", s.SourceWidth),
            ("$sh", s.SourceHeight), ("$fps", s.Fps), ("$br", s.Bitrate), ("$enc", s.Encoder), ("$status", s.Status), ("$fc", s.FrameCount),
            ("$act", s.Activity), ("$err", s.Error), ("$created", s.CreatedUtcMs), ("$fin", s.FinalizedUtcMs));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Writes the final state of a segment (times, size, status) in one statement.</summary>
    public void UpdateSegment(SegmentRecord s) => Exec("""
        UPDATE segments SET start_utc = $start, end_utc = $end, duration_ms = $dur, file_path = $path, file_size = $size, status = $status,
            frame_count = $fc, activity = $act, error = $err, finalized_utc = $fin WHERE id = $id
        """, ("$start", s.StartUtcMs), ("$end", s.EndUtcMs), ("$dur", s.DurationMs), ("$path", s.FilePath), ("$size", s.FileSize), ("$status", s.Status),
        ("$fc", s.FrameCount), ("$act", s.Activity), ("$err", s.Error), ("$fin", s.FinalizedUtcMs), ("$id", s.Id));

    public SegmentRecord? GetSegment(string id)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT {SegmentColumns} FROM segments WHERE id = $id", null, ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSegment(r) : null;
    }

    /// <summary>Segments overlapping [startMs, endMs), ordered by start.</summary>
    public List<SegmentRecord> GetSegmentsInRange(long startMs, long endMs)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"""
            SELECT {SegmentColumns} FROM segments
            WHERE start_utc < $end AND COALESCE(end_utc, $now) > $start
            ORDER BY start_utc
            """, null, ("$start", startMs), ("$end", endMs), ("$now", TimeUtil.NowMs()));
        return ReadAll(cmd);
    }

    public List<SegmentRecord> GetSegmentsWhere(string whereSql, params (string, object?)[] args)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT {SegmentColumns} FROM segments WHERE {whereSql} ORDER BY start_utc", null, args);
        return ReadAll(cmd);
    }

    private static List<SegmentRecord> ReadAll(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<SegmentRecord>();
        while (r.Read()) list.Add(ReadSegment(r));
        return list;
    }

    /// <summary>Start times of all segments, used to mark days that have recordings.</summary>
    public List<long> GetAllSegmentStarts()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT start_utc FROM segments ORDER BY start_utc");
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    public long GetTotalStorageBytes()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT COALESCE(SUM(file_size), 0) FROM segments");
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ---------------------------------------------------------------- gaps

    public long InsertGap(GapRecord g)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            INSERT INTO gaps(device_id, display_id, start_utc, end_utc, reason, detail) VALUES($dev, $disp, $start, $end, $reason, $detail);
            SELECT last_insert_rowid();
            """, null, ("$dev", g.DeviceId), ("$disp", g.DisplayId), ("$start", g.StartUtcMs), ("$end", g.EndUtcMs), ("$reason", g.Reason), ("$detail", g.Detail));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void CloseGap(long id, long endMs) =>
        Exec("UPDATE gaps SET end_utc = MAX(start_utc, $end) WHERE id = $id AND end_utc IS NULL", ("$end", endMs), ("$id", id));

    /// <summary>Closes every still-open gap of a device (e.g. a "stopped" gap when the recorder starts again).</summary>
    public int CloseOpenGaps(string deviceId, long endMs) =>
        Exec("UPDATE gaps SET end_utc = MAX(start_utc, $end) WHERE device_id = $dev AND end_utc IS NULL", ("$end", endMs), ("$dev", deviceId));

    public List<GapRecord> GetGapsInRange(long startMs, long endMs)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT id, device_id, display_id, start_utc, end_utc, reason, detail FROM gaps
            WHERE start_utc < $end AND COALESCE(end_utc, $now) > $start ORDER BY start_utc
            """, null, ("$start", startMs), ("$end", endMs), ("$now", TimeUtil.NowMs()));
        using var r = cmd.ExecuteReader();
        var list = new List<GapRecord>();
        while (r.Read())
            list.Add(new GapRecord { Id = r.GetInt64(0), DeviceId = r.GetString(1), DisplayId = NStr(r, 2), StartUtcMs = r.GetInt64(3), EndUtcMs = NLong(r, 4), Reason = r.GetString(5), Detail = NStr(r, 6) });
        return list;
    }

    // ---------------------------------------------------------------- thumbnails

    public void InsertThumbnail(ThumbnailRecord t) =>
        Exec("INSERT OR REPLACE INTO thumbnails(segment_id, ts_utc, file_path) VALUES($s, $t, $p)", ("$s", t.SegmentId), ("$t", t.TsUtcMs), ("$p", t.FilePath));

    public List<ThumbnailRecord> GetThumbnails(IEnumerable<string> segmentIds)
    {
        var ids = segmentIds.Distinct().ToList();
        var list = new List<ThumbnailRecord>();
        if (ids.Count == 0) return list;
        using var c = Open();
        foreach (var chunk in ids.Chunk(400))
        {
            using var cmd = c.CreateCommand();
            var names = new List<string>();
            for (int i = 0; i < chunk.Length; i++) { names.Add("$p" + i); cmd.Parameters.AddWithValue("$p" + i, chunk[i]); }
            cmd.CommandText = $"SELECT segment_id, ts_utc, file_path FROM thumbnails WHERE segment_id IN ({string.Join(',', names)}) ORDER BY ts_utc";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new ThumbnailRecord { SegmentId = r.GetString(0), TsUtcMs = r.GetInt64(1), FilePath = r.GetString(2) });
        }
        return list;
    }

    // ---------------------------------------------------------------- deletion

    /// <summary>Removes segment and thumbnail rows in one transaction. Files must be deleted by the caller first.</summary>
    public void DeleteSegmentRows(IEnumerable<string> segmentIds)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var id in segmentIds)
        {
            using (var a = Cmd(c, "DELETE FROM thumbnails WHERE segment_id = $id", tx, ("$id", id))) a.ExecuteNonQuery();
            using (var b = Cmd(c, "DELETE FROM segments WHERE id = $id", tx, ("$id", id))) b.ExecuteNonQuery();
        }
        // Drop sessions that no longer have segments and are not running.
        using (var s = Cmd(c, "DELETE FROM sessions WHERE status <> 'active' AND id NOT IN (SELECT DISTINCT session_id FROM segments)", tx)) s.ExecuteNonQuery();
        tx.Commit();
    }

    public int DeleteGapsBefore(long utcMs) => Exec("DELETE FROM gaps WHERE end_utc IS NOT NULL AND end_utc < $t", ("$t", utcMs));
}
