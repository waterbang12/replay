using System.Security.Cryptography;
using LifeReplay.Core;

namespace LifeReplay.Analysis;

/// <summary>
/// Downloads the topic model on first use into &lt;data dir&gt;/models and verifies it. Files are pinned to an exact
/// Hugging Face revision and SHA-256, so a changed or tampered file is rejected.
/// Model: sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2 (Apache-2.0), ONNX int8 export by Xenova.
/// </summary>
public sealed class ModelStore
{
    public const string ModelName = "paraphrase-multilingual-MiniLM-L12-v2";
    private const string Revision = "2c4055b12046f11709e9df2c122e59ffbdc2f900";
    private const string BaseUrl = "https://huggingface.co/Xenova/paraphrase-multilingual-MiniLM-L12-v2/resolve/" + Revision + "/";

    private static readonly (string RemotePath, string LocalName, string Sha256, long Bytes)[] Files =
    [
        ("onnx/model_quantized.onnx", "model_quantized.onnx", "66fc00f5f29afcaff34092e1bdd20008ca3918265a82fb9695a551e510cc4ebc", 118_000_000),
        ("tokenizer.json", "tokenizer.json", "b60b6b43406a48bf3638526314f3d232d97058bc93472ff2de930d43686fa441", 17_000_000),
    ];

    public string Directory { get; }
    public string ModelPath => Path.Combine(Directory, "model_quantized.onnx");
    public string TokenizerPath => Path.Combine(Directory, "tokenizer.json");
    public static long DownloadBytes => Files.Sum(f => f.Bytes);

    public ModelStore(AppPaths paths) : this(Path.Combine(paths.DataDir, "models", ModelName)) { }
    public ModelStore(string directory) { Directory = directory; }

    public bool IsInstalled => File.Exists(ModelPath) && File.Exists(TokenizerPath);

    /// <summary>Downloads missing files. <paramref name="progress"/> receives 0..1.</summary>
    public async Task InstallAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(Directory);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        long total = DownloadBytes, done = 0;
        foreach (var (remote, local, sha, bytes) in Files)
        {
            var target = Path.Combine(Directory, local);
            if (File.Exists(target) && Sha256(target) == sha) { done += bytes; progress?.Report(done / (double)total); continue; }
            var temp = target + ".download";
            using (var response = await http.GetAsync(BaseUrl + remote, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(temp);
                var buffer = new byte[1 << 16];
                int n;
                long fileDone = 0;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    fileDone += n;
                    progress?.Report(Math.Min(1, (done + Math.Min(fileDone, bytes)) / (double)total));
                }
            }
            var actual = Sha256(temp);
            if (actual != sha)
            {
                File.Delete(temp);
                throw new InvalidDataException($"{local}: checksum mismatch (expected {sha}, got {actual}); not installed");
            }
            File.Move(temp, target, overwrite: true);
            done += bytes;
            Log.Info($"Topic model: installed {local}");
        }
        progress?.Report(1);
    }

    public void Uninstall()
    {
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
    }

    private static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}
