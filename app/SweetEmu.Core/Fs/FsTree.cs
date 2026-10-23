// In-memory file tree that the root builder assembles (from APFS volumes plus
// generated files) and the HFS+ writer serializes. A FsInode reachable under
// several names is a hard link.
using System;

namespace SweetEmu.Core.Fs;

public enum NodeKind { Dir, File, Symlink, Special }

public sealed class FsInode
{
    public NodeKind Kind;
    /// Full st_mode including the S_IFMT type bits.
    public ushort Mode;
    public uint Uid, Gid;
    public uint BsdFlags;
    /// Nanoseconds since 1970-01-01 UTC.
    public ulong CreateNs, ModNs, ChangeNs, AccessNs;
    public uint RawDevice;
    /// Where the data sits in the source image (read order hint for fast copying).
    public ulong SourceOrder = ulong.MaxValue;

    // Dir
    public Dictionary<string, FsInode>? Entries;
    // File: either bytes or a lazily opened stream of Length bytes
    public long Length;
    public Func<Stream>? Open;
    public byte[]? Bytes;
    // Symlink
    public string? Target;

    public const ushort S_IFDIR = 0x4000, S_IFREG = 0x8000, S_IFLNK = 0xA000;

    public static ulong NowNs() => (ulong)(DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100;

    public static FsInode NewDir(ushort perm, uint uid, uint gid)
    {
        ulong now = NowNs();
        return new FsInode
        {
            Kind = NodeKind.Dir, Mode = (ushort)(S_IFDIR | perm), Uid = uid, Gid = gid,
            Entries = new Dictionary<string, FsInode>(StringComparer.Ordinal),
            CreateNs = now, ModNs = now, ChangeNs = now, AccessNs = now,
        };
    }

    public static FsInode NewFile(byte[] content, ushort perm, uint uid, uint gid)
    {
        ulong now = NowNs();
        return new FsInode
        {
            Kind = NodeKind.File, Mode = (ushort)(S_IFREG | perm), Uid = uid, Gid = gid,
            Bytes = content, Length = content.Length,
            CreateNs = now, ModNs = now, ChangeNs = now, AccessNs = now,
        };
    }

    public void SetContent(byte[] content)
    {
        Bytes = content;
        Open = null;
        Length = content.Length;
        ModNs = ChangeNs = NowNs();
    }

    public byte[] ReadAll()
    {
        if (Bytes != null) return Bytes;
        using var s = Open!();
        var b = new byte[Length];
        s.ReadExactly(b);
        return b;
    }

    public Stream OpenRead() => Bytes != null ? new MemoryStream(Bytes, false) : Open!();

    public ushort Perm
    {
        get => (ushort)(Mode & 0x0FFF);
        set => Mode = (ushort)((Mode & 0xF000) | (value & 0x0FFF));
    }
}

/// Path operations on a tree rooted at a directory inode. Paths are "a/b/c" (no leading slash needed).
public sealed class FsTree(FsInode root)
{
    public FsInode Root { get; } = root;

    static string[] Split(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    public FsInode? Get(string path)
    {
        var cur = Root;
        foreach (var part in Split(path))
        {
            if (cur.Kind != NodeKind.Dir || !cur.Entries!.TryGetValue(part, out var next)) return null;
            cur = next;
        }
        return cur;
    }

    public bool Exists(string path) => Get(path) != null;

    /// mkdir -p: missing components are created with the given owner/perm (existing ones untouched).
    public FsInode MkdirP(string path, ushort perm = 0x1ED, uint uid = 0, uint gid = 0)
    {
        var cur = Root;
        foreach (var part in Split(path))
        {
            if (!cur.Entries!.TryGetValue(part, out var next))
            {
                next = FsInode.NewDir(perm, uid, gid);
                cur.Entries[part] = next;
            }
            if (next.Kind != NodeKind.Dir) throw new IOException($"not a directory: {path}");
            cur = next;
        }
        return cur;
    }

    /// `install -d -o uid -g gid -m perm`: create if needed and (re)apply owner and mode.
    public FsInode InstallDir(string path, ushort perm, uint uid, uint gid)
    {
        var d = MkdirP(path);
        d.Uid = uid;
        d.Gid = gid;
        d.Perm = perm;
        return d;
    }

    public (FsInode Dir, string Name) Parent(string path)
    {
        var parts = Split(path);
        var dir = Get(string.Join('/', parts[..^1])) ?? throw new IOException($"no parent directory for {path}");
        return (dir, parts[^1]);
    }

    /// Put an inode at path (parent must exist). Replaces an existing entry.
    public void Put(string path, FsInode node)
    {
        var (dir, name) = Parent(path);
        dir.Entries![name] = node;
    }

    public bool Remove(string path)
    {
        var (dir, name) = Parent(path);
        return dir.Entries!.Remove(name);
    }

    /// Every entry below root: (path without leading slash, inode), parents before children,
    /// siblings in ordinal name order.
    public IEnumerable<(string Path, FsInode Node)> Walk()
    {
        var stack = new Stack<(string Path, FsInode Node)>();
        void PushChildren(string p, FsInode d)
        {
            foreach (var (name, child) in d.Entries!.OrderByDescending(e => e.Key, StringComparer.Ordinal))
                stack.Push((p.Length == 0 ? name : p + "/" + name, child));
        }
        PushChildren("", Root);
        while (stack.Count > 0)
        {
            var (path, node) = stack.Pop();
            yield return (path, node);
            if (node.Kind == NodeKind.Dir) PushChildren(path, node);
        }
    }
}
