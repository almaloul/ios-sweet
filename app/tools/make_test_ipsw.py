#!/usr/bin/env python3
"""Build a synthetic IPSW for end-to-end tests of SweetEmu's Prepare step.

  make_test_ipsw.py OS.apfs SYSTEMOS.apfs APPOS.apfs OUT.ipsw [--lzfse-tool PATH]

OS.apfs is wrapped as a UDIF .dmg (zlib chunks, like Apple's tools make), the
cryptexes stay raw APFS (as in real iOS 16 IPSWs), the device tree is an IM4P
with an LZFSE payload holding a small D22AP-like tree with a /product node.
Large entries are stored (not deflated), as in real IPSWs.
"""
import argparse, base64, os, plistlib, struct, subprocess, sys, tempfile, zipfile, zlib

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '../../tools'))
import adt  # noqa: E402

SECTOR = 512
CHUNK_SECTORS = 2048  # 1 MiB chunks


def udif(raw: bytes) -> bytes:
    """Minimal UDIF: one blkx partition, zlib chunks (zero runs as 'ignore')."""
    nsect = (len(raw) + SECTOR - 1) // SECTOR
    raw = raw.ljust(nsect * SECTOR, b'\0')
    data = bytearray()
    chunks = []
    for s in range(0, nsect, CHUNK_SECTORS):
        n = min(CHUNK_SECTORS, nsect - s)
        piece = raw[s * SECTOR:(s + n) * SECTOR]
        if piece.count(0) == len(piece):
            chunks.append((0x00000002, s, n, len(data), 0))
            continue
        comp = zlib.compress(piece, 6)
        chunks.append((0x80000005, s, n, len(data), len(comp)))
        data += comp
    chunks.append((0xFFFFFFFF, nsect, 0, len(data), 0))
    mish = bytearray(b'mish' + struct.pack('>I', 1) + struct.pack('>QQQ', 0, nsect, 0) +
                     struct.pack('>II', 0, 0) + b'\0' * 24 + b'\0' * 136 + struct.pack('>I', len(chunks)))
    assert len(mish) == 0xCC
    for t, s, n, off, ln in chunks:
        mish += struct.pack('>IIQQQQ', t, 0, s, n, off, ln)
    plist = plistlib.dumps({'resource-fork': {'blkx': [
        {'Attributes': '0x0050', 'CFName': 'Apple_APFS (Apple_APFS : 1)', 'Data': bytes(mish), 'ID': '0',
         'Name': 'Apple_APFS (Apple_APFS : 1)'}]}})
    xml_off = len(data)
    koly = bytearray(512)
    koly[0:4] = b'koly'
    struct.pack_into('>III', koly, 4, 4, 512, 1)
    struct.pack_into('>QQQ', koly, 16, 0, 0, len(data))      # running data fork, data fork offset, length
    struct.pack_into('>II', koly, 56, 1, 1)                  # segment number / count
    struct.pack_into('>QQ', koly, 0xD8, xml_off, len(plist))
    struct.pack_into('>I', koly, 488, 1)                     # image variant
    struct.pack_into('>Q', koly, 492, nsect)
    return bytes(data) + plist + bytes(koly)


def der(tag, payload):
    n = len(payload)
    if n < 0x80:
        return bytes([tag, n]) + payload
    ln = n.to_bytes((n.bit_length() + 7) // 8, 'big')
    return bytes([tag, 0x80 | len(ln)]) + ln + payload


def device_tree(lzfse_tool):
    root = adt.Node()
    root.props = [('name', 0, b'device-tree\0'), ('compatible', 0, b'D22AP\0iPhone10,3\0AppleARM\0')]
    product = adt.Node()
    product.props = [('name', 0, b'product\0'), ('AAPL,phandle', 0, b'\x09\0\0\0'),
                     ('product-name', 0, b'iPhone X\0'), ('artwork-device-subtype', 0, struct.pack('<I', 2436)),
                     ('mobile-device-min-ver', 1, b'\0' * 4)]
    chosen = adt.Node()
    chosen.props = [('name', 0, b'chosen\0')]
    root.children = [chosen, product]
    raw = adt.serialize(root)
    with tempfile.TemporaryDirectory() as t:
        open(os.path.join(t, 'in'), 'wb').write(raw)
        subprocess.check_call([lzfse_tool, '-encode', '-i', os.path.join(t, 'in'), '-o', os.path.join(t, 'out')])
        comp = open(os.path.join(t, 'out'), 'rb').read()
    body = der(0x16, b'IM4P') + der(0x16, b'dtre') + der(0x16, b'EmbeddedDeviceTrees-test') + der(0x04, comp) + \
        der(0x30, der(0x02, b'\x01') + der(0x02, len(raw).to_bytes(4, 'big')))
    return der(0x30, body), raw


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('os'); ap.add_argument('sysos'); ap.add_argument('appos'); ap.add_argument('out')
    ap.add_argument('--lzfse-tool', default=os.environ.get('LZFSE_TOOL', 'lzfse'), help='Apple lzfse CLI (github.com/lzfse/lzfse)')
    ap.add_argument('--dmg-out', help='also write the OS .dmg here (to check it with dmg2img)')
    a = ap.parse_args()
    os_dmg = udif(open(a.os, 'rb').read())
    if a.dmg_out:
        open(a.dmg_out, 'wb').write(os_dmg)
    im4p, raw_dt = device_tree(a.lzfse_tool)
    manifest = {
        'ProductVersion': '16.7.16', 'ProductBuildVersion': '20H392',
        'SupportedProductTypes': ['iPhone10,3', 'iPhone10,6'],
        'BuildIdentities': [
            {'Info': {'DeviceClass': 'd221ap', 'RestoreBehavior': 'Erase'}, 'Manifest': {}},
            {'Info': {'DeviceClass': 'd22ap', 'RestoreBehavior': 'Update'}, 'Manifest': {}},
            {'Info': {'DeviceClass': 'd22ap', 'RestoreBehavior': 'Erase'}, 'Manifest': {
                'OS': {'Info': {'Path': '098-12345-678.dmg'}},
                'Cryptex1,SystemOS': {'Info': {'Path': '098-23456-789.dmg'}},
                'Cryptex1,AppOS': {'Info': {'Path': '098-34567-890.dmg'}},
                'DeviceTree': {'Info': {'Path': 'Firmware/all_flash/DeviceTree.d22ap.im4p'}},
            }},
        ],
    }
    with zipfile.ZipFile(a.out, 'w', allowZip64=True) as z:
        z.writestr('BuildManifest.plist', plistlib.dumps(manifest), zipfile.ZIP_DEFLATED)
        z.writestr('098-12345-678.dmg', os_dmg, zipfile.ZIP_STORED)
        z.write(a.sysos, '098-23456-789.dmg', zipfile.ZIP_STORED)
        # the AppOS cryptex deflated, to exercise the extract-to-temp path
        z.write(a.appos, '098-34567-890.dmg', zipfile.ZIP_DEFLATED)
        z.writestr('Firmware/all_flash/DeviceTree.d22ap.im4p', im4p, zipfile.ZIP_DEFLATED)
    print(f'{a.out}: OS dmg {len(os_dmg)} bytes, device tree {len(raw_dt)} bytes')


if __name__ == '__main__':
    main()
