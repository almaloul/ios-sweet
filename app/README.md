# SweetEmu: the standalone Windows app

SweetEmu runs the ios-sweet iOS 16 port on QEMU from one folder, with no
Linux, WSL or Python needed: drop the IPSW in, click **Prepare** once, then **Start**.

```
SweetEmu\
  SweetEmu.exe
  IPSW\      <- put iPhone10,3,iPhone10,6_16.7.16_20H392_Restore.ipsw here
  Kernel\    kernel.development.sweet (bundled by the app workflow)
  qemu\      QEMU (bundled by the app workflow)
  Data\      created by Prepare: ios16-root.img, d22ap.adt, logs
```

> **Status (2026-10-23):** everything up to booting QEMU is built and tested
> here (see Tests), but no real IPSW has gone through Prepare yet (my build
> machine cannot download one), and the virt kernel (patch 0082) has not been
> booted yet. Expect the first real run to surface problems; the log pane
> shows what happened.

## Getting the app

**From GitHub Actions (recommended).** In your fork, run the `xnu` workflow
(builds the kernel), then the `app` workflow. Download its
`SweetEmu-win-x64` artifact: SweetEmu.exe, the kernel and a trimmed QEMU,
ready to unzip.

**Building yourself** (Windows, .NET 8 SDK or Visual Studio 2022):

```powershell
dotnet publish app/SweetEmu -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o SweetEmu
```

Then add `Kernel\kernel.development.sweet` and install QEMU 8.2+
(<https://qemu.weilnetz.de/w64/>) or copy it into `qemu\`.

## What Prepare does

It replaces `tools/mkiosroot.sh` + `tools/sweet-root-tweaks.py` (which needed
Linux, apfs-fuse and hfsprogs) with native code:

1. Opens the IPSW (zip, Zip64) and picks the `d22ap` erase identity from
   BuildManifest.plist.
2. Reads the OS image (UDIF .dmg, LZFSE/zlib chunks) and the SystemOS and AppOS
   cryptexes (raw APFS, sealed volumes), decompressing APFS-compressed files
   (zlib, LZVN, LZFSE, LZBITMAP).
3. Assembles the root exactly like mkiosroot.sh: cryptexes under
   `/private/preboot/Cryptexes/{OS,App}`, the shared cache and every cryptex-only
   file hard-linked into the root, logd stores, the plain system keybag, then the
   bring-up tweaks (launchd.plist, containers, Setup skip, audio, prefs).
4. Writes a journaled, case-sensitive HFS+ image (Apple's fsck_hfs layout rules,
   hard links included) in one pass, reading the source in on-disk order.
5. Extracts the D22AP device tree (IMG4/IM4P, LZFSE) for the `/product` node.

Start then builds the QEMU device tree and boot payload (the C# versions of
`mkadt.py --virt` and `mkpayload.py`) and launches QEMU the way
`tools/sweet-virt.py` does.

## Command line

`SweetEmu.Cli` exposes the same engine (any OS with .NET 8):

```
dotnet run --project app/SweetEmu.Cli -- prepare HOME
dotnet run --project app/SweetEmu.Cli -- run HOME [--discard] [--headless --secs N --screenshot shot.png]
dotnet run --project app/SweetEmu.Cli -- ipsw-info FILE.ipsw
dotnet run --project app/SweetEmu.Cli -- apfs-dump IMAGE           # list an APFS image/.dmg with hashes
dotnet run --project app/SweetEmu.Cli -- apfs2hfs IMAGE OUT.img     # APFS volume -> HFS+ image
dotnet run --project app/SweetEmu.Cli -- adt OUT [D22AP.adt] [SEED] # same bytes as mkadt.py --virt
dotnet run --project app/SweetEmu.Cli -- payload KERNEL ADT OUT     # same bytes as mkpayload.py
```

## Tests

`dotnet run --project app/SweetEmu.Tests` runs the suite; tests that need
outside data skip when it is missing. What was verified on Linux:

| Piece | Check |
|---|---|
| LZFSE / LZVN | round trip against Apple's reference encoder (1 B to 4 MB, raw/LZVN/bvx2 blocks) |
| LZBITMAP | dissect.util's test vectors (SHA-256 of the output) |
| decmpfs | zlib (inline + resource fork), LZFSE block table, stored variants |
| APFS reader | listings + SHA-256 of every file identical to dissect.apfs on its 5 sample images (compressed files, hard links, symlinks, devices, Unicode names, snapshots) |
| UDIF | a synthetic .dmg decoded identically by `dmg2img` and SweetEmu |
| HFS+ writer | Apple `fsck_hfs` (hfsprogs 540.1) clean on all samples and on a 250,000-entry tree (4-level catalog, hard links, Unicode, `:` names); contents verified via 7-Zip |
| Prepare | synthetic IPSW (BuildManifest, UDIF OS image, raw + deflated cryptexes, LZFSE IM4P device tree) -> fsck-clean image with the cryptex links, keybag, logd stores and prefs |
| Tweaks | inputs and outputs checked with Python's `plistlib` against what sweet-root-tweaks.py produces |
| ADT, payload, guest RAM layout, ramfb stub | byte-identical to mkadt.py / mkpayload.py / sweet-virt.py; a stand-in kernel boots through the C# launcher with the correct screen |
| Window | compiled with Mono and exercised under Xvfb with a fake engine (`SweetEmu/Preview`) |

Not verifiable here: a real Apple IPSW (sealed system volume, LZBITMAP files
at scale), the WinForms build itself (the Windows Desktop SDK is not available on
Linux; the app workflow builds it), and booting iOS.

Test data: set `SWEETEMU_APFS_DIR` to a folder of APFS images with `.ref.txt`
listings made by `app/tools/apfs_ref_dump.py`. `app/tools/make_test_ipsw.py`
builds the synthetic IPSW.

## Credits

See [THIRD-PARTY.md](THIRD-PARTY.md).
