// Read-only APFS: container superblock/checkpoint, object maps, B-trees and the
// file-system tree of a volume, including sealed (signed system) volumes whose
// file extents live in a separate "fext" tree. Unencrypted volumes only.
// Structures follow Apple's "Apple File System Reference".
using System;
using System.Buffers.Binary;
using System.Text;
using SweetEmu.Core.Compression;
using SweetEmu.Core.IO;

namespace SweetEmu.Core.Apfs;

public sealed class ApfsContainer : IDisposable
{
    readonly IByteSource dev;
    readonly bool ownsDev;
    public int BlockSize { get; }
    public ulong Xid { get; }
    readonly Dictionary<ulong, ulong> omap;
    readonly ulong[] fsOids;

    const uint NxMagic = 0x4253584E; // "NXSB"

    public ApfsContainer(IByteSource dev, bool ownsDev = false)
    {
        this.dev = dev;
        this.ownsDev = ownsDev;
        var b0 = dev.ReadBytes(0, 4096);
        if (BinaryPrimitives.ReadUInt32LittleEndian(b0.AsSpan(32)) != NxMagic) throw new InvalidDataException("APFS: no container superblock");
        BlockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(b0.AsSpan(36));
        if (BlockSize < 4096 || BlockSize > 65536) throw new InvalidDataException("APFS: bad block size");
        var sb = ReadBlock(0);

        // Newest valid superblock in the checkpoint descriptor area (when contiguous).
        uint descBlocks = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(104));
        ulong descBase = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(112));
        ulong bestXid = ObjXid(sb);
        if ((descBlocks & 0x80000000) == 0 && descBlocks < 100000)
        {
            for (uint i = 0; i < descBlocks; i++)
            {
                var b = ReadBlock(descBase + i);
                if (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(32)) == NxMagic && (ObjType(b) & 0xffff) == 1 &&
                    ChecksumOk(b) && ObjXid(b) > bestXid)
                {
                    sb = b;
                    bestXid = ObjXid(b);
                }
            }
        }
        Xid = bestXid;
        ulong omapOid = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(160));
        omap = LoadOmap(omapOid, Xid);
        int maxFs = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(180));
        fsOids = Enumerable.Range(0, Math.Min(maxFs, 100))
            .Select(i => BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(184 + 8 * i)))
            .Where(o => o != 0).ToArray();
    }

    public IEnumerable<ApfsVolume> Volumes()
    {
        foreach (var oid in fsOids)
        {
            if (omap.TryGetValue(oid, out var paddr)) yield return new ApfsVolume(this, ReadBlock(paddr));
        }
    }

    public byte[] ReadBlock(ulong paddr)
    {
        var b = new byte[BlockSize];
        dev.ReadAt((long)paddr * BlockSize, b);
        return b;
    }

    public void ReadBlocks(ulong paddr, Span<byte> buffer) => dev.ReadAt((long)paddr * BlockSize, buffer);

    internal static ulong ObjXid(byte[] b) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(16));
    internal static uint ObjType(byte[] b) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(24));

    public static bool ChecksumOk(byte[] b)
    {
        ulong sum1 = 0, sum2 = 0;
        const ulong Mod = 0xFFFFFFFF;
        for (int i = 8; i < b.Length; i += 4)
        {
            sum1 = (sum1 + BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i))) % Mod;
            sum2 = (sum2 + sum1) % Mod;
        }
        ulong c1 = Mod - ((sum1 + sum2) % Mod);
        ulong c2 = Mod - ((sum1 + c1) % Mod);
        return BinaryPrimitives.ReadUInt64LittleEndian(b) == ((c2 << 32) | c1);
    }

    /// Whole object map: virtual oid -> physical address of the newest version <= xid.
    internal Dictionary<ulong, ulong> LoadOmap(ulong omapPaddr, ulong xid)
    {
        var om = ReadBlock(omapPaddr);
        ulong treeOid = BinaryPrimitives.ReadUInt64LittleEndian(om.AsSpan(48));
        var best = new Dictionary<ulong, (ulong Xid, ulong Paddr)>();
        var tree = new BTree(this, treeOid, null);
        foreach (var (k, v) in tree.Records())
        {
            ulong oid = BinaryPrimitives.ReadUInt64LittleEndian(k);
            ulong x = BinaryPrimitives.ReadUInt64LittleEndian(k[8..]);
            if (x > xid) continue;
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(v);
            ulong paddr = BinaryPrimitives.ReadUInt64LittleEndian(v[8..]);
            if ((flags & 1) != 0) continue; // OMAP_VAL_DELETED
            if (!best.TryGetValue(oid, out var cur) || x > cur.Xid) best[oid] = (x, paddr);
        }
        return best.ToDictionary(kv => kv.Key, kv => kv.Value.Paddr);
    }

    public void Dispose()
    {
        if (ownsDev) dev.Dispose();
    }

    /// Find the APFS container in a disk image: at offset 0, or in a GPT partition.
    public static IByteSource Locate(IByteSource disk)
    {
        var head = disk.ReadBytes(0, 4096);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(32)) == NxMagic) return disk;
        foreach (int sector in new[] { 512, 4096 })
        {
            var gpt = disk.ReadBytes(sector, 512);
            if (!gpt.AsSpan(0, 8).SequenceEqual("EFI PART"u8)) continue;
            long entriesLba = (long)BinaryPrimitives.ReadUInt64LittleEndian(gpt.AsSpan(72));
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(gpt.AsSpan(80));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(gpt.AsSpan(84));
            var apfsGuid = Guid.Parse("7C3457EF-0000-11AA-AA11-00306543ECAC");
            var entries = disk.ReadBytes(entriesLba * sector, Math.Min(count, 256) * size);
            for (int i = 0; i < Math.Min(count, 256); i++)
            {
                var e = entries.AsSpan(i * size, size);
                if (new Guid(e[..16]) != apfsGuid) continue;
                long first = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[32..]);
                long last = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[40..]);
                return new SubSource(disk, first * sector, (last - first + 1) * sector);
            }
        }
        throw new InvalidDataException("no APFS container found in the image");
    }
}

