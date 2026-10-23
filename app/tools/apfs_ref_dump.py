#!/usr/bin/env python3
"""Reference listing of every APFS volume in an image, via dissect.apfs (pip).
Same format as `SweetEmu.Cli apfs-dump`, so the two can be diffed:
  path<TAB>type<TAB>mode<TAB>uid<TAB>gid<TAB>size<TAB>sha256-or-link-target
"""
import hashlib, sys
from dissect.apfs import APFS

def walk(vol, node, path, out):
    for name, entry in sorted(node.listdir().items()):
        child = entry.inode if hasattr(entry, 'inode') else vol.get(entry)
        p = path + '/' + name
        if child.is_dir():
            t, extra, size = 'd', '', 0
        elif child.is_symlink():
            t, extra, size = 'l', child.readlink(), 0
        elif child.is_file():
            h = hashlib.sha256()
            f = child.open()
            n = 0
            while True:
                b = f.read(1 << 20)
                if not b:
                    break
                h.update(b)
                n += len(b)
            t, extra, size = 'f', h.hexdigest(), n
        else:
            t, extra, size = 'o', '', 0
        out.append(f'{p}\t{t}\t{child.mode & 0o7777:o}\t{child.uid}\t{child.gid}\t{size}\t{extra}')
        if t == 'd':
            walk(vol, child, p, out)

with open(sys.argv[1], 'rb') as fh:
    apfs = APFS(fh)
    for vol in apfs.volumes:
        if vol.is_encrypted:
            print(f'# volume {vol.name}: encrypted, skipped')
            continue
        out = []
        walk(vol, vol.get('/'), '', out)
        print(f'# volume {vol.name}')
        print('\n'.join(sorted(out)))
