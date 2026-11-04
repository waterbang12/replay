using Microsoft.Data.Sqlite;

namespace LifeReplay.Core;

/// <summary>
/// SQLite access. Opens a pooled connection per call, so it is safe to use from any thread and from the recorder and
/// viewer processes at the same time (WAL mode, busy retries). This file has the connection, the helpers and
/// devices/displays/sessions; each feature's tables are in a partial file in that feature's folder
/// (Recording/, Activity/, Phone/, Storage/MetadataStore.AnalysisCache.cs).
/// </summary>
public sealed partial class MetadataStore
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
}
