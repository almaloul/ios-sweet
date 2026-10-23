// HFS+/APFS transparent compression ("com.apple.decmpfs" xattr + optional resource fork).
using System;
using System.Buffers.Binary;
using System.IO.Compression;

namespace SweetEmu.Core.Compression;

public static class Decmpfs
{
    public const uint Magic = 0x636d7066; // "fpmc" little endian

    public static bool InResourceFork(uint type) => type is 4 or 8 or 10 or 12 or 14;

    public static (uint Type, ulong Size) Header(ReadOnlySpan<byte> xattr)
    {
        if (xattr.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(xattr) != Magic)
            throw new InvalidDataException("decmpfs: bad header");
        return (BinaryPrimitives.ReadUInt32LittleEndian(xattr[4..]), BinaryPrimitives.ReadUInt64LittleEndian(xattr[8..]));
    }

    /// <summary>Decompress a file. rsrc returns the com.apple.ResourceFork contents when the data lives there.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> xattr, Func<byte[]> rsrc)
    {
        var (type, size64) = Header(xattr);
        if (size64 > int.MaxValue) throw new InvalidDataException("decmpfs: file too large");
        int size = (int)size64;
        var outBuf = new byte[size];
        var c = xattr[16..];

        if (!InResourceFork(type))
        {
            int n;
            switch (type)
            {
                case 1:
                    c[..size].CopyTo(outBuf);
                    n = size;
                    break;
                case 3:
                    n = c.Length > 0 && (c[0] & 0x0F) == 0x0F ? Stored(c, outBuf, 0) : Zlib(c, outBuf, 0, size);
                    break;
                case 7:
                    n = c.Length > 0 && c[0] == 0x06 ? Stored(c, outBuf, 0) : Lzvn.Decode(c, outBuf, 0, size);
                    break;
                case 9:
                    n = Stored(c, outBuf, 0);
                    break;
                case 11:
                    n = size == 0 ? 0 : c.Length > 0 && c[0] == 0xFF ? Stored(c, outBuf, 0) : Lzfse.Decode(c, outBuf);
                    break;
                case 13:
                    n = c.Length > 0 && c[0] == 0xFF ? Stored(c, outBuf, 0) : Lzbitmap.Decode(c, outBuf, 0, size);
                    break;
                default:
                    throw new NotSupportedException($"decmpfs: compression type {type}");
            }
            if (n != size) throw new InvalidDataException($"decmpfs type {type}: {n} bytes, expected {size}");
            return outBuf;
        }

        byte[] r = rsrc() ?? throw new InvalidDataException("decmpfs: missing resource fork");
        const int Block = 0x10000;
        int blocks = (size + Block - 1) / Block;

        if (type == 4)
        {
            // Classic resource fork: BE header with data offset; the cmpf resource
            // (after a u32 length) is a LE table of {offset, size} pairs.
            int dataOff = (int)BinaryPrimitives.ReadUInt32BigEndian(r);
            int baseOff = dataOff + 4;
            int entries = (int)BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(baseOff));
            if (entries < blocks) throw new InvalidDataException("decmpfs zlib: short block table");
            for (int k = 0; k < blocks; k++)
            {
                int off = (int)BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(baseOff + 4 + 8 * k));
                int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(baseOff + 8 + 8 * k));
                var src = r.AsSpan(baseOff + off, len);
                int expect = Math.Min(Block, size - k * Block);
                int n = (src[0] & 0x0F) == 0x0F ? Stored(src, outBuf, k * Block) : Zlib(src, outBuf, k * Block, expect);
                if (n != expect) throw new InvalidDataException("decmpfs zlib: block size");
            }
            return outBuf;
        }

        // Types 8/10/12/14: (blocks + 1) LE u32 offsets, then the blocks.
        for (int k = 0; k < blocks; k++)
        {
            int off = (int)BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(4 * k));
            int next = (int)BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(4 * k + 4));
            var src = r.AsSpan(off, next - off);
            int expect = Math.Min(Block, size - k * Block);
            int at = k * Block;
            int n = type switch
            {
                8 => src[0] == 0x06 ? Stored(src, outBuf, at) : Lzvn.Decode(src, outBuf, at, expect),
                10 => Stored(src, outBuf, at),
                12 => src[0] == 0xFF ? Stored(src, outBuf, at) : Lzfse.Decode(src, outBuf.AsSpan(at, expect)),
                14 => src[0] == 0xFF ? Stored(src, outBuf, at) : Lzbitmap.Decode(src, outBuf, at, expect),
                _ => throw new NotSupportedException($"decmpfs: compression type {type}"),
            };
            if (n != expect) throw new InvalidDataException($"decmpfs type {type}: block {k} is {n} bytes, expected {expect}");
        }
        return outBuf;
    }

    // Stored variants: one marker byte, then the raw bytes.
    static int Stored(ReadOnlySpan<byte> src, byte[] dst, int at)
    {
        int n = src.Length - 1;
        src.Slice(1, n).CopyTo(dst.AsSpan(at));
        return n;
    }

    static int Zlib(ReadOnlySpan<byte> src, byte[] dst, int at, int expect)
    {
        using var ms = new MemoryStream(src.ToArray(), false);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        int total = 0;
        while (total < expect)
        {
            int n = z.Read(dst, at + total, expect - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
