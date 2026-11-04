using System.Text;
using System.Text.Json;

namespace LifeReplay.Analysis;

/// <summary>
/// SentencePiece Unigram tokenizer (XLM-RoBERTa style) loaded from a Hugging Face tokenizer.json:
/// whitespace split, "▁" word prefix, then the most probable segmentation of each word (Viterbi over
/// vocabulary log-probabilities). Text is NFKC-normalized, which matches the model's normalizer closely.
/// </summary>
public sealed class UnigramTokenizer
{
    private const char Meta = '▁'; // ▁
    private readonly Dictionary<string, (int Id, float Score)> _vocab;
    private readonly int _maxPieceLength;
    private readonly float _unkScore;

    public int BosId { get; } = 0;   // <s>
    public int PadId { get; } = 1;   // <pad>
    public int EosId { get; } = 2;   // </s>
    public int UnkId { get; }

    private UnigramTokenizer(Dictionary<string, (int, float)> vocab, int unkId, int maxPieceLength, float minScore)
    {
        _vocab = vocab;
        UnkId = unkId;
        _maxPieceLength = maxPieceLength;
        _unkScore = minScore - 10f; // unknown characters are much less likely than any real piece
        if (vocab.TryGetValue("<s>", out var s)) BosId = s.Item1;
        if (vocab.TryGetValue("<pad>", out var p)) PadId = p.Item1;
        if (vocab.TryGetValue("</s>", out var e)) EosId = e.Item1;
    }

    public static UnigramTokenizer Load(string tokenizerJsonPath)
    {
        using var stream = File.OpenRead(tokenizerJsonPath);
        using var doc = JsonDocument.Parse(stream);
        var model = doc.RootElement.GetProperty("model");
        if (model.GetProperty("type").GetString() != "Unigram") throw new NotSupportedException("Only Unigram tokenizers are supported");
        int unk = model.TryGetProperty("unk_id", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 3;
        var vocab = new Dictionary<string, (int, float)>(260_000, StringComparer.Ordinal);
        int id = 0, maxLen = 1;
        float minScore = 0;
        foreach (var entry in model.GetProperty("vocab").EnumerateArray())
        {
            var piece = entry[0].GetString() ?? "";
            float score = (float)entry[1].GetDouble();
            vocab.TryAdd(piece, (id, score));
            maxLen = Math.Max(maxLen, piece.Length);
            minScore = Math.Min(minScore, score);
            id++;
        }
        return new UnigramTokenizer(vocab, unk, Math.Min(maxLen, 48), minScore);
    }

    /// <summary>Token ids including &lt;s&gt; and &lt;/s&gt;, truncated to <paramref name="maxTokens"/>.</summary>
    public int[] Encode(string text, int maxTokens = 128)
    {
        var ids = new List<int>(32) { BosId };
        var normalized = (text ?? "").Normalize(NormalizationForm.FormKC);
        foreach (var word in normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            EncodeWord(Meta + word, ids);
            if (ids.Count >= maxTokens - 1) break;
        }
        if (ids.Count > maxTokens - 1) ids.RemoveRange(maxTokens - 1, ids.Count - (maxTokens - 1));
        ids.Add(EosId);
        return ids.ToArray();
    }

    /// <summary>Viterbi: best[i] = highest total score of any segmentation of word[0..i).</summary>
    private void EncodeWord(string word, List<int> output)
    {
        int n = word.Length;
        var best = new float[n + 1];
        var backLen = new int[n + 1];
        var backId = new int[n + 1];
        for (int i = 1; i <= n; i++) best[i] = float.NegativeInfinity;
        for (int end = 1; end <= n; end++)
        {
            for (int len = 1; len <= Math.Min(_maxPieceLength, end); len++)
            {
                int start = end - len;
                if (float.IsNegativeInfinity(best[start])) continue;
                // Don't cut a UTF-16 surrogate pair in half.
                if (char.IsLowSurrogate(word[start])) continue;
                if (_vocab.TryGetValue(word.Substring(start, len), out var v))
                {
                    float s = best[start] + v.Score;
                    if (s > best[end]) { best[end] = s; backLen[end] = len; backId[end] = v.Id; }
                }
            }
            if (float.IsNegativeInfinity(best[end]))
            {
                // No vocabulary piece ends here: treat one character (or surrogate pair) as unknown.
                int len = end >= 2 && char.IsLowSurrogate(word[end - 1]) && char.IsHighSurrogate(word[end - 2]) ? 2 : 1;
                if (!float.IsNegativeInfinity(best[end - len]))
                {
                    best[end] = best[end - len] + _unkScore;
                    backLen[end] = len;
                    backId[end] = UnkId;
                }
            }
        }
        var pieces = new Stack<int>();
        for (int i = n; i > 0; i -= Math.Max(1, backLen[i])) pieces.Push(backId[i]);
        // Merge consecutive unknowns into one, as SentencePiece does.
        int last = -1;
        foreach (var p in pieces)
        {
            if (p == UnkId && last == UnkId) continue;
            output.Add(p);
            last = p;
        }
    }

    /// <summary>For tests/diagnostics: the piece strings for ids.</summary>
    public string[] Pieces(IEnumerable<int> ids)
    {
        var reverse = _vocab.ToDictionary(kv => kv.Value.Id, kv => kv.Key);
        return ids.Select(i => reverse.TryGetValue(i, out var s) ? s : "?").ToArray();
    }
}
