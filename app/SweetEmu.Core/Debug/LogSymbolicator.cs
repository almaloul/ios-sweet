// Annotate the kernel's "SMP ..." (sampler) and "CRASH ..." lines in serial.txt
// with symbol names. Header lines carry the shared-cache slide and the main
// binary's load address:
//   SMP SpringBoard[26] slide 0xafb4000 mh 0x100ffc000
//   CRASH passd[72] sig 6 ... slide 0x2cb10000 mh 0x102104000
// Following "SMP name tN pc .. lr .. bt .." / "CRASH name pc|bt .." lines are
// annotated: shared-cache addresses (>= 0x180000000) by symbol, others as
// main-binary offsets.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SweetEmu.Core.Apfs;

namespace SweetEmu.Core.Debug;

public static class LogSymbolicator
{
    static readonly Regex Header = new(@"^(SMP|CRASH) ([^\[\s]+)\[(\d+)\].*?slide 0x([0-9a-f]+) mh 0x([0-9a-f]+)", RegexOptions.Compiled);
    static readonly Regex Frames = new(@"^(SMP|CRASH) (\S+) (t\d+ |pc |bt )", RegexOptions.Compiled);
    static readonly Regex Addr = new(@"0x([0-9a-f]{9,16})", RegexOptions.Compiled);
    const ulong CacheStart = 0x180000000;

    public static string Annotate(IEnumerable<string> lines, DyldCache cache)
    {
        var slides = new Dictionary<string, (ulong Slide, ulong Mh)>();
        var sb = new StringBuilder();
        foreach (var raw in lines)
        {
            // The sampler's long lines can run into the next "SMP" without a newline.
            foreach (var line in Regex.Split(raw, @"(?<=.)(?=SMP )"))
            {
                sb.AppendLine(line);
                var h = Header.Match(line);
                if (h.Success)
                {
                    slides[h.Groups[2].Value] = (Hex(h.Groups[4].Value), Hex(h.Groups[5].Value));
                    continue;
                }
                var f = Frames.Match(line);
                if (!f.Success || !slides.TryGetValue(f.Groups[2].Value, out var s)) continue;
                var seen = new HashSet<ulong>();
                foreach (Match m in Addr.Matches(line))
                {
                    ulong a = Hex(m.Groups[1].Value);
                    if (!seen.Add(a) || a == 0) continue;
                    string? name = null;
                    if (a >= CacheStart + s.Slide && a < 0x300000000) name = cache.Symbolicate(a - s.Slide);
                    else if (a >= s.Mh && a < s.Mh + 0x10000000) name = $"{f.Groups[2].Value} + 0x{a - s.Mh:x}";
                    if (name != null) sb.AppendLine($"        0x{a:x}  {name}");
                }
            }
        }
        return sb.ToString();
    }

    static ulong Hex(string s) => ulong.Parse(s, NumberStyles.HexNumber);

    /// The dyld shared cache from the IPSW's SystemOS cryptex.
    public static DyldCache OpenFromIpsw(Ipsw ipsw, string tempDir, Action<string> log, List<IDisposable> keepOpen)
    {
        log($"reading SystemOS cryptex ({ipsw.SystemOsImage})");
        var src = ipsw.OpenDiskImage(ipsw.SystemOsImage, tempDir);
        keepOpen.Add(src);
        var c = new ApfsContainer(ApfsContainer.Locate(src));
        keepOpen.Add(c);
        var vol = c.Volumes().First(v => !v.Encrypted);
        vol.Load();
        ulong dir = ApfsVolume.RootDirId;
        foreach (var part in "System/Library/Caches/com.apple.dyld".Split('/'))
        {
            var e = vol.Children.GetValueOrDefault(dir)?.FirstOrDefault(x => x.Name == part)
                ?? throw new FileNotFoundException("cryptex has no " + part);
            dir = e.FileId;
        }
        var entries = vol.Children.GetValueOrDefault(dir) ?? new List<ApfsDirEntry>();
        log($"  {entries.Count} files in System/Library/Caches/com.apple.dyld");
        Stream? Open(string name)
        {
            var e = entries.FirstOrDefault(x => x.Name == name);
            return e != null && vol.Inodes.TryGetValue(e.FileId, out var n) && n.IsRegular ? vol.OpenFile(n) : null;
        }
        var cache = new DyldCache(Open);
        log($"  shared cache: {cache.ImageCount} images");
        return cache;
    }
}
