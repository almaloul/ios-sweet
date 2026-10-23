// Writes an FsTree as a case-sensitive, journaled HFS+ (HFSX) volume image,
// the format of the SWEET iOS root (what `mkfs.hfsplus -s -J` + a copy produced).
//
// Layout: volume header | allocation bitmap | journal info block | journal |
// extents B-tree | attributes B-tree | catalog B-tree | file data (contiguous,
// one extent per file) | free space | alternate volume header.
//
// The journal is left zeroed with kJIJournalNeedInitMask set, exactly as Linux's
// mkfs.hfsplus does: XNU initializes it on the first mount. Hard links use the
// modern (Leopard+) scheme Apple's fsck_hfs checks: indirect "iNode<id>" files in
// the "HFS+ Private Data" folder, link files with prev/next chain IDs, the first
// link ID in the inode's reserved1. Extended attributes are not copied.
// Reference: Apple TN1150, hfs_format.h.
using System;
using System.Buffers.Binary;
using System.Text;
using SweetEmu.Core.Fs;

namespace SweetEmu.Core.Hfs;

public sealed class HfsWriter
{
    public const int BlockSize = 4096;
    const int CatalogNodeSize = 8192, ExtentsNodeSize = 4096, AttributesNodeSize = 8192;
    const long JournalSize = 8L << 20;
    const long ExtentsFileSize = 1L << 20;
    const long AttributesFileSize = 4L << 20;
    const uint HfsEpochOffset = 2082844800; // 1904 -> 1970

    const ushort kHFSPlusFolderRecord = 1, kHFSPlusFileRecord = 2, kHFSPlusFolderThreadRecord = 3, kHFSPlusFileThreadRecord = 4;
    const ushort kHFSThreadExistsMask = 0x0002, kHFSHasLinkChainMask = 0x0020, kHFSHasFolderCountMask = 0x0010;
    const uint kHardLinkFileType = 0x686C6E6B, kHFSPlusCreator = 0x6866732B; // 'hlnk' 'hfs+'
    const uint kSymLinkFileType = 0x736C6E6B, kSymLinkCreator = 0x72686170;  // 'slnk' 'rhap'
    const uint kJournalFileType = 0x6a726e6c;                                // 'jrnl'
    const ushort kIsInvisible = 0x4000, kNameLocked = 0x1000;
    // On disk the name starts with four NUL characters (XNU shows them as U+2400).
    public const string PrivateDirName = "\0\0\0\0HFS+ Private Data";

    public string VolumeName { get; init; } = "iOSRoot";
    public Action<long, long>? Progress { get; init; }   // (bytes written, total data bytes)

    // ---- catalog model ----

    sealed class CatRecord
    {
        public uint ParentId;
        public string Name = "";        // HFS form: decomposed, ':' -> '/'
        public byte[] Data = Array.Empty<byte>();
        public byte[]? Key;
    }

    sealed class FileAlloc
    {
        public FsInode Node = null!;
        public long Blocks;
        public uint StartBlock;
    }

    sealed class Item
    {
        public uint Cnid;
        public uint ParentCnid;
        public string HfsName = "";
        public FsInode Node = null!;
        public bool IsLink;             // hard link file record (data lives in the inode file)
        public uint LinkRef, PrevLink, NextLink;
        public bool IsIndirect;         // "iNode<n>" file in the private dir
        public uint FirstLink, LinkCount;
        public FileAlloc? Alloc;
        public uint Valence, FolderCount;
        public byte[]? SymlinkData;
    }

