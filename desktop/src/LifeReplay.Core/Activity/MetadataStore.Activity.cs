using Microsoft.Data.Sqlite;

namespace LifeReplay.Core;

/// <summary>Activity tables: the window log (focus_events) and time waiting on AI tools (ai_waits).</summary>
public sealed partial class MetadataStore
{
    // ---------------------------------------------------------------- focus (window titles)

    public long InsertFocus(FocusRecord f)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            INSERT INTO focus_events(device_id, start_utc, end_utc, process, title) VALUES($d, $s, $e, $p, $t);
            SELECT last_insert_rowid();
            """, null, ("$d", f.DeviceId), ("$s", f.StartUtcMs), ("$e", f.EndUtcMs), ("$p", f.Process), ("$t", f.Title));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void UpdateFocusEnd(long id, long endMs) =>
        Exec("UPDATE focus_events SET end_utc = MAX(start_utc, $e) WHERE id = $id", ("$e", endMs), ("$id", id));

    public List<FocusRecord> GetFocusInRange(long startMs, long endMs)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT id, device_id, start_utc, end_utc, process, title FROM focus_events
            WHERE start_utc < $end AND end_utc > $start ORDER BY start_utc
            """, null, ("$start", startMs), ("$end", endMs));
        using var r = cmd.ExecuteReader();
        var list = new List<FocusRecord>();
        while (r.Read())
            list.Add(new FocusRecord { Id = r.GetInt64(0), DeviceId = r.GetString(1), StartUtcMs = r.GetInt64(2), EndUtcMs = r.GetInt64(3), Process = r.GetString(4), Title = r.GetString(5) });
        return list;
    }

    public int DeleteFocusBefore(long utcMs) => Exec("DELETE FROM focus_events WHERE end_utc < $t", ("$t", utcMs));

    // ---------------------------------------------------------------- waiting on AI tools

    public long InsertAiWait(long startMs, long endMs, string tool)
    {
        using var c = Open();
        using var cmd = Cmd(c, "INSERT INTO ai_waits(start_utc, end_utc, tool) VALUES($s, $e, $t); SELECT last_insert_rowid();", null,
            ("$s", startMs), ("$e", endMs), ("$t", tool));
        return (long)cmd.ExecuteScalar()!;
    }

    public void UpdateAiWaitEnd(long id, long endMs) => Exec("UPDATE ai_waits SET end_utc = $e WHERE id = $i", ("$e", endMs), ("$i", id));

    public List<AiWait> GetAiWaits(long startMs, long endMs)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT id, start_utc, end_utc, tool FROM ai_waits WHERE start_utc < $e AND end_utc > $s ORDER BY start_utc", null,
            ("$s", startMs), ("$e", endMs));
        using var r = cmd.ExecuteReader();
        var list = new List<AiWait>();
        while (r.Read()) list.Add(new AiWait(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3)));
        return list;
    }

    public int DeleteAiWaitsBefore(long utcMs) => Exec("DELETE FROM ai_waits WHERE end_utc < $t", ("$t", utcMs));

    /// <summary>The window log with time spent waiting on AI cut in as "Waiting on AI" (see <see cref="AiTools.Splice"/>).</summary>
    public List<FocusRecord> GetActivityInRange(long startMs, long endMs) =>
        AiTools.Splice(GetFocusInRange(startMs, endMs), GetAiWaits(startMs, endMs));

    public int DeleteFocusInRange(long startMs, long endMs) =>
        Exec("DELETE FROM focus_events WHERE start_utc < $e AND end_utc > $s", ("$s", startMs), ("$e", endMs));

    // ---------------------------------------------------------------- day list

    /// <summary>Hours (UTC ms / 3,600,000) with any time record (window log, iPhone events), for the day list.</summary>
    public List<long> GetActivityHours()
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT start_utc / 3600000 FROM focus_events
            UNION SELECT ts_utc / 3600000 FROM app_events
            """);
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }
}
