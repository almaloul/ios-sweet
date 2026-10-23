using SweetEmu.Core.Apfs;

namespace SweetEmu.Core.Fs;

/// Turns a loaded APFS volume into an FsInode tree (contents read lazily, decompressed).
public static class ApfsImport
{
    public static FsInode Import(ApfsVolume vol, Action<string>? warn = null)
    {
        vol.Load();
        var map = new Dictionary<ulong, FsInode>();
        var rootNode = vol.Inodes[ApfsVolume.RootDirId];
        var root = Convert(vol, rootNode, warn);
        map[ApfsVolume.RootDirId] = root;
        var stack = new Stack<(ulong Id, FsInode Dir)>();
        stack.Push((ApfsVolume.RootDirId, root));
        while (stack.Count > 0)
        {
            var (id, dir) = stack.Pop();
            if (!vol.Children.TryGetValue(id, out var kids)) continue;
            foreach (var e in kids)
            {
                if (!vol.Inodes.TryGetValue(e.FileId, out var n))
                {
                    warn?.Invoke($"missing inode {e.FileId} for {e.Name}");
                    continue;
                }
                if (!map.TryGetValue(e.FileId, out var node))
                {
                    node = Convert(vol, n, warn);
                    map[e.FileId] = node;
                    if (node.Kind == NodeKind.Dir) stack.Push((e.FileId, node));
                }
                else if (node.Kind == NodeKind.Dir)
                {
                    warn?.Invoke($"directory hard link skipped: {e.Name}");
                    continue;
                }
                else if (node.Kind == NodeKind.Symlink)
                {
                    node = Convert(vol, n, warn); // HFS+ cannot hard-link symlinks: separate copy
                }
                dir.Entries![e.Name] = node;
            }
        }
        return root;
    }

    static FsInode Convert(ApfsVolume vol, ApfsInode n, Action<string>? warn)
    {
        var f = new FsInode
        {
            Mode = n.Mode,
            Uid = n.Uid,
            Gid = n.Gid,
            BsdFlags = n.BsdFlags & ~0x20u, // UF_COMPRESSED: contents are stored decompressed
            CreateNs = n.CreateTime,
            ModNs = n.ModTime,
            ChangeNs = n.ChangeTime,
            AccessNs = n.AccessTime,
        };
        if (n.IsDir)
        {
            f.Kind = NodeKind.Dir;
            f.Entries = new Dictionary<string, FsInode>(StringComparer.Ordinal);
        }
        else if (n.IsSymlink)
        {
            f.Kind = NodeKind.Symlink;
            f.Target = vol.ReadSymlink(n);
        }
        else if (n.IsRegular)
        {
            f.Kind = NodeKind.File;
            f.Length = (long)vol.FileSize(n);
            f.Open = () => vol.OpenFile(n);
            f.SourceOrder = vol.FirstBlock(n);
        }
        else
        {
            f.Kind = NodeKind.Special;
        }
        return f;
    }
}
