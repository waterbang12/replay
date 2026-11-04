namespace LifeReplay.Core;

/// <summary>Output size and bitrate for a capture source, plus the storage estimate that follows from them.</summary>
public sealed record EncodingPlan(int SourceWidth, int SourceHeight, int Width, int Height, int Fps, int BitrateBps)
{
    /// <summary>Bits per pixel per frame for screen content (text and UI compress well).</summary>
    public static double BitsPerPixel(string quality) => quality switch
    {
        "low" => 0.04,
        "high" => 0.12,
        _ => 0.07,
    };

    public static EncodingPlan Compute(int sourceWidth, int sourceHeight, RecorderConfig cfg)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new ArgumentException("Invalid source size");
        double scale = 1.0;
        if (cfg.MaxOutputHeight > 0 && sourceHeight > cfg.MaxOutputHeight)
            scale = (double)cfg.MaxOutputHeight / sourceHeight;
        // H.264 hardware encoders commonly top out around 4096 wide.
        if (sourceWidth * scale > 4096) scale = 4096.0 / sourceWidth;
        int w = Even((int)Math.Round(sourceWidth * scale));
        int h = Even((int)Math.Round(sourceHeight * scale));
        int bitrate = cfg.BitrateKbps > 0
            ? cfg.BitrateKbps * 1000
            : (int)Math.Clamp(w * (double)h * cfg.Fps * BitsPerPixel(cfg.Quality), 300_000, 50_000_000);
        return new EncodingPlan(sourceWidth, sourceHeight, w, h, cfg.Fps, bitrate);
    }

    private static int Even(int v) => Math.Max(2, v - (v % 2));

    /// <summary>
    /// Storage per display per hour of recording. Media Foundation's H.264 encoders run at a constant bitrate
    /// here, so this matches real usage closely (measured: 46.6 MB per 5 minutes at 1.31 Mbit/s).
    /// </summary>
    public long MaxBytesPerHour => (long)(BitrateBps / 8.0 * 3600);

    public static long EstimateMaxBytesPerHour(IEnumerable<EncodingPlan> plans) => plans.Sum(p => p.MaxBytesPerHour);
}
