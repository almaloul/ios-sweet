SweetEmu - iOS 16 on QEMU (ios-sweet)
=====================================

1. Put the iPhone X iOS 16.7.16 IPSW in the IPSW folder
   (file name like iPhone10,3,iPhone10,6_16.7.16_20H392_Restore.ipsw, from Apple).
2. Run SweetEmu.exe and click Prepare. It reads the IPSW and builds the iOS
   disk image in the Data folder (about 16 GB; this takes a while, once).
3. Click Start. A QEMU window opens with the phone screen.
   Mouse = finger. Esc = Home, PgUp/PgDn = volume, End = lock (or use the buttons).

The first boot sits on the Apple logo for a long time: the whole ARM64 phone is
emulated on your PC's CPU, and iOS draws its UI without a GPU here.

Folders next to SweetEmu.exe:
  IPSW\    the IPSW you provide (never shared, never uploaded)
  Kernel\  kernel.development.sweet, the ios-sweet XNU kernel
  qemu\    QEMU (GPL), used to run the virtual machine
  Data\    created by Prepare: the iOS disk image, logs
  Screenshots\  from the Screenshot button

"Discard changes" runs iOS on a throwaway copy, so the disk image stays as Prepare made it.

iOS is Apple's software. This is for personal research on your own copy;
do not share the disk image.