/// B-tree over physical or virtual (omap-resolved) nodes. Records are yielded in key order.
internal sealed class BTree
{
    readonly ApfsContainer c;
    readonly Dictionary<ulong, ulong>? omap;
    readonly byte[] root;
    readonly bool physical;
    readonly int keySize, valSize, nodeSize;

    const ushort BtnodeRoot = 1, BtnodeLeaf = 2, BtnodeFixed = 4;

    public BTree(ApfsContainer c, ulong rootOid, Dictionary<ulong, ulong>? omap)
    {
        this.c = c;
        this.omap = omap;
        ulong paddr = omap == null ? rootOid : Resolve(omap, rootOid);
        root = c.ReadBlock(paddr);
        var info = root.AsSpan(root.Length - 40);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(info);
        nodeSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
        keySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
        valSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[12..]);
        physical = (flags & 0x10) != 0 || omap == null; // BTREE_PHYSICAL
        if (nodeSize != c.BlockSize) nodeSize = c.BlockSize;
    }

    static ulong Resolve(Dictionary<ulong, ulong> omap, ulong oid) =>
        omap.TryGetValue(oid, out var p) ? p : throw new InvalidDataException($"APFS: object {oid} not in the object map");

    public IEnumerable<(byte[] Key, byte[] Value)> Records()
    {
        var stack = new Stack<byte[]>();
        stack.Push(root);
        // Depth-first, children pushed in reverse so leaves come out in key order.
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(32));
            int nkeys = (int)BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(36));
            int tocOff = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(40));
            int tocLen = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(42));
            bool leaf = (flags & BtnodeLeaf) != 0, isRoot = (flags & BtnodeRoot) != 0, fixedKv = (flags & BtnodeFixed) != 0;
            int toc = 56 + tocOff, keyStart = 56 + tocOff + tocLen;
            int valEnd = node.Length - (isRoot ? 40 : 0);

            var entries = new List<(byte[] K, byte[] V)>(nkeys);
            for (int i = 0; i < nkeys; i++)
            {
                int ko, kl, vo, vl;
                if (fixedKv)
                {
                    ko = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(toc + 4 * i));
                    vo = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(toc + 4 * i + 2));
                    kl = keySize;
                    vl = leaf ? valSize : 8;
                }
                else
                {
                    ko = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(toc + 8 * i));
                    kl = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(toc + 8 * i + 2));
                    vo = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(toc + 8 * i + 4));
                    vl = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(toc + 8 * i + 6));
                }
                if (vo == 0xffff) continue; // ghost entry (no value)
                var k = node.AsSpan(keyStart + ko, kl).ToArray();
                var v = node.AsSpan(valEnd - vo, vl).ToArray();
                entries.Add((k, v));
            }

            if (leaf)
            {
                foreach (var e in entries) yield return e;
                continue;
            }
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                // Index value: child oid (hashed trees append the child's hash).
                ulong child = BinaryPrimitives.ReadUInt64LittleEndian(entries[i].V);
                ulong paddr = physical ? child : Resolve(omap!, child);
                stack.Push(c.ReadBlock(paddr));
            }
        }
    }
}

