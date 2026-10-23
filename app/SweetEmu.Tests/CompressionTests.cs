using System.Security.Cryptography;
using SweetEmu.Core.Compression;

namespace SweetEmu.Tests;

public static class CompressionTests
{
    // Mixed data: text-like runs, repeats at many distances, random noise.
    static byte[] Sample(int size, int seed)
    {
        var rng = new Random(seed);
        var b = new byte[size];
        string[] words = { "launchd", "SpringBoard", "Library", "Preferences", "com.apple.", "/System/", "0123456789", "\0\0\0\0" };
        int i = 0;
        while (i < size)
        {
            int mode = rng.Next(10);
            if (mode < 5)
            {
                var w = System.Text.Encoding.ASCII.GetBytes(words[rng.Next(words.Length)]);
                for (int k = 0; k < w.Length && i < size; k++) b[i++] = w[k];
            }
            else if (mode < 8 && i > 64)
            {
                int dist = 1 + rng.Next(Math.Min(i, rng.Next(2) == 0 ? 16 : 200000));
                int len = 3 + rng.Next(300);
                for (int k = 0; k < len && i < size; k++, i++) b[i] = b[i - dist];
            }
            else
            {
                int len = rng.Next(64);
                for (int k = 0; k < len && i < size; k++) b[i++] = (byte)rng.Next(256);
            }
        }
        return b;
    }

