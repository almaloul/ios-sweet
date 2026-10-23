// Minimal Zip64-aware reader: central directory + random access to stored entries
// (the large DMGs inside an IPSW are stored, so they are read in place).
using System;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SweetEmu.Core.IO;

public sealed class ZipFile : IDisposable
{
    readonly IByteSource src;
    public IReadOnlyList<Entry> Entries { get; }

    public sealed class Entry
    {
        public required string Name { get; init; }
        public ushort Method { get; init; }
        public long CompressedSize { get; init; }
        public long Size { get; init; }
        internal long LocalHeader { get; init; }
    }

    public ZipFile(string path) : this(new FileSource(path)) { }

    public ZipFile(IByteSource src)
    {
        this.src = src;
        // End of central directory: last 22..65557 bytes.
        int tail = (int)Math.Min(src.Length, 65557);
        var t = src.ReadBytes(src.Length - tail, tail);
        int eocd = -1;
        for (int i = tail - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(i)) == 0x06054b50) { eocd = i; break; }
        }
        if (eocd < 0) throw new InvalidDataException("not a zip file (no end of central directory)");
        long count = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(eocd + 10));
        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(eocd + 12));
        long cdOff = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(eocd + 16));
        // Zip64 locator just before the EOCD.
        if (eocd >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(eocd - 20)) == 0x07064b50)
        {
            long z64 = (long)BinaryPrimitives.ReadUInt64LittleEndian(t.AsSpan(eocd - 12));
            var r = src.ReadBytes(z64, 56);
            if (BinaryPrimitives.ReadUInt32LittleEndian(r) == 0x06064b50)
            {
                count = (long)BinaryPrimitives.ReadUInt64LittleEndian(r.AsSpan(32));
                cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(r.AsSpan(40));
                cdOff = (long)BinaryPrimitives.ReadUInt64LittleEndian(r.AsSpan(48));
            }
        }
        var cd = src.ReadBytes(cdOff, checked((int)cdSize));
        var list = new List<Entry>();
        int p = 0;
        for (long i = 0; i < count && p + 46 <= cd.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p)) != 0x02014b50) throw new InvalidDataException("zip: bad central directory");
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 8));
            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 10));
            long csize = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p + 20));
            long usize = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p + 24));
            int nlen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 28));
            int xlen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 30));
            int clen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 32));
            long local = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p + 42));
            string name = ((flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.Latin1).GetString(cd, p + 46, nlen);
            // Zip64 extended information: only the fields that overflowed are present, in this order.
            int x = p + 46 + nlen, xend = x + xlen;
            while (x + 4 <= xend)
            {
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(x));
                ushort sz = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(x + 2));
                if (id == 1)
                {
                    int q = x + 4;
                    if (usize == 0xFFFFFFFF) { usize = (long)BinaryPrimitives.ReadUInt64LittleEndian(cd.AsSpan(q)); q += 8; }
                    if (csize == 0xFFFFFFFF) { csize = (long)BinaryPrimitives.ReadUInt64LittleEndian(cd.AsSpan(q)); q += 8; }
                    if (local == 0xFFFFFFFF) { local = (long)BinaryPrimitives.ReadUInt64LittleEndian(cd.AsSpan(q)); }
                }
                x += 4 + sz;
            }
            list.Add(new Entry { Name = name, Method = method, CompressedSize = csize, Size = usize, LocalHeader = local });
            p += 46 + nlen + xlen + clen;
        }
        Entries = list;
    }

    public Entry? Find(string name) =>
        Entries.FirstOrDefault(e => e.Name == name) ??
        Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    long DataOffset(Entry e)
    {
        var h = src.ReadBytes(e.LocalHeader, 30);
        if (BinaryPrimitives.ReadUInt32LittleEndian(h) != 0x04034b50) throw new InvalidDataException($"zip: bad local header for {e.Name}");
        return e.LocalHeader + 30 + BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(26)) + BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(28));
    }

    /// Random access to a stored entry, or null when it is compressed.
    public IByteSource? OpenStored(Entry e) =>
        e.Method == 0 ? new SubSource(src, DataOffset(e), e.Size) : null;

    /// Sequential stream of the uncompressed entry.
    public Stream OpenRead(Entry e)
    {
        var raw = new SubSource(src, DataOffset(e), e.CompressedSize).AsStream();
        return e.Method switch
        {
            0 => raw,
            8 => new DeflateStream(raw, CompressionMode.Decompress),
            _ => throw new NotSupportedException($"zip: compression method {e.Method} ({e.Name})"),
        };
    }

    public byte[] ReadAll(Entry e)
    {
        using var s = OpenRead(e);
        var b = new byte[checked((int)e.Size)];
        s.ReadExactly(b);
        return b;
    }

    public void Dispose() => src.Dispose();
}
