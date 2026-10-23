# iOS 16 on QEMU `virt` (Windows, Linux, macOS)

The same SWEET kernel that boots the Redmi Note 10 Pro can boot on QEMU's
generic `virt` board, so the iOS 16.7 userland runs in a window on a PC. It is
an emulator in the QEMU sense (the whole ARM64 machine is emulated), built on
this port's approach: XNU ported to non-Apple hardware, with the unmodified
iPhone X userland on top.

> **Status (2026-10-23): milestone 1 code complete, not yet booted.**
> The kernel side (patch `0082`) has not been through a CI build or an XNU
> boot yet. The parts that could be tested without a kernel were, against
> real QEMU devices (see "Tests" below). Expect the first CI build or the
> first boot to need fixes.

## Easiest on Windows: the SweetEmu app

[`app/`](../app/README.md) is a standalone Windows app that does all of the
steps below natively (no WSL, Python or Linux tools): unzip the
`SweetEmu-win-x64` artifact from the `app` workflow, put the IPSW in its
`IPSW` folder, click **Prepare**, then **Start**. The rest of this page is the
manual route and how it works underneath.

## How it works

| Phone (`sweet`)                         | QEMU `virt`                                                                 |
|-----------------------------------------|-----------------------------------------------------------------------------|
| ABL + `shim` load the payload           | `tools/sweet-virt.py` loads the same payload into guest RAM                 |
| Panel framebuffer at `0x9c000000`       | Same address; a pre-kernel stub points QEMU's `ramfb` at it, so the window shows it |
| UFS root in `userdata` (`rd=ufsroot0`)  | virtio-blk disk = the raw `ios16-root.img` (`rd=vblk0`)                     |
| Goodix touch, PMIC buttons              | virtio-tablet (mouse = finger), virtio-keyboard (buttons)                   |
| Console in the framebuffer + log ring   | PL011 serial: the window's `serial0` tab and `OUT/serial.txt`              |
| 8 cores via PSCI `smc`                  | 1 vCPU for now (see Roadmap)                                                |

Nothing changes for the phone: the new code only activates when the device
tree has the nodes that `tools/mkadt.py --virt` adds (`/sweet-virtio`,
`/arm-io/uart0`) and the boot-args say `rd=vblk0`.

New pieces:

