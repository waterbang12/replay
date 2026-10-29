using Microsoft.Data.Sqlite;

namespace LifeReplay.Core;

/// <summary>
/// Forward-only schema migrations tracked with PRAGMA user_version. Each migration runs in its own transaction.
/// Never edit a shipped migration: append a new one.
/// </summary>
public static class Migrations
{
    public static readonly (int Version, string Sql)[] All =
    [
        (1, """
            CREATE TABLE devices (
                id            TEXT PRIMARY KEY,
                name          TEXT NOT NULL,
                platform      TEXT NOT NULL,
                created_utc   INTEGER NOT NULL,
                last_seen_utc INTEGER
            );
            CREATE TABLE displays (
                id             TEXT PRIMARY KEY,
                device_id      TEXT NOT NULL REFERENCES devices(id),
                name           TEXT NOT NULL,
                width          INTEGER NOT NULL,
                height         INTEGER NOT NULL,
                left_px        INTEGER NOT NULL DEFAULT 0,
                top_px         INTEGER NOT NULL DEFAULT 0,
                is_primary     INTEGER NOT NULL DEFAULT 0,
                first_seen_utc INTEGER NOT NULL,
                last_seen_utc  INTEGER NOT NULL
            );
            CREATE TABLE sessions (
                id          TEXT PRIMARY KEY,
                device_id   TEXT NOT NULL REFERENCES devices(id),
                start_utc   INTEGER NOT NULL,
                end_utc     INTEGER,
                status      TEXT NOT NULL,
                end_reason  TEXT,
                app_version TEXT
            );
            CREATE INDEX ix_sessions_device_start ON sessions(device_id, start_utc);
            CREATE TABLE segments (
                id            TEXT PRIMARY KEY,
                session_id    TEXT NOT NULL REFERENCES sessions(id),
                device_id     TEXT NOT NULL REFERENCES devices(id),
                display_id    TEXT NOT NULL,
                start_utc     INTEGER NOT NULL,
                end_utc       INTEGER,
                duration_ms   INTEGER,
                file_path     TEXT NOT NULL,
                file_size     INTEGER,
                width         INTEGER NOT NULL,
                height        INTEGER NOT NULL,
                source_width  INTEGER NOT NULL,
                source_height INTEGER NOT NULL,
                fps           REAL NOT NULL,
                bitrate       INTEGER NOT NULL,
                encoder       TEXT NOT NULL,
                status        TEXT NOT NULL,
                frame_count   INTEGER NOT NULL DEFAULT 0,
                activity      TEXT,
                error         TEXT,
                created_utc   INTEGER NOT NULL,
                finalized_utc INTEGER
            );
            CREATE INDEX ix_segments_start ON segments(start_utc);
            CREATE INDEX ix_segments_display_start ON segments(display_id, start_utc);
            CREATE INDEX ix_segments_session ON segments(session_id);
            CREATE INDEX ix_segments_status ON segments(status);
            CREATE TABLE gaps (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id  TEXT NOT NULL,
                display_id TEXT,
                start_utc  INTEGER NOT NULL,
                end_utc    INTEGER,
                reason     TEXT NOT NULL,
                detail     TEXT
            );
            CREATE INDEX ix_gaps_start ON gaps(start_utc);
            CREATE TABLE thumbnails (
                segment_id TEXT NOT NULL,
                ts_utc     INTEGER NOT NULL,
                file_path  TEXT NOT NULL,
                PRIMARY KEY (segment_id, ts_utc)
            );
            """),
        (2, """
            CREATE TABLE focus_events (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id  TEXT NOT NULL,
                start_utc  INTEGER NOT NULL,
                end_utc    INTEGER NOT NULL,
                process    TEXT NOT NULL,
                title      TEXT NOT NULL
            );
            CREATE INDEX ix_focus_start ON focus_events(start_utc);
            """),
        (3, """
            CREATE TABLE text_embeddings (
                model  TEXT NOT NULL,
                text   TEXT NOT NULL,
                vector BLOB NOT NULL,
                PRIMARY KEY (model, text)
            );
            CREATE TABLE title_labels (
                item        TEXT PRIMARY KEY,
                task        TEXT NOT NULL,
                created_utc INTEGER NOT NULL
            );
            """),
    ];

    public static int LatestVersion => All[^1].Version;

    public static int GetVersion(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public static void Apply(SqliteConnection c)
    {
        using (var wal = c.CreateCommand())
        {
            // WAL lets the viewer read while the recorder writes; NORMAL sync is crash-safe in WAL mode.
            wal.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            wal.ExecuteNonQuery();
        }
        int current = GetVersion(c);
        if (current > LatestVersion)
            throw new InvalidOperationException($"Database schema v{current} is newer than this app (v{LatestVersion}). Update LifeReplay.");
        foreach (var (version, sql) in All)
        {
            if (version <= current) continue;
            using var tx = c.BeginTransaction();
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql + $"\nPRAGMA user_version = {version};";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            Log.Info($"Database migrated to schema v{version}");
        }
    }
}
