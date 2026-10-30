using LifeReplay.Core;

namespace LifeReplay.Analysis;

/// <summary>A window (app + tab/document) as it appears in the day's title log.</summary>
public sealed record TitleItem(string App, string Item, long DurationMs, long FirstStartMs)
{
    /// <summary>
    /// Text given to the model. Only short, generic titles ("Inbox", "YouTube") get the app name for context;
    /// adding it to every title made all tabs of one browser look alike and pulled unrelated tabs together.
    /// </summary>
    public string Text => Item.Equals(App, StringComparison.OrdinalIgnoreCase) ? App
        : Item.Length < 12 && !Item.Contains(' ') ? $"{Item} ({App})" : Item;
}

/// <summary>A group of windows. <see cref="Category"/> is a <see cref="ProgressKinds"/> value.</summary>
public sealed record Topic(string Label, long DurationMs, List<TitleItem> Items, string Category = ProgressKinds.Unsorted);

public enum MatchHow { Keyword, Learned, Meaning, Context, Manual }

public sealed record TaskStatus(PlanTask Task, long SpentMs, double? FractionDone, long? RemainingMs, List<(TitleItem Item, MatchHow How, long Ms)> Items);

public sealed record PlanStatus(
    List<TaskStatus> Tasks, List<(TitleItem Item, long Ms, string? Suggestion)> Unassigned,
    long PlannedTotalMs, long SpentOnPlanMs, long ExpectedByNowMs, double DayFraction, long? AheadMs);

/// <summary>Turns a day's window-title log into topics and, if there's a plan, per-task time and ahead/behind.</summary>
public static class DayAnalysis
{
    /// <summary>Cosine similarity needed to count a window toward a task by meaning (calibrated on real titles).</summary>
    public const float MeaningThreshold = 0.40f;
    /// <summary>The best task must beat the runner-up by this much, otherwise it's left for you to decide.</summary>
    public const float MeaningMargin = 0.03f;
    /// <summary>
    /// Groups merge when their average similarity is at least this. Measured on real titles: related pages
    /// score 0.48–0.60, while the worst unrelated pairs reach 0.40–0.43.
    /// </summary>
    public const float TopicThreshold = 0.45f;
    /// <summary>A window similar to one you assigned by hand (at least this much) gets the same task.</summary>
    public const float LearnedThreshold = 0.80f;

    public static List<TitleItem> Items(IEnumerable<FocusRecord> focus, long fromMs, long toMs)
    {
        var map = new Dictionary<(string, string), (long Ms, long First)>();
        foreach (var f in focus)
        {
            long d = Math.Min(f.EndUtcMs, toMs) - Math.Max(f.StartUtcMs, fromMs);
            if (d <= 0 || f.Title == "(private)") continue;
            var key = (FocusSummary.AppName(f.Process), FocusSummary.ItemName(f.Process, f.Title));
            var cur = map.GetValueOrDefault(key, (0, long.MaxValue));
            map[key] = (cur.Ms + d, Math.Min(cur.First, f.StartUtcMs));
        }
        return map.Select(kv => new TitleItem(kv.Key.Item1, kv.Key.Item2, kv.Value.Ms, kv.Value.First))
            .OrderByDescending(i => i.DurationMs).ToList();
    }

    // ------------------------------------------------------------------ topics (no plan needed)

    /// <summary>
    /// Groups windows by meaning with average-linkage agglomerative clustering. Each topic is named after its
    /// longest-used window. Only the 300 longest-used windows are clustered; the rest go to "Other".
    /// </summary>
    public static List<Topic> FindTopics(IReadOnlyList<TitleItem> items, IReadOnlyList<float[]> vectors, float threshold = TopicThreshold)
    {
        int n = Math.Min(items.Count, 300);
        var clusters = Enumerable.Range(0, n).Select(i => new List<int> { i }).ToList();
        var sim = new float[n, n];
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                sim[i, j] = sim[j, i] = SentenceEmbedder.Cosine(vectors[i], vectors[j]);
        while (clusters.Count > 1)
        {
            int bi = -1, bj = -1;
            double best = threshold;
            for (int a = 0; a < clusters.Count; a++)
                for (int b = a + 1; b < clusters.Count; b++)
                {
                    double s = 0;
                    foreach (var x in clusters[a]) foreach (var y in clusters[b]) s += sim[x, y];
                    s /= clusters[a].Count * clusters[b].Count;
                    if (s >= best) { best = s; bi = a; bj = b; }
                }
            if (bi < 0) break;
            clusters[bi].AddRange(clusters[bj]);
            clusters.RemoveAt(bj);
        }
        var topics = clusters.Select(c =>
        {
            var members = c.Select(i => items[i]).OrderByDescending(i => i.DurationMs).ToList();
            return new Topic(members[0].Item, members.Sum(m => m.DurationMs), members);
        }).ToList();
        if (items.Count > n)
        {
            var rest = items.Skip(n).ToList();
            topics.Add(new Topic("Other windows", rest.Sum(r => r.DurationMs), rest));
        }
        return topics.OrderByDescending(t => t.DurationMs).ToList();
    }