public enum ApfsType : byte
{
    Inode = 3, Xattr = 4, SiblingLink = 5, DstreamId = 6, FileExtent = 8, DirRec = 9,
}

public sealed class ApfsInode
{
    public ulong Id, ParentId, PrivateId;
    public ulong CreateTime, ModTime, ChangeTime, AccessTime; // ns since 1970
    public ulong InternalFlags;
    public int NChildrenOrLinks;
    public uint BsdFlags, Uid, Gid;
    public ushort Mode;
    public ulong Size;              // data stream size (logical)
    public ulong UncompressedSize;
    public bool HasDstream;

    public const ushort S_IFMT = 0xF000, S_IFDIR = 0x4000, S_IFREG = 0x8000, S_IFLNK = 0xA000;
    public bool IsDir => (Mode & S_IFMT) == S_IFDIR;
    public bool IsRegular => (Mode & S_IFMT) == S_IFREG;
    public bool IsSymlink => (Mode & S_IFMT) == S_IFLNK;
    public bool IsCompressed => (BsdFlags & 0x20) != 0; // UF_COMPRESSED
}

public sealed class ApfsDirEntry
{
    public required string Name;
    public ulong FileId;
    public byte DType;
}

public sealed class ApfsXattr
{
    public required string Name;
    public byte[]? Embedded;
    public ulong StreamId;          // when stored as a data stream
    public ulong StreamSize;
}

public readonly record struct ApfsExtent(ulong Logical, ulong Length, ulong PhysBlock);

public sealed class ApfsVolume
{
    readonly ApfsContainer c;
    public string Name { get; }
    public ushort Role { get; }
    public ulong IncompatFeatures { get; }
    public bool Sealed => (IncompatFeatures & 0x20) != 0;
    public bool CaseInsensitive => (IncompatFeatures & 0x1) != 0;
    public bool HashedDirKeys => (IncompatFeatures & 0x9) != 0;
    public bool Encrypted { get; }
    readonly ulong rootTreeOid, omapOid, fextTreeOid;
    readonly uint rootTreeType;

    public Dictionary<ulong, ApfsInode> Inodes { get; } = new();
    public Dictionary<ulong, List<ApfsDirEntry>> Children { get; } = new();
    public Dictionary<ulong, List<ApfsXattr>> Xattrs { get; } = new();
    readonly Dictionary<ulong, List<ApfsExtent>> extents = new();
    bool loaded;

    public const ulong RootDirId = 2;

