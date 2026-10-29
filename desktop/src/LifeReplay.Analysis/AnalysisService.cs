using LifeReplay.Core;

namespace LifeReplay.Analysis;

/// <summary>
/// Owns the embedding model (loaded on first use, about 250 MB while loaded) and the vector cache.
/// Thread-safe; call from a background thread, since the first call loads the model (~1 s).
/// </summary>
public sealed class AnalysisService : IDisposable
{
    private readonly MetadataStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, float[]> _memory = [];
    private SentenceEmbedder? _embedder;

    public ModelStore Model { get; }

    public AnalysisService(AppPaths paths, MetadataStore store)
    {
        _store = store;
        Model = new ModelStore(paths);
    }

    /// <summary>Vectors for all texts: memory cache, then the database cache, then the model.</summary>
    public Dictionary<string, float[]> Vectors(IEnumerable<string> texts)
    {
        var wanted = texts.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        lock (_gate)
        {
            var missing = wanted.Where(t => !_memory.ContainsKey(t)).ToList();
            if (missing.Count > 0)
            {
                foreach (var (t, v) in _store.GetEmbeddings(ModelStore.ModelName, missing)) _memory[t] = v;
                missing = missing.Where(t => !_memory.ContainsKey(t)).ToList();
            }
            if (missing.Count > 0)
            {
                _embedder ??= new SentenceEmbedder(Model.ModelPath, Model.TokenizerPath);
                // Every result is cached, so the model is only needed for new titles: free it after a minute idle.
                _idleUnload ??= new Timer(_ => Unload(), null, Timeout.Infinite, Timeout.Infinite);
                _idleUnload.Change(TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
                var vecs = _embedder.Embed(missing);
                var fresh = new List<KeyValuePair<string, float[]>>();
                for (int i = 0; i < missing.Count; i++) { _memory[missing[i]] = vecs[i]; fresh.Add(new(missing[i], vecs[i])); }
                _store.PutEmbeddings(ModelStore.ModelName, fresh);
            }
            return wanted.ToDictionary(t => t, t => _memory[t]);
        }
    }

    public List<Topic> Topics(IReadOnlyList<FocusRecord> focus, long fromMs, long toMs)
    {
        var items = DayAnalysis.Items(focus, fromMs, toMs);
        if (items.Count == 0) return [];
        var v = Vectors(items.Take(300).Select(i => i.Text));
        return DayAnalysis.FindTopics(items, items.Take(300).Select(i => v[i.Text]).ToList());
    }

    public PlanStatus Plan(TodoPlan plan, IReadOnlyList<FocusRecord> focus, long fromMs, long toMs, long nowMs, TimeSpan defaultStart, TimeSpan defaultEnd)
    {
        var labels = _store.GetTitleLabels();
        var items = DayAnalysis.Items(focus, fromMs, toMs);
        var texts = items.Select(i => i.Text)
            .Concat(plan.Tasks.Select(t => t.Title + (t.Match.Count > 0 ? " " + string.Join(" ", t.Match) : "")))
            .Concat(labels.Keys);
        var v = Vectors(texts);
        return DayAnalysis.Match(plan, focus, fromMs, toMs, nowMs, defaultStart, defaultEnd,
            text => v.TryGetValue(text, out var x) ? x : Vectors([text])[text], labels);
    }

    /// <summary>Frees the model's memory (vectors stay cached in the database).</summary>
    public void Unload()
    {
        bool had;
        lock (_gate)
        {
            had = _embedder != null;
            _embedder?.Dispose();
            _embedder = null;
        }
        if (!had) return;
        // The tokenizer's 250k-entry vocabulary is in the managed heap; compact it away now rather than "eventually".
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Log.Info("Topic model unloaded");
    }

    private Timer? _idleUnload;

    public void Dispose()
    {
        _idleUnload?.Dispose();
        Unload();
    }
}