    /// <summary>
    /// Sorts every window into Progress / Not progress / Unsorted (<see cref="ProgressKinds"/>), then groups the
    /// windows of each kind by meaning. A window you didn't sort yourself follows the most similar window you did
    /// sort (cosine ≥ <see cref="LearnedThreshold"/>), before the built-in rules.
    /// Only the 600 longest-used windows get vectors; the rest are sorted by rule into "Other windows".
    /// </summary>
    public static List<Topic> Categorize(IReadOnlyList<TitleItem> items, Func<IReadOnlyList<string>, IReadOnlyList<float[]>> vectorsFor,
        IReadOnlyDictionary<string, string>? manual = null)
    {
        manual ??= new Dictionary<string, string>();
        var ordered = items.OrderByDescending(i => i.DurationMs).ToList();
        var top = ordered.Take(600).ToList();
        var sorted = manual.Where(m => ProgressKinds.IsKind(m.Value)).ToList();
        var texts = top.Select(i => i.Text).Concat(sorted.Select(m => m.Key)).Distinct().ToList();
        var vecList = texts.Count > 0 ? vectorsFor(texts) : [];
        var vec = new Dictionary<string, float[]>();
        for (int i = 0; i < texts.Count; i++) vec[texts[i]] = vecList[i];

        string KindOf(TitleItem item)
        {
            if (manual.TryGetValue(item.Text, out var k) && ProgressKinds.IsKind(k)) return k;
            if (vec.TryGetValue(item.Text, out var v))
            {
                float best = LearnedThreshold;
                string? kind = null;
                foreach (var (text, kk) in sorted)
                {
                    if (!vec.TryGetValue(text, out var lv)) continue;
                    float s = SentenceEmbedder.Cosine(v, lv);
                    if (s >= best) { best = s; kind = kk; }
                }
                if (kind != null) return kind;
            }
            return ProgressKinds.Rule(item);
        }

        var result = new List<Topic>();
        var topSet = top.ToHashSet();
        foreach (var group in ordered.GroupBy(KindOf).OrderBy(g => Array.IndexOf(ProgressKinds.All, g.Key)))
        {
            var withVec = group.Where(topSet.Contains).ToList();
            var topics = withVec.Count > 0 ? FindTopics(withVec, withVec.Select(i => vec[i.Text]).ToList()) : [];
            var rest = group.Where(i => !topSet.Contains(i)).ToList();
            if (rest.Count > 0) topics.Add(new Topic("Other windows", rest.Sum(r => r.DurationMs), rest));
            result.AddRange(topics.OrderByDescending(t => t.DurationMs).Select(t => t with { Category = group.Key }));
        }
        return result;
    }

    // ------------------------------------------------------------------ plan matching

