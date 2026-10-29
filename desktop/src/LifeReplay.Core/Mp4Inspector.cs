using System.Buffers.Binary;

namespace LifeReplay.Core;

public sealed record Mp4Info(bool IsValid, string? Error, long DurationMs, int Width, int Height, bool Fragmented, long FileSize, int VideoTracks)
{
    public static Mp4Info Invalid(string error, long size = 0) => new(false, error, 0, 0, 0, false, size, 0);
}

/// <summary>
/// Structural integrity check for MP4 files: walks the top-level boxes, requires ftyp + moov + media data, and reads
/// duration and video size from mvhd/tkhd. A file cut off mid-write (crash, power loss) fails this check.
/// </summary>
public static class Mp4Inspector
{
    private const int MaxMoovBytes = 64 * 1024 * 1024;

    public static Mp4Info Inspect(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Inspect(fs);
        }
        catch (Exception ex)
        {
            return Mp4Info.Invalid($"cannot read file: {ex.Message}");
        }
    }

    public static Mp4Info Inspect(Stream s)
    {
        long len = s.Length;
        if (len < 16) return Mp4Info.Invalid("file too small", len);
        bool ftyp = false, mdat = false, moof = false;
        byte[]? moov = null;
        long pos = 0;
        Span<byte> header = stackalloc byte[16];
        int boxIndex = 0;
        while (pos < len)
        {
            if (len - pos < 8) return Mp4Info.Invalid($"trailing {len - pos} bytes after last box", len);
            s.Position = pos;
            ReadExactly(s, header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = System.Text.Encoding.ASCII.GetString(header.Slice(4, 4));
            int headerSize = 8;
            if (size == 1)
            {
                if (len - pos < 16) return Mp4Info.Invalid("truncated large box header", len);
                ReadExactly(s, header.Slice(8, 8));
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = len - pos; // box extends to end of file
            }
            if (size < headerSize) return Mp4Info.Invalid($"invalid size for box '{type}'", len);
            if (pos + size > len) return Mp4Info.Invalid($"box '{type}' is truncated ({pos + size - len} bytes missing)", len);
            if (boxIndex == 0 && type != "ftyp") return Mp4Info.Invalid($"first box is '{type}', expected 'ftyp'", len);

            switch (type)
            {
                case "ftyp": ftyp = true; break;
                case "mdat": mdat = true; break;
                case "moof": moof = true; break;
                case "moov":
                    long bodyLen = size - headerSize;
                    if (bodyLen > MaxMoovBytes) return Mp4Info.Invalid("moov box unreasonably large", len);
                    moov = new byte[bodyLen];
                    s.Position = pos + headerSize;
                    ReadExactly(s, moov);
                    break;
            }
            pos += size;
            boxIndex++;
        }
        if (!ftyp) return Mp4Info.Invalid("missing ftyp", len);
        if (moov == null) return Mp4Info.Invalid("missing moov (file was not finalized)", len);
        if (!mdat && !moof) return Mp4Info.Invalid("no media data", len);

        long durationMs = 0;
        int width = 0, height = 0, videoTracks = 0;
        bool fragmented = moof;
        uint timescale = 0;
        foreach (var (type, body) in Children(moov))
        {
            if (type == "mvhd") { durationMs = ParseMvhdDurationMs(body); timescale = ParseTimescale(body); }
            else if (type == "mvex")
            {
                fragmented = true;
                // Fragmented files (e.g. from the iPhone) keep the total duration in mvex/mehd.
                foreach (var (t2, b2) in Children(body))
                {
                    if (t2 != "mehd" || timescale == 0 || b2.Length < 8) continue;
                    ulong fragDuration = b2[0] == 1 && b2.Length >= 12 ? BinaryPrimitives.ReadUInt64BigEndian(b2.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(b2.AsSpan(4));
                    long ms = (long)(fragDuration * 1000UL / timescale);
                    if (ms > durationMs) durationMs = ms;
                }
            }
            else if (type == "trak")
            {
                var (w, h, isVideo) = ParseTrak(body);
                if (isVideo) { videoTracks++; if (width == 0) { width = w; height = h; } }
            }
        }
        if (videoTracks == 0) return new Mp4Info(false, "no video track", durationMs, 0, 0, fragmented, len, 0);
        return new Mp4Info(true, null, durationMs, width, height, fragmented, len, videoTracks);
    }

    private static void ReadExactly(Stream s, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer[read..]);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    private static IEnumerable<(string Type, byte[] Body)> Children(byte[] data)
    {
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            int hdr = 8;
            if (size == 1 && pos + 16 <= data.Length) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8)); hdr = 16; }
            else if (size == 0) size = data.Length - pos;
            if (size < hdr || pos + size > data.Length) yield break;
            yield return (type, data.AsSpan(pos + hdr, (int)size - hdr).ToArray());
            pos += (int)size;
        }
    }

    private static long ParseMvhdDurationMs(byte[] b)
    {
        if (b.Length < 20) return 0;
        int version = b[0];
        uint timescale;
        ulong duration;
        if (version == 1)
        {
            if (b.Length < 32) return 0;
            timescale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(20));
            duration = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(24));
        }
        else
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12));
            duration = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(16));
        }
        if (timescale == 0 || duration == ulong.MaxValue || duration == uint.MaxValue) return 0;
        return (long)(duration * 1000UL / timescale);
    }

    private static uint ParseTimescale(byte[] mvhd)
    {
        if (mvhd.Length < 24) return 0;
        return mvhd[0] == 1 ? BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(20)) : BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(12));
    }

    private static (int W, int H, bool IsVideo) ParseTrak(byte[] trak)
    {
        int w = 0, h = 0;
        bool video = false;
        foreach (var (type, body) in Children(trak))
        {
            if (type == "tkhd")
            {
                int off = body.Length > 0 && body[0] == 1 ? 88 : 76; // width/height are the last 8 bytes
                if (body.Length >= off + 8)
                {
                    w = (int)(BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(off)) >> 16);
                    h = (int)(BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(off + 4)) >> 16);
                }
            }
            else if (type == "mdia")
            {
                foreach (var (t2, b2) in Children(body))
                    if (t2 == "hdlr" && b2.Length >= 12 && System.Text.Encoding.ASCII.GetString(b2, 8, 4) == "vide") video = true;
            }
        }
        return (w, h, video);
    }
}
