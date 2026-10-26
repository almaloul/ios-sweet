using System.Text;
using SweetEmu.Core.Boot;

namespace SweetEmu.Tests;

public static class BootTests
{
    /// The app parses the IPSW's D22AP tree and then the generated tree in one
    /// process (Start). A shared parse cursor once made the second parse fail.
    [Test]
    public static void AdtParsesRepeatedlyInOneProcess()
    {
        var product = new AdtNode();
        product.Props.Add(("name", 0, Encoding.ASCII.GetBytes("product\0")));
        product.Props.Add(("product-name", 0, Encoding.ASCII.GetBytes("iPhone X\0")));
        var d22 = new AdtNode();
        d22.Props.Add(("name", 0, Encoding.ASCII.GetBytes("device-tree\0")));
        d22.Children.Add(product);
        byte[] d22Bytes = d22.Serialize();

        for (int i = 0; i < 3; i++)
        {
            var adt = VirtAdt.Build(d22Bytes);
            var root = AdtNode.Parse(adt);
            if (root.Child("chosen")?.Prop("dram-base") == null) throw new Exception("no /chosen dram-base");
            if (root.Child("product")?.Prop("product-name") == null) throw new Exception("no /product copied");
        }
    }
}

public static class VirtSmpTests
{
    [Test]
    public static void AdtHasOneCpuNodePerCoreWithQemuNumbering()
    {
        var root = AdtNode.Parse(VirtAdt.Build(null, new byte[64], cpus: 4));
        var cpus = root.Child("cpus") ?? throw new Exception("no /cpus");
        if (cpus.Children.Count != 4) throw new Exception($"{cpus.Children.Count} cpu nodes");
        for (int n = 0; n < 4; n++)
        {
            var c = cpus.Children[n];
            if (c.Name != $"cpu{n}") throw new Exception("name " + c.Name);
            if (BitConverter.ToUInt32(c.Prop("reg")!) != n) throw new Exception($"cpu{n} reg");
            if (Encoding.ASCII.GetString(c.Prop("psci-method")!).TrimEnd('\0') != "hvc") throw new Exception("psci-method");
            if (Encoding.ASCII.GetString(c.Prop("state")!).TrimEnd('\0') != (n == 0 ? "running" : "stopped")) throw new Exception("state");
        }
        // One core: no psci-method, so an older kernel (no 0086) still boots.
        var one = AdtNode.Parse(VirtAdt.Build(null, new byte[64], cpus: 1)).Child("cpus")!;
        if (one.Children.Count != 1 || one.Children[0].Prop("psci-method") != null) throw new Exception("single-cpu tree changed");
    }
}

public static class HfsReaderTests
{
    [Test]
    public static void ReadsBackWhatHfsWriterWrote()
    {
        var tree = new SweetEmu.Core.Fs.FsTree(SweetEmu.Core.Fs.FsInode.NewDir(0x1ED, 0, 0));
        tree.MkdirP("System/Library/Caches/com.apple.dyld", 0x1ED, 0, 0);
        tree.MkdirP("private/preboot/Cryptexes/OS", 0x1ED, 0, 0);
        var data = new byte[5_000_000];
        new Random(7).NextBytes(data);
        var f = SweetEmu.Core.Fs.FsInode.NewFile(data, 0x1A4, 0, 0);
        tree.Put("private/preboot/Cryptexes/OS/dsc", f);
        tree.Put("System/Library/Caches/com.apple.dyld/dsc", f); // hard link
        tree.Put("small", SweetEmu.Core.Fs.FsInode.NewFile(System.Text.Encoding.ASCII.GetBytes("hello"), 0x1A4, 0, 0));
        string img = Path.Combine(Path.GetTempPath(), "hfsreader-test.img");
        new SweetEmu.Core.Hfs.HfsWriter().Write(tree, img, 64L << 20);
        try
        {
            using var r = new SweetEmu.Core.Hfs.HfsReader(new SweetEmu.Core.IO.FileSource(img));
            foreach (var p in new[] { "System/Library/Caches/com.apple.dyld/dsc", "private/preboot/Cryptexes/OS/dsc" })
            {
                var h = r.Find(p) ?? throw new Exception("not found: " + p);
                if (h.Length != data.Length) throw new Exception($"{p}: length {h.Length}");
                if (!r.ReadFork(h, 0, data.Length).AsSpan().SequenceEqual(data)) throw new Exception(p + ": data differs");
                if (!r.ReadFork(h, 1234567, 100).AsSpan().SequenceEqual(data.AsSpan(1234567, 100))) throw new Exception(p + ": offset read differs");
            }
            var s = r.Find("small") ?? throw new Exception("small not found");
            if (System.Text.Encoding.ASCII.GetString(r.ReadFork(s, 0, 5)) != "hello") throw new Exception("small differs");
            if (r.Find("nope/x") != null) throw new Exception("found a missing path");
        }
        finally { File.Delete(img); }
    }
}
