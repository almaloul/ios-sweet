// LZBITMAP decoder (Apple's "ZBM" format used for APFS transparent compression,
// decmpfs types 13/14). Format as documented by libzbitmap (Ernesto A. Fernandez)
// and apfs-fuse; written from that description.
//
// Stream: "ZBM\x09", then chunks. Chunk header: u24 compressed size (incl. header),
// u24 uncompressed size. Uncompressed size 0 = end; compressed == uncompressed + 6
// = stored. Otherwise three more u24 offsets (distances, bitmaps, tokens, all
// relative to the chunk start); literals start at chunk offset 15. The last 17
// bytes of the chunk hold 12 packed 10-bit (bitmap byte, distance kind) entries
// for token values 3..14. Tokens are nibbles; each emits 8 output bytes, one per
// bitmap bit (1 = literal, 0 = copy from `distance` back).
using System;

namespace SweetEmu.Core.Compression;

public static class Lzbitmap
{
    /// <summary>Decode src into dst (starting at dstPos, up to maxOut bytes). Returns bytes written.</summary>
    public static int Decode(ReadOnlySpan<byte> src, Span<byte> dst, int dstPos, int maxOut)
    {
        if (src.Length < 4 || src[0] != (byte)'Z' || src[1] != (byte)'B' || src[2] != (byte)'M' || src[3] != 9)
            throw new InvalidDataException("LZBITMAP: bad magic");
        int s = 4, d = dstPos, end = dstPos + maxOut;
        int distance = 8;
        Span<byte> mapBitmap = stackalloc byte[15];
        Span<byte> mapKind = stackalloc byte[15];

        while (true)
        {
            Need(src, s + 6);
            int csize = U24(src, s), usize = U24(src, s + 3);
            if (usize == 0) break;
            if (csize == usize + 6)
            {
                Need(src, s + csize);
                if (d + usize > end) throw new InvalidDataException("LZBITMAP: output overflow");
                src.Slice(s + 6, usize).CopyTo(dst[d..]);
                d += usize;
                s += csize;
                continue;
            }
            Need(src, s + csize);
            if (csize < 15 + 17) throw new InvalidDataException("LZBITMAP: short chunk");
            var c = src.Slice(s, csize);
            int distOff = U24(c, 6), bmOff = U24(c, 9), tokOff = U24(c, 12), litOff = 15;
            int tokEnd = csize - 17;
            if (distOff > csize || bmOff > csize || tokOff > tokEnd) throw new InvalidDataException("LZBITMAP: bad offsets");

            // 12 x 10-bit entries in the last 17 bytes (little endian bit order).
            int bitPos = 0;
            for (int i = 0; i < 15; i++)
            {
                if (i < 3)
                {
                    mapBitmap[i] = 0;
                    mapKind[i] = (byte)i;
                    continue;
                }
                int v = 0;
                for (int b = 0; b < 10; b++, bitPos++)
                    v |= ((c[tokEnd + (bitPos >> 3)] >> (bitPos & 7)) & 1) << b;
                mapBitmap[i] = (byte)(v & 0xff);
                mapKind[i] = (byte)((v >> 8) & 3);
            }

            int nibble = tokOff * 2, nibbleEnd = tokEnd * 2;
            int remaining = usize;
            int prev = -1;
            while (remaining > 0)
            {
                int idx;
                if (prev >= 0)
                {
                    idx = prev;
                    prev = -1;
                }
                else
                {
                    if (nibble >= nibbleEnd) break;
                    idx = Nib(c, nibble++);
                }
                if (idx == 0xF) throw new InvalidDataException("LZBITMAP: bad token");

                int repeat;
                int next = nibble < nibbleEnd ? Nib(c, nibble++) : -1;
                if (next != 0xF)
                {
                    prev = next;    // -1 at the very end of the token stream
                    repeat = 1;
                }
                else
                {
                    int total = 4;
                    int r = 0xF;
                    while (r == 0xF)
                    {
                        if (nibble >= nibbleEnd) throw new InvalidDataException("LZBITMAP: truncated repeat");
                        r = Nib(c, nibble++);
                        total += r;
                    }
                    repeat = total;
                }

                for (int k = 0; k < repeat && remaining > 0; k++)
                {
                    int bitmap, kind = mapKind[idx];
                    if (idx < 3)
                    {
                        if (bmOff >= csize) throw new InvalidDataException("LZBITMAP: bitmap overrun");
                        bitmap = c[bmOff++];
                    }
                    else bitmap = mapBitmap[idx];
                    if (kind == 1)
                    {
                        distance = c[distOff++];
                    }
                    else if (kind == 2)
                    {
                        distance = c[distOff] | (c[distOff + 1] << 8);
                        distOff += 2;
                    }
                    for (int b = 0; b < 8 && remaining > 0; b++, remaining--)
                    {
                        if (d >= end) throw new InvalidDataException("LZBITMAP: output overflow");
                        if ((bitmap & 1) != 0)
                        {
                            dst[d++] = c[litOff++];
                        }
                        else
                        {
                            if (distance == 0 || distance > d) throw new InvalidDataException("LZBITMAP: bad distance");
                            dst[d] = dst[d - distance];
                            d++;
                        }
                        bitmap >>= 1;
                    }
                }
                if (prev == -1 && nibble >= nibbleEnd && remaining > 0) break;
            }
            if (remaining != 0) throw new InvalidDataException("LZBITMAP: chunk ended early");
            s += csize;
        }
        return d - dstPos;
    }

    static int Nib(ReadOnlySpan<byte> c, int n) => (c[n >> 1] >> ((n & 1) * 4)) & 0xF;

    static int U24(ReadOnlySpan<byte> b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16);

    static void Need(ReadOnlySpan<byte> src, int n)
    {
        if (n > src.Length) throw new InvalidDataException("LZBITMAP: truncated");
    }
}
