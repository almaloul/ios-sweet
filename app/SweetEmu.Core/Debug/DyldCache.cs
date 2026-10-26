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
    sealed record Mapping(ulong Address, ulong Size, ulong FileOffset, Stream File, string FileName, uint MaxProt, uint InitProt);

    sealed class Image
    {
        public ulong Address;
        public string Path = "";
        public ulong TextSize;              // 0 = not parsed yet
        public List<(string Name, ulong Vm, ulong Size)> Segments = new();
        public bool HeaderParsed;
        public ulong LeVm, LeFile;
        public uint SymOff, NSyms, StrOff, StrSize;
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
        AddFile(main, hdr, mainName);
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
                AddFile(s, ReadAt(s, 0, 0x200), mainName + suffix);
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

    void AddFile(Stream s, byte[] hdr, string name)
    {
        files.Add(s);
        uint off = U32(hdr, 0x10), count = U32(hdr, 0x14);
        var m = ReadAt(s, off, (int)count * 32);
        for (int i = 0; i < count; i++)
            mappings.Add(new Mapping(U64(m, i * 32), U64(m, i * 32 + 8), U64(m, i * 32 + 16), s, name, U32(m, i * 32 + 24), U32(m, i * 32 + 28)));
        // dyld_cache_mapping_and_slide_info: address, size, fileOffset, slideInfoFileOffset, slideInfoFileSize, flags, maxProt, initProt
        if (off > 0x13c && U32(hdr, 0x138) != 0)
        {
            uint so = U32(hdr, 0x138), sc = U32(hdr, 0x13c);
            var ms = ReadAt(s, so, (int)sc * 56);
            for (int i = 0; i < sc; i++)
                slides.Add((U64(ms, i * 56), U64(ms, i * 56 + 8), U64(ms, i * 56 + 24), U64(ms, i * 56 + 32), U64(ms, i * 56 + 40), name, s));
        }
    }

    readonly List<(ulong Address, ulong Size, ulong InfoOff, ulong InfoSize, ulong Flags, string File, Stream S)> slides = new();

    /// The slide info covering an address: "v2 page 0x4000 delta_mask .. value_add .." plus the decoded pointer.
    string SlideInfo(ulong addr, ulong raw)
    {
        var sl = slides.FirstOrDefault(x => addr >= x.Address && addr < x.Address + x.Size);
        if (sl.File == null) return "  no slide info for this address";
        if (sl.InfoSize == 0) return $"  mapping-with-slide {sl.File} 0x{sl.Address:x} flags 0x{sl.Flags:x}: no slide info";
        var h = ReadAt(sl.S, (long)sl.InfoOff, 48);
        uint ver = U32(h, 0), pageSize = U32(h, 4);
        var sb = new StringBuilder($"  slide info v{ver} (in {sl.File} at 0x{sl.InfoOff:x}, {sl.InfoSize} bytes) page size 0x{pageSize:x} flags 0x{sl.Flags:x}");
        if (ver == 2)
        {
            ulong deltaMask = U64(h, 24), valueAdd = U64(h, 32);
            ulong v = raw & ~deltaMask;
            if (v != 0) v += valueAdd;
            int pageIndex = (int)((addr - sl.Address) / pageSize);
            uint startsOff = U32(h, 8), startsCount = U32(h, 12);
            ushort start = pageIndex < startsCount ? BinaryPrimitives.ReadUInt16LittleEndian(ReadAt(sl.S, (long)sl.InfoOff + startsOff + 2 * pageIndex, 2)) : (ushort)0xffff;
            sb.Append($"\n  v2 delta_mask 0x{deltaMask:x} value_add 0x{valueAdd:x}: pointer -> unslid 0x{v:x}; page {pageIndex} start 0x{start:x}");
        }
        else if (ver == 3)
        {
            ulong authAdd = U64(h, 16);
            ulong v = (raw >> 63) != 0 ? authAdd + (raw & 0xFFFFFFFF) : ((raw & 0x7FFFFFFFFFF) | (((raw >> 43) & 0xff) << 56));
            sb.Append($"\n  v3 auth_value_add 0x{authAdd:x}: pointer -> unslid 0x{v:x}");
        }
        else if (ver == 5)
        {
            ulong valueAdd = U64(h, 16);
            sb.Append($"\n  v5 value_add 0x{valueAdd:x}: pointer -> unslid 0x{valueAdd + (raw & 0x7FFFFFFFF):x}");
        }
        return sb.ToString();
    }

    /// count instruction words from addr, one per line.
    public string Words(ulong addr, int count)
    {
        var w = ReadVm(addr, count * 4);
        if (w == null) return $"0x{addr:x}: not mapped";
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            ulong a = addr + (ulong)(i * 4);
            sb.AppendLine($"  0x{a:x}: {U32(w, i * 4):x8}{(Symbolicate(a) is { } n && n.EndsWith(" + 0x0") ? "   <" + n + ">" : "")}");
        }
        return sb.ToString();
    }

    /// count u64 values from addr, raw and as rebased pointers (unslid) where slide info applies.
    public string Qwords(ulong addr, int count)
    {
        var w = ReadVm(addr, count * 8);
        if (w == null) return $"0x{addr:x}: not mapped";
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            ulong a = addr + (ulong)(i * 8), raw = U64(w, i * 8);
            var sl = slides.FirstOrDefault(x => a >= x.Address && a < x.Address + x.Size);
            string dec = "";
            if (sl.File != null && sl.InfoSize > 0)
            {
                var h = ReadAt(sl.S, (long)sl.InfoOff, 48);
                if (U32(h, 0) == 2)
                {
                    ulong v = raw & ~U64(h, 24);
                    if (v != 0) v += U64(h, 32);
                    dec = $"  ptr 0x{v:x}" + (v != 0 && Symbolicate(v) is { } n ? "  " + n : "");
                }
            }
            sb.AppendLine($"  0x{a:x}: 0x{raw:x16}{dec}");
        }
        return sb.ToString();
    }

    /// A pointer stored at addr, rebased (unslid) with the v2 slide info of its mapping.
    ulong Ptr(ulong addr)
    {
        var w = ReadVm(addr, 8);
        if (w == null) return 0;
        ulong raw = U64(w, 0);
        var sl = slides.FirstOrDefault(x => addr >= x.Address && addr < x.Address + x.Size);
        if (sl.File == null || sl.InfoSize == 0) return raw;
        var h = ReadAt(sl.S, (long)sl.InfoOff, 48);
        if (U32(h, 0) != 2) return raw;
        ulong v = raw & ~U64(h, 24);
        return v == 0 ? 0 : v + U64(h, 32);
    }

    string VmString(ulong addr)
    {
        var b = ReadVm(addr, 1);
        if (b == null) return "?";
        var sb = new StringBuilder();
        for (ulong i = 0; i < 300; i++)
        {
            var c = ReadVm(addr + i, 1);
            if (c == null || c[0] == 0) break;
            sb.Append((char)c[0]);
        }
        return sb.ToString();
    }

    /// An Objective-C class in the cache: its class_ro and every method with the IMP it resolves to.
    public string ObjcClass(ulong cls)
    {
        var sb = new StringBuilder($"class 0x{cls:x} {Symbolicate(cls)}\n");
        ulong bits = Ptr(cls + 0x20);
        ulong ro = bits & 0x00007ffffffffff8UL;
        var rw = ReadVm(ro, 0x30);
        if (rw == null) return sb.Append($"  class_ro 0x{ro:x} not mapped\n").ToString();
        sb.AppendLine($"  class_ro 0x{ro:x} flags 0x{U32(rw, 0):x} name \"{VmString(Ptr(ro + 0x18))}\"");
        ulong ml = Ptr(ro + 0x20);
        sb.AppendLine($"  baseMethods 0x{ml:x} {Symbolicate(ml)}");
        var h = ml == 0 ? null : ReadVm(ml, 8);
        if (h == null) return sb.ToString();
        uint entsizeFlags = U32(h, 0), count = U32(h, 4);
        bool small = (entsizeFlags & 0x80000000) != 0;
        uint entsize = entsizeFlags & 0xFFFC;
        sb.AppendLine($"  method list: entsizeAndFlags 0x{entsizeFlags:x} ({(small ? "small/relative" : "big")}, entsize {entsize}), {count} methods");
        for (uint i = 0; i < count && i < 400; i++)
        {
            ulong e = ml + 8 + i * entsize;
            if (small)
            {
                var m = ReadVm(e, 12);
                if (m == null) break;
                int nameOff = BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(0));
                int impOff = BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(8));
                ulong nameField = e, impField = e + 8;
                ulong imp = (ulong)((long)impField + impOff);
                // Name: relative to a selector ref (pointer to the string), or directly to the string.
                ulong target = (ulong)((long)nameField + nameOff);
                string viaRef = VmString(Ptr(target)), direct = VmString(target);
                string name = viaRef.Length > 1 && viaRef.All(ch => ch >= 32 && ch < 127) ? viaRef : direct;
                sb.AppendLine($"  [{i}] {name}  nameOff 0x{(uint)nameOff:x} impOff 0x{(uint)impOff:x} -> imp 0x{imp:x} {Symbolicate(imp)}");
            }
            else
            {
                ulong sel = Ptr(e), imp = Ptr(e + 16);
                sb.AppendLine($"  [{i}] {VmString(sel)} -> imp 0x{imp:x} {Symbolicate(imp)}");
            }
        }
        return sb.ToString();
    }

    /// Address of a symbol: "libobjc.A.dylib:_objc_msgSend".
    public string FindSymbol(string spec)
    {
        int c = spec.IndexOf(':');
        string imgName = spec[..c], sym = spec[(c + 1)..];
        var img = images.FirstOrDefault(i => i.Name == imgName);
        if (img == null) return $"{spec}: no image {imgName}";
        Parse(img);
        var hits = img.Symbols!.Where(x => x.Name == sym).ToList();
        return hits.Count == 0 ? $"{spec}: not found" : $"{spec} = " + string.Join(", ", hits.Select(h => $"0x{h.Addr:x}"));
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

    void ParseHeader(Image img)
    {
        if (img.HeaderParsed) return;
        img.HeaderParsed = true;
        var mh = ReadVm(img.Address, 32);
        if (mh == null || U32(mh, 0) != 0xfeedfacf) { img.TextSize = 0x1000; return; }
        uint ncmds = U32(mh, 16), sizeofcmds = U32(mh, 20);
        var cmds = ReadVm(img.Address + 32, (int)sizeofcmds);
        if (cmds == null) return;
        int o = 0;
        for (int i = 0; i < ncmds && o + 8 <= cmds.Length; i++)
        {
            uint cmd = U32(cmds, o), size = U32(cmds, o + 4);
            if (cmd == 0x19) // LC_SEGMENT_64
            {
                string seg = CStr(cmds, o + 8, 16);
                if (seg == "__TEXT") img.TextSize = U64(cmds, o + 32);
                img.Segments.Add((seg, U64(cmds, o + 24), U64(cmds, o + 32)));
                if (seg == "__LINKEDIT") { img.LeVm = U64(cmds, o + 24); img.LeFile = U64(cmds, o + 40); }
            }
            else if (cmd == 0x2) // LC_SYMTAB
            {
                img.SymOff = U32(cmds, o + 8); img.NSyms = U32(cmds, o + 12); img.StrOff = U32(cmds, o + 16); img.StrSize = U32(cmds, o + 20);
            }
            if (size == 0) break;
            o += (int)size;
        }
    }

    void Parse(Image img)
    {
        if (img.Symbols != null) return;
        ParseHeader(img);
        img.Symbols = new List<(ulong, string)>();
        if (locals.TryGetValue(img.Address, out var l)) AddNlists(img.Symbols, localNl, localStrs, l.Start, l.Count);
        if (img.NSyms > 0 && img.LeVm != 0 && img.NSyms < 5_000_000)
        {
            var nl = ReadVm(img.LeVm + img.SymOff - img.LeFile, (int)img.NSyms * 16);
            var strs = ReadVm(img.LeVm + img.StrOff - img.LeFile, (int)img.StrSize);
            if (nl != null && strs != null) AddNlists(img.Symbols, nl, strs, 0, img.NSyms);
        }
        img.Symbols.Sort((a, b) => a.Addr.CompareTo(b.Addr));
    }

    /// "CoreFoundation`__CFRunLoopRun + 0x4f8" for an unslid address, or null.
    public string? Symbolicate(ulong addr)
    {
        var img = ImageFor(addr);
        string seg = "";
        if (img == null)
        {
            // Not code: an address in some image's data segments (class objects, selector refs...).
            var hit = SegmentFor(addr);
            if (hit == null) return null;
            img = hit.Value.Img;
            seg = $" [{hit.Value.Seg}]";
            Parse(img);
        }
        var syms = img.Symbols!;
        int lo = 0, hi = syms.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (syms[mid].Addr <= addr) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (best < 0) return $"{img.Name} + 0x{addr - img.Address:x}{seg}";
        var (a, n) = syms[best];
        if (n.StartsWith('_')) n = n[1..];
        return $"{img.Name}`{n} + 0x{addr - a:x}{seg}";
    }

    List<(ulong Start, ulong End, Image Img, string Seg)>? segIndex;

    (Image Img, string Seg)? SegmentFor(ulong addr)
    {
        if (segIndex == null)
        {
            segIndex = new();
            foreach (var img in images)
            {
                ParseHeader(img);
                foreach (var sg in img.Segments)
                    if (sg.Name != "__LINKEDIT" && sg.Name != "__TEXT" && sg.Size > 0)
                        segIndex.Add((sg.Vm, sg.Vm + sg.Size, img, sg.Name));
            }
            segIndex.Sort((a, b) => a.Start.CompareTo(b.Start));
        }
        int lo = 0, hi = segIndex.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (segIndex[mid].Start <= addr) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (best < 0 || addr >= segIndex[best].End) return null;
        return (segIndex[best].Img, segIndex[best].Seg);
    }

    static string Prot(uint p) => $"{((p & 1) != 0 ? 'r' : '-')}{((p & 2) != 0 ? 'w' : '-')}{((p & 4) != 0 ? 'x' : '-')}";

    /// Everything known about an unslid address: which cache file/mapping (and its
    /// protections), which image and segment, the nearest symbol in any segment,
    /// and the first instruction words there.
    public string Describe(ulong addr)
    {
        var sb = new StringBuilder($"0x{addr:x}:\n");
        var m = mappings.FirstOrDefault(x => addr >= x.Address && addr < x.Address + x.Size);
        sb.AppendLine(m == null ? "  not in any cache mapping"
            : $"  mapping {m.FileName} 0x{m.Address:x}-0x{m.Address + m.Size:x} init {Prot(m.InitProt)} max {Prot(m.MaxProt)}");
        foreach (var img in images)
        {
            ParseHeader(img);
            foreach (var seg in img.Segments)
            {
                if (addr < seg.Vm || addr >= seg.Vm + seg.Size || seg.Name == "__LINKEDIT") continue;
                Parse(img);
                sb.AppendLine($"  image {img.Path} segment {seg.Name} + 0x{addr - seg.Vm:x}");
                var near = img.Symbols!.Where(x => x.Addr <= addr).Select(x => (x.Addr, x.Name)).DefaultIfEmpty((0UL, "")).MaxBy(x => x.Item1);
                if (near.Item1 != 0) sb.AppendLine($"  nearest symbol {near.Item2} + 0x{addr - near.Item1:x}");
            }
        }
        var words = ReadVm(addr & ~3UL, 16);
        if (words != null)
        {
            sb.AppendLine("  words " + string.Join(" ", Enumerable.Range(0, 4).Select(i => $"{U32(words, i * 4):x8}")));
            var q = ReadVm(addr & ~7UL, 8);
            if (q != null)
            {
                ulong raw = U64(q, 0);
                sb.AppendLine($"  u64 at 0x{addr & ~7UL:x} = 0x{raw:x}");
                sb.AppendLine(SlideInfo(addr & ~7UL, raw));
            }
        }
        return sb.ToString();
    }

    /// For a return address: the call instruction before it and, for a direct BL, its target.
    public string DescribeCallBefore(ulong lr)
    {
        var w = ReadVm(lr - 4, 4);
        if (w == null) return $"0x{lr:x}: not mapped";
        uint insn = U32(w, 0);
        var sb = new StringBuilder($"call before 0x{lr:x}: {insn:x8}");
        if ((insn & 0xFC000000) == 0x94000000) // BL imm26
        {
            long imm = ((long)(insn & 0x03FFFFFF) << 38) >> 36;
            ulong target = (ulong)((long)(lr - 4) + imm);
            sb.Append($" = BL 0x{target:x}\n").Append(Describe(target));
        }
        else if ((insn & 0xFFFFFC1F) == 0xD63F0000) sb.Append($" = BLR x{(insn >> 5) & 31}\n");
        else sb.Append('\n');
        return sb.ToString();
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
