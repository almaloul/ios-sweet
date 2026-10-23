#!/usr/bin/env python3
"""Run the SWEET iOS 16 kernel + userland on QEMU's virt board, with a window.

  sweet-virt.py PAYLOAD DISK [options] [-- extra QEMU arguments]

PAYLOAD  SWEETXNU payload from tools/mkpayload.py, built with an ADT from
         `tools/mkadt.py OUT.adt --virt` and the virt boot-args (docs/VIRT.md)
DISK     the raw HFS+ iOS root image from tools/mkiosroot.sh (ios16-root.img);
         use --snapshot to keep it unchanged

Works on Windows, Linux and macOS with only Python 3 and QEMU (8.2 or newer)
installed. The QEMU window shows the 1080x2400 panel through ramfb; the mouse
is the touchscreen (virtio-tablet), and the keyboard gives the buttons:

  Esc / Home   Home button          PgUp / PgDn   volume up / down
  End          side (power) button

The kernel console goes to the "serial0" tab of the window and to
OUTDIR/serial.txt.

For scripted runs: --headless --secs N --screenshot shot.png
"""
import argparse, importlib.util, json, os, shutil, socket, struct, subprocess, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
STUB_BIN = os.path.join(HERE, 'virt', 'stub', 'ramfb_stub.bin')
STUB_PA = 0x40200000            # above the dtb QEMU places at RAM base
STUB_PARAMS_OFF = 0xf00         # tools/virt/stub/link.ld
VIRT_RAM_BASE = 0x40000000


def load_qemu_sweet():
    spec = importlib.util.spec_from_file_location('qemu_sweet', os.path.join(HERE, 'qemu-sweet.py'))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def find_qemu(explicit):
    if explicit:
        return explicit
    exe = shutil.which('qemu-system-aarch64')
    if exe:
        return exe
    if os.name == 'nt':
        for base in (os.environ.get('ProgramFiles', r'C:\Program Files'), r'C:\Program Files\qemu'):
            cand = os.path.join(base, 'qemu', 'qemu-system-aarch64.exe')
            if os.path.isfile(cand):
                return cand
            cand = os.path.join(base, 'qemu-system-aarch64.exe')
            if os.path.isfile(cand):
                return cand
    sys.exit('qemu-system-aarch64 not found: install QEMU (Windows: https://qemu.weilnetz.de/w64/) '
             'or pass --qemu PATH')


def qarg(path):
    """Escape a path for a QEMU option value (commas are doubled)."""
    return path.replace(',', ',,')


def stub_image(fb, boot_args_pa, entry_pa, ramfb=True):
    fb_base, fb_w, fb_h, fb_stride = fb
    stub = bytearray(open(STUB_BIN, 'rb').read())
    params = struct.pack('<3Q4I', boot_args_pa, entry_pa, fb_base, fb_w, fb_h, fb_stride, 1 if ramfb else 0)
    stub[STUB_PARAMS_OFF:STUB_PARAMS_OFF + len(params)] = params
    return bytes(stub)


class QMP:
    def __init__(self, port):
        self.s = None
        for _ in range(200):
            try:
                self.s = socket.create_connection(('127.0.0.1', port), timeout=30)
                break
            except OSError:
                time.sleep(0.05)
        if self.s is None:
            raise RuntimeError('cannot reach QEMU monitor')
        self.f = self.s.makefile('rw')
        self.f.readline()
        self.cmd('qmp_capabilities')

    def cmd(self, execute, **args):
        self.f.write(json.dumps({'execute': execute, 'arguments': args}) + '\n')
        self.f.flush()
        while True:
            r = json.loads(self.f.readline())
            if 'return' in r:
                return r['return']
            if 'error' in r:
                raise RuntimeError(f'{execute}: {r["error"]}')


