// Symbolication against iOS's dyld shared cache (split cache: main file,
// .01 ... subcaches, .symbols with the local symbols). Enough of the format to
// turn an unslid address into "Image`symbol + 0xoff":
//   - every file: dyld_cache_header mappings (address, size, fileOffset)
//   - main file: image list (mach header address + install path)
//   - .symbols: dyld_cache_local_symbols_info + 64-bit entries + nlist_64
//   - each image (lazily): LC_SEGMENT_64 __TEXT/__LINKEDIT + LC_SYMTAB nlists
// Layout as in Apple's dyld (dyld_cache_format.h, dyld-1042 era).
using System.Buffers.Binary;
using System.Text;

namespace SweetEmu.Core.Debug;

public sealed class DyldCache : IDisposable
{
    sealed record Mapping(ulong Address, ulong Size, ulong FileOffset, Stream File);

    sealed class Image
    {
        public ulong Address;
        public string Path = "";
        public ulong TextSize;              // 0 = not parsed yet
        public List<(ulong Addr, string Name)>? Symbols;
        public string Name => Path[(Path.LastIndexOf('/') + 1)..];
    }

    readonly List<Stream> files = new();
    readonly List<Mapping> mappings = new();
    readonly List<Image> images = new();        // sorted by address
    readonly Dictionary<ulong, (uint Start, uint Count)> locals = new(); // image address -> its slice of localNl
    byte[] localNl = Array.Empty<byte>(), localStrs = Array.Empty<byte>();
    public ulong BaseAddress { get; }
    public int ImageCount => images.Count;

    /// open: file name in the cache directory -> seekable stream, or null if absent.
    /// mainName: e.g. "dyld_shared_cache_arm64".
    public DyldCache(Func<string, Stream?> open, string mainName = "dyld_shared_cache_arm64")
    {
        var main = open(mainName) ?? throw new FileNotFoundException("dyld shared cache not found: " + mainName);
        var hdr = ReadAt(main, 0, 0x200);
        if (!Encoding.ASCII.GetString(hdr, 0, 7).StartsWith("dyld_v1")) throw new InvalidDataException("not a dyld shared cache");
        uint mappingOffset = U32(hdr, 0x10);
        AddFile(main, hdr);
        BaseAddress = mappings[0].Address;

        // Image list (new location when the header is long enough).
        uint imgOff = mappingOffset > 0x1c0 ? U32(hdr, 0x1c0) : U32(hdr, 0x18);
        uint imgCount = mappingOffset > 0x1c0 ? U32(hdr, 0x1c4) : U32(hdr, 0x1c);
        var infos = ReadAt(main, imgOff, (int)imgCount * 32);
        for (int i = 0; i < imgCount; i++)
        {
            ulong addr = U64(infos, i * 32);
            uint pathOff = U32(infos, i * 32 + 24);
            images.Add(new Image { Address = addr, Path = CString(main, pathOff) });
        }
        images.Sort((a, b) => a.Address.CompareTo(b.Address));

        // Subcaches: name.01, name.02, ... (and name.NN.dyldlinkedit / .dylddata variants).
        for (int i = 1; i < 200; i++)
        {
            bool any = false;
            foreach (var suffix in new[] { $".{i:00}", $".{i:00}.dyldlinkedit", $".{i:00}.dylddata", $".{i:00}.dyldreadonly" })
            {
                var s = open(mainName + suffix);
                if (s == null) continue;
                AddFile(s, ReadAt(s, 0, 0x200));
                any = true;
            }
            if (!any && i > 1) break;
        }

        // Local (non-exported) symbols.
        var sym = open(mainName + ".symbols");
        if (sym != null)
        {
            files.Add(sym);
            var sh = ReadAt(sym, 0, 0x200);
            ulong lsOff = U64(sh, 0x48);
            bool entries64 = U32(sh, 0x10) >= 0x190; // offsetof(dyld_cache_header, symbolFileUUID)
            if (lsOff != 0) LoadLocals(sym, lsOff, entries64);
        }
    }

    void AddFile(Stream s, byte[] hdr)
    {
        files.Add(s);
        uint off = U32(hdr, 0x10), count = U32(hdr, 0x14);
        var m = ReadAt(s, off, (int)count * 32);
        for (int i = 0; i < count; i++)
            mappings.Add(new Mapping(U64(m, i * 32), U64(m, i * 32 + 8), U64(m, i * 32 + 16), s));
    }

    void LoadLocals(Stream sym, ulong lsOff, bool entries64)
    {
        var info = ReadAt(sym, (long)lsOff, 24);
        uint nlistOff = U32(info, 0), nlistCount = U32(info, 4), strOff = U32(info, 8), strSize = U32(info, 12);
        uint entOff = U32(info, 16), entCount = U32(info, 20);
        localNl = ReadAt(sym, (long)lsOff + nlistOff, checked((int)nlistCount * 16));
        localStrs = ReadAt(sym, (long)lsOff + strOff, (int)strSize);
        int esz = entries64 ? 16 : 12;
        var ents = ReadAt(sym, (long)lsOff + entOff, (int)entCount * esz);
        for (int i = 0; i < entCount; i++)
        {
            int o = i * esz;
            ulong dylibOffset = entries64 ? U64(ents, o) : U32(ents, o);
            uint start = U32(ents, o + (entries64 ? 8 : 4)), count = U32(ents, o + (entries64 ? 12 : 8));
            if ((ulong)start + count <= nlistCount) locals[BaseAddress + dylibOffset] = (start, count);
        }
    }

