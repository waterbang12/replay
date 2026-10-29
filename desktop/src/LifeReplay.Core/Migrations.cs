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
        (4, """
            CREATE TABLE app_events (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id    TEXT NOT NULL,
                source       TEXT NOT NULL,
                app          TEXT NOT NULL,
                event_type   TEXT NOT NULL CHECK (event_type IN ('opened', 'closed')),
                ts_utc       INTEGER NOT NULL,
                tz_offset    INTEGER NOT NULL,
                event_id     TEXT NOT NULL,
                id_derived   INTEGER NOT NULL DEFAULT 0,
                received_utc INTEGER NOT NULL,
                UNIQUE (device_id, event_id)
            );
            CREATE INDEX ix_app_events_ts ON app_events(ts_utc);
            """),
        // AI coach (LifeReplay.Coach). Only these tables are written by the coach; recording data is read-only to it.
        (5, """
            -- Every request that was (or would have been) sent: exact payload, outcome, credits. The audit log.
            CREATE TABLE coach_requests (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc    INTEGER NOT NULL,
                kind           TEXT NOT NULL,
                model          TEXT NOT NULL,
                endpoint       TEXT NOT NULL,
                payload        TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL,
                bytes          INTEGER NOT NULL,
                status         TEXT NOT NULL,
                attempts       INTEGER NOT NULL DEFAULT 0,
                credits        INTEGER NOT NULL DEFAULT 0,
                response       TEXT,
                error          TEXT
            );
            CREATE INDEX ix_coach_requests_created ON coach_requests(created_utc);
            -- Suggestions and notifications shown to the user, with the evidence they rest on.
            CREATE TABLE coach_interventions (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc  INTEGER NOT NULL,
                day          TEXT NOT NULL,
                kind         TEXT NOT NULL,
                source       TEXT NOT NULL,
                state        TEXT NOT NULL,
                message      TEXT NOT NULL,
                actions      TEXT NOT NULL,
                evidence     TEXT NOT NULL,
                counterpoint TEXT,
                confidence   TEXT,
                task_id      TEXT,
                request_id   INTEGER REFERENCES coach_requests(id) ON DELETE SET NULL
            );
            CREATE INDEX ix_coach_interventions_day ON coach_interventions(day);
            CREATE TABLE coach_feedback (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                intervention_id INTEGER NOT NULL REFERENCES coach_interventions(id) ON DELETE CASCADE,
                created_utc     INTEGER NOT NULL,
                response        TEXT NOT NULL CHECK (response IN ('accepted', 'dismissed', 'snoozed')),
                note            TEXT
            );
            -- What actually happened after a suggestion (measured from the activity log, not asked).
            CREATE TABLE coach_outcomes (
                intervention_id INTEGER PRIMARY KEY REFERENCES coach_interventions(id) ON DELETE CASCADE,
                measured_utc    INTEGER NOT NULL,
                window_ms       INTEGER NOT NULL,
                task_id         TEXT,
                focus_ms        INTEGER NOT NULL,
                followed        INTEGER NOT NULL
            );
            -- Deterministic evaluations, so later reviews and calibration can see how a day unfolded.
            CREATE TABLE coach_evaluations (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc INTEGER NOT NULL,
                day         TEXT NOT NULL,
                state       TEXT NOT NULL,
                confidence  REAL NOT NULL,
                snapshot    TEXT NOT NULL
            );
            CREATE INDEX ix_coach_evaluations_day ON coach_evaluations(day);
            -- Private ledger of finished work with its evidence. Nothing is "showable" unless you switch it on.
            CREATE TABLE coach_ledger (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                day          TEXT NOT NULL,
                task         TEXT NOT NULL,
                planned_ms   INTEGER,
                focus_ms     INTEGER NOT NULL,
                evidence     TEXT NOT NULL,
                showable     INTEGER NOT NULL DEFAULT 0,
                created_utc  INTEGER NOT NULL,
                UNIQUE (day, task)
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
