using System.Security.Cryptography;
using SweetEmu.Core.Apfs;
using SweetEmu.Core.Fs;
using SweetEmu.Core.Hfs;
using SweetEmu.Core.IO;

namespace SweetEmu.Tests;

/// Needs APFS sample images and their reference listings (app/tools/apfs_ref_dump.py output):
/// SWEETEMU_APFS_DIR=dir with NAME.bin + NAME.ref.txt (e.g. dissect.apfs's test images).
public static class FilesystemTests
{
    static string DataDir() => Assert.Tool("SWEETEMU_APFS_DIR", "/home/claude/ref/apfsimg");

    static IEnumerable<string> Images() =>
        Directory.EnumerateFiles(DataDir(), "*.bin").Where(f => File.Exists(Path.ChangeExtension(f, ".ref.txt")));

    static List<string> Listing(ApfsVolume vol)
    {
        var lines = new List<string>();
        void Walk(ulong dir, string path)
        {
            if (!vol.Children.TryGetValue(dir, out var kids)) return;
            foreach (var e in kids)
            {
                var n = vol.Inodes[e.FileId];
                string p = path + "/" + e.Name, t, extra = "";
                ulong size = 0;
                if (n.IsDir) t = "d";
                else if (n.IsSymlink) { t = "l"; extra = vol.ReadSymlink(n); }
                else if (n.IsRegular)
                {
                    t = "f";
                    using var s = vol.OpenFile(n);
                    var ms = new MemoryStream();
                    s.CopyTo(ms);
                    size = (ulong)ms.Length;
                    extra = Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
                }
                else t = "o";
                lines.Add($"{p}\t{t}\t{Convert.ToString(n.Mode & 0xFFF, 8)}\t{n.Uid}\t{n.Gid}\t{size}\t{extra}");
                if (n.IsDir) Walk(e.FileId, p);
            }
        }
        Walk(ApfsVolume.RootDirId, "");
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    [Test]
    public static void ApfsListingsMatchReference()
    {
        int checkedImages = 0;
        foreach (var img in Images())
        {
            var expected = File.ReadAllText(Path.ChangeExtension(img, ".ref.txt")).Split('\n').Where(l => l.Length > 0).ToList();
            var actual = new List<string>();
            using var src = new FileSource(img);
            using var c = new ApfsContainer(ApfsContainer.Locate(src));
            foreach (var vol in c.Volumes())
            {
                if (vol.Encrypted)
                {
                    actual.Add($"# volume {vol.Name}: encrypted, skipped");
                    continue;
                }
                vol.Load();
                actual.Add($"# volume {vol.Name}");
                actual.AddRange(Listing(vol));
            }
            Assert.Equal(string.Join("\n", expected), string.Join("\n", actual), Path.GetFileName(img));
            checkedImages++;
        }
        Assert.True(checkedImages > 0, "no sample images");
    }

    [Test]
    public static void HfsImagesPassFsck()
    {
        var fsck = Assert.Tool("FSCK_HFSPLUS", "/usr/sbin/fsck.hfsplus");
        foreach (var img in Images())
        {
            using var src = new FileSource(img);
            using var c = new ApfsContainer(ApfsContainer.Locate(src));
            var vol = c.Volumes().First(v => !v.Encrypted);
            var tree = new FsTree(ApfsImport.Import(vol));
            var outImg = Path.Combine(Path.GetTempPath(), Path.GetFileName(img) + ".hfs");
            new HfsWriter { VolumeName = vol.Name }.Write(tree, outImg, 64L << 20);
            var log = Assert.Run(fsck, "-f", "-n", outImg);
            Assert.True(log.Contains("appears to be OK"), $"{Path.GetFileName(img)}: {log}");
            File.Delete(outImg);
        }
    }
}
