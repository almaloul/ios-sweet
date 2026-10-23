// Apple UDIF disk images (.dmg): "koly" trailer + XML plist with "mish" block tables.
// Exposes the decompressed disk as a random-access IByteSource with a small chunk cache.
using System;
using System.Buffers.Binary;
using System.IO.Compression;
using SweetEmu.Core.Compression;

namespace SweetEmu.Core.IO;

public sealed class Udif : IByteSource
{
    const int Sector = 512;

    readonly IByteSource file;
    readonly bool ownsFile;
    readonly Chunk[] chunks;         // sorted by disk offset
    public long Length { get; }
    public IReadOnlyList<(string Name, long Offset, long Length)> Partitions { get; }

    readonly object cacheLock = new();
    readonly LinkedList<(int Index, byte[] Data)> lru = new();
    const int CacheChunks = 128;

    struct Chunk
    {
        public uint Type;
        public long DiskOffset, DiskLength, FileOffset, FileLength;
    }

    public static bool IsUdif(IByteSource src)
    {
        if (src.Length < 512) return false;
        Span<byte> sig = stackalloc byte[4];
        src.ReadAt(src.Length - 512, sig);
        return sig.SequenceEqual("koly"u8);
    }

    public Udif(IByteSource file, bool ownsFile = false)
    {
        this.file = file;
        this.ownsFile = ownsFile;
        var koly = file.ReadBytes(file.Length - 512, 512);
        if (!koly.AsSpan(0, 4).SequenceEqual("koly"u8)) throw new InvalidDataException("UDIF: no koly trailer");
        long dataFork = (long)BinaryPrimitives.ReadUInt64BigEndian(koly.AsSpan(24));
        long xmlOff = (long)BinaryPrimitives.ReadUInt64BigEndian(koly.AsSpan(0xD8));
        long xmlLen = (long)BinaryPrimitives.ReadUInt64BigEndian(koly.AsSpan(0xE0));
        if (xmlLen <= 0 || xmlLen > 256 << 20) throw new InvalidDataException("UDIF: no XML property list");
        var plist = Plist.Read(file.ReadBytes(xmlOff, (int)xmlLen)) as PDict ?? throw new InvalidDataException("UDIF: bad plist");
        var blkx = plist.Dict("resource-fork")?.Array("blkx") ?? throw new InvalidDataException("UDIF: no blkx");

        var list = new List<Chunk>();
        var parts = new List<(string, long, long)>();
        foreach (var e in blkx.OfType<PDict>())
        {
            var mish = e.Get("Data") as byte[];
            if (mish == null || mish.Length < 0xCC || !mish.AsSpan(0, 4).SequenceEqual("mish"u8)) continue;
            long partStart = (long)BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(8));
            long partSectors = (long)BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(16));
            long dataOff = (long)BinaryPrimitives.ReadUInt64BigEndian(mish.AsSpan(24));
            int n = (int)BinaryPrimitives.ReadUInt32BigEndian(mish.AsSpan(0xC8));
            parts.Add((e.Str("Name") ?? e.Str("CFName") ?? "", partStart * Sector, partSectors * Sector));
            for (int i = 0; i < n; i++)
            {
                var c = mish.AsSpan(0xCC + 40 * i, 40);
                uint type = BinaryPrimitives.ReadUInt32BigEndian(c);
                if (type == 0xFFFFFFFF || type == 0x7FFFFFFE) continue;
                list.Add(new Chunk
                {
                    Type = type,
                    DiskOffset = ((long)BinaryPrimitives.ReadUInt64BigEndian(c[8..]) + partStart) * Sector,
                    DiskLength = (long)BinaryPrimitives.ReadUInt64BigEndian(c[16..]) * Sector,
                    FileOffset = dataFork + dataOff + (long)BinaryPrimitives.ReadUInt64BigEndian(c[24..]),
                    FileLength = (long)BinaryPrimitives.ReadUInt64BigEndian(c[32..]),
                });
            }
        }
        list.Sort((a, b) => a.DiskOffset.CompareTo(b.DiskOffset));
        chunks = list.ToArray();
        Length = chunks.Length == 0 ? 0 : chunks[^1].DiskOffset + chunks[^1].DiskLength;
        Partitions = parts;
    }

    public void ReadAt(long offset, Span<byte> buffer)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            long pos = offset + done;
            int ci = Find(pos);
            if (ci < 0)
            {
                // Hole between chunks or past the end: zeros up to the next chunk.
                long next = NextStart(pos);
                int n = (int)Math.Min(buffer.Length - done, next - pos);
                buffer.Slice(done, n).Clear();
                done += n;
                continue;
            }
            ref readonly var c = ref chunks[ci];
            long within = pos - c.DiskOffset;
            int count = (int)Math.Min(buffer.Length - done, c.DiskLength - within);
            switch (c.Type)
            {
                case 0:
                case 2:
                    buffer.Slice(done, count).Clear();
                    break;
                case 1:
                    file.ReadAt(c.FileOffset + within, buffer.Slice(done, count));
                    break;
                default:
                    Decoded(ci).AsSpan((int)within, count).CopyTo(buffer[done..]);
                    break;
            }
            done += count;
        }
    }

    int Find(long pos)
    {
        int lo = 0, hi = chunks.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (pos < chunks[mid].DiskOffset) hi = mid - 1;
            else if (pos >= chunks[mid].DiskOffset + chunks[mid].DiskLength) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    long NextStart(long pos)
    {
        foreach (var c in chunks)
            if (c.DiskOffset > pos) return c.DiskOffset;
        return long.MaxValue;
    }

    byte[] Decoded(int ci)
    {
        lock (cacheLock)
        {
            for (var n = lru.First; n != null; n = n.Next)
            {
                if (n.Value.Index == ci)
                {
                    lru.Remove(n);
                    lru.AddFirst(n);
                    return n.Value.Data;
                }
            }
        }
        var c = chunks[ci];
        var src = file.ReadBytes(c.FileOffset, checked((int)c.FileLength));
        var dst = new byte[c.DiskLength];
        int got = c.Type switch
        {
            0x80000005 => Inflate(src, dst),
            0x80000007 => Lzfse.Decode(src, dst),
            0x80000004 => Adc(src, dst),
            0x80000006 => throw new NotSupportedException("UDIF: bzip2 chunks are not supported"),
            _ => throw new NotSupportedException($"UDIF: chunk type 0x{c.Type:x8}"),
        };
        if (got != dst.Length) throw new InvalidDataException($"UDIF: chunk {ci} decoded to {got} bytes, expected {dst.Length}");
        lock (cacheLock)
        {
            lru.AddFirst((ci, dst));
            if (lru.Count > CacheChunks) lru.RemoveLast();
        }
        return dst;
    }

    static int Inflate(byte[] src, byte[] dst)
    {
        using var z = new ZLibStream(new MemoryStream(src), CompressionMode.Decompress);
        int total = 0;
        while (total < dst.Length)
        {
            int n = z.Read(dst, total, dst.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    // Apple Data Compression (old images).
    static int Adc(byte[] src, byte[] dst)
    {
        int s = 0, d = 0;
        while (s < src.Length && d < dst.Length)
        {
            byte b = src[s];
            if ((b & 0x80) != 0)
            {
                int n = (b & 0x7F) + 1;
                Array.Copy(src, s + 1, dst, d, n);
                s += n + 1;
                d += n;
            }
            else if ((b & 0x40) != 0)
            {
                int n = (b & 0x3F) + 4;
                int off = (src[s + 1] << 8 | src[s + 2]) + 1;
                for (int i = 0; i < n; i++, d++) dst[d] = dst[d - off];
                s += 3;
            }
            else
            {
                int n = ((b & 0x3F) >> 2) + 3;
                int off = ((b & 0x3) << 8 | src[s + 1]) + 1;
                for (int i = 0; i < n; i++, d++) dst[d] = dst[d - off];
                s += 2;
            }
        }
        return d;
    }

    public void Dispose()
    {
        if (ownsFile) file.Dispose();
    }
}
