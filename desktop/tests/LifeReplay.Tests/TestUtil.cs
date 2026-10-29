using System.Buffers.Binary;
using System.Text;
using LifeReplay.Core;

namespace LifeReplay.Tests;

/// <summary>Temporary data directory with a migrated database, deleted after the test.</summary>
public sealed class TempData : IDisposable
{
    public AppPaths Paths { get; }
    public MetadataStore Store { get; }

    public TempData()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lifereplay-tests", Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(dir);
        Paths.EnsureCreated();
        Store = new MetadataStore(Paths.DatabasePath);
        Store.Migrate();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Paths.DataDir, true); } catch { }
    }

    public SegmentRecord AddSegment(string displayId, long start, long end, string status = SegmentStatus.Finalized,
        string? activity = null, string sessionId = "s1", string deviceId = "dev1", long size = 1000, bool writeFile = false)
    {
        // Foreign keys are enforced: the device and session must exist first (as in the real recorder).
        using (var c = Store.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO devices(id, name, platform, created_utc) VALUES($d, $d, 'windows', 0);
                INSERT OR IGNORE INTO sessions(id, device_id, start_utc, status) VALUES($s, $d, $start, 'ended');
                """;
            cmd.Parameters.AddWithValue("$d", deviceId);
            cmd.Parameters.AddWithValue("$s", sessionId);
            cmd.Parameters.AddWithValue("$start", start);
            cmd.ExecuteNonQuery();
        }
        var id = Guid.NewGuid().ToString();
        var rel = $"recordings/test/{displayId}/{id}.mp4";
        var seg = new SegmentRecord
        {
            Id = id, SessionId = sessionId, DeviceId = deviceId, DisplayId = displayId, StartUtcMs = start,
            EndUtcMs = status == SegmentStatus.Recording ? null : end, DurationMs = end - start, FilePath = rel, FileSize = size,
            Width = 1920, Height = 1080, SourceWidth = 1920, SourceHeight = 1080, Fps = 10, Bitrate = 1_000_000, Encoder = "test",
            Status = status, Activity = activity, CreatedUtcMs = start,
        };
        Store.InsertSegment(seg);
        if (writeFile)
        {
            var full = Paths.ToFull(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, Mp4Builder.Build(10_000, 1920, 1080));
        }
        return seg;
    }
}

/// <summary>Builds minimal but structurally valid MP4 files (ftyp, moov/mvhd/trak, mdat) for tests.</summary>
public static class Mp4Builder
{
    public static byte[] Box(string type, params byte[][] children)
    {
        int len = 8 + children.Sum(c => c.Length);
        var b = new byte[len];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)len);
        Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        int pos = 8;
        foreach (var c in children) { c.CopyTo(b, pos); pos += c.Length; }
        return b;
    }

    public static byte[] Build(long durationMs, int width, int height, bool includeMoov = true, bool includeMdat = true)
    {
        var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), new byte[4], Encoding.ASCII.GetBytes("isomavc1"));
        var mvhd = new byte[100];
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(12), 1000);            // timescale
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(16), (uint)durationMs); // duration
        var tkhd = new byte[84];
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(76), (uint)width << 16);
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(80), (uint)height << 16);
        var hdlr = new byte[24];
        Encoding.ASCII.GetBytes("vide").CopyTo(hdlr, 8);
        var trak = Box("trak", Box("tkhd", tkhd), Box("mdia", Box("hdlr", hdlr)));
        var moov = Box("moov", Box("mvhd", mvhd), trak);
        var mdat = Box("mdat", new byte[256]);
        var parts = new List<byte[]> { ftyp };
        if (includeMdat) parts.Add(mdat);
        if (includeMoov) parts.Add(moov);
        return parts.SelectMany(p => p).ToArray();
    }
}
