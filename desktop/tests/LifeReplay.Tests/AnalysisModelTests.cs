using LifeReplay.Analysis;

namespace LifeReplay.Tests;

/// <summary>
/// Checks against the real model. Runs when the model files exist in LIFEREPLAY_TEST_MODEL_DIR or the default
/// data folder (downloaded on first use by the viewer); otherwise the tests return early.
/// </summary>
public class AnalysisModelTests
{
    private static ModelStore? Store()
    {
        var dir = Environment.GetEnvironmentVariable("LIFEREPLAY_TEST_MODEL_DIR");
        var store = dir != null ? new ModelStore(dir) : new ModelStore(Core.AppPaths.Resolve());
        return store.IsInstalled ? store : null;
    }

    [Fact]
    public void TokenizerMatchesXlmRobertaIds()
    {
        var s = Store();
        if (s == null) return;
        var tok = UnigramTokenizer.Load(s.TokenizerPath);
        // Reference ids from the Hugging Face XLM-R tokenizer: <s> ▁Hello ▁world </s>
        Assert.Equal([0, 35378, 8999, 2], tok.Encode("Hello world"));
        var korean = tok.Encode("안녕하세요 세계");
        Assert.Equal(0, korean[0]);
        Assert.Equal(2, korean[^1]);
        Assert.DoesNotContain(tok.UnkId, korean);
    }

    [Fact]
    public void RelatedTitlesAreCloserThanUnrelated()
    {
        var s = Store();
        if (s == null) return;
        using var e = new SentenceEmbedder(s.ModelPath, s.TokenizerPath);
        var v = e.Embed([
            "pwn.college - Program Misuse",                  // 0
            "binary exploitation challenge, privileged mode", // 1
            "Introduction to point-set topology.pdf",         // 2
            "위상수학 강의 노트",                                // 3 "topology lecture notes"
            "YouTube - cooking pasta at home",                // 4
        ]);
        Assert.Equal(384, v[0].Length);
        float pwnExploit = SentenceEmbedder.Cosine(v[0], v[1]);
        float pwnCooking = SentenceEmbedder.Cosine(v[0], v[4]);
        float topoKorean = SentenceEmbedder.Cosine(v[2], v[3]);
        float topoCooking = SentenceEmbedder.Cosine(v[2], v[4]);
        Assert.True(pwnExploit > pwnCooking, $"{pwnExploit} vs {pwnCooking}");
        Assert.True(topoKorean > topoCooking, $"{topoKorean} vs {topoCooking}");
    }
}
