using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LifeReplay.Analysis;

/// <summary>
/// Turns short texts (window titles, todo items) into normalized embedding vectors with a small multilingual
/// sentence-transformer (paraphrase-multilingual-MiniLM-L12-v2, int8 ONNX, runs on the CPU).
/// Cosine similarity of two vectors = their dot product; ~1 means the same meaning, ~0 unrelated.
/// </summary>
public sealed class SentenceEmbedder : IDisposable
{
    private readonly InferenceSession _session;
    private readonly UnigramTokenizer _tokenizer;
    private readonly bool _wantsTokenTypes;

    public int Dimensions { get; private set; }

    public SentenceEmbedder(string modelPath, string tokenizerPath)
    {
        _tokenizer = UnigramTokenizer.Load(tokenizerPath);
        var options = new SessionOptions
        {
            // Keep the rest of the PC responsive: a couple of threads is plenty for short titles.
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 4),
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // Short titles don't benefit from ONNX Runtime's memory pool, and the pool keeps memory after use.
            EnableCpuMemArena = false,
            EnableMemoryPattern = false,
        };
        _session = new InferenceSession(modelPath, options);
        _wantsTokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");
    }

    public float[][] Embed(IReadOnlyList<string> texts, int batchSize = 32)
    {
        var result = new float[texts.Count][];
        for (int offset = 0; offset < texts.Count; offset += batchSize)
        {
            int count = Math.Min(batchSize, texts.Count - offset);
            var encoded = new int[count][];
            for (int i = 0; i < count; i++) encoded[i] = _tokenizer.Encode(texts[offset + i]);
            int seq = encoded.Max(e => e.Length);
            var ids = new DenseTensor<long>([count, seq]);
            var mask = new DenseTensor<long>([count, seq]);
            var types = new DenseTensor<long>([count, seq]);
            for (int b = 0; b < count; b++)
                for (int t = 0; t < seq; t++)
                {
                    bool real = t < encoded[b].Length;
                    ids[b, t] = real ? encoded[b][t] : _tokenizer.PadId;
                    mask[b, t] = real ? 1 : 0;
                }
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", ids),
                NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            };
            if (_wantsTokenTypes) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", types));
            using var outputs = _session.Run(inputs);
            var hidden = outputs.First().AsTensor<float>(); // [batch, seq, dim]
            int dim = hidden.Dimensions[2];
            Dimensions = dim;
            for (int b = 0; b < count; b++)
            {
                // Mean pooling over real tokens, then L2 normalization (how the model was trained).
                var v = new float[dim];
                int n = 0;
                for (int t = 0; t < seq; t++)
                {
                    if (mask[b, t] == 0) continue;
                    n++;
                    for (int d = 0; d < dim; d++) v[d] += hidden[b, t, d];
                }
                double norm = 0;
                for (int d = 0; d < dim; d++) { v[d] /= Math.Max(1, n); norm += v[d] * v[d]; }
                norm = Math.Sqrt(norm);
                if (norm > 0) for (int d = 0; d < dim; d++) v[d] = (float)(v[d] / norm);
                result[offset + b] = v;
            }
        }
        return result;
    }

    public static float Cosine(float[] a, float[] b)
    {
        float s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    public void Dispose() => _session.Dispose();
}
