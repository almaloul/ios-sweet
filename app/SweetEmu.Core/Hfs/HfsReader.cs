// Minimal read-only HFS+/HFSX: path lookup through the catalog B-tree (leaf
// chain), hard links resolved through "HFS+ Private Data/iNode<n>", data forks
// with up to 8 extents (no extents-overflow file). Enough to check what
// HfsWriter put into an image.
using System.Buffers.Binary;
using System.Text;
using SweetEmu.Core.IO;

namespace SweetEmu.Core.Hfs;

public sealed class HfsReader : IDisposable
{
    readonly IByteSource dev;
    public uint BlockSize { get; }
    readonly Dictionary<(uint Parent, string Name), byte[]> records = new();

    public sealed record HfsFile(uint Id, long Length, (uint Start, uint Count)[] Extents);

    public HfsReader(IByteSource dev)
    {
        this.dev = dev;
        var vh = dev.ReadBytes(1024, 512);
        ushort sig = BinaryPrimitives.ReadUInt16BigEndian(vh);
        if (sig != 0x482B && sig != 0x4858) throw new InvalidDataException("not an HFS+ volume");
        BlockSize = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(0x28));
        var catalog = Fork(vh.AsSpan(0x110));
        byte[] Node(uint n, int nodeSize) => ReadFork(catalog, (long)n * nodeSize, nodeSize);
        var header = ReadFork(catalog, 0, 512);
        int nodeSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(14 + 18));
        uint leaf = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(14 + 10));
        int guard = 0;
        while (leaf != 0 && guard++ < 10_000_000)
        {
            var node = Node(leaf, nodeSize);
            int n = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(10));
            for (int i = 0; i < n; i++)
            {
                int off = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(nodeSize - 2 * (i + 1)));
                int keyLen = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(off));
                uint parent = BinaryPrimitives.ReadUInt32BigEndian(node.AsSpan(off + 2));
                int nameLen = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(off + 6));
                string name = Encoding.BigEndianUnicode.GetString(node, off + 8, nameLen * 2);
                int rec = off + 2 + keyLen;
                short type = BinaryPrimitives.ReadInt16BigEndian(node.AsSpan(rec));
                if (type == 1 || type == 2) records[(parent, name)] = node.AsSpan(rec, type == 1 ? 88 : 248).ToArray();
            }
            leaf = BinaryPrimitives.ReadUInt32BigEndian(node);
        }
    }

    static HfsFile Fork(ReadOnlySpan<byte> f, uint id = 0)
    {
        var ext = new (uint, uint)[8];
        for (int i = 0; i < 8; i++)
            ext[i] = (BinaryPrimitives.ReadUInt32BigEndian(f[(16 + 8 * i)..]), BinaryPrimitives.ReadUInt32BigEndian(f[(20 + 8 * i)..]));
        return new HfsFile(id, (long)BinaryPrimitives.ReadUInt64BigEndian(f), ext);
    }

    /// Bytes [offset, offset+len) of a fork (holes past the extents read as zeros).
    public byte[] ReadFork(HfsFile f, long offset, int len)
    {
        var b = new byte[len];
        long fileBlock = 0;
        foreach (var (start, count) in f.Extents)
        {
            if (count == 0) break;
            long extStart = fileBlock * BlockSize, extEnd = (fileBlock + count) * BlockSize;
            long from = Math.Max(offset, extStart), to = Math.Min(offset + len, extEnd);
            if (from < to)
                dev.ReadAt((long)start * BlockSize + (from - extStart), b.AsSpan((int)(from - offset), (int)(to - from)));
            fileBlock += count;
        }
        return b;
    }

    /// The data fork of a file by path ("System/Library/..."), following hard links.
    public HfsFile? Find(string path)
    {
        uint parent = 2; // root folder
        byte[]? rec = null;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!records.TryGetValue((parent, part), out rec)) return null;
            if (BinaryPrimitives.ReadInt16BigEndian(rec) == 1) parent = BinaryPrimitives.ReadUInt32BigEndian(rec.AsSpan(8));
        }
        if (rec == null || BinaryPrimitives.ReadInt16BigEndian(rec) != 2) return null;
        if (BinaryPrimitives.ReadUInt32BigEndian(rec.AsSpan(48)) == 0x686C6E6B) // 'hlnk'
        {
            uint inode = BinaryPrimitives.ReadUInt32BigEndian(rec.AsSpan(44));
            var priv = records.GetValueOrDefault((2u, HfsWriter.PrivateDirName));
            if (priv == null) return null;
            uint privId = BinaryPrimitives.ReadUInt32BigEndian(priv.AsSpan(8));
            if (!records.TryGetValue((privId, "iNode" + inode), out rec)) return null;
        }
        return Fork(rec.AsSpan(88), BinaryPrimitives.ReadUInt32BigEndian(rec.AsSpan(8)));
    }

    public void Dispose() => dev.Dispose();
}