- `xnu/patches/0082-sweet-virt-qemu-virtio.patch`
  - `bsd/dev/sweet_virtio_core.h`: polled virtio-mmio v2 core, no OS
    dependencies (blk requests, input events, touch state machine)
  - `bsd/dev/sweet_virtio.c`: `vblk0`/`rvblk0` BSD disk and the input
    thread feeding `sweet_hid_touch()` / `sweet_hid_key()`
  - `IOKitBSDInit.cpp`: `rd=vblk0`
  - `SWEET.h`: `VMAPPLE_UART` (xnu's PL011 console; inert without `uart0`)
- `tools/mkadt.py --virt`: `--qemu` + `--highmem` + the two nodes above
- `tools/sweet-virt.py`: launcher (stdlib Python, any OS)
- `tools/virt/stub/`: the ramfb pre-kernel stub (`ramfb_stub.bin` is
  committed, so no compiler is needed to run)
- `tools/virt/selftest/`: tests that run without a kernel

## What you need

- **Windows 10/11 x64** (or Linux/macOS), 16 GB RAM recommended (the guest
  gets about 8 GB of address space; QEMU only commits what is touched)
- **QEMU 8.2 or newer** for Windows: <https://qemu.weilnetz.de/w64/>
- **Python 3.9+** (python.org or `winget install Python.Python.3.12`)
- **WSL2 with Ubuntu**, only to build the iOS root image once
  (`apfs-fuse`, `hfsprogs`)
- A **GitHub fork** of this repo: the kernel builds on GitHub's macOS runners
- The iPhone X (`iPhone10,3`) **iOS 16.7.16 (20H392) IPSW** from Apple.
  iOS is Apple's software and is not distributed here; Apple's license does
  not allow running it on non-Apple hardware. Personal research only, never
  share images.

## 1. Build the kernel (GitHub Actions)

Push this branch to your fork's `main` (or run the workflow by hand):

```powershell
gh workflow run xnu.yml -R YOU/ios-sweet
gh run watch -R YOU/ios-sweet
gh run download RUN_ID -R YOU/ios-sweet -n xnu-kernel -D xnu/out
```

The result is `xnu/out/kernel.development.sweet`, the same kernel the phone
uses.

## 2. Build the iOS root image (WSL2, once)

Follow README step 2 inside WSL2 (`tools/mkiosroot.sh` +
`tools/sweet-root-tweaks.py`). Keep the raw `ios16-root.img`; the sparse
`.simg` is only for fastboot. Also extract `research/ipsw-x/d22ap.adt`
(README step 2, last block): without its `/product` node MobileGestalt does
not know the device. The `ipsw` tool also has native Windows builds.

Copy the image somewhere on the Windows side (e.g. `C:\ios-sweet\`); QEMU
reads it much faster from NTFS than from `\\wsl$`.

## 3. Build the virt payload

```powershell
py tools\mkadt.py virt.adt --virt
py tools\mkpayload.py xnu\out\kernel.development.sweet virt.adt payload-virt.bin --cmdline "debug=0xc serial=3 cpus=1 keepsyms=1 -enable_kprintf_spam -noprogress rd=vblk0 rootrw=1 unentitled_ios_sim_launch=1 ignition_level=1 wdt_test=no_user_checkin launchd_unsecure_cache=1 wdt=-1 sweetfb_noconsole=1 sweetfb_hz=30"
```

Differences from the phone boot-args: `rd=vblk0` instead of
`rd=ufsroot0 ufsrw=1`. Drop `sweetfb_noconsole=1` to also see the kernel
console drawn in the window.

## 4. Run

```powershell
py tools\sweet-virt.py payload-virt.bin C:\ios-sweet\ios16-root.img
```

Add `--snapshot` to throw away all writes when QEMU exits (handy while
experimenting; first boots write a lot). Close the window to stop.

| Input          | Does                     |
|----------------|--------------------------|
| Mouse button   | finger on the screen     |
| Esc or Home    | Home button              |
| PgUp / PgDn    | volume up / down         |
| End            | side (power) button: lock / wake |

The kernel log is in the `serial0` tab (View menu) and in
`sweet-virt-run\serial.txt`. The window zooms to fit; the panel is
1080×2400.

Useful options: `--display sdl`, `--multitouch` (virtio-multitouch, for real
touchscreens), `--headless --secs 600 --screenshot shot.png` for unattended
runs, `--gdb`, `--dry-run` (print the QEMU command), and anything after
`--` is passed to QEMU.

### Speed

On an x86 PC, QEMU translates every ARM instruction (TCG), and iOS draws
the whole UI on the CPU here, so expect a long boot (the phone already takes
about 4 minutes on real ARM cores) and a sluggish UI. Measurements are
welcome in NOTES.md. A Windows-on-ARM PC could avoid the translation if
QEMU's hardware acceleration works there for arm64 guests; untested.

## Tests (no kernel needed)

```sh
python3 tools/virt/selftest/run.py           # virtio core vs. QEMU's devices
python3 tools/virt/selftest/launcher_test.py # payload -> stub -> ramfb -> screenshot
xvfb-run -a python3 tools/virt/selftest/launcher_test.py --display gtk
python3 tools/virt/stub/build.py             # rebuild ramfb_stub.bin
```

`run.py` runs `sweet_virtio_core.h` (taken from patch 0082) bare-metal: it
reads and writes a patterned virtio-blk disk, wraps the ring, and checks
that taps injected over QMP arrive scaled to panel pixels and that keys
arrive. `launcher_test.py` boots a stand-in "kernel" through
`sweet-virt.py` and checks the QEMU display pixel by pixel. They need
clang, ld.lld and llvm-objcopy (Linux/macOS/WSL).

## Roadmap

1. **Boot to SpringBoard on virt** (this milestone): CI build of 0082,
   first boot, fix what breaks. Watch the serial log for `SWEET VBLK`,
   `SWEET VINPUT` and `SWEET HID: touch`.
2. **SMP**: QEMU's PSCI conduit is `hvc` (no EL2/EL3) and virt's MPIDRs
   number cores in Aff0, while `SweetCPU.cpp` calls `smc` and expects the
   core in Aff1. Make both come from the ADT, then `--cpus 4` with MTTCG.
3. **Hardware keyboard**: a HID keyboard service so typing reaches text
   fields (today only the four buttons are mapped).
4. **Network**: virtio-net, then the same configd questions as the phone's
   USB Ethernet (NOTES.md).
5. **Faster display**: dirty-rectangle copies, or rendering straight into
   the ramfb buffer at the iPhone X size.
