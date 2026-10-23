#!/usr/bin/env python3
"""Bare-metal test of the SWEET virtio core (bsd/dev/sweet_virtio_core.h) in QEMU.

  run.py [--core PATH/sweet_virtio_core.h] [--keep]

Builds selftest.c with clang + ld.lld for aarch64, boots it on QEMU virt with
a patterned virtio-blk disk, a virtio-tablet and a virtio-keyboard, injects
touches and keys over QMP once the guest prints READY FOR INPUT, and exits 0
when the guest prints SELFTEST PASS. Without --core, the header is taken
from the new-file hunk in xnu/patches/*sweet-virt*.patch.

Needs: qemu-system-aarch64, clang, ld.lld (Linux or macOS; QMP uses a
unix socket).
"""
import argparse, glob, json, os, socket, subprocess, sys, tempfile, time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '../../..'))
CORE = 'bsd/dev/sweet_virtio_core.h'


def core_from_patch(dst):
    patches = sorted(glob.glob(os.path.join(REPO, 'xnu/patches/*sweet-virt*.patch')))
    for p in patches:
        lines = open(p).read().split('\n')
        for i, l in enumerate(lines):
            if l == f'+++ b/{CORE}':
                out = []
                for l2 in lines[i + 2:]:
                    if l2.startswith('diff --git') or not (l2.startswith('+') or l2 == ''):
                        break
                    out.append(l2[1:])
                while out and out[-1] == '':
                    out.pop()
                open(dst, 'w').write('\n'.join(out) + '\n')
                return
    sys.exit(f'no {CORE} hunk in xnu/patches/*sweet-virt*.patch')


def build(work, core):
    inc = os.path.join(work, 'inc')
    os.makedirs(inc, exist_ok=True)
    hdr = os.path.join(inc, 'sweet_virtio_core.h')
    if core:
        open(hdr, 'w').write(open(core).read())
    else:
        core_from_patch(hdr)
    cflags = ['--target=aarch64-none-elf', '-ffreestanding', '-nostdlib', '-O2',
              '-mgeneral-regs-only', '-mstrict-align', '-fno-stack-protector',
              '-Wall', '-Wextra', '-Werror', '-Wno-unused-parameter', '-I', inc]
    objs = []
    for src in ('start.S', 'selftest.c'):
        obj = os.path.join(work, src + '.o')
        subprocess.check_call(['clang', *cflags, '-c', os.path.join(HERE, src), '-o', obj])
        objs.append(obj)
    elf = os.path.join(work, 'selftest.elf')
    subprocess.check_call(['ld.lld', '-T', os.path.join(HERE, 'link.ld'), *objs, '-o', elf])
    return elf


def make_disk(path, sectors=2048):
    with open(path, 'wb') as f:
        for s in range(sectors):
            f.write(bytes(((s * 7 + i) & 0xff) for i in range(512)))


class QMP:
    def __init__(self, path):
        self.s = socket.socket(socket.AF_UNIX)
        for _ in range(100):
            try:
                self.s.connect(path)
                break
            except OSError:
                time.sleep(0.05)
        self.f = self.s.makefile('rw')
        self.f.readline()
        self.cmd('qmp_capabilities')

    def cmd(self, execute, **args):
        self.f.write(json.dumps({'execute': execute, 'arguments': args}) + '\n')
        self.f.flush()
        while True:
            r = json.loads(self.f.readline())
            if 'return' in r or 'error' in r:
                if 'error' in r:
                    raise RuntimeError(f'{execute}: {r["error"]}')
                return r['return']


def abs_event(axis, value):
    return {'type': 'abs', 'data': {'axis': axis, 'value': value}}


def btn_event(down):
    return {'type': 'btn', 'data': {'down': down, 'button': 'left'}}


def key_event(qcode, down):
    return {'type': 'key', 'data': {'down': down, 'key': {'type': 'qcode', 'data': qcode}}}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--core', help='path to sweet_virtio_core.h (default: from the patch)')
    ap.add_argument('--keep', action='store_true', help='keep the work directory')
    a = ap.parse_args()

    work = tempfile.mkdtemp(prefix='svio-selftest-')
    elf = build(work, a.core)
    disk = os.path.join(work, 'disk.img')
    make_disk(disk)
    serial = os.path.join(work, 'serial.txt')
    mon = os.path.join(work, 'qmp.sock')
    cmd = ['qemu-system-aarch64', '-M', 'virt,gic-version=3', '-cpu', 'cortex-a76', '-m', '512M',
           '-display', 'none', '-serial', f'file:{serial}', '-qmp', f'unix:{mon},server,nowait',
           '-global', 'virtio-mmio.force-legacy=false',
           '-drive', f'if=none,id=d0,format=raw,file={disk}', '-device', 'virtio-blk-device,drive=d0',
           '-device', 'virtio-tablet-device', '-device', 'virtio-keyboard-device',
           '-device', f'loader,file={elf},cpu-num=0']
    p = subprocess.Popen(cmd, stdin=subprocess.DEVNULL)
    ok = False
    try:
        q = QMP(mon)
        t0 = time.time()
        injected = False
        while p.poll() is None and time.time() - t0 < 60:
            text = open(serial, errors='replace').read() if os.path.exists(serial) else ''
            if 'READY FOR INPUT' in text and not injected:
                injected = True
                time.sleep(0.2)
                # tap at the panel center, drag, release, tap again in a corner
                q.cmd('input-send-event', events=[abs_event('x', 16384), abs_event('y', 16384)])
                q.cmd('input-send-event', events=[btn_event(True)])
                q.cmd('input-send-event', events=[abs_event('x', 17000), abs_event('y', 16000)])
                q.cmd('input-send-event', events=[btn_event(False)])
                q.cmd('input-send-event', events=[abs_event('x', 100), abs_event('y', 32000)])
                q.cmd('input-send-event', events=[btn_event(True)])
                q.cmd('input-send-event', events=[btn_event(False)])
                for k in ('esc', 'pgup', 'q'):
                    q.cmd('input-send-event', events=[key_event(k, True)])
                    q.cmd('input-send-event', events=[key_event(k, False)])
            if 'SELFTEST PASS' in text or 'SELFTEST FAIL' in text:
                ok = 'SELFTEST PASS' in text
                break
            time.sleep(0.1)
    finally:
        time.sleep(0.2)
        if p.poll() is None:
            p.kill()
        p.wait()
        text = open(serial, errors='replace').read() if os.path.exists(serial) else ''
        ok = 'SELFTEST PASS' in text
        sys.stdout.write(text.replace('\r', ''))
        if not a.keep:
            subprocess.call(['rm', '-rf', work])
        else:
            print('work dir:', work)
    print('RESULT:', 'PASS' if ok else 'FAIL')
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
