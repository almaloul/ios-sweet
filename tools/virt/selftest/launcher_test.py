#!/usr/bin/env python3
"""End-to-end test of tools/sweet-virt.py's boot path without an XNU kernel.

Builds fbtest (a stand-in kernel linked at the --virt high-memory base),
wraps it in a SWEETXNU v1 payload exactly as tools/mkpayload.py lays one out,
boots it headless through sweet-virt.py (ram image + ramfb stub + boot_args),
takes a screenshot of the QEMU display and checks that it shows what fbtest
drew into the phone framebuffer address. That proves the stub found ramfb,
the window shows 0x9c000000 at 1080x2400, and boot_args reached the kernel.

Needs clang, ld.lld, llvm-objcopy and qemu-system-aarch64.
"""
import os, struct, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.abspath(os.path.join(HERE, '../..'))
PHYS_BASE = 0x100000000         # mkadt.py --virt dram-base
MEM_SIZE = 0xfcb00000
ENTRY = PHYS_BASE + 0x4000
BOOT_LINE_LENGTH = 608


def build(work):
    cflags = ['--target=aarch64-none-elf', '-ffreestanding', '-nostdlib', '-O2', '-mgeneral-regs-only',
              '-mstrict-align', '-fno-stack-protector', '-Wall', '-Werror']
    objs = []
    for src in ('fbtest.S', 'fbtest.c'):
        o = os.path.join(work, src + '.o')
        subprocess.check_call(['clang', *cflags, '-c', os.path.join(HERE, src), '-o', o])
        objs.append(o)
    elf = os.path.join(work, 'fbtest.elf')
    subprocess.check_call(['ld.lld', f'--Ttext={ENTRY:#x}', '-e', '_start', *objs, '-o', elf])
    raw = os.path.join(work, 'fbtest.bin')
    subprocess.check_call(['llvm-objcopy', '-O', 'binary', elf, raw])
    return open(raw, 'rb').read()


def payload(code):
    """SWEETXNU v1: header, cmdline, one segment, ADT (tools/mkpayload.py layout)."""
    nsegs = 1
    seg_off = 0x4000
    adt = b'\0' * 64
    adt_off = seg_off + ((len(code) + 0x3fff) & ~0x3fff)
    kernel_end = ENTRY + ((len(code) + 0x3fff) & ~0x3fff)
    hdr = struct.pack('<8sII', b'SWEETXNU', 1, nsegs)
    hdr += struct.pack('<7Q', PHYS_BASE, ENTRY, adt_off, len(adt), kernel_end, PHYS_BASE, MEM_SIZE)
    hdr += b'fbtest'.ljust(BOOT_LINE_LENGTH, b'\0')
    assert len(hdr) == 0x2a8
    hdr += struct.pack('<4Q', ENTRY, len(code), seg_off, len(code))
    blob = bytearray(hdr.ljust(seg_off, b'\0'))
    blob += code
    blob = blob.ljust(adt_off, b'\0') + adt
    return bytes(blob)


def read_ppm(path):
    data = open(path, 'rb').read()
    parts = data.split(maxsplit=4)
    assert parts[0] == b'P6', parts[0]
    w, h, maxv = int(parts[1]), int(parts[2]), int(parts[3])
    pix = parts[4]
    return w, h, pix


def main():
    # --display gtk: test the windowed path (needs an X display, e.g. xvfb-run)
    display = sys.argv[sys.argv.index('--display') + 1] if '--display' in sys.argv else None
    work = tempfile.mkdtemp(prefix='sweet-virt-test-')
    pl = os.path.join(work, 'payload.bin')
    open(pl, 'wb').write(payload(build(work)))
    shot = os.path.join(work, 'shot.ppm')
    r = subprocess.call([sys.executable, os.path.join(TOOLS, 'sweet-virt.py'), pl,
                         '--out', os.path.join(work, 'run'), '--secs', '6',
                         *(['--display', display] if display else ['--headless']),
                         '--screenshot', shot])
    serial = open(os.path.join(work, 'run', 'serial.txt'), errors='replace').read()
    print(serial.strip())
    ok = r == 0 and os.path.exists(shot)
    if ok:
        w, h, pix = read_ppm(shot)
        px = lambda x, y: tuple(pix[(y * w + x) * 3:(y * w + x) * 3 + 3])
        checks = [
            ((w, h) == (1080, 2400), f'display is {w}x{h}'),
            (px(10, 10) == (255, 0, 0), f'top band red: {px(10, 10)}'),
            (px(10, 1300) == (0, 255, 0), f'middle band green: {px(10, 1300)}'),
            (px(10, 2390) == (0, 0, 255), f'bottom band blue: {px(10, 2390)}'),
            (px(540, 1200) == (255, 255, 255), f'center square white: {px(540, 1200)}'),
            ('ramfb on' in serial, 'stub reported ramfb'),
        ]
        for good, what in checks:
            print(('  ok   ' if good else '  FAIL ') + what)
            ok = ok and good
    print('RESULT:', 'PASS' if ok else 'FAIL', '(work dir', work + ')')
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
