// IPSW (zip) access: BuildManifest, the OS/cryptex disk images and the device tree.
using System;
using System.Buffers.Binary;
using SweetEmu.Core.Compression;
using SweetEmu.Core.IO;

namespace SweetEmu.Core;

public sealed class Ipsw : IDisposable
{
    public const string ExpectedDeviceClass = "d22ap";   // iPhone X (iPhone10,3)
    public const string TestedBuild = "20H392";           // iOS 16.7.16, what the port is tuned for

    readonly ZipFile zip;
    public string Path { get; }
    public string ProductVersion { get; }
    public string BuildVersion { get; }
    public IReadOnlyList<string> SupportedProductTypes { get; }
    readonly PDict manifest;   // the chosen BuildIdentity's Manifest

    public Ipsw(string path)
    {
        Path = path;
        zip = new ZipFile(path);
        var bm = zip.Find("BuildManifest.plist") ?? throw new InvalidDataException("not an IPSW: no BuildManifest.plist");
        var root = Plist.Read(zip.ReadAll(bm)) as PDict ?? throw new InvalidDataException("bad BuildManifest.plist");
        ProductVersion = root.Str("ProductVersion") ?? "?";
        BuildVersion = root.Str("ProductBuildVersion") ?? "?";
        SupportedProductTypes = root.Array("SupportedProductTypes")?.OfType<string>().ToList() ?? new List<string>();
        var ids = root.Array("BuildIdentities")?.OfType<PDict>().ToList() ?? throw new InvalidDataException("BuildManifest: no BuildIdentities");
        PDict? pick = null;
        foreach (var id in ids)
        {
            var info = id.Dict("Info");
            if (!string.Equals(info?.Str("DeviceClass"), ExpectedDeviceClass, StringComparison.OrdinalIgnoreCase)) continue;
            if (info?.Str("RestoreBehavior") == "Erase") { pick = id; break; }
            pick ??= id;
        }
        manifest = pick?.Dict("Manifest") ?? throw new InvalidDataException(
            $"this IPSW has no {ExpectedDeviceClass} (iPhone X, iPhone10,3) build identity; " +
            $"it is for {string.Join(", ", SupportedProductTypes)}");
    }

    public string? ComponentPath(string name) => manifest.Dict(name)?.Dict("Info")?.Str("Path");

    public string OsImage => ComponentPath("OS") ?? throw new InvalidDataException("BuildManifest: no OS image");
    public string SystemOsImage => ComponentPath("Cryptex1,SystemOS") ?? throw new InvalidDataException("BuildManifest: no Cryptex1,SystemOS (needs iOS 16 or later)");
    public string AppOsImage => ComponentPath("Cryptex1,AppOS") ?? throw new InvalidDataException("BuildManifest: no Cryptex1,AppOS");
    public string DeviceTreePath => ComponentPath("DeviceTree") ?? throw new InvalidDataException("BuildManifest: no DeviceTree");

    /// Open a disk image inside the IPSW as an APFS container source (UDIF decoded when needed).
    /// Compressed zip entries are extracted to tempDir first.
    public IByteSource OpenDiskImage(string entryName, string tempDir)
    {
        var e = zip.Find(entryName) ?? throw new InvalidDataException($"IPSW: missing {entryName}");
        IByteSource src = zip.OpenStored(e) ?? Extract(e, tempDir);
        if (Udif.IsUdif(src)) src = new Udif(src, ownsFile: true);
        return src;
    }

    IByteSource Extract(ZipFile.Entry e, string tempDir)
    {
        Directory.CreateDirectory(tempDir);
        string path = System.IO.Path.Combine(tempDir, System.IO.Path.GetFileName(e.Name));
        if (!File.Exists(path) || new FileInfo(path).Length != e.Size)
        {
            using var s = zip.OpenRead(e);
            using var f = File.Create(path + ".part");
            s.CopyTo(f, 1 << 20);
            f.Close();
            File.Move(path + ".part", path, true);
        }
        return new FileSource(path);
    }

    /// The device tree (Apple Device Tree blob) from its IMG4 payload.
    public byte[] ReadDeviceTree()
    {
        var e = zip.Find(DeviceTreePath) ?? throw new InvalidDataException($"IPSW: missing {DeviceTreePath}");
        return Img4.PayloadFromIm4p(zip.ReadAll(e));
    }

