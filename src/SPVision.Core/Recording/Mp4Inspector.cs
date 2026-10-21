using System.Buffers.Binary;

namespace SPVision.Core;

/// <summary>What <see cref="Mp4Inspector"/> found in a file.</summary>
/// <param name="IsValid">A complete ISO MP4: <c>ftyp</c>, <c>moov</c> with at least one video sample, and media data.</param>
/// <param name="Duration">Movie duration from <c>mvhd</c> (zero when unknown).</param>
/// <param name="VideoSamples">Video frames listed in the sample tables.</param>
/// <param name="Problem">Why the file is not valid (Turkish, for the user); null when valid.</param>
public sealed record Mp4Info(bool IsValid, TimeSpan Duration, long VideoSamples, string? Problem);

/// <summary>
/// Checks that a saved recording is a real, finished MP4 before the user is told "Kayıt kaydedildi". Reads only the
/// box headers (ISO/IEC 14496-12): the top-level boxes must include <c>ftyp</c>, a non-empty <c>mdat</c> and a
/// <c>moov</c> (written last, on finalization) whose video track lists samples. Hikvision's own "IMKH" stream files
/// (what NET_DVR_SaveRealData writes) and empty or unfinished files are rejected.
/// </summary>
public static class Mp4Inspector
{
    /// <summary>Largest moov read into memory (a 24 h recording's tables stay far below this).</summary>
    const long MaxMoovSize = 64 * 1024 * 1024;

    public static Mp4Info Inspect(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Inspect(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Invalid($"dosya okunamadı ({e.Message})");
        }
    }

    public static Mp4Info Inspect(Stream stream)
    {
        var length = stream.Length;
        if (length == 0)
        {
            return Invalid("dosya boş");
        }
        Span<byte> header = stackalloc byte[16];
        bool ftyp = false;
        long mdat = 0;
        byte[]? moov = null;
        long position = 0;
        while (position + 8 <= length)
        {
            stream.Position = position;
            if (stream.ReadAtLeast(header[..8], 8, throwOnEndOfStream: false) < 8)
            {
                break;
            }
            if (position == 0 && header[..4].SequenceEqual("IMKH"u8))
            {
                return Invalid("Hikvision'a özel akış dosyası (standart MP4 değil)");
            }
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = header.Slice(4, 4);
            int headerSize = 8;
            if (size == 1)
            {
                if (stream.ReadAtLeast(header[8..], 8, throwOnEndOfStream: false) < 8)
                {
                    return Invalid("dosya yarım kalmış");
                }
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = length - position; // box runs to the end of the file
            }
            if (size < headerSize || position + size > length)
            {
                return Invalid(position == 0 ? "MP4 başlığı yok" : "dosya yarım kalmış");
            }
            if (position == 0 && !type.SequenceEqual("ftyp"u8))
            {
                return Invalid("MP4 başlığı yok");
            }
            if (type.SequenceEqual("ftyp"u8))
            {
                ftyp = true;
            }
            else if (type.SequenceEqual("mdat"u8))
            {
                mdat += size - headerSize;
            }
            else if (type.SequenceEqual("moov"u8))
            {
                if (size > MaxMoovSize)
                {
                    return Invalid("MP4 dizini okunamayacak kadar büyük");
                }
                moov = new byte[size - headerSize];
                stream.ReadExactly(moov);
            }
            position += size;
        }
        if (!ftyp)
        {
            return Invalid("MP4 başlığı yok");
        }
        if (moov is null)
        {
            return Invalid("dosya tamamlanmamış (MP4 dizini yazılmamış)");
        }
        if (mdat == 0)
        {
            return Invalid("dosyada görüntü verisi yok");
        }
        var duration = ReadDuration(moov);
        long samples = 0;
        foreach (var trak in Children(moov, "trak"u8))
        {
            if (Find(trak, "mdia"u8) is { } mdia && IsVideo(mdia) &&
                Find(mdia, "minf"u8) is { } minf && Find(minf, "stbl"u8) is { } stbl && Find(stbl, "stsz"u8) is { } stsz &&
                stsz.Length >= 12)
            {
                samples += BinaryPrimitives.ReadUInt32BigEndian(stsz.AsSpan(8));
            }
        }
        return samples == 0 ? Invalid("dosyada görüntü karesi yok") with { Duration = duration } : new Mp4Info(true, duration, samples, null);
    }

    static Mp4Info Invalid(string problem) => new(false, TimeSpan.Zero, 0, problem);

    static bool IsVideo(byte[] mdia) =>
        Find(mdia, "hdlr"u8) is { Length: >= 12 } hdlr && hdlr.AsSpan(8, 4).SequenceEqual("vide"u8);

    static TimeSpan ReadDuration(byte[] moov)
    {
        if (Find(moov, "mvhd"u8) is not { Length: >= 20 } mvhd)
        {
            return TimeSpan.Zero;
        }
        uint timescale;
        ulong duration;
        if (mvhd[0] == 1)
        {
            if (mvhd.Length < 32)
            {
                return TimeSpan.Zero;
            }
            timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(20));
            duration = BinaryPrimitives.ReadUInt64BigEndian(mvhd.AsSpan(24));
        }
        else
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(12));
            duration = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(16));
        }
        return timescale == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)duration / timescale);
    }

    /// <summary>Payload of the first child box of the given type.</summary>
    static byte[]? Find(byte[] parent, ReadOnlySpan<byte> type)
    {
        foreach (var (boxType, start, size) in Boxes(parent))
        {
            if (type.SequenceEqual(parent.AsSpan(boxType, 4)))
            {
                return parent.AsSpan(start, size).ToArray();
            }
        }
        return null;
    }

    static IEnumerable<byte[]> Children(byte[] parent, byte[] type)
    {
        foreach (var (boxType, start, size) in Boxes(parent))
        {
            if (parent.AsSpan(boxType, 4).SequenceEqual(type))
            {
                yield return parent.AsSpan(start, size).ToArray();
            }
        }
    }

    static IEnumerable<byte[]> Children(byte[] parent, ReadOnlySpan<byte> type) => Children(parent, type.ToArray());

    /// <summary>(offset of the type field, payload offset, payload size) of each child box.</summary>
    static List<(int Type, int Start, int Size)> Boxes(byte[] parent)
    {
        var boxes = new List<(int, int, int)>();
        int position = 0;
        while (position + 8 <= parent.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(parent.AsSpan(position));
            int headerSize = 8;
            if (size == 1 && position + 16 <= parent.Length)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(parent.AsSpan(position + 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = parent.Length - position;
            }
            if (size < headerSize || position + size > parent.Length)
            {
                break;
            }
            boxes.Add((position + 4, position + headerSize, (int)size - headerSize));
            position += (int)size;
        }
        return boxes;
    }
}
