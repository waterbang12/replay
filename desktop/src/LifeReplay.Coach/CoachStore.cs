using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LifeReplay.Core;
using Microsoft.Data.Sqlite;

namespace LifeReplay.Coach;

/// <summary>
/// The coach's own tables (schema v5). Uses the shared pooled connections of <see cref="MetadataStore"/>, so WAL
/// mode and busy retries apply; the recorder keeps writing while the coach reads. The coach never writes to
/// recording tables.
/// </summary>
public sealed class CoachStore(MetadataStore store)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    private long Insert(string sql, params (string, object?)[] args)
    {
        using var c = store.Open();
        using var cmd = Cmd(c, sql + "; SELECT last_insert_rowid();", args);
        return (long)cmd.ExecuteScalar()!;
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var c = store.Open();
        using var cmd = Cmd(c, sql, args);
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- requests (audit log + budget)

    public long InsertRequest(long nowMs, string kind, string model, string endpoint, string payload) =>
        Insert("INSERT INTO coach_requests(created_utc, kind, model, endpoint, payload, payload_sha256, bytes, status) VALUES($t,$k,$m,$e,$p,$h,$b,'pending')",
            ("$t", nowMs), ("$k", kind), ("$m", model), ("$e", endpoint), ("$p", payload),
            ("$h", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant()), ("$b", Encoding.UTF8.GetByteCount(payload)));

    public void FinishRequest(long id, string status, int attempts, int credits, string? response, string? error) =>
        Exec("UPDATE coach_requests SET status=$s, attempts=$a, credits=$c, response=$r, error=$e WHERE id=$id",
            ("$s", status), ("$a", attempts), ("$c", credits), ("$r", response), ("$e", error), ("$id", id));

    public int CreditsSince(long sinceMs)
    {
        using var c = store.Open();
        using var cmd = Cmd(c, "SELECT COALESCE(SUM(credits),0) FROM coach_requests WHERE created_utc >= $t", ("$t", sinceMs));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int AttemptsSince(long sinceMs)
    {
        using var c = store.Open();
        using var cmd = Cmd(c, "SELECT COALESCE(SUM(attempts),0) FROM coach_requests WHERE created_utc >= $t", ("$t", sinceMs));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public sealed record RequestRow(long Id, long CreatedUtcMs, string Kind, string Model, string Endpoint, string Payload, int Bytes,
        string Status, int Attempts, int Credits, string? Response, string? Error);

    public List<RequestRow> RecentRequests(int limit)
    {
        using var c = store.Open();
        using var cmd = Cmd(c, "SELECT id, created_utc, kind, model, endpoint, payload, bytes, status, attempts, credits, response, error FROM coach_requests ORDER BY id DESC LIMIT $n", ("$n", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<RequestRow>();
        while (r.Read())
            list.Add(new RequestRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6),
                r.GetString(7), r.GetInt32(8), r.GetInt32(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11)));
        return list;
    }

    // ---------------------------------------------------------------- interventions, feedback, outcomes

    /// <summary><paramref name="evidence"/>: the cited evidence as "ev2: text", so it stays readable later. Defaults to the ids.</summary>
    public long InsertIntervention(long nowMs, DateOnly day, string kind, string source, ScheduleState state, Suggestion s, long? requestId,
        IReadOnlyList<string>? evidence = null)
    {
        string? task = s.Actions.FirstOrDefault(a => a.TaskId.Length > 0)?.TaskId;
        return Insert("""
            INSERT INTO coach_interventions(created_utc, day, kind, source, state, message, actions, evidence, counterpoint, confidence, task_id, request_id)
            VALUES($t,$d,$k,$s,$st,$m,$a,$e,$c,$cf,$task,$r)
            """,
            ("$t", nowMs), ("$d", day.ToString("yyyy-MM-dd")), ("$k", kind), ("$s", source), ("$st", CoachNames.Of(state)),
            ("$m", s.Message), ("$a", JsonSerializer.Serialize(s.Actions, Json)), ("$e", JsonSerializer.Serialize(evidence ?? s.EvidenceIds, Json)),
            ("$c", s.Counterpoint), ("$cf", s.Confidence), ("$task", task), ("$r", requestId));
    }

    public void AddFeedback(long interventionId, string response, long nowMs, string? note = null) =>
        Exec("INSERT INTO coach_feedback(intervention_id, created_utc, response, note) VALUES($i,$t,$r,$n)",
            ("$i", interventionId), ("$t", nowMs), ("$r", response), ("$n", note));

    public List<Intervention> Interventions(DateOnly day) => QueryInterventions("i.day = $d", ("$d", day.ToString("yyyy-MM-dd")));

    public List<Intervention> InterventionsSince(long sinceMs) => QueryInterventions("i.created_utc >= $t", ("$t", sinceMs));

    private List<Intervention> QueryInterventions(string where, params (string, object?)[] args)
    {
        using var c = store.Open();
        using var cmd = Cmd(c, $"""
            SELECT i.id, i.created_utc, i.day, i.kind, i.source, i.state, i.message, i.actions, i.evidence, i.counterpoint, i.confidence, i.task_id, i.request_id,
                   (SELECT f.response FROM coach_feedback f WHERE f.intervention_id = i.id ORDER BY f.id DESC LIMIT 1),
                   o.measured_utc, o.window_ms, o.task_id, o.focus_ms, o.followed
            FROM coach_interventions i LEFT JOIN coach_outcomes o ON o.intervention_id = i.id
            WHERE {where} ORDER BY i.created_utc
            """, args);
        using var r = cmd.ExecuteReader();
        var list = new List<Intervention>();
        while (r.Read())
        {
            long id = r.GetInt64(0);
            var outcome = r.IsDBNull(14) ? null
                : new InterventionOutcome(id, r.GetInt64(14), r.GetInt64(15), r.IsDBNull(16) ? null : r.GetString(16), r.GetInt64(17), r.GetInt64(18) != 0);
            list.Add(new Intervention(id, r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                JsonSerializer.Deserialize<List<SuggestedAction>>(r.GetString(7), Json) ?? [],
                JsonSerializer.Deserialize<List<string>>(r.GetString(8), Json) ?? [],
                r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11),
                r.IsDBNull(12) ? null : r.GetInt64(12), r.IsDBNull(13) ? null : r.GetString(13), outcome));
        }
        return list;
    }

    public void InsertOutcome(InterventionOutcome o) =>
        Exec("INSERT OR REPLACE INTO coach_outcomes(intervention_id, measured_utc, window_ms, task_id, focus_ms, followed) VALUES($i,$t,$w,$k,$f,$x)",
            ("$i", o.InterventionId), ("$t", o.MeasuredUtcMs), ("$w", o.WindowMs), ("$k", o.TaskId), ("$f", o.FocusMs), ("$x", o.Followed ? 1 : 0));

    // ---------------------------------------------------------------- evaluations and ledger

    public void InsertEvaluation(long nowMs, DayEvaluation ev) =>
        Exec("INSERT INTO coach_evaluations(created_utc, day, state, confidence, snapshot) VALUES($t,$d,$s,$c,$j)",
            ("$t", nowMs), ("$d", ev.Day.ToString("yyyy-MM-dd")), ("$s", CoachNames.Of(ev.State)), ("$c", ev.Confidence),
            ("$j", JsonSerializer.Serialize(new
            {
                ev.DayFraction, ev.PlannedMs, ev.PlannedWithEvidenceMs, ev.VerifiedDoneMs, ev.ExpectedByNowMs, ev.UnplannedMs, ev.DistractionMs,
                Tasks = ev.Tasks.Select(t => new { t.Id, t.Title, t.PlannedMs, t.FocusMs, t.VerifiedFraction, State = CoachNames.Of(t.State) }),
            }, Json)));

    public long? LastEvaluationUtc(DateOnly day)
    {
        using var c = store.Open();
        using var cmd = Cmd(c, "SELECT MAX(created_utc) FROM coach_evaluations WHERE day = $d", ("$d", day.ToString("yyyy-MM-dd")));
        var v = cmd.ExecuteScalar();
        return v is long l ? l : null;
    }

    public sealed record LedgerRow(long Id, string Day, string Task, long? PlannedMs, long FocusMs, string Evidence, bool Showable);

    /// <summary>Finished tasks with their evidence. Re-recording the same day + task updates it; "showable" is kept.</summary>
    public void UpsertLedger(DateOnly day, string task, long? plannedMs, long focusMs, string evidence, long nowMs) =>
        Exec("""
            INSERT INTO coach_ledger(day, task, planned_ms, focus_ms, evidence, created_utc) VALUES($d,$k,$p,$f,$e,$t)
            ON CONFLICT(day, task) DO UPDATE SET planned_ms = excluded.planned_ms, focus_ms = excluded.focus_ms, evidence = excluded.evidence
            """, ("$d", day.ToString("yyyy-MM-dd")), ("$k", task), ("$p", plannedMs), ("$f", focusMs), ("$e", evidence), ("$t", nowMs));

    public void SetShowable(long id, bool showable) => Exec("UPDATE coach_ledger SET showable=$s WHERE id=$i", ("$s", showable ? 1 : 0), ("$i", id));

    public List<LedgerRow> Ledger(DateOnly fromDay, DateOnly toDay)
    {
        using var c = store.Open();
        using var cmd = Cmd(c, "SELECT id, day, task, planned_ms, focus_ms, evidence, showable FROM coach_ledger WHERE day >= $a AND day <= $b ORDER BY day, id",
            ("$a", fromDay.ToString("yyyy-MM-dd")), ("$b", toDay.ToString("yyyy-MM-dd")));
        using var r = cmd.ExecuteReader();
        var list = new List<LedgerRow>();
        while (r.Read())
            list.Add(new LedgerRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.GetInt64(4), r.GetString(5), r.GetInt64(6) != 0));
        return list;
    }
}
