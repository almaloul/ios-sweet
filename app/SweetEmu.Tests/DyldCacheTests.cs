using System.Buffers.Binary;
using System.Text;
using SweetEmu.Core.Debug;

namespace SweetEmu.Tests;

public static class DyldCacheTests
{
    static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    static void W64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v);
    static void Str(byte[] b, int o, string s) => Encoding.ASCII.GetBytes(s + "\0").CopyTo(b, o);

    static void Nlist(byte[] b, int o, uint strx, ulong value)
    {
        W32(b, o, strx);
        b[o + 4] = 0x0f; // N_SECT | N_EXT
        b[o + 5] = 1;
        W64(b, o + 8, value);
    }

    /// A one-image split cache: main file (header, mapping, image list, the
    /// image's Mach-O with __TEXT/__LINKEDIT/LC_SYMTAB) + .symbols (locals).
    static Dictionary<string, byte[]> MakeCache()
    {
        const ulong Base = 0x180000000;
        var main = new byte[0x10000];
        Str(main, 0, "dyld_v1   arm64");
        W32(main, 0x10, 0x200); W32(main, 0x14, 1);          // mappings
        W32(main, 0x1c0, 0x240); W32(main, 0x1c4, 1);        // images
        W64(main, 0x200, Base); W64(main, 0x208, 0x10000); W64(main, 0x210, 0);
        W64(main, 0x240, Base + 0x1000); W32(main, 0x240 + 24, 0x300);
        Str(main, 0x300, "/usr/lib/libtest.dylib");
        // Mach-O at 0x1000
        int m = 0x1000;
        W32(main, m, 0xfeedfacf); W32(main, m + 16, 3); W32(main, m + 20, 72 + 72 + 24);
        int c = m + 32;
        W32(main, c, 0x19); W32(main, c + 4, 72); Str(main, c + 8, "__TEXT");
        W64(main, c + 24, Base + 0x1000); W64(main, c + 32, 0x2000);
        c += 72;
        W32(main, c, 0x19); W32(main, c + 4, 72); Str(main, c + 8, "__LINKEDIT");
        W64(main, c + 24, Base + 0x8000); W64(main, c + 32, 0x1000); W64(main, c + 40, 0x8000);
        c += 72;
        W32(main, c, 0x2); W32(main, c + 4, 24); W32(main, c + 8, 0x8000); W32(main, c + 12, 2); W32(main, c + 16, 0x8100); W32(main, c + 20, 0x40);
        Nlist(main, 0x8000, 1, Base + 0x1100);
        Nlist(main, 0x8010, 11, Base + 0x1800);
        Str(main, 0x8101, "_exported");
        Str(main, 0x810b, "_other");

        var sym = new byte[0x1000];
        Str(sym, 0, "dyld_v1   arm64");
        W32(sym, 0x10, 0x200); W32(sym, 0x14, 0);
        W64(sym, 0x48, 0x400); W64(sym, 0x50, 0x200);
        int ls = 0x400;
        W32(sym, ls, 24); W32(sym, ls + 4, 1);               // nlist
        W32(sym, ls + 8, 40); W32(sym, ls + 12, 16);         // strings
        W32(sym, ls + 16, 56); W32(sym, ls + 20, 1);         // entries
        sym[ls + 24 + 4] = 0x0e; sym[ls + 24 + 5] = 1; W32(sym, ls + 24, 1); W64(sym, ls + 24 + 8, Base + 0x1400);
        Str(sym, ls + 41, "_local_fn");
        W64(sym, ls + 56, 0x1000); W32(sym, ls + 64, 0); W32(sym, ls + 68, 1);
        return new() { ["dyld_shared_cache_arm64"] = main, ["dyld_shared_cache_arm64.symbols"] = sym };
    }

    [Test]
    public static void SymbolicatesSyntheticCache()
    {
        var files = MakeCache();
        using var cache = new DyldCache(n => files.TryGetValue(n, out var b) ? new MemoryStream(b) : null);
        void Expect(ulong a, string? want)
        {
            string? got = cache.Symbolicate(a);
            if (got != want) throw new Exception($"0x{a:x}: got {got ?? "null"}, want {want ?? "null"}");
        }
        Expect(0x180001104, "libtest.dylib`exported + 0x4");
        Expect(0x180001450, "libtest.dylib`local_fn + 0x50");
        Expect(0x180001810, "libtest.dylib`other + 0x10");
        Expect(0x180004000, null); // past __TEXT

        string log = "SMP SpringBoard[26] slide 0x1000 mh 0x100ffc000\n" +
                     "SMP SpringBoard t0 pc 0x180002450 lr 0x100ffd010 sp 0x16edfd960 bt 0x180002104SMP backboardd[9] slide 0x0 mh 0x104000000\n";
        string outp = LogSymbolicator.Annotate(log.Split('\n'), cache);
        foreach (var want in new[] { "0x180002450  libtest.dylib`local_fn + 0x50", "0x100ffd010  SpringBoard + 0x1010",
                                     "0x180002104  libtest.dylib`exported + 0x4", "\nSMP backboardd[9] slide" })
            if (!outp.Contains(want)) throw new Exception("missing: " + want + "\n" + outp);
    }
}