    /// Write the tree to `path` as an image of `imageSize` bytes (rounded down to blocks).
    public void Write(FsTree tree, string path, long imageSize, CancellationToken cancel = default)
    {
        long totalBlocks = imageSize / BlockSize;
        if (totalBlocks > uint.MaxValue) throw new ArgumentException("image too large for 4 KiB HFS+ blocks");

        var items = new List<Item>();
        var byNode = new Dictionary<FsInode, List<Item>>(ReferenceEqualityComparer.Instance);
        uint nextCnid = 16;
        uint now = HfsTime(FsInode.NowNs());

        // CNIDs 16/17: journal files; then the private data folder; then the tree.
        uint journalCnid = nextCnid++, jibCnid = nextCnid++, privCnid = nextCnid++;

        var root = new Item { Cnid = 2, ParentCnid = 1, HfsName = HfsName(VolumeName), Node = tree.Root };
        items.Add(root);
        var dirItems = new Dictionary<uint, Item> { [2] = root };

        // Walk: directories get folder records; files are grouped by inode for hard links.
        var queue = new Queue<Item>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            foreach (var (name, child) in dir.Node.Entries!.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                cancel.ThrowIfCancellationRequested();
                var it = new Item { Cnid = nextCnid++, ParentCnid = dir.Cnid, HfsName = HfsName(name), Node = child };
                items.Add(it);
                dir.Valence++;
                if (child.Kind == NodeKind.Dir)
                {
                    dir.FolderCount++;
                    dirItems[it.Cnid] = it;
                    queue.Enqueue(it);
                }
                else if (child.Kind == NodeKind.File)
                {
                    if (!byNode.TryGetValue(child, out var l)) byNode[child] = l = new List<Item>();
                    l.Add(it);
                }
            }
        }

        // Root also holds the two journal files and the private folder.
        root.Valence += 3;
        root.FolderCount += 1;
        var privDir = new Item
        {
            Cnid = privCnid, ParentCnid = 2, HfsName = PrivateDirName,
            Node = FsInode.NewDir(0, 0, 0),
        };
        privDir.Node.Mode = FsInode.S_IFDIR; // no permission bits, as XNU creates it
        privCreate = HfsTime(privDir.Node.CreateNs);
        dirItems[privCnid] = privDir;

        // Hard links: one indirect inode file per multiply-linked file.
        var indirect = new List<Item>();
        foreach (var (node, links) in byNode)
        {
            if (links.Count < 2) continue;
            var ind = new Item
            {
                Cnid = nextCnid++, ParentCnid = privCnid, Node = node, IsIndirect = true,
                LinkCount = (uint)links.Count, FirstLink = links[0].Cnid,
            };
            ind.HfsName = "iNode" + ind.Cnid;
            for (int i = 0; i < links.Count; i++)
            {
                links[i].IsLink = true;
                links[i].LinkRef = ind.Cnid;
                links[i].PrevLink = i > 0 ? links[i - 1].Cnid : 0;
                links[i].NextLink = i + 1 < links.Count ? links[i + 1].Cnid : 0;
            }
            indirect.Add(ind);
            privDir.Valence++;
        }
        items.Add(privDir);
        items.AddRange(indirect);

        // Data allocations: one per file inode (the indirect file holds hard-linked data).
        var allocs = new List<FileAlloc>();
        foreach (var it in items)
        {
            if (it.Node.Kind == NodeKind.File && !it.IsLink)
            {
                it.Alloc = new FileAlloc { Node = it.Node, Blocks = (it.Node.Length + BlockSize - 1) / BlockSize };
                allocs.Add(it.Alloc);
            }
            else if (it.Node.Kind == NodeKind.Symlink)
            {
                it.SymlinkData = Encoding.UTF8.GetBytes(it.Node.Target ?? "");
                it.Alloc = new FileAlloc { Node = it.Node, Blocks = (it.SymlinkData.Length + BlockSize - 1) / BlockSize };
                allocs.Add(it.Alloc);
            }
        }

        // ---- layout ----
        long bitmapBytes = (totalBlocks + 7) / 8;
        long bitmapBlocks = (bitmapBytes + BlockSize - 1) / BlockSize;
        long cur = 1;                                   // block 0: boot blocks + volume header
        long bitmapStart = cur; cur += bitmapBlocks;
        long jibBlock = cur; cur += 1;
        long journalStart = cur; cur += JournalSize / BlockSize;
        long extentsStart = cur; cur += ExtentsFileSize / BlockSize;
        long attrStart = cur; cur += AttributesFileSize / BlockSize;

        // Catalog size: build once with placeholder extents (record sizes do not depend on them).
        var records = BuildRecords(items, journalCnid, jibCnid, jibBlock, journalStart, now);
        int catNodesUsed = CountNodes(records, CatalogNodeSize);
        long catalogBytes = RoundUp(Math.Max((long)catNodesUsed * CatalogNodeSize * 3 / 2 + (4L << 20), 8L << 20), CatalogNodeSize);
        int mapCapacity = (CatalogNodeSize - 256) * 8;
        if (catalogBytes / CatalogNodeSize > mapCapacity) catalogBytes = (long)mapCapacity * CatalogNodeSize;
        if (catNodesUsed >= catalogBytes / CatalogNodeSize) throw new InvalidOperationException("catalog too large");
        long catalogStart = cur; cur += catalogBytes / BlockSize;

