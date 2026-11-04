using Microsoft.Data.Sqlite;

namespace LifeReplay.Core;

/// <summary>iPhone app events (app_events), from the Shortcuts log in iCloud Drive.</summary>
public sealed partial class MetadataStore
{
    // ---------------------------------------------------------------- iPhone app events

    /// <summary>Stores events in one transaction; already-known (device, event_id) pairs are skipped. Returns how many were new.</summary>
    public int InsertAppEvents(IEnumerable<AppEvent> events)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        int added = 0;
        foreach (var e in events)
        {
            using var cmd = Cmd(c, """
                INSERT OR IGNORE INTO app_events(device_id, source, app, event_type, ts_utc, tz_offset, event_id, id_derived, received_utc)
                VALUES($d, $s, $a, $t, $ts, $tz, $id, $der, $r)
                """, tx, ("$d", e.DeviceId), ("$s", e.Source), ("$a", e.App), ("$t", e.Type), ("$ts", e.TsUtcMs), ("$tz", e.TzOffsetMinutes),
                ("$id", e.EventId), ("$der", e.EventIdDerived ? 1 : 0), ("$r", e.ReceivedUtcMs));
            added += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return added;
    }

    /// <summary>Events with phone timestamps in [startMs, endMs).</summary>
    public List<AppEvent> GetAppEvents(long startMs, long endMs)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT id, device_id, source, app, event_type, ts_utc, tz_offset, event_id, id_derived, received_utc
            FROM app_events WHERE ts_utc >= $s AND ts_utc < $e ORDER BY ts_utc, id
            """, null, ("$s", startMs), ("$e", endMs));
        using var r = cmd.ExecuteReader();
        var list = new List<AppEvent>();
        while (r.Read())
            list.Add(new AppEvent
            {
                Id = r.GetInt64(0), DeviceId = r.GetString(1), Source = r.GetString(2), App = r.GetString(3), Type = r.GetString(4),
                TsUtcMs = r.GetInt64(5), TzOffsetMinutes = r.GetInt32(6), EventId = r.GetString(7), EventIdDerived = r.GetInt32(8) != 0,
                ReceivedUtcMs = r.GetInt64(9),
            });
        return list;
    }

    /// <summary>For the sync status display: last received event and how late events arrive.</summary>
    public (long? LastReceivedUtcMs, long? LastEventUtcMs, int Count) GetAppEventStatus()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT MAX(received_utc), MAX(ts_utc), COUNT(*) FROM app_events");
        using var r = cmd.ExecuteReader();
        r.Read();
        return (NLong(r, 0), NLong(r, 1), r.GetInt32(2));
    }

    public int DeleteAppEventsBefore(long utcMs) => Exec("DELETE FROM app_events WHERE ts_utc < $t", ("$t", utcMs));
}