    static void AddNlists(List<(ulong, string)> into, byte[] nl, byte[] strs, uint start, uint count)
    {
        for (uint k = start; k < start + count; k++)
        {
            int p = (int)k * 16;
            byte type = nl[p + 4];
            if ((type & 0xe0) != 0 || (type & 0x0e) != 0x0e) continue; // no stabs; N_SECT only
            ulong v = U64(nl, p + 8);
            if (v != 0) into.Add((v, CStr(strs, (int)U32(nl, p))));
        }
    }

    /// Bytes at an unslid virtual address, or null when not mapped.
    public byte[]? ReadVm(ulong addr, int len)
    {
        foreach (var m in mappings)
            if (addr >= m.Address && addr + (ulong)len <= m.Address + m.Size)
                return ReadAt(m.File, (long)(m.FileOffset + (addr - m.Address)), len);
        return null;
    }

    Image? ImageFor(ulong addr)
    {
        int lo = 0, hi = images.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (images[mid].Address <= addr) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (best < 0) return null;
        var img = images[best];
        Parse(img);
        return addr < img.Address + img.TextSize ? img : null;
    }

    void Parse(Image img)
    {
        if (img.Symbols != null) return;
        img.Symbols = new List<(ulong, string)>();
        if (locals.TryGetValue(img.Address, out var l)) AddNlists(img.Symbols, localNl, localStrs, l.Start, l.Count);
        var mh = ReadVm(img.Address, 32);
        if (mh == null || U32(mh, 0) != 0xfeedfacf) { img.TextSize = 0x1000; return; }
        uint ncmds = U32(mh, 16), sizeofcmds = U32(mh, 20);
        var cmds = ReadVm(img.Address + 32, (int)sizeofcmds);
        if (cmds == null) return;
        ulong leVm = 0, leFile = 0;
        uint symoff = 0, nsyms = 0, stroff = 0, strsize = 0;
        int o = 0;
        for (int i = 0; i < ncmds && o + 8 <= cmds.Length; i++)
        {
            uint cmd = U32(cmds, o), size = U32(cmds, o + 4);
            if (cmd == 0x19) // LC_SEGMENT_64
            {
                string seg = CStr(cmds, o + 8, 16);
                if (seg == "__TEXT") img.TextSize = U64(cmds, o + 32);
                if (seg == "__LINKEDIT") { leVm = U64(cmds, o + 24); leFile = U64(cmds, o + 40); }
            }
            else if (cmd == 0x2) // LC_SYMTAB
            {
                symoff = U32(cmds, o + 8); nsyms = U32(cmds, o + 12); stroff = U32(cmds, o + 16); strsize = U32(cmds, o + 20);
            }
            if (size == 0) break;
            o += (int)size;
        }
        if (nsyms > 0 && leVm != 0 && nsyms < 5_000_000)
        {
            var nl = ReadVm(leVm + symoff - leFile, (int)nsyms * 16);
            var strs = ReadVm(leVm + stroff - leFile, (int)strsize);
            if (nl != null && strs != null) AddNlists(img.Symbols, nl, strs, 0, nsyms);
        }
        img.Symbols.Sort((a, b) => a.Addr.CompareTo(b.Addr));
    }

    /// "CoreFoundation`__CFRunLoopRun + 0x4f8" for an unslid address, or null.
    public string? Symbolicate(ulong addr)
    {
        var img = ImageFor(addr);
        if (img == null) return null;
        var syms = img.Symbols!;
        int lo = 0, hi = syms.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (syms[mid].Addr <= addr) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (best < 0) return $"{img.Name} + 0x{addr - img.Address:x}";
        var (a, n) = syms[best];
        if (n.StartsWith('_')) n = n[1..];
        return $"{img.Name}`{n} + 0x{addr - a:x}";
    }

    public void Dispose()
    {
        foreach (var f in files) f.Dispose();
    }

    // ---- helpers ----

    static byte[] ReadAt(Stream s, long off, int len)
    {
        var b = new byte[len];
        s.Position = off;
        int done = 0;
        while (done < len)
        {
            int n = s.Read(b, done, len - done);
            if (n <= 0) break;
            done += n;
        }
        return b;
    }

    static string CString(Stream s, long off)
    {
        var b = ReadAt(s, off, 1024);
        return CStr(b, 0);
    }

    static string CStr(byte[] b, int off, int max = int.MaxValue)
    {
        if (off < 0 || off >= b.Length) return "?";
        int end = off;
        int limit = (int)Math.Min((long)b.Length, (long)off + max);
        while (end < limit && b[end] != 0) end++;
        return Encoding.UTF8.GetString(b, off, end - off);
    }

    static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));
}
