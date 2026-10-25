// SweetEmu command line: the same engine as the Windows app, for scripting and testing.
using System.Security.Cryptography;
using SweetEmu.Core.Apfs;
using SweetEmu.Core.IO;

namespace SweetEmu.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0) return Usage();
        try
        {
            return args[0] switch
            {
                "apfs-dump" => ApfsDump(args[1..]),
                _ => Commands.Run(args),
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }
    }

    internal static int Usage()
    {
        Console.Error.WriteLine("""
            usage: SweetEmu.Cli <command> ...
              apfs-dump IMAGE                 list every file of every APFS volume (path, type, mode, owner, size, sha256)
            """ + Commands.UsageText);
        return 2;
    }

    static IByteSource OpenImage(string path)
    {
        IByteSource src = new FileSource(path);
        if (Udif.IsUdif(src)) src = new Udif(src, ownsFile: true);
        return ApfsContainer.Locate(src);
    }

    static int ApfsDump(string[] args)
    {
        using var src = OpenImage(args[0]);
        using var c = new ApfsContainer(src);
        foreach (var vol in c.Volumes())
        {
            if (vol.Encrypted)
            {
                Console.WriteLine($"# volume {vol.Name}: encrypted, skipped");
                continue;
            }
            vol.Load();
            var lines = new List<string>();
            Walk(vol, ApfsVolume.RootDirId, "", lines);
            lines.Sort(StringComparer.Ordinal);
            Console.WriteLine($"# volume {vol.Name}");
            foreach (var l in lines) Console.WriteLine(l);
        }
        return 0;
    }

    static void Walk(ApfsVolume vol, ulong dir, string path, List<string> outLines)
    {
        if (!vol.Children.TryGetValue(dir, out var kids)) return;
        foreach (var e in kids)
        {
            var n = vol.Inodes[e.FileId];
            string p = path + "/" + e.Name;
            string t, extra = "";
            ulong size = 0;
            if (n.IsDir) t = "d";
            else if (n.IsSymlink)
            {
                t = "l";
                extra = vol.ReadSymlink(n);
            }
            else if (n.IsRegular)
            {
                t = "f";
                using var s = vol.OpenFile(n);
                using var sha = SHA256.Create();
                var buf = new byte[1 << 20];
                int r;
                while ((r = s.Read(buf, 0, buf.Length)) > 0)
                {
                    sha.TransformBlock(buf, 0, r, null, 0);
                    size += (ulong)r;
                }
                sha.TransformFinalBlock(buf, 0, 0);
                extra = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            }
            else t = "o";
            outLines.Add($"{p}\t{t}\t{Convert.ToString(n.Mode & 0xFFF, 8)}\t{n.Uid}\t{n.Gid}\t{size}\t{extra}");
            if (n.IsDir) Walk(vol, e.FileId, p, outLines);
        }
    }
}

public static partial class Commands
{
    public static string UsageText => """
          apfs2hfs IMAGE OUT.img [SIZE_MB]  copy the first APFS volume into a new journaled HFSX image
          adt OUT [D22AP.adt] [SEED_HEX]    the QEMU virt device tree (tools/mkadt.py --virt)
          payload KERNEL ADT OUT [BOOTARGS] SWEETXNU payload (tools/mkpayload.py)
          ipsw-info IPSW                   what the app would use from an IPSW
          prepare HOME                     build Data/ios16-root.img from HOME/IPSW/*.ipsw
          run HOME [--headless] [--secs N] [--screenshot F] [--discard]   boot it in QEMU
          symbolicate HOME [LOG] [OUT]     name the addresses in SMP/CRASH lines of serial.txt
          cache-addr HOME ADDR|call:LR...  describe unslid shared-cache addresses (call: = the call before a return address)
        """;

