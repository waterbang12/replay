using Microsoft.Data.Sqlite;

namespace LifeReplay.Core;

/// <summary>Caches and choices for LifeReplay.Analysis: embeddings, plan-task labels, progress kinds.</summary>
public sealed partial class MetadataStore
{
    // ---------------------------------------------------------------- analysis caches

    /// <summary>Cached embedding vectors for texts (so each window title is only run through the model once).</summary>
    public Dictionary<string, float[]> GetEmbeddings(string model, IEnumerable<string> texts)
    {
        var wanted = texts.Distinct().ToList();
        var result = new Dictionary<string, float[]>();
        if (wanted.Count == 0) return result;
        using var c = Open();
        foreach (var chunk in wanted.Chunk(400))
        {
            using var cmd = c.CreateCommand();
            var names = new List<string>();
            for (int i = 0; i < chunk.Length; i++) { names.Add("$t" + i); cmd.Parameters.AddWithValue("$t" + i, chunk[i]); }
            cmd.Parameters.AddWithValue("$m", model);
            cmd.CommandText = $"SELECT text, vector FROM text_embeddings WHERE model = $m AND text IN ({string.Join(',', names)})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var bytes = (byte[])r[1];
                var v = new float[bytes.Length / 4];
                Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
                result[r.GetString(0)] = v;
            }
        }
        return result;
    }

    public void PutEmbeddings(string model, IEnumerable<KeyValuePair<string, float[]>> items)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var (text, v) in items)
        {
            var bytes = new byte[v.Length * 4];
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
            using var cmd = Cmd(c, "INSERT OR REPLACE INTO text_embeddings(model, text, vector) VALUES($m, $t, $v)", tx, ("$m", model), ("$t", text), ("$v", bytes));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Your manual "this window belongs to that task" choices; used to learn future matches.</summary>
    public Dictionary<string, string> GetTitleLabels()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT item, task FROM title_labels");
        using var r = cmd.ExecuteReader();
        var d = new Dictionary<string, string>();
        while (r.Read()) d[r.GetString(0)] = r.GetString(1);
        return d;
    }

    public void SetTitleLabel(string item, string? task)
    {
        if (task == null) Exec("DELETE FROM title_labels WHERE item = $i", ("$i", item));
        else Exec("INSERT OR REPLACE INTO title_labels(item, task, created_utc) VALUES($i, $t, $n)", ("$i", item), ("$t", task), ("$n", TimeUtil.NowMs()));
    }

    /// <summary>Your "progress / not_progress / unsorted" choices per window text (Topics view).</summary>
    public Dictionary<string, string> GetTopicKinds()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT item, kind FROM topic_kinds");
        using var r = cmd.ExecuteReader();
        var d = new Dictionary<string, string>();
        while (r.Read()) d[r.GetString(0)] = r.GetString(1);
        return d;
    }

    /// <summary>Sorts these windows into <paramref name="kind"/> (null forgets your choice) in one transaction.</summary>
    public void SetTopicKinds(IEnumerable<string> items, string? kind)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        long now = TimeUtil.NowMs();
        foreach (var item in items.Distinct())
        {
            using var cmd = kind == null
                ? Cmd(c, "DELETE FROM topic_kinds WHERE item = $i", tx, ("$i", item))
                : Cmd(c, "INSERT OR REPLACE INTO topic_kinds(item, kind, created_utc) VALUES($i, $k, $n)", tx, ("$i", item), ("$k", kind), ("$n", now));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