    static byte[] RefEncode(byte[] data)
    {
        var tool = Assert.Tool("LZFSE_TOOL", "/home/claude/ref/lzfse/build/bin/lzfse");
        var dir = Directory.CreateTempSubdirectory("lzfse");
        try
        {
            string inp = Path.Combine(dir.FullName, "in"), outp = Path.Combine(dir.FullName, "out");
            File.WriteAllBytes(inp, data);
            Assert.Run(tool, "-encode", "-i", inp, "-o", outp);
            return File.ReadAllBytes(outp);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Test]
    public static void LzfseRoundTripsReferenceEncoder()
    {
        // < 4096 bytes: the encoder emits an LZVN block; larger: bvx2 blocks
        // (multiple blocks beyond 10000 matches / 40000 literals).
        foreach (int size in new[] { 1, 7, 100, 3000, 4095, 4096, 5000, 65536, 300000, 4_000_000 })
        {
            var data = Sample(size, size);
            var enc = RefEncode(data);
            var dec = Lzfse.Decode(enc, size);
            Assert.Bytes(data, dec, $"lzfse size {size}");
        }
    }

    [Test]
    public static void LzfseEmptyStream()
    {
        Assert.Equal(0, Lzfse.Decode("bvx$"u8, Span<byte>.Empty), "empty");
    }

    [Test]
    public static void LzfseIncompressibleUsesRawBlock()
    {
        var data = new byte[70000];
        new Random(5).NextBytes(data);
        var enc = RefEncode(data);
        Assert.Bytes(data, Lzfse.Decode(enc, data.Length), "random data");
    }

    [Test]
    public static void LzvnRawStreamFromReferenceEncoder()
    {
        // Take the LZVN payload out of a bvxn block: that is what decmpfs type 7/8 stores.
        var data = Sample(3500, 42);
        var enc = RefEncode(data);
        Assert.Equal(0x6e787662u, BitConverter.ToUInt32(enc, 0), "bvxn magic");
        int raw = BitConverter.ToInt32(enc, 4), payload = BitConverter.ToInt32(enc, 8);
        var outBuf = new byte[raw];
        int n = Lzvn.Decode(enc.AsSpan(12, payload), outBuf, 0, raw);
        Assert.Equal(raw, n, "lzvn size");
        Assert.Bytes(data, outBuf, "lzvn data");
    }

    // dissect.util tests/compression/test_lzbitmap.py vectors (input hex, sha256 of output).
    static readonly (string Hex, string Sha)[] ZbmVectors =
    {
        ("5a424d093100002b0000536d616c6c2066696c657320646f6e2774206765" +
         "7420636f6d7072657373656420617420616c6c2e2e2e0a060000000000",
         "8c929efb5fd28b5b82385b67b408f5e775a4756d7cc6373eebddb8668343ad40"),
        ("5a424d092d0000a0000018000018000018000061616161616161617835ef" +
         "340f0000f10f00000000000000000000000000060000000000",
         "ef56118ff333a8bfeffc346c4987a1a178762570b3eb1d704a2c1e9b3a877561"),
        ("5a424d09d80100df36002301005001007601004c6f72656d20697073756d" +
         "20646f6c6f722073742061657420636e7365636574757220616469706973" +
         "696e67656c69742e517573716566617563627520657820736170656e7661" +
         "6570656c6c6e6573656d6c61637261496e20696475727573206d69707269" +
         "74656c757364206f76616c7354656d7075206c656f7561656e6561646469" +
         "6d206e2074656d706f72506c6e7276697675736672696c61206c636e636d" +
         "657473626962656e64756d67657461732e496c696d6173206e6c6d616c75" +
         "64616c696e616e746567726e6e63707365722e5574206864726572697473" +
         "6d7072766c637373207074656e74747469736f6f73712e41646c696f7261" +
         "6f7175657465727562696f74726370746f686d6f732e0a0a4c0d2c341b41" +
         "3e26113c5f6e805b6b7c65529d967ec4b310edb922ca7b1deca5faf4434a" +
         "fbfa52fb8272b2ffb7016f7fcbc1b9f1373af99e3eb4e94fa9b3bafe39d3" +
         "6959add6f36b55eecdb59d2ec3d1d029fc0055a9cbed016111718114103f" +
         "480147110116011040f125f3ffffffffffffffffffffffffffffffffffff" +
         "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff" +
         "ffffffffffffffff1f002cf68f57d9c576d73bfd7cd3d756000006000000" +
         "0000",
         "73d3dd96ca2e2f0144a117019256d770ee7c6febeaee09b24956c723ae22b529"),
    };

    [Test]
    public static void LzbitmapVectors()
    {
        int i = 0;
        foreach (var (hex, sha) in ZbmVectors)
        {
            var src = Convert.FromHexString(hex);
            var buf = new byte[1 << 20];
            int n = Lzbitmap.Decode(src, buf, 0, buf.Length);
            var got = Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, n))).ToLowerInvariant();
            Assert.Equal(sha, got, $"vector {i++} sha256");
        }
    }

    [Test]
    public static void DecmpfsAttrAndRsrcVariants()
    {
        var data = Sample(150000, 9);
        // type 3: zlib in the xattr (small)
        var small = data.AsSpan(0, 2000).ToArray();
        var z = ZlibCompress(small);
        Assert.Bytes(small, Decmpfs.Decompress(Header(3, small.Length, z), () => null!), "type 3");
        // type 4: zlib blocks in a classic resource fork
        Assert.Bytes(data, Decmpfs.Decompress(Header(4, data.Length, Array.Empty<byte>()), () => ZlibRsrc(data)), "type 4");
        // type 12: LZFSE blocks behind an offset table (blocks from the reference encoder)
        var blocks = new List<byte[]>();
        for (int at = 0; at < data.Length; at += 0x10000)
            blocks.Add(RefEncode(data.AsSpan(at, Math.Min(0x10000, data.Length - at)).ToArray()));
        Assert.Bytes(data, Decmpfs.Decompress(Header(12, data.Length, Array.Empty<byte>()), () => OffsetTable(blocks)), "type 12");
        // type 9 / 1: stored
        Assert.Bytes(small, Decmpfs.Decompress(Header(9, small.Length, [0xCC, .. small]), () => null!), "type 9");
    }

    static byte[] Header(uint type, long size, byte[] payload)
    {
        var h = new byte[16 + payload.Length];
        BitConverter.GetBytes(Decmpfs.Magic).CopyTo(h, 0);
        BitConverter.GetBytes(type).CopyTo(h, 4);
        BitConverter.GetBytes((ulong)size).CopyTo(h, 8);
        payload.CopyTo(h, 16);
        return h;
    }

    static byte[] ZlibCompress(ReadOnlySpan<byte> data)
    {
        var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
            z.Write(data);
        return ms.ToArray();
    }

    static byte[] OffsetTable(List<byte[]> blocks)
    {
        int head = 4 * (blocks.Count + 1);
        var ms = new MemoryStream();
        int off = head;
        foreach (var b in blocks)
        {
            ms.Write(BitConverter.GetBytes(off));
            off += b.Length;
        }
        ms.Write(BitConverter.GetBytes(off));
        foreach (var b in blocks) ms.Write(b);
        return ms.ToArray();
    }

    static byte[] ZlibRsrc(byte[] data)
    {
        var blocks = new List<byte[]>();
        for (int at = 0; at < data.Length; at += 0x10000)
            blocks.Add(ZlibCompress(data.AsSpan(at, Math.Min(0x10000, data.Length - at))));
        // [BE u32 data offset = 0x100][...pad to 0x100][u32 resource length][LE entries][{off,size}...][blocks]
        var ms = new MemoryStream();
        var hdr = new byte[0x100];
        hdr[2] = 1; // 0x00000100 big endian
        ms.Write(hdr);
        ms.Write(new byte[4]);
        ms.Write(BitConverter.GetBytes(blocks.Count));
        int off = 4 + 8 * blocks.Count;
        foreach (var b in blocks)
        {
            ms.Write(BitConverter.GetBytes(off));
            ms.Write(BitConverter.GetBytes(b.Length));
            off += b.Length;
        }
        foreach (var b in blocks) ms.Write(b);
        return ms.ToArray();
    }
}