def free_port():
    s = socket.socket()
    s.bind(('127.0.0.1', 0))
    port = s.getsockname()[1]
    s.close()
    return port


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n\n')[0],
                                 epilog=__doc__.split('\n\n', 1)[1],
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('payload', help='SWEETXNU payload (tools/mkpayload.py)')
    ap.add_argument('disk', nargs='?', help='raw iOS root image (virtio-blk); omit for kernel-only tests')
    ap.add_argument('--out', default='sweet-virt-run', help='directory for serial.txt and temporary files')
    ap.add_argument('--snapshot', action='store_true', help='discard all writes to DISK when QEMU exits')
    ap.add_argument('--readonly', action='store_true', help='attach DISK read-only (iOS will not boot fully)')
    ap.add_argument('--cpus', type=int, default=1, help='vCPUs (the kernel currently brings up 1 on virt)')
    ap.add_argument('--mem', help='guest RAM, e.g. 8G (default: what the payload needs)')
    ap.add_argument('--display', default='gtk', help='QEMU display: gtk (default), sdl, cocoa, none')
    ap.add_argument('--accel', default='tcg,tb-size=1024', help='QEMU -accel value (default: tcg,tb-size=1024)')
    ap.add_argument('--cpu', default='cortex-a76', help='QEMU CPU model (default: cortex-a76)')
    ap.add_argument('--multitouch', action='store_true', help='use virtio-multitouch instead of virtio-tablet (QEMU 8.1+)')
    ap.add_argument('--qemu', help='path to qemu-system-aarch64')
    ap.add_argument('--headless', action='store_true', help='no window (implies --display none)')
    ap.add_argument('--secs', type=float, help='stop after N seconds')
    ap.add_argument('--screenshot', help='with --secs: save the panel as PNG before stopping')
    ap.add_argument('--gdb', action='store_true', help='start halted with a gdb stub on :1234')
    ap.add_argument('--dry-run', action='store_true', help='print the QEMU command and exit')
    argv = sys.argv[1:]
    extra = argv[argv.index('--') + 1:] if '--' in argv else []   # after --: extra QEMU arguments
    a = ap.parse_args(argv[:argv.index('--')] if '--' in argv else argv)

    qs = load_qemu_sweet()
    payload = open(a.payload, 'rb').read()
    phys_base, img, ba_pa, entry_pa = qs.build_image(payload)
    mem_size = struct.unpack_from('<Q', payload, 0x40)[0]
    if phys_base < 0xa0000000:
        sys.exit('payload RAM overlaps the framebuffer at 0x9c000000: build the ADT with --virt (high memory)')

    os.makedirs(a.out, exist_ok=True)
    out = os.path.abspath(a.out)
    ram_bin, stub_bin = os.path.join(out, 'ram.bin'), os.path.join(out, 'stub.bin')
    open(ram_bin, 'wb').write(img)
    fb = (qs.FB_BASE, qs.FB_W, qs.FB_H, qs.FB_STRIDE)
    open(stub_bin, 'wb').write(stub_image(fb, ba_pa, entry_pa))
    print(f'phys_base={phys_base:#x} boot_args={ba_pa:#x} entry={entry_pa:#x}', file=sys.stderr)

    ram_gib = max(4, -(-(phys_base + mem_size - VIRT_RAM_BASE) // (1 << 30)))
    display = 'none' if a.headless else a.display
    if display == 'gtk':
        display = 'gtk,zoom-to-fit=on,show-tabs=on'
    qmp_port = free_port()
    serial = os.path.join(out, 'serial.txt')
    cmd = [find_qemu(a.qemu),
           '-M', 'virt,gic-version=3', '-cpu', a.cpu, '-smp', str(a.cpus), '-m', a.mem or f'{ram_gib}G',
           '-accel', a.accel, '-no-reboot',
           '-display', display, '-device', 'ramfb',
           '-chardev', f'vc,id=ser0,logfile={qarg(serial)}' if display != 'none'
           else f'file,id=ser0,path={qarg(serial)}',
           '-serial', 'chardev:ser0',
           '-qmp', f'tcp:127.0.0.1:{qmp_port},server=on,wait=off',
           '-global', 'virtio-mmio.force-legacy=false',
           '-device', 'virtio-multitouch-device' if a.multitouch else 'virtio-tablet-device',
           '-device', 'virtio-keyboard-device',
           '-device', f'loader,file={qarg(ram_bin)},addr={phys_base:#x},force-raw=on',
           '-device', f'loader,file={qarg(stub_bin)},addr={STUB_PA:#x},force-raw=on',
           '-device', f'loader,addr={STUB_PA:#x},cpu-num=0']
    if a.disk:
        opts = f'if=none,id=root,format=raw,cache=writeback,file={qarg(os.path.abspath(a.disk))}'
        if a.snapshot:
            opts += ',snapshot=on'
        if a.readonly:
            opts += ',readonly=on'
        cmd += ['-drive', opts, '-device', 'virtio-blk-device,drive=root']
    if a.gdb:
        cmd += ['-S', '-gdb', 'tcp::1234']
    cmd += extra

    if a.dry_run:
        print(subprocess.list2cmdline(cmd) if os.name == 'nt' else ' '.join(cmd))
        return 0

    p = subprocess.Popen(cmd, stdin=subprocess.DEVNULL)
    try:
        if a.secs is None:
            print(f'QEMU running (monitor: QMP tcp 127.0.0.1:{qmp_port}); close the window to stop.',
                  file=sys.stderr)
            return p.wait()
        t0 = time.time()
        while p.poll() is None and time.time() - t0 < a.secs:
            time.sleep(0.2)
        if p.poll() is not None:
            print(f'QEMU exited early: {p.returncode}', file=sys.stderr)
            return 1
        q = QMP(qmp_port)
        if a.screenshot:
            shot = os.path.abspath(a.screenshot)
            if shot.lower().endswith('.ppm'):
                q.cmd('screendump', filename=shot)
            else:
                try:
                    q.cmd('screendump', filename=shot, format='png')
                except RuntimeError:            # QEMU < 7.1: PPM only
                    q.cmd('screendump', filename=shot + '.ppm')
                    shot += '.ppm'
            print('screenshot:', shot, file=sys.stderr)
        try:
            q.cmd('quit')
        except (OSError, ValueError, RuntimeError):
            pass                            # QEMU may close the socket before replying
        p.wait(timeout=30)
        return 0
    except KeyboardInterrupt:
        return 130
    finally:
        if p.poll() is None:
            p.kill()
            p.wait()
        for f in (ram_bin, stub_bin):
            try:
                os.unlink(f)
            except OSError:
                pass


if __name__ == '__main__':
    sys.exit(main())