    /// <summary>
    /// Assigns each logged window interval to a task, in this order of trust:
    /// 1. your manual choice for that exact window; 2. the task's match keywords; 3. similarity to a window you
    /// assigned by hand; 4. meaning (best task ≥ 0.40 and clearly ahead of the runner-up);
    /// 5. context: an unassigned window between two windows of the same task (≤ 5 min apart) gets that task.
    /// Everything else is "unassigned", with the best guess as a suggestion.
    /// </summary>
    public static PlanStatus Match(
        TodoPlan plan, IReadOnlyList<FocusRecord> focus, long fromMs, long toMs, long nowMs, TimeSpan defaultStart, TimeSpan defaultEnd,
        Func<string, float[]> vectorOf, IReadOnlyDictionary<string, string> labels)
    {
        var tasks = plan.Tasks;
        var taskVecs = tasks.Select(t => vectorOf(t.Title + (t.Match.Count > 0 ? " " + string.Join(" ", t.Match) : ""))).ToList();
        var labeled = labels.Where(l => tasks.Any(t => t.Title == l.Value)).Select(l => (Vec: vectorOf(l.Key), Task: l.Value)).ToList();

        // Per interval: (item, ms, task index or -1, how, suggestion)
        var rows = new List<(FocusRecord F, TitleItem Item, long Ms, int Task, MatchHow How, int Suggest)>();
        var itemCache = new Dictionary<(string, string), (int, MatchHow, int)>();
        foreach (var f in focus.OrderBy(f => f.StartUtcMs))
        {
            long ms = Math.Min(f.EndUtcMs, toMs) - Math.Max(f.StartUtcMs, fromMs);
            if (ms <= 0 || f.Title == "(private)") continue;
            var app = FocusSummary.AppName(f.Process);
            var name = FocusSummary.ItemName(f.Process, f.Title);
            var item = new TitleItem(app, name, ms, f.StartUtcMs);
            if (!itemCache.TryGetValue((app, name), out var decision))
            {
                decision = Decide(item, f.Title, tasks, taskVecs, labeled, labels, vectorOf);
                itemCache[(app, name)] = decision;
            }
            rows.Add((f, item, ms, decision.Item1, decision.Item2, decision.Item3));
        }
        // Context: fill unassigned intervals sandwiched between the same task.
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Task >= 0) continue;
            int prev = i - 1, next = i + 1;
            while (prev >= 0 && rows[prev].Task < 0) prev--;
            while (next < rows.Count && rows[next].Task < 0) next++;
            if (prev < 0 || next >= rows.Count || rows[prev].Task != rows[next].Task) continue;
            if (rows[i].F.StartUtcMs - rows[prev].F.EndUtcMs > 300_000 || rows[next].F.StartUtcMs - rows[i].F.EndUtcMs > 300_000) continue;
            rows[i] = rows[i] with { Task = rows[prev].Task, How = MatchHow.Context };
        }

        var statuses = new List<TaskStatus>();
        for (int t = 0; t < tasks.Count; t++)
        {
            var mine = rows.Where(r => r.Task == t).ToList();
            long spent = mine.Sum(r => r.Ms);
            var grouped = mine.GroupBy(r => (r.Item.App, r.Item.Item))
                .Select(g => (g.First().Item, g.GroupBy(x => x.How).OrderByDescending(x => x.Sum(y => y.Ms)).First().Key, g.Sum(x => x.Ms)))
                .OrderByDescending(x => x.Item3).ToList();
            var task = tasks[t];
            double? fraction = task.Done ? 1 : task.ProgressTotal is > 0 ? Math.Clamp((double)task.ProgressDone!.Value / task.ProgressTotal.Value, 0, 1) : null;
            long? remaining = task.Done ? 0
                : fraction is > 0 and < 1 ? (long)(spent / fraction.Value * (1 - fraction.Value))   // at your pace so far
                : task.PlannedMs is { } p ? Math.Max(0, p - spent) : null;
            statuses.Add(new TaskStatus(task, spent, fraction, remaining, grouped));
        }
        var unassigned = rows.Where(r => r.Task < 0).GroupBy(r => (r.Item.App, r.Item.Item))
            .Select(g => (g.First().Item, g.Sum(x => x.Ms), g.First().Suggest is >= 0 and var s ? tasks[s].Title : (string?)null))
            .OrderByDescending(x => x.Item2).ToList();

        // Ahead/behind: by now you "should" have done (planned total × share of the working day passed).
        var dayStartLocal = TimeUtil.ToLocal(fromMs).Date;
        var start = TimeUtil.LocalToUtcMs(dayStartLocal + (plan.DayStart ?? defaultStart));
        var end = TimeUtil.LocalToUtcMs(dayStartLocal + (plan.DayEnd ?? defaultEnd));
        double dayFraction = end <= start ? 1 : Math.Clamp((nowMs - start) / (double)(end - start), 0, 1);
        long plannedTotal = tasks.Sum(t => t.PlannedMs ?? 0);
        long spentOnPlan = statuses.Where(s => s.Task.PlannedMs != null).Sum(s => Math.Min(s.SpentMs, s.Task.PlannedMs!.Value));
        long expected = (long)(plannedTotal * dayFraction);
        long? ahead = plannedTotal > 0 ? spentOnPlan - expected : null;
        return new PlanStatus(statuses, unassigned, plannedTotal, spentOnPlan, expected, dayFraction, ahead);
    }

    private static (int Task, MatchHow How, int Suggest) Decide(TitleItem item, string rawTitle, List<PlanTask> tasks, List<float[]> taskVecs,
        List<(float[] Vec, string Task)> labeled, IReadOnlyDictionary<string, string> labels, Func<string, float[]> vectorOf)
    {
        // 1. Manual choice for this exact window.
        if (labels.TryGetValue(item.Text, out var manual))
        {
            int m = tasks.FindIndex(t => t.Title == manual);
            if (m >= 0) return (m, MatchHow.Manual, m);
            if (manual == "") return (-1, MatchHow.Manual, -1); // explicitly "not part of any task"
        }
        // 2. Keywords (in the raw title too, so "match: pwn.college" works whatever the app adds).
        for (int t = 0; t < tasks.Count; t++)
            foreach (var k in tasks[t].Match)
                if (rawTitle.Contains(k, StringComparison.OrdinalIgnoreCase) || item.Text.Contains(k, StringComparison.OrdinalIgnoreCase))
                    return (t, MatchHow.Keyword, t);
        var v = vectorOf(item.Text);
        // 3. Similar to a window you assigned by hand.
        if (labeled.Count > 0)
        {
            var (best, task) = labeled.Select(l => (SentenceEmbedder.Cosine(v, l.Vec), l.Task)).MaxBy(x => x.Item1);
            if (best >= LearnedThreshold) { int i = tasks.FindIndex(t => t.Title == task); if (i >= 0) return (i, MatchHow.Learned, i); }
        }
        // 4. Meaning.
        if (tasks.Count == 0) return (-1, MatchHow.Meaning, -1);
        var scores = taskVecs.Select((tv, i) => (S: SentenceEmbedder.Cosine(v, tv), I: i)).OrderByDescending(x => x.S).ToList();
        float second = scores.Count > 1 ? scores[1].S : -1;
        if (scores[0].S >= MeaningThreshold && scores[0].S - second >= MeaningMargin) return (scores[0].I, MatchHow.Meaning, scores[0].I);
        return (-1, MatchHow.Meaning, scores[0].S >= 0.30f ? scores[0].I : -1);
    }
}
