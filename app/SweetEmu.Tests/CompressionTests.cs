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

    /// Multi-chunk ZBM stream from libzbitmap's compressor (70000 bytes = 3 chunks).
    /// The repetition period restarts at 8 in every chunk; carrying it over from the
    /// previous chunk corrupted bytes after offset 32768 (seen in the iOS 16 dyld cache).
    [Test]
    public static void LzbitmapMultiChunkFromLibzbitmap()
    {
        var src = Convert.FromBase64String("WkJNCasEAACAABoCACUCAC4CAAAAABMAAAAATQD1AAGGAKsA6QD7AKYTJQECQAAFAigAIQD6AAMRE7UAFgBnAASPAAWkBZIABp0ABzkAEAAIWwAJ8hOEAAqwAPUAWRMLeAvqACQAegxPAA12AA7iAA+IAHIAQxNOABAiAFIAmgARmROXADAAEvIAywAbABOEAN4TFN0AeQAVvwAvAM4A1ABAAIUAdwAWF6QAGIkAGQoAkgAoABr5ABuVAE8btgAcNgAdnAC6ADoT9QAeOQAfPAAEAJIfII8gSwBrACGvAMAAOAAiWQA/iwAi3wBGACOYANIjhhMkHQAlTgAmBgBnAIsAJygcACkeAA0Tlz0AExcxKioqKioqUyoTACsrKysrK98ThSwsLCwsLLcsqSw4AC0tLS0tLT0YAACcAA+nAAAuLi4uLi5yAC8vL8ovLy+tAAAwMDAwMDAxMTExMTGvADIyMjIyMjMzMzMzM/zQDgA0NDQ0kjQ0NDQ0NDQ0NDQ0NDQ0NDQyot8ANCIANTU1NesANjY2NkQAwTYbE08AEhM3Nzc3KABNCwA3ODg4OHHgAGQAAEsAOb05OT4TOa8T6ACqAOzEEzo6OjohAJg7ADs7O7QTijs8PDw8bQA9PT09TwDAHwCSAD4+Pj4+az5wPj4+Pj4+Pj4+Pj4+Pj4+Pj4+Pj4+Pj4TPiMsPz8/Pz8/Pz8/Pz8/Pz8/P5oA6ULw+PAw+KAgqPjwgP+CiBQJEoIKCjD/RzRvmfNPNL9md/NUNX9ViPNNBDZDNH+Z81E1/y93NJ+IVfOQOf91N680/1E1/380M0Tz71XzTzT/L7vzbzRfd/OfVfM/NP//azY0T4jzeDf/X13zD1XzjDj/RDNDNA9m8/+hOr9E8/+oOu80/3+q8x80T2bz/080n6rzL7vzhJg5nzTfd/OROf+3O1+EOF+78/+SOY80X2bzpTqzO/8/NC9V899E8y80n3fzr5nzTTOq86U6n6rzVjV/ZnOXOTM0//9Oszv//0vz/x9380fz/3E3M6rzbjY0//+yO280j6rzQDTfZvNfNP8/d/NfNJ9VM5nzjGDzbzT//2k2MzSZM3fzL0Tz7zQzRPOvqvNiNv9A81Q1UzVPd/P/RfNrNl/VXfO3O/9B829m80Y074jzQPNXNf//QfMfu/P/R/NWNf9uNjNm80fz/+80/w9m8480D1WDOP8APA81g4PzoPPJPDP0wvNI80HznzofOjP0Mv+vOB84NB/0MjM0HzRfNB80fzsfO6/0srMzs7MzY/Nh83lTM3NT800vNh82//9C5EQ0pfNQ8/8f9DL//0kv83vzcfP/Qi/z/5/0Mv/fNjMwNjNMREDPPJPzQDNT85bz4DNT80cPPP8fOzM7/0AP83Mzc/NDM0PzjDODOTM5gzODM0NEMDM2/w9HM0fzTA/zPzljaWNjMzczN/8CRIQzNC84MziPNTM1MzkzOT84MzgzOA/0MG82Mzb/fzUzTUTzPzgzOK80MzSv9DD/HzkzOf9BD5Mzk/PjM8PzkDM3TzxfPG83D/QA9G/zgEPzqfNVM0PPPH86vzqfNTM2AAgABCAEIAABCAEAJIQIAADtBAAAgABPAgBeAgBuAgBAnwB7AKYTg0AbAEEJAEKeQrYTyqAxQ0NeQ0NDQ0NDQ0NDQ0MaQ0NDAAADREREREREREREkagAE5JERUVFRUVFRUVFRkZGRkZGRkZG5QC5E0dHR0dHR0dHR/IPPRkAAEhImglIAEnmSQDbAEkAlEoASiwAS0uAAExM5GUAAOAAmwBNTcIAYXUAUABOTkFO9QB0E09PMRNQUPYAmAAFAFFRCFHaAFJSIRMOAM0AYwBTU1RUyQCaAFVVElWSVSQAVlYkE1dXfQBYWBwAWVlaV1r0AADpAPwArQBbW9kABACsAFxcXX9del2yAF5evgBWE19fLABEE6wAIgBgYJ1gYWE/E2JiH2IQAGNj6gBTAGRkwmS3E8kA6GTeAGVlZmbTAAZnZwIAaGglAGlpGABqatxqGAC7AGtr+ACuywBsbGxsbO9sbW1tbW3GAG5ubm5uyQAqAG9vb1ZvbxMAE3BwcHBwGACaOwAAcXFxcXFxcXERcXFxIHFx4nF5cXFxcXFxcXFxcXFxcXFxcQAAAAB7cnJycnJycnJycnJycnNzc3Nzc3Nzc3Nzc3NiAHl0dHR0dHR0dHR0dHRuABi6dHV1dXV1dXV1dXV1dUt1fgB2dnZ2dnZudnZ2dnZ2AFZ2d3d3d3d3d3d3d3d3PRMjeHh4eHh4eHh4eDkTTP4AP/cAeQB5eXm1AC0Aenp6eiN6e3t7e6AAegAwE3x8fHy/iAAAXHx9fX3TfQA2E9sAfn5+fm0TiCKGAAAANQB/f39/aQBmf034gEj4EPgogNj48GhgUCAKDAZIEUIKUAYDA0KICVBQNC9Vu/NkNj9E83s33zSTOf//SvP/TWDAMztP1PQGRMQ4bzn/WfNCXzwPNy82Ny82TzRPNL/0Nf//Rl/znvOU82HzZPNIX/N/ijAvijzfRDz/Rkc3/92EMzc3/481NV83TvO/Ozv/SjT/Xzs7/0E0M5WVM1NT8/+Ag/NINDg4PzU1Nf9/OjpDNP9BQ/OvOjpjYzNE8/9lY/MfRDOTk/P/fHPzsLNEM0ND8y81Nf9NNB82Nl85qQNT8/9DNP//TT437zk5/19E8/9NQzSUk/NCNP9sY/OfRKOj8/+fRPP/LzU1T0Tz/98Ot/OvNTU/NzePOjozRPNUU/OOg/OTk/MfRPP/30RD8280NP+Kg0Qzo6Pz/2tj80Q0Tzc3HzY2Hzk5jzc3/0E0//9KQ0TzzzY2/0k0MzQ0/883Nz9E8/8vOTk1NQ9ENDQfNjbvOTlvNDQfNTVfRPP/30Tz/62j80ZDNP8/Ojr/QzT//3dzM0M0//+io/NENDQ0/784OA81NTNE84mD8x89PB843/TB803zQPP/QR/z/7HzsPNOH/P/kfOQOA84j0RANGPz/2zzYPNADz0PODAPPDM4LzwzOC88Mzz/Qj9OBEQA9Ds/N5MzqjM1z/TJ8/8f9DkzOo86/770yPOh86fzSvNFQ/NCn/NHNX81/0cfQC/zsfNC80fzj/Q4/082fzD0xvNh82XzevOUN4Mzg5Mz00TE8184MzifNzM3j/Qw/0gzQ/Of9DDPODM4zzozMDM2r/Qw/z87MzAzNT80MzRD1DSD82AzY/P/UzNT80IP82gzY/OvuzOwM3Uzc/QwTzczN/9EM0PzfgAIAAECIABCQABAACSICAAAtgAAcBEATwAATwAATwAAgAAQAIF8AMAALwAOALmCXBPIEz0TagAMAMeDAB0ALADBACmDPQCEZgCGhIUUAEEAhpsAOQCH9ADiAIyImQAuEzz/L4jzbzT/UDV/mfNRNT+Z80k0L2bzD2bzZTazOzNV83g9r4jzWjUzd/NONA+7NP+ZOf9ENE80/09V8x+qQ/OPd/NPqvNF88+I83+qQzRvd/OfZjMDAAgQAAKAEAACBECIIAgAAAAGAAAAAAA=");
        var buf = new byte[70000];
        int n = Lzbitmap.Decode(src, buf, 0, buf.Length);
        Assert.Equal(70000, n, "length");
        Assert.Equal("f5c55a0b7f8283d26c75d0565cd7f550983a14ebbce5ff1cfcfc6515d6866c1d",
            Convert.ToHexString(SHA256.HashData(buf)).ToLowerInvariant(), "sha256");
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
