#!/usr/bin/env python3
"""Debug dump of an HFS+/HFSX image: volume header, special files, catalog records."""
import struct, sys, time

f = open(sys.argv[1], 'rb')
def rd(off, n):
    f.seek(off); return f.read(n)
vh = rd(1024, 512)
sig, ver, attr, lmv, jib = struct.unpack('>2sHIII', vh[:16])
bs = struct.unpack('>I', vh[40:44])[0]
tot, free, nextalloc, rclump, dclump, nextcnid, wc = struct.unpack('>IIIIIII', vh[44:72])
print(f'sig={sig} ver={ver} attr=0x{attr:x} lastMounted={lmv:#x} jib={jib} bs={bs} total={tot} free={free} nextCNID={nextcnid}')
print('files', struct.unpack('>II', vh[32:40]), 'finderInfo', vh[80:112].hex())
def fork(off, name):
    ls, clump, tb = struct.unpack('>QII', vh[off:off+16])
    ext = [struct.unpack('>II', vh[off+16+8*i:off+24+8*i]) for i in range(8)]
    print(f'{name}: size={ls} clump={clump} blocks={tb} ext={[e for e in ext if e[1]]}')
    return ext
alloc = fork(112, 'alloc'); exts = fork(192, 'extents'); cat = fork(272, 'catalog'); attrf = fork(352, 'attributes'); fork(432, 'startup')
if jib:
    ji = rd(jib*bs, 512)
    flags, = struct.unpack('>I', ji[:4]); off, size = struct.unpack('>QQ', ji[36:52])
    print(f'journal info: flags={flags:#x} offset={off} size={size}')
    jh = rd(off, 64)
    print('journal header', jh[:44].hex())
def btree(ext, name, dumprec):
    start = ext[0][0]*bs
    hdr = rd(start, 512)
    depth, root, leafrecs, first, last, nsz, maxk, totn, freen = struct.unpack('>HIIIIHHII', hdr[14:44])
    clump, bttype, kct, battr = struct.unpack('>IBBI', hdr[46:56])
    print(f'{name}: depth={depth} root={root} leafrecs={leafrecs} first={first} last={last} nodesize={nsz} maxkey={maxk} total={totn} free={freen} type={bttype} cmp={kct:#x} attr={battr:#x}')
    n = first
    while n and dumprec:
        node = rd(start + n*nsz, nsz)
        fl, bl, kind, height, nrec = struct.unpack('>IIbBH', node[:12])
        offs = [struct.unpack('>H', node[nsz-2*(i+1):nsz-2*i])[0] for i in range(nrec+1)]
        for i in range(nrec):
            rec = node[offs[i]:offs[i+1]]
            dumprec(rec)
        n = fl
def cat_rec(rec):
    klen, parent, nlen = struct.unpack('>HIH', rec[:8])
    name = rec[8:8+2*nlen].decode('utf-16-be')
    d = rec[2+klen:]
    if len(d) % 2: pass
    rt, = struct.unpack('>h', d[:2])
    if rt in (1, 2):
        flags = struct.unpack('>H', d[2:4])[0]
        cnid = struct.unpack('>I', d[8:12])[0]
        own, grp, aflags, oflags, mode, special = struct.unpack('>IIBBHI', d[32:48])
        extra = ''
        if rt == 2:
            ftype, creator = d[48:52], d[52:56]
            r1 = struct.unpack('>I', d[4:8])[0]
            ls, clump, tb = struct.unpack('>QII', d[88:104])
            e0 = struct.unpack('>II', d[104:112])
            extra = f' type={ftype} creator={creator} r1={r1} size={ls} blocks={tb} ext0={e0}'
        else:
            val = struct.unpack('>I', d[4:8])[0]
            extra = f' valence={val} folderCount={struct.unpack(">I", d[84:88])[0]}'
        print(f'  {"DIR " if rt==1 else "FILE"} ({parent},{name!r}) cnid={cnid} flags={flags:#x} own={own} grp={grp} mode={mode:o} special={special}{extra}')
    else:
        p, = struct.unpack('>I', d[4:8]); nl, = struct.unpack('>H', d[8:10])
        print(f'  THRD ({parent},{name!r}) -> ({p},{d[10:10+2*nl].decode("utf-16-be")!r}) type={rt}')
btree(cat, 'catalog', cat_rec)
btree(exts, 'extents', None)
if attrf[0][1]: btree(attrf, 'attributes', lambda r: print('  ATTR', r[:64].hex()))