        // Lay out (and later copy) file data in source order: sequential reads of the
        // compressed source image instead of jumping between its chunks.
        allocs.Sort((x, y) => x.Node.SourceOrder.CompareTo(y.Node.SourceOrder));
        long dataBytes = 0;
        foreach (var a in allocs)
        {
            a.StartBlock = (uint)cur;
            cur += a.Blocks;
            dataBytes += a.Node.Kind == NodeKind.File ? a.Node.Length : 0;
        }
        long altVhBlock = (totalBlocks * BlockSize - 1024) / BlockSize;
        if (cur >= altVhBlock) throw new IOException($"the files need {cur * (long)BlockSize >> 20} MiB; make the image larger");
        long usedBlocks = cur + 1 + (totalBlocks - 1 - altVhBlock);

        records = BuildRecords(items, journalCnid, jibCnid, jibBlock, journalStart, now);
        var catalog = BuildBTree(records, CatalogNodeSize, (int)(catalogBytes / CatalogNodeSize), maxKeyLength: 516,
            keyCompareType: 0xBC, attributes: 6, clumpSize: (uint)catalogBytes);

        // ---- write ----
        using var f = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.None);
        TryMakeSparse(f);
        f.SetLength(totalBlocks * (long)BlockSize);

        // Allocation bitmap
        var bitmap = new byte[bitmapBlocks * BlockSize];
        void Mark(long start, long count)
        {
            for (long b = start; b < start + count; b++) bitmap[b >> 3] |= (byte)(0x80 >> (int)(b & 7));
        }
        Mark(0, cur);
        Mark(altVhBlock, totalBlocks - altVhBlock);
        WriteAt(f, bitmapStart * BlockSize, bitmap);

        // Journal info block (journal itself stays zero: XNU initializes it).
        var jib = new byte[BlockSize];
        BinaryPrimitives.WriteUInt32BigEndian(jib, 1 | 4);   // kJIJournalInFSMask | kJIJournalNeedInitMask
        BinaryPrimitives.WriteUInt64BigEndian(jib.AsSpan(36), (ulong)(journalStart * BlockSize));
        BinaryPrimitives.WriteUInt64BigEndian(jib.AsSpan(44), (ulong)JournalSize);
        WriteAt(f, jibBlock * BlockSize, jib);

        WriteAt(f, extentsStart * BlockSize, BuildBTree(new List<CatRecord>(), ExtentsNodeSize,
            (int)(ExtentsFileSize / ExtentsNodeSize), maxKeyLength: 10, keyCompareType: 0, attributes: 2, clumpSize: (uint)ExtentsFileSize));
        WriteAt(f, attrStart * BlockSize, BuildBTree(new List<CatRecord>(), AttributesNodeSize,
            (int)(AttributesFileSize / AttributesNodeSize), maxKeyLength: 266, keyCompareType: 0, attributes: 6, clumpSize: (uint)AttributesFileSize));
        WriteAt(f, catalogStart * BlockSize, catalog);

        // File data
        long written = 0;
        var buf = new byte[4 << 20];
        var writeOrder = items.Where(i => i.Alloc != null).OrderBy(i => i.Alloc!.StartBlock).ToList();
        foreach (var it in writeOrder)
        {
            cancel.ThrowIfCancellationRequested();
            f.Position = (long)it.Alloc.StartBlock * BlockSize;
            if (it.SymlinkData != null)
            {
                f.Write(it.SymlinkData);
                continue;
            }
            long left = it.Node.Length;
            if (left == 0) continue;
            using var s = it.Node.OpenRead();
            while (left > 0)
            {
                int want = (int)Math.Min(buf.Length, left);
                int got = 0;
                while (got < want)
                {
                    int n = s.Read(buf, got, want - got);
                    if (n == 0) throw new IOException($"short read in {it.HfsName}");
                    got += n;
                }
                f.Write(buf, 0, got);
                left -= got;
                written += got;
                Progress?.Invoke(written, dataBytes);
            }
        }

        // Volume headers
        int files = 0, folders = 0;
        foreach (var it in items)
        {
            if (it.Cnid == 2) continue;
            if (it.Node.Kind == NodeKind.Dir) folders++;
            else files++;
        }
        files += 2; // journal files
        var vh = new byte[512];
        var v = vh.AsSpan();
        v[0] = (byte)'H'; v[1] = (byte)'X';
        BinaryPrimitives.WriteUInt16BigEndian(v[2..], 5);
        BinaryPrimitives.WriteUInt32BigEndian(v[4..], 0x80000000 | 0x2000 | 0x100); // unused-node-fix | journaled | unmounted
        BinaryPrimitives.WriteUInt32BigEndian(v[8..], 0x31302e30);                // '10.0'
        BinaryPrimitives.WriteUInt32BigEndian(v[12..], (uint)jibBlock);
        BinaryPrimitives.WriteUInt32BigEndian(v[16..], now);                      // createDate
        BinaryPrimitives.WriteUInt32BigEndian(v[20..], now);                      // modifyDate
        BinaryPrimitives.WriteUInt32BigEndian(v[28..], now);                      // checkedDate
        BinaryPrimitives.WriteUInt32BigEndian(v[32..], (uint)files);
        BinaryPrimitives.WriteUInt32BigEndian(v[36..], (uint)folders);
        BinaryPrimitives.WriteUInt32BigEndian(v[40..], BlockSize);
        BinaryPrimitives.WriteUInt32BigEndian(v[44..], (uint)totalBlocks);
        BinaryPrimitives.WriteUInt32BigEndian(v[48..], (uint)(totalBlocks - usedBlocks));
        BinaryPrimitives.WriteUInt32BigEndian(v[52..], (uint)cur);                // nextAllocation
        BinaryPrimitives.WriteUInt32BigEndian(v[56..], 65536);
        BinaryPrimitives.WriteUInt32BigEndian(v[60..], 65536);
        BinaryPrimitives.WriteUInt32BigEndian(v[64..], nextCnid);
        BinaryPrimitives.WriteUInt64BigEndian(v[72..], 1);                        // encodingsBitmap: MacRoman
        var uuid = Guid.NewGuid().ToByteArray();
        uuid.AsSpan(0, 8).CopyTo(v[(80 + 24)..]);                                // finderInfo[6..7]: volume UUID
        Fork(v[112..], bitmapBytes, (uint)bitmapStart, (uint)bitmapBlocks, (uint)(bitmapBlocks * BlockSize));
        Fork(v[192..], ExtentsFileSize, (uint)extentsStart, (uint)(ExtentsFileSize / BlockSize), (uint)ExtentsFileSize);
        Fork(v[272..], catalogBytes, (uint)catalogStart, (uint)(catalogBytes / BlockSize), (uint)catalogBytes);
        Fork(v[352..], AttributesFileSize, (uint)attrStart, (uint)(AttributesFileSize / BlockSize), (uint)AttributesFileSize);
        WriteAt(f, 1024, vh);
        WriteAt(f, totalBlocks * BlockSize - 1024, vh);
        f.Flush(true);
    }

    static void Fork(Span<byte> b, long size, uint start, uint blocks, uint clump)
    {
        BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)size);
        BinaryPrimitives.WriteUInt32BigEndian(b[8..], clump);
        BinaryPrimitives.WriteUInt32BigEndian(b[12..], blocks);
        if (blocks > 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b[16..], start);
            BinaryPrimitives.WriteUInt32BigEndian(b[20..], blocks);
        }
    }

    static void WriteAt(FileStream f, long offset, byte[] data)
    {
        f.Position = offset;
        f.Write(data);
    }

    static long RoundUp(long v, long a) => (v + a - 1) / a * a;

    public static uint HfsTime(ulong ns)
    {
        ulong s = ns / 1_000_000_000UL + HfsEpochOffset;
        return s > uint.MaxValue ? uint.MaxValue : (uint)s;
    }

    /// HFS+ catalog name: Apple-style canonical decomposition, ':' stored as '/'.
    public static string HfsName(string posixName)
    {
        var s = posixName.Replace(':', '/');
        if (s.All(c => c < 0x80)) return s;
        // Apple does not decompose U+2000-U+2FFF, U+F900-U+FAFF and U+2F800-U+2FAFF.
        var sb = new StringBuilder();
        var run = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            int cp = char.ConvertToUtf32(s, i);
            int len = char.IsSurrogatePair(s, i) ? 2 : 1;
            bool keep = (cp >= 0x2000 && cp <= 0x2FFF) || (cp >= 0xF900 && cp <= 0xFAFF) || (cp >= 0x2F800 && cp <= 0x2FAFF);
            if (keep)
            {
                sb.Append(run.ToString().Normalize(NormalizationForm.FormD));
                run.Clear();
                sb.Append(s, i, len);
            }
            else run.Append(s, i, len);
            i += len - 1;
        }
        sb.Append(run.ToString().Normalize(NormalizationForm.FormD));
        return sb.ToString();
    }

    // ---- catalog records ----

    List<CatRecord> BuildRecords(List<Item> items, uint journalCnid, uint jibCnid, long jibBlock, long journalStart, uint now)
    {
        var recs = new List<CatRecord>(items.Count * 2 + 8);
        foreach (var it in items)
        {
            var n = it.Node;
            if (n.Kind == NodeKind.Dir)
            {
                recs.Add(new CatRecord { ParentId = it.ParentCnid, Name = it.HfsName, Data = FolderRecord(it) });
                recs.Add(new CatRecord { ParentId = it.Cnid, Name = "", Data = Thread(kHFSPlusFolderThreadRecord, it.ParentCnid, it.HfsName) });
            }
            else
            {
                recs.Add(new CatRecord { ParentId = it.ParentCnid, Name = it.HfsName, Data = FileRecord(it) });
                recs.Add(new CatRecord { ParentId = it.Cnid, Name = "", Data = Thread(kHFSPlusFileThreadRecord, it.ParentCnid, it.HfsName) });
            }
        }
        // Journal files in the root, as mkfs.hfsplus makes them.
        recs.Add(new CatRecord { ParentId = 2, Name = ".journal", Data = JournalFileRecord(journalCnid, now, JournalSize, (uint)journalStart) });
        recs.Add(new CatRecord { ParentId = journalCnid, Name = "", Data = Thread(kHFSPlusFileThreadRecord, 2, ".journal") });
        recs.Add(new CatRecord { ParentId = 2, Name = ".journal_info_block", Data = JournalFileRecord(jibCnid, now, BlockSize, (uint)jibBlock) });
        recs.Add(new CatRecord { ParentId = jibCnid, Name = "", Data = Thread(kHFSPlusFileThreadRecord, 2, ".journal_info_block") });

        foreach (var r in recs) r.Key = CatalogKey(r.ParentId, r.Name);
        recs.Sort((a, b) => CompareKeys(a, b));
        for (int i = 1; i < recs.Count; i++)
        {
            if (CompareKeys(recs[i - 1], recs[i]) == 0)
                throw new InvalidOperationException($"duplicate catalog name \"{recs[i].Name}\" in folder {recs[i].ParentId} (names that differ only in Unicode normalization?)");
        }
        return recs;
    }

    static int CompareKeys(CatRecord a, CatRecord b)
    {
        int c = a.ParentId.CompareTo(b.ParentId);
        return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
    }

    static byte[] CatalogKey(uint parent, string name)
    {
        if (name.Length > 255) throw new IOException($"name too long for HFS+: {name}");
        var k = new byte[2 + 6 + 2 * name.Length];
        BinaryPrimitives.WriteUInt16BigEndian(k, (ushort)(6 + 2 * name.Length));
        BinaryPrimitives.WriteUInt32BigEndian(k.AsSpan(2), parent);
        BinaryPrimitives.WriteUInt16BigEndian(k.AsSpan(6), (ushort)name.Length);
        for (int i = 0; i < name.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(k.AsSpan(8 + 2 * i), name[i]);
        return k;
    }

    static byte[] Thread(ushort type, uint parent, string name)
    {
        var d = new byte[10 + 2 * name.Length];
        BinaryPrimitives.WriteUInt16BigEndian(d, type);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(4), parent);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(8), (ushort)name.Length);
        for (int i = 0; i < name.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(10 + 2 * i), name[i]);
        return d;
    }

    static void Dates(Span<byte> d, FsInode n)
    {
        BinaryPrimitives.WriteUInt32BigEndian(d, HfsTime(n.CreateNs));
        BinaryPrimitives.WriteUInt32BigEndian(d[4..], HfsTime(n.ModNs));
        BinaryPrimitives.WriteUInt32BigEndian(d[8..], HfsTime(n.ChangeNs));
        BinaryPrimitives.WriteUInt32BigEndian(d[12..], HfsTime(n.AccessNs));
    }

    static void BsdInfo(Span<byte> d, uint owner, uint group, uint bsdFlags, ushort mode, uint special)
    {
        BinaryPrimitives.WriteUInt32BigEndian(d, owner);
        BinaryPrimitives.WriteUInt32BigEndian(d[4..], group);
        d[8] = (byte)((bsdFlags >> 16) & 0xFF);                 // adminFlags (SF_*)
        d[9] = (byte)(bsdFlags & 0xFF & ~0x20u);                 // ownerFlags (UF_*), minus UF_COMPRESSED
        BinaryPrimitives.WriteUInt16BigEndian(d[10..], mode);
        BinaryPrimitives.WriteUInt32BigEndian(d[12..], special);
    }

    static byte[] FolderRecord(Item it)
    {
        var d = new byte[88];
        var n = it.Node;
        BinaryPrimitives.WriteUInt16BigEndian(d, kHFSPlusFolderRecord);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(2), kHFSHasFolderCountMask);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(4), it.Valence);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), it.Cnid);
        Dates(d.AsSpan(12), n);
        bool priv = it.HfsName == PrivateDirName && it.ParentCnid == 2;
        BsdInfo(d.AsSpan(32), n.Uid, n.Gid, priv ? 0x2u /* UF_IMMUTABLE */ : n.BsdFlags, n.Mode, 0);
        ushort finder = (ushort)(priv ? kIsInvisible | kNameLocked : (n.BsdFlags & 0x8000) != 0 ? kIsInvisible : 0);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(48 + 8), finder);   // FndrDirInfo.frFlags
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(84), it.FolderCount);
        return d;
    }

    uint privCreate; // link files carry the private folder's create date (fsck_hfs checks it)

    byte[] FileRecord(Item it)
    {
        var d = new byte[248];
        var n = it.Node;
        ushort flags = kHFSThreadExistsMask;
        if (it.IsLink || it.IsIndirect) flags |= kHFSHasLinkChainMask;
        BinaryPrimitives.WriteUInt16BigEndian(d, kHFSPlusFileRecord);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(2), flags);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(4), it.IsIndirect ? it.FirstLink : 0); // reserved1 = hl_firstLinkID
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), it.Cnid);
        Dates(d.AsSpan(12), n);
        ushort finderFlags = (n.BsdFlags & 0x8000) != 0 ? kIsInvisible : (ushort)0;
        if (it.IsLink)
        {
            // Link files: prev/next link IDs in owner/group, the link reference in special.
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(12), privCreate);
            BsdInfo(d.AsSpan(32), it.PrevLink, it.NextLink, 0, (ushort)(FsInode.S_IFREG | 0x1A4), it.LinkRef);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(48), kHardLinkFileType);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(52), kHFSPlusCreator);
            BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(56), finderFlags);
            return d;
        }
        uint special = it.IsIndirect ? it.LinkCount : n.Kind == NodeKind.Special ? n.RawDevice : 1;
        BsdInfo(d.AsSpan(32), n.Uid, n.Gid, n.BsdFlags, n.Mode, special);
        if (n.Kind == NodeKind.Symlink)
        {
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(48), kSymLinkFileType);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(52), kSymLinkCreator);
        }
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(56), finderFlags);
        if (it.Alloc != null)
        {
            long size = it.SymlinkData?.Length ?? n.Length;
            uint blocks = (uint)it.Alloc.Blocks;
            BinaryPrimitives.WriteUInt64BigEndian(d.AsSpan(88), (ulong)size);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(100), blocks);
            if (blocks > 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(104), it.Alloc.StartBlock);
                BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(108), blocks);
            }
        }
        return d;
    }

    static byte[] JournalFileRecord(uint cnid, uint now, long size, uint start)
    {
        var d = new byte[248];
        BinaryPrimitives.WriteUInt16BigEndian(d, kHFSPlusFileRecord);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(2), kHFSThreadExistsMask);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), cnid);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(12), now);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(16), now);
        BsdInfo(d.AsSpan(32), 0, 0, 0, FsInode.S_IFREG, 1);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(48), kJournalFileType);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(52), kHFSPlusCreator);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(56), kIsInvisible | kNameLocked);
        uint blocks = (uint)(size / BlockSize);
        BinaryPrimitives.WriteUInt64BigEndian(d.AsSpan(88), (ulong)size);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(100), blocks);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(104), start);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(108), blocks);
        return d;
    }

    // ---- B-trees ----

    static int RecordSize(CatRecord r) => r.Key!.Length + r.Data.Length;

    static int CountNodes(List<CatRecord> recs, int nodeSize)
    {
        // Mirror BuildBTree's packing to know how many nodes the tree will use.
        var (leaves, _) = PackLeaves(recs, nodeSize);
        int total = 1 + leaves.Count;
        var level = leaves.Select(l => recs[l.First].Key!).ToList();
        while (level.Count > 1)
        {
            var groups = PackIndex(level, nodeSize);
            total += groups.Count;
            level = groups.Select(g => level[g.First]).ToList();
        }
        return total;
    }

    static (List<(int First, int Count)>, int) PackLeaves(List<CatRecord> recs, int nodeSize)
    {
        var nodes = new List<(int, int)>();
        int i = 0;
        while (i < recs.Count)
        {
            int start = i, used = 14 + 2;
            while (i < recs.Count && used + RecordSize(recs[i]) + 2 <= nodeSize)
            {
                used += RecordSize(recs[i]) + 2;
                i++;
            }
            if (i == start) throw new InvalidOperationException("catalog record larger than a node");
            nodes.Add((start, i - start));
        }
        return (nodes, recs.Count);
    }

    static List<(int First, int Count)> PackIndex(List<byte[]> keys, int nodeSize)
    {
        var nodes = new List<(int, int)>();
        int i = 0;
        while (i < keys.Count)
        {
            int start = i, used = 14 + 2;
            while (i < keys.Count && used + keys[i].Length + 4 + 2 <= nodeSize)
            {
                used += keys[i].Length + 4 + 2;
                i++;
            }
            nodes.Add((start, i - start));
        }
        return nodes;
    }

    /// Serialize a whole B-tree file: header node, leaves, index levels (root last).
    static byte[] BuildBTree(List<CatRecord> recs, int nodeSize, int totalNodes, ushort maxKeyLength,
        byte keyCompareType, uint attributes, uint clumpSize)
    {
        var file = new byte[(long)totalNodes * nodeSize];
        int nextNode = 1;
        uint depth = 0, root = 0, firstLeaf = 0, lastLeaf = 0;

        if (recs.Count > 0)
        {
            var (leafGroups, _) = PackLeaves(recs, nodeSize);
            var levelNodes = new List<int>();
            var levelKeys = new List<byte[]>();
            for (int g = 0; g < leafGroups.Count; g++)
            {
                int num = nextNode++;
                levelNodes.Add(num);
                levelKeys.Add(recs[leafGroups[g].First].Key!);
                var payload = new List<byte[]>();
                for (int k = 0; k < leafGroups[g].Count; k++)
                {
                    var r = recs[leafGroups[g].First + k];
                    payload.Add(r.Key!.Concat(r.Data).ToArray());
                }
                WriteNode(file, num, nodeSize,
                    fLink: g + 1 < leafGroups.Count ? (uint)(num + 1) : 0,
                    bLink: g > 0 ? (uint)(num - 1) : 0,
                    kind: unchecked((sbyte)-1), height: 1, payload);
            }
            firstLeaf = (uint)levelNodes[0];
            lastLeaf = (uint)levelNodes[^1];
            depth = 1;
            byte height = 1;
            while (levelNodes.Count > 1)
            {
                height++;
                var groups = PackIndex(levelKeys, nodeSize);
                var newNodes = new List<int>();
                var newKeys = new List<byte[]>();
                int first = nextNode;
                for (int g = 0; g < groups.Count; g++)
                {
                    int num = nextNode++;
                    newNodes.Add(num);
                    newKeys.Add(levelKeys[groups[g].First]);
                    var payload = new List<byte[]>();
                    for (int k = 0; k < groups[g].Count; k++)
                    {
                        var key = levelKeys[groups[g].First + k];
                        var rec = new byte[key.Length + 4];
                        key.CopyTo(rec, 0);
                        BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(key.Length), (uint)levelNodes[groups[g].First + k]);
                        payload.Add(rec);
                    }
                    WriteNode(file, num, nodeSize,
                        fLink: g + 1 < groups.Count ? (uint)(num + 1) : 0,
                        bLink: g > 0 ? (uint)(num - 1) : 0,
                        kind: 0, height: height, payload);
                }
                levelNodes = newNodes;
                levelKeys = newKeys;
                depth++;
            }
            root = (uint)levelNodes[0];
        }

        // Header node
        var h = file.AsSpan(0, nodeSize);
        h[8] = 1; // kBTHeaderNode
        BinaryPrimitives.WriteUInt16BigEndian(h[10..], 3);
        BinaryPrimitives.WriteUInt16BigEndian(h[14..], (ushort)depth);
        BinaryPrimitives.WriteUInt32BigEndian(h[16..], root);
        BinaryPrimitives.WriteUInt32BigEndian(h[20..], (uint)recs.Count);
        BinaryPrimitives.WriteUInt32BigEndian(h[24..], firstLeaf);
        BinaryPrimitives.WriteUInt32BigEndian(h[28..], lastLeaf);
        BinaryPrimitives.WriteUInt16BigEndian(h[32..], (ushort)nodeSize);
        BinaryPrimitives.WriteUInt16BigEndian(h[34..], maxKeyLength);
        BinaryPrimitives.WriteUInt32BigEndian(h[36..], (uint)totalNodes);
        BinaryPrimitives.WriteUInt32BigEndian(h[40..], (uint)(totalNodes - nextNode));
        BinaryPrimitives.WriteUInt32BigEndian(h[46..], clumpSize);
        h[50] = 0;                       // btreeType
        h[51] = keyCompareType;
        BinaryPrimitives.WriteUInt32BigEndian(h[52..], attributes);
        int mapStart = 248, mapEnd = nodeSize - 8;
        if (nextNode > (mapEnd - mapStart) * 8) throw new InvalidOperationException("B-tree needs map nodes");
        for (int n = 0; n < nextNode; n++) h[mapStart + (n >> 3)] |= (byte)(0x80 >> (n & 7));
        BinaryPrimitives.WriteUInt16BigEndian(h[(nodeSize - 2)..], 14);
        BinaryPrimitives.WriteUInt16BigEndian(h[(nodeSize - 4)..], 120);
        BinaryPrimitives.WriteUInt16BigEndian(h[(nodeSize - 6)..], 248);
        BinaryPrimitives.WriteUInt16BigEndian(h[(nodeSize - 8)..], (ushort)mapEnd);
        return file;
    }

    static void WriteNode(byte[] file, int num, int nodeSize, uint fLink, uint bLink, sbyte kind, byte height, List<byte[]> records)
    {
        var n = file.AsSpan(num * nodeSize, nodeSize);
        BinaryPrimitives.WriteUInt32BigEndian(n, fLink);
        BinaryPrimitives.WriteUInt32BigEndian(n[4..], bLink);
        n[8] = (byte)kind;
        n[9] = height;
        BinaryPrimitives.WriteUInt16BigEndian(n[10..], (ushort)records.Count);
        int off = 14;
        for (int i = 0; i < records.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(n[(nodeSize - 2 * (i + 1))..], (ushort)off);
            records[i].CopyTo(n[off..]);
            off += records[i].Length;
        }
        BinaryPrimitives.WriteUInt16BigEndian(n[(nodeSize - 2 * (records.Count + 1))..], (ushort)off);
    }

    static void TryMakeSparse(FileStream f)
    {
        if (!OperatingSystem.IsWindows()) return; // Linux/macOS files are sparse already
        try
        {
            const uint FSCTL_SET_SPARSE = 0x000900C4;
            DeviceIoControl(f.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch
        {
            // Not fatal: the image just takes its full size on disk.
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, IntPtr inBuf, uint inSize,
        IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);
}