    public static int Run(string[] args)
    {
        switch (args[0])
        {
            case "apfs2hfs":
            {
                IByteSource src = new FileSource(args[1]);
                if (Udif.IsUdif(src)) src = new Udif(src, ownsFile: true);
                using var c = new ApfsContainer(ApfsContainer.Locate(src));
                var vol = c.Volumes().First(v => !v.Encrypted);
                var tree = new SweetEmu.Core.Fs.FsTree(SweetEmu.Core.Fs.ApfsImport.Import(vol, w => Console.Error.WriteLine("warning: " + w)));
                long size = (args.Length > 3 ? long.Parse(args[3]) : 256) << 20;
                new SweetEmu.Core.Hfs.HfsWriter { VolumeName = vol.Name }.Write(tree, args[2], size);
                Console.WriteLine($"wrote {args[2]}");
                return 0;
            }
            case "adt":
            {
                byte[]? d22 = args.Length > 2 && args[2] != "-" ? File.ReadAllBytes(args[2]) : null;
                byte[]? seed = args.Length > 3 ? Convert.FromHexString(args[3]) : null;
                File.WriteAllBytes(args[1], SweetEmu.Core.Boot.VirtAdt.Build(d22, seed));
                return 0;
            }
            case "payload":
            {
                var (blob, info) = SweetEmu.Core.Boot.Payload.Build(File.ReadAllBytes(args[1]), File.ReadAllBytes(args[2]),
                    args.Length > 4 ? args[4] : "debug=0xc serial=3 cpus=1 keepsyms=1");
                File.WriteAllBytes(args[3], blob);
                Console.WriteLine($"fixups: {info.Fixups}, virt_base 0x{info.VirtBase:x}, entry 0x{info.Entry:x}, size {info.Size}");
                return 0;
            }
            case "layout": // debug: guest RAM image + stub for a payload (compare with tools/qemu-sweet.py)
            {
                var g = SweetEmu.Core.Boot.Payload.Layout(File.ReadAllBytes(args[1]));
                File.WriteAllBytes(args[2], g.Ram);
                File.WriteAllBytes(args[3], SweetEmu.Core.Boot.Payload.Stub(SweetEmu.Core.Boot.QemuSession.StubBinary(), g.BootArgsPa, g.EntryPa));
                Console.WriteLine($"phys_base=0x{g.PhysBase:x} boot_args=0x{g.BootArgsPa:x} entry=0x{g.EntryPa:x}");
                return 0;
            }
            case "boot-payload": // debug: boot any payload headless, screenshot after N seconds
            {
                var q = SweetEmu.Core.Boot.QemuSession.Start(new SweetEmu.Core.Boot.QemuOptions
                {
                    QemuExe = SweetEmu.Core.Boot.QemuLocator.Find(AppContext.BaseDirectory) ?? "qemu-system-aarch64",
                    Payload = File.ReadAllBytes(args[1]),
                    WorkDir = Path.GetFullPath(args[2]),
                    Display = "none",
                });
                Thread.Sleep(TimeSpan.FromSeconds(double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)));
                q.Screenshot(Path.GetFullPath(args[4]));
                q.Quit();
                q.Process.WaitForExit(10000);
                q.Dispose();
                Console.WriteLine(File.Exists(q.SerialLog) ? File.ReadAllText(q.SerialLog).Trim() : "(no serial)");
                return 0;
            }
            case "hfs-stress": // debug: synthetic big tree -> HFS image (then run fsck.hfsplus on it)
            {
                int n = int.Parse(args[2]);
                var rng = new Random(1);
                var tree = new SweetEmu.Core.Fs.FsTree(SweetEmu.Core.Fs.FsInode.NewDir(0x1ED, 0, 0));
                string[] parts = { "System", "Library", "Frameworks", "PrivateFrameworks", "usr", "lib", "Caches", "Résumé", "日本語", "Ünïcödé", "a:b", "x" };
                var dirs = new List<string> { "" };
                var files = new List<SweetEmu.Core.Fs.FsInode>();
                for (int i = 0; i < n; i++)
                {
                    string parent = dirs[rng.Next(dirs.Count)];
                    string name = parts[rng.Next(parts.Length)] + "_" + i.ToString("x") + (rng.Next(4) == 0 ? new string('n', rng.Next(1, 200)) : "");
                    string path = parent.Length == 0 ? name : parent + "/" + name;
                    int kind = rng.Next(10);
                    if (kind < 2 && dirs.Count < n / 4)
                    {
                        tree.MkdirP(path, 0x1ED, 501, 501);
                        dirs.Add(path);
                    }
                    else if (kind < 3 && files.Count > 0)
                        tree.Put(path, files[rng.Next(files.Count)]); // hard link
                    else if (kind < 4)
                        tree.Put(path, new SweetEmu.Core.Fs.FsInode { Kind = SweetEmu.Core.Fs.NodeKind.Symlink, Mode = 0xA1ED, Target = "../" + name });
                    else
                    {
                        var f = SweetEmu.Core.Fs.FsInode.NewFile(System.Text.Encoding.UTF8.GetBytes(path), 0x1A4, 0, 0);
                        files.Add(f);
                        tree.Put(path, f);
                    }
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                new SweetEmu.Core.Hfs.HfsWriter().Write(tree, args[1], 4L << 30);
                Console.WriteLine($"{n} entries written in {sw.ElapsedMilliseconds} ms");
                return 0;
            }
            case "ipsw-info":
            {
                using var ipsw = new SweetEmu.Core.Ipsw(args[1]);
                Console.WriteLine($"iOS {ipsw.ProductVersion} ({ipsw.BuildVersion}) for {string.Join(", ", ipsw.SupportedProductTypes)}");
                foreach (var c in new[] { "OS", "Cryptex1,SystemOS", "Cryptex1,AppOS", "DeviceTree" })
                    Console.WriteLine($"  {c}: {ipsw.ComponentPath(c)}");
                return 0;
            }
            case "symbolicate":
            {
                var emu = new SweetEmu.Core.Emulator(args[1]);
                string logPath = args.Length > 2 ? args[2] : Path.Combine(emu.RunDir, "serial.txt");
                string outPath = args.Length > 3 ? args[3] : Path.ChangeExtension(logPath, ".symbolicated.txt");
                using var ipsw = new SweetEmu.Core.Ipsw(emu.FindIpsw() ?? throw new FileNotFoundException("no IPSW in " + emu.IpswDir));
                var keep = new List<IDisposable>();
                try
                {
                    using var cache = SweetEmu.Core.Debug.LogSymbolicator.OpenFromIpsw(ipsw, emu.TempDir, Console.WriteLine, keep);
                    File.WriteAllText(outPath, SweetEmu.Core.Debug.LogSymbolicator.Annotate(File.ReadLines(logPath), cache));
                    Console.WriteLine($"wrote {outPath}");
                }
                finally
                {
                    for (int i = keep.Count - 1; i >= 0; i--) keep[i].Dispose();
                }
                return 0;
            }
            case "cache-addr":
            {
                var emu = new SweetEmu.Core.Emulator(args[1]);
                using var ipsw = new SweetEmu.Core.Ipsw(emu.FindIpsw() ?? throw new FileNotFoundException("no IPSW in " + emu.IpswDir));
                var keep = new List<IDisposable>();
                try
                {
                    using var cache = SweetEmu.Core.Debug.LogSymbolicator.OpenFromIpsw(ipsw, emu.TempDir, Console.WriteLine, keep);
                    foreach (var a in args.Skip(2))
                    {
                        bool call = a.StartsWith("call:");
                        ulong v = Convert.ToUInt64((call ? a[5..] : a).Replace("0x", ""), 16);
                        Console.WriteLine(call ? cache.DescribeCallBefore(v) : cache.Describe(v));
                    }
                }
                finally
                {
                    for (int i = keep.Count - 1; i >= 0; i--) keep[i].Dispose();
                }
                return 0;
            }
            case "prepare":
            {
                var emu = new SweetEmu.Core.Emulator(args[1]);
                int last = -1;
                emu.Prepare(new SweetEmu.Core.EmulatorSettings(), Console.WriteLine, f =>
                {
                    int pct = (int)(f * 100);
                    if (pct / 5 != last / 5) Console.WriteLine($"  {pct}%");
                    last = pct;
                }, CancellationToken.None);
                return 0;
            }
            case "run":
            {
                var emu = new SweetEmu.Core.Emulator(args[1]);
                var s = new SweetEmu.Core.EmulatorSettings
                {
                    DiscardChanges = args.Contains("--discard"),
                    Display = args.Contains("--headless") ? "none" : "gtk",
                };
                string? Opt(string name) => Array.IndexOf(args, name) is int i && i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
                using var q = emu.Start(s);
                Console.WriteLine(q.CommandLine);
                if (Opt("--secs") is string secs)
                {
                    q.Process.WaitForExit(TimeSpan.FromSeconds(double.Parse(secs, System.Globalization.CultureInfo.InvariantCulture)));
                    if (Opt("--screenshot") is string shot && !q.Process.HasExited) q.Screenshot(Path.GetFullPath(shot));
                    q.Quit();
                }
                q.Process.WaitForExit();
                return 0;
            }
            default:
                return Program.Usage();
        }
    }
}
