#!/usr/bin/env python3
"""Compare an extracted directory tree with an apfs_ref_dump.py listing (file hashes, symlink targets)."""
import hashlib, os, sys, unicodedata
listing, root = sys.argv[1], sys.argv[2]
nfc = lambda s: unicodedata.normalize('NFC', s)
paths = {}
for dp, dns, fns in os.walk(root):
    for n in dns + fns:
        full = os.path.join(dp, n)
        paths[nfc('/' + os.path.relpath(full, root))] = full
ok = bad = 0
for line in open(listing, encoding='utf-8', newline='\n'):
    if line.startswith('#'):
        continue
    p, t, mode, uid, gid, size, extra = line.rstrip('\n').split('\t')
    full = paths.get(nfc(p))
    if full is None:
        print('MISSING', p); bad += 1; continue
    if t == 'f':
        h = hashlib.sha256(open(full, 'rb').read()).hexdigest()
        if h != extra:
            print('CONTENT', p); bad += 1; continue
    elif t == 'l':
        target = os.readlink(full) if os.path.islink(full) else open(full, encoding='utf-8').read()
        if target != extra:
            print('LINK', p, repr(target), repr(extra)); bad += 1; continue
    ok += 1
print(f'{ok} ok, {bad} bad')
sys.exit(1 if bad else 0)