    public void Dispose() => zip.Dispose();
}

public static class Img4
{
    /// IM4P ::= SEQUENCE { "IM4P", type, description, OCTET STRING payload, [compression] ... }
    public static byte[] PayloadFromIm4p(byte[] der)
    {
        int p = 0;
        var (tag, len) = Tlv(der, ref p);
        if (tag != 0x30) throw new InvalidDataException("IM4P: not a DER sequence");
        int end = p + len;
        var magic = Next(der, ref p, 0x16);
        if (System.Text.Encoding.ASCII.GetString(magic) != "IM4P") throw new InvalidDataException("not an IM4P");
        Next(der, ref p, 0x16); // type
        Next(der, ref p, 0x16); // description
        var payload = Next(der, ref p, 0x04);
        long? lzfseSize = null;
        if (p < end && der[p] == 0x30)
        {
            // Compression info: SEQUENCE { INTEGER algorithm (1 = LZFSE), INTEGER uncompressed size }
            int q = p;
            var (_, clen) = Tlv(der, ref q);
            int cend = q + clen;
            long algo = Integer(Next(der, ref q, 0x02));
            long size = Integer(Next(der, ref q, 0x02));
            if (algo == 1) lzfseSize = size;
            p = cend;
        }
        if (payload.Length >= 4 && payload.AsSpan(0, 4).SequenceEqual("bvx2"u8) || lzfseSize != null)
        {
            if (lzfseSize is long n) return Lzfse.Decode(payload, checked((int)n));
            var buf = new byte[64 << 20];
            int got = Lzfse.Decode(payload, buf);
            return buf.AsSpan(0, got).ToArray();
        }
        if (payload.Length >= 0x180 && payload.AsSpan(0, 8).SequenceEqual("complzss"u8))
        {
            int usize = (int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(12));
            int csize = (int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(16));
            return Lzss(payload.AsSpan(0x180, Math.Min(csize, payload.Length - 0x180)), usize);
        }
        return payload;
    }

    static (int Tag, int Len) Tlv(byte[] b, ref int p)
    {
        int tag = b[p++];
        int len = b[p++];
        if ((len & 0x80) != 0)
        {
            int n = len & 0x7F;
            len = 0;
            for (int i = 0; i < n; i++) len = (len << 8) | b[p++];
        }
        return (tag, len);
    }

    static byte[] Next(byte[] b, ref int p, int expectTag)
    {
        var (tag, len) = Tlv(b, ref p);
        if (tag != expectTag) throw new InvalidDataException($"IM4P: expected tag 0x{expectTag:x2}, got 0x{tag:x2}");
        var v = b.AsSpan(p, len).ToArray();
        p += len;
        return v;
    }

    static long Integer(byte[] v)
    {
        long x = 0;
        foreach (var c in v) x = (x << 8) | c;
        return x;
    }

    // Classic LZSS (N=4096, F=18, threshold 2), as used by iBoot images.
    static byte[] Lzss(ReadOnlySpan<byte> src, int usize)
    {
        const int N = 4096, F = 18, T = 2;
        var text = new byte[N + F - 1];
        int r = N - F;
        var dst = new byte[usize];
        int d = 0, s = 0;
        uint flags = 0;
        while (d < usize)
        {
            if (((flags >>= 1) & 0x100) == 0)
            {
                if (s >= src.Length) break;
                flags = src[s++] | 0xFF00u;
            }
            if ((flags & 1) != 0)
            {
                if (s >= src.Length) break;
                byte c = src[s++];
                dst[d++] = c;
                text[r++] = c;
                r &= N - 1;
            }
            else
            {
                if (s + 1 >= src.Length) break;
                int i = src[s++], j = src[s++];
                i |= (j & 0xF0) << 4;
                j = (j & 0x0F) + T;
                for (int k = 0; k <= j && d < usize; k++)
                {
                    byte c = text[(i + k) & (N - 1)];
                    dst[d++] = c;
                    text[r++] = c;
                    r &= N - 1;
                }
            }
        }
        return dst;
    }
}