    internal ApfsVolume(ApfsContainer c, byte[] sb)
    {
        this.c = c;
        if (BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(32)) != 0x42535041) throw new InvalidDataException("APFS: bad volume superblock");
        IncompatFeatures = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(56));
        omapOid = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(128));
        rootTreeOid = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(136));
        rootTreeType = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(116));
        ulong fsFlags = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(264));
        Encrypted = (fsFlags & 1) == 0; // APFS_FS_UNENCRYPTED clear
        int nameLen = Array.IndexOf(sb, (byte)0, 704, 256) - 704;
        Name = Encoding.UTF8.GetString(sb, 704, nameLen < 0 ? 256 : nameLen);
        Role = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(964));
        fextTreeOid = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(1032));
    }

    /// Read the whole file-system tree into memory (names, inodes, xattrs, extents).
    public void Load()
    {
        if (loaded) return;
        if (Encrypted) throw new NotSupportedException($"APFS volume \"{Name}\" is encrypted");
        var omap = c.LoadOmap(omapOid, c.Xid);
        // OBJ_PHYSICAL (0x40000000) in the tree type: the root oid is a block address.
        var tree = new BTree(c, rootTreeOid, (rootTreeType & 0x40000000) != 0 ? null : omap);
        foreach (var (k, v) in tree.Records())
        {
            ulong hdr = BinaryPrimitives.ReadUInt64LittleEndian(k);
            ulong id = hdr & 0x0FFFFFFFFFFFFFFF;
            var type = (ApfsType)(hdr >> 60);
            switch (type)
            {
                case ApfsType.Inode:
                    Inodes[id] = ParseInode(id, v);
                    break;
                case ApfsType.DirRec:
                {
                    string name;
                    if (HashedDirKeys)
                    {
                        int len = (int)(BinaryPrimitives.ReadUInt32LittleEndian(k.AsSpan(8)) & 0x3FF);
                        name = CStr(k, 12, len);
                    }
                    else
                    {
                        int len = BinaryPrimitives.ReadUInt16LittleEndian(k.AsSpan(8));
                        name = CStr(k, 10, len);
                    }
                    var e = new ApfsDirEntry
                    {
                        Name = name,
                        FileId = BinaryPrimitives.ReadUInt64LittleEndian(v),
                        DType = (byte)(BinaryPrimitives.ReadUInt16LittleEndian(v.AsSpan(16)) & 0xF),
                    };
                    if (!Children.TryGetValue(id, out var list)) Children[id] = list = new();
                    list.Add(e);
                    break;
                }
                case ApfsType.Xattr:
                {
                    int len = BinaryPrimitives.ReadUInt16LittleEndian(k.AsSpan(8));
                    var x = new ApfsXattr { Name = CStr(k, 10, len) };
                    ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(v);
                    int dlen = BinaryPrimitives.ReadUInt16LittleEndian(v.AsSpan(2));
                    if ((flags & 1) != 0)
                    {
                        x.StreamId = BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(4));
                        x.StreamSize = BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(12));
                    }
                    else
                    {
                        x.Embedded = v.AsSpan(4, Math.Min(dlen, v.Length - 4)).ToArray();
                    }
                    if (!Xattrs.TryGetValue(id, out var list)) Xattrs[id] = list = new();
                    list.Add(x);
                    break;
                }
                case ApfsType.FileExtent:
                    AddExtent(id, BinaryPrimitives.ReadUInt64LittleEndian(k.AsSpan(8)),
                        BinaryPrimitives.ReadUInt64LittleEndian(v) & 0x00FFFFFFFFFFFFFF,
                        BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(8)));
                    break;
            }
        }
        if (Sealed && fextTreeOid != 0)
        {
            // Physical tree: key {private_id, logical_addr}, value {len_and_flags, phys_block_num}.
            var fext = new BTree(c, fextTreeOid, null);
            foreach (var (k, v) in fext.Records())
            {
                AddExtent(BinaryPrimitives.ReadUInt64LittleEndian(k), BinaryPrimitives.ReadUInt64LittleEndian(k.AsSpan(8)),
                    BinaryPrimitives.ReadUInt64LittleEndian(v) & 0x00FFFFFFFFFFFFFF,
                    BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(8)));
            }
        }
        foreach (var l in extents.Values) l.Sort((a, b) => a.Logical.CompareTo(b.Logical));
        loaded = true;
    }

    void AddExtent(ulong id, ulong logical, ulong len, ulong phys)
    {
        if (!extents.TryGetValue(id, out var list)) extents[id] = list = new();
        list.Add(new ApfsExtent(logical, len, phys));
    }

    static string CStr(byte[] b, int at, int len)
    {
        int n = len;
        while (n > 0 && b[at + n - 1] == 0) n--;
        return Encoding.UTF8.GetString(b, at, n);
    }

    static ApfsInode ParseInode(ulong id, byte[] v)
    {
        var n = new ApfsInode
        {
            Id = id,
            ParentId = BinaryPrimitives.ReadUInt64LittleEndian(v),
            PrivateId = BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(8)),
            CreateTime = BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(16)),
            ModTime = BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(24)),
            ChangeTime = BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(32)),
            AccessTime = BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(40)),
            InternalFlags = BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(48)),
            NChildrenOrLinks = BinaryPrimitives.ReadInt32LittleEndian(v.AsSpan(56)),
            BsdFlags = BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(68)),
            Uid = BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(72)),
            Gid = BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(76)),
            Mode = BinaryPrimitives.ReadUInt16LittleEndian(v.AsSpan(80)),
        };
        if (v.Length >= 92) n.UncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(84));
        // Extended fields: count, used bytes, then {type u8, flags u8, size u16} headers and 8-aligned data.
        if (v.Length >= 96)
        {
            int num = BinaryPrimitives.ReadUInt16LittleEndian(v.AsSpan(92));
            int data = 96 + 4 * num;
            for (int i = 0; i < num; i++)
            {
                int h = 96 + 4 * i;
                byte xtype = v[h];
                int size = BinaryPrimitives.ReadUInt16LittleEndian(v.AsSpan(h + 2));
                if (xtype == 8 && data + 8 <= v.Length) // INO_EXT_TYPE_DSTREAM
                {
                    n.Size = BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(data));
                    n.HasDstream = true;
                }
                data += (size + 7) & ~7;
            }
        }
        return n;
    }

    public ApfsXattr? GetXattr(ulong id, string name) =>
        Xattrs.TryGetValue(id, out var l) ? l.FirstOrDefault(x => x.Name == name) : null;

    public byte[] ReadXattr(ApfsXattr x)
    {
        if (x.Embedded != null) return x.Embedded;
        var buf = new byte[checked((int)x.StreamSize)];
        ReadStream(x.StreamId, x.StreamSize, 0, buf);
        return buf;
    }

    public string ReadSymlink(ApfsInode n)
    {
        var x = GetXattr(n.Id, "com.apple.fs.symlink") ?? throw new InvalidDataException($"APFS: symlink {n.Id} without target");
        var b = ReadXattr(x);
        int len = b.Length;
        while (len > 0 && b[len - 1] == 0) len--;
        return Encoding.UTF8.GetString(b, 0, len);
    }

    /// First physical block of a file's data (or of its resource fork when compressed there).
    public ulong FirstBlock(ApfsInode n)
    {
        ulong id = n.PrivateId;
        if (n.IsCompressed && GetXattr(n.Id, "com.apple.ResourceFork") is { Embedded: null } r) id = r.StreamId;
        if (extents.TryGetValue(id, out var l))
            foreach (var e in l)
                if (e.PhysBlock != 0) return e.PhysBlock;
        return ulong.MaxValue - 1;
    }

    /// Logical file size as the user sees it (decompressed size for compressed files).
    public ulong FileSize(ApfsInode n)
    {
        if (n.IsCompressed && GetXattr(n.Id, "com.apple.decmpfs") is { } x)
            return Decmpfs.Header(ReadXattr(x)).Size;
        return n.Size;
    }

    /// Read [offset, offset+buffer.Length) of a data stream (holes read as zeros).
    public void ReadStream(ulong streamId, ulong streamSize, ulong offset, Span<byte> buffer)
    {
        buffer.Clear();
        if (!extents.TryGetValue(streamId, out var list)) return;
        ulong end = Math.Min(streamSize, offset + (ulong)buffer.Length);
        int bs = c.BlockSize;
        // Binary search the first extent that ends after offset.
        int lo = 0, hi = list.Count - 1, first = list.Count;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (list[mid].Logical + list[mid].Length > offset)
            {
                first = mid;
                hi = mid - 1;
            }
            else lo = mid + 1;
        }
        byte[]? tmp = null;
        for (int i = first; i < list.Count && list[i].Logical < end; i++)
        {
            var e = list[i];
            ulong from = Math.Max(offset, e.Logical), to = Math.Min(end, e.Logical + e.Length);
            if (from >= to || e.PhysBlock == 0) continue;
            ulong within = from - e.Logical;
            ulong disk = e.PhysBlock * (ulong)bs + within;
            int count = (int)(to - from);
            var dst = buffer.Slice((int)(from - offset), count);
            if (disk % (ulong)bs == 0)
            {
                c.ReadBlocks(disk / (ulong)bs, dst);
            }
            else
            {
                tmp ??= new byte[bs];
                ulong pos = disk;
                int done = 0;
                while (done < count)
                {
                    ulong blk = pos / (ulong)bs;
                    int boff = (int)(pos % (ulong)bs);
                    int n = Math.Min(bs - boff, count - done);
                    c.ReadBlocks(blk, tmp);
                    tmp.AsSpan(boff, n).CopyTo(dst[done..]);
                    done += n;
                    pos += (ulong)n;
                }
            }
        }
    }

    /// Open a regular file's contents (transparently decompressed).
    public Stream OpenFile(ApfsInode n)
    {
        if (n.IsCompressed && GetXattr(n.Id, "com.apple.decmpfs") is { } x)
        {
            var hdr = ReadXattr(x);
            var (type, size) = Decmpfs.Header(hdr);
            if (Decmpfs.InResourceFork(type) && type != 4)
                return new BlockCompressedStream(this, n, hdr, type, (long)size);
            return new MemoryStream(Decmpfs.Decompress(hdr, () => ReadXattr(GetXattr(n.Id, "com.apple.ResourceFork")
                ?? throw new InvalidDataException("missing resource fork"))), false);
        }
        return new DstreamStream(this, n.PrivateId, n.Size);
    }

    sealed class DstreamStream(ApfsVolume v, ulong id, ulong size) : Stream
    {
        long pos;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => (long)size;
        public override long Position { get => pos; set => pos = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = (int)Math.Max(0, Math.Min(buffer.Length, (long)size - pos));
            if (n == 0) return 0;
            v.ReadStream(id, size, (ulong)pos, buffer[..n]);
            pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => pos + offset, _ => (long)size + offset };
            return pos;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// decmpfs types 8/10/12/14: 64 KiB blocks behind an offset table in the resource fork,
    /// decoded one block at a time so large files never sit in memory whole.
    sealed class BlockCompressedStream : Stream
    {
        readonly ApfsVolume v;
        readonly uint type;
        readonly long size;
        readonly ApfsXattr rsrc;
        readonly uint[] offsets;
        long pos;
        int cachedBlock = -1;
        readonly byte[] block = new byte[0x10000];

        public BlockCompressedStream(ApfsVolume v, ApfsInode n, byte[] hdr, uint type, long size)
        {
            this.v = v;
            this.type = type;
            this.size = size;
            rsrc = v.GetXattr(n.Id, "com.apple.ResourceFork") ?? throw new InvalidDataException("decmpfs: missing resource fork");
            int blocks = (int)((size + 0xFFFF) / 0x10000);
            var table = new byte[4 * (blocks + 1)];
            ReadRsrc(0, table);
            offsets = new uint[blocks + 1];
            for (int i = 0; i <= blocks; i++) offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(4 * i));
        }

        void ReadRsrc(long at, Span<byte> dst)
        {
            if (rsrc.Embedded != null) rsrc.Embedded.AsSpan((int)at, dst.Length).CopyTo(dst);
            else v.ReadStream(rsrc.StreamId, rsrc.StreamSize, (ulong)at, dst);
        }

        void Load(int k)
        {
            if (k == cachedBlock) return;
            int len = (int)(offsets[k + 1] - offsets[k]);
            var src = new byte[len];
            ReadRsrc(offsets[k], src);
            int expect = (int)Math.Min(0x10000, size - (long)k * 0x10000);
            var hdr = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(hdr, Decmpfs.Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4), type);
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(8), (ulong)expect);
            // Decode this block alone through a one-block offset table.
            var one = new byte[8 + len];
            BinaryPrimitives.WriteUInt32LittleEndian(one, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(one.AsSpan(4), (uint)(8 + len));
            src.CopyTo(one, 8);
            var data = Decmpfs.Decompress(hdr, () => one);
            data.CopyTo(block, 0);
            cachedBlock = k;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => size;
        public override long Position { get => pos; set => pos = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int done = 0;
            while (done < buffer.Length && pos < size)
            {
                int k = (int)(pos / 0x10000);
                Load(k);
                int within = (int)(pos % 0x10000);
                int n = (int)Math.Min(Math.Min(buffer.Length - done, 0x10000 - within), size - pos);
                block.AsSpan(within, n).CopyTo(buffer[done..]);
                done += n;
                pos += n;
            }
            return done;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => pos + offset, _ => size + offset };
            return pos;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
