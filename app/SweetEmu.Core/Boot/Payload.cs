// tools/mkpayload.py (kernel Mach-O + ADT -> SWEETXNU payload) and the part of
// tools/qemu-sweet.py that lays a payload out in guest RAM with boot_args.
using System.Buffers.Binary;
using System.Text;

namespace SweetEmu.Core.Boot;

public static class Payload
{
    const uint LC_SEGMENT_64 = 0x19, LC_UNIXTHREAD = 0x5, LC_DYLD_CHAINED_FIXUPS = 0x80000034;
    const ulong Page = 0x4000;
    public const int BootLineLength = 608;
    const int Cmdline = 0x48, SegTable = Cmdline + BootLineLength;

    record Seg(string Name, ulong VmAddr, ulong VmSize, ulong FileOff, ulong FileSize);

    public sealed record Info(ulong VirtBase, ulong Entry, ulong KernelEnd, int Fixups, int Size);

    /// Build a payload. kernel: the Mach-O; adt: the device tree; cmdline: boot-args.
    public static (byte[] Payload, Info Info) Build(byte[] kernel, byte[] adt, string cmdline)
    {
        var buf = (byte[])kernel.Clone();
        if (BinaryPrimitives.ReadUInt32LittleEndian(buf) != 0xfeedfacf) throw new InvalidDataException("kernel: not a 64-bit Mach-O");
        int ncmds = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16));
        var segs = new List<Seg>();
        ulong? entry = null;
        (uint DataOff, uint Size)? fixups = null;
        int off = 32;
        for (int i = 0; i < ncmds; i++)
        {
            uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + 4));
            if (cmd == LC_SEGMENT_64)
            {
                int z = Array.IndexOf(buf, (byte)0, off + 8, 16);
                string name = Encoding.ASCII.GetString(buf, off + 8, (z < 0 ? off + 24 : z) - (off + 8));
                segs.Add(new Seg(name,
                    BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off + 24)),
                    BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off + 32)),
                    BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off + 40)),
                    BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off + 48))));
            }
            else if (cmd == LC_UNIXTHREAD)
            {
                // arm_thread_state64: x0..x28, fp, lr, sp, pc
                entry = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off + 16 + 32 * 8));
            }
            else if (cmd == LC_DYLD_CHAINED_FIXUPS)
            {
                fixups = (BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + 8)), BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + 12)));
            }
            off += size;
        }
        if (entry == null) throw new InvalidDataException("kernel: no LC_UNIXTHREAD");
        int nfix = fixups is { } fx ? ApplyChainedFixups(buf, segs, fx.DataOff) : 0;

        var used = segs.Where(s => s.VmSize != 0).ToList();
        var text = segs[0];
        if (text.Name != "__TEXT" || text.FileOff != 0) throw new InvalidDataException("kernel: first segment is not __TEXT at offset 0");
        ulong virtBase = text.VmAddr & ~(0x2000000UL - 1);
        ulong kernelEnd = used.Max(s => s.VmAddr + s.VmSize);
        kernelEnd = (kernelEnd + Page - 1) & ~(Page - 1);
        var (physBase, memSize) = AdtDram(adt);

        var ms = new MemoryStream();
        ms.Write(new byte[SegTable + 32 * used.Count]);
        var table = new List<(ulong, ulong, ulong, ulong)>();
        foreach (var s in used)
        {
            long o = ms.Length;
            ms.Write(buf, (int)s.FileOff, (int)s.FileSize);
            ms.Write(new byte[(16 - ms.Length % 16) % 16]);
            table.Add((s.VmAddr, s.VmSize, (ulong)o, s.FileSize));
        }
        long adtOff = ms.Length;
        ms.Write(adt);
        ms.Write(new byte[(16 - ms.Length % 16) % 16]);
        var outb = ms.ToArray();

        Encoding.ASCII.GetBytes("SWEETXNU").CopyTo(outb, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(outb.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(outb.AsSpan(12), (uint)used.Count);
        ulong[] hdr = { virtBase, entry.Value, (ulong)adtOff, (ulong)adt.Length, kernelEnd, physBase, memSize };
        for (int i = 0; i < hdr.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(outb.AsSpan(0x10 + 8 * i), hdr[i]);
        var cl = Encoding.UTF8.GetBytes(cmdline);
        Array.Copy(cl, 0, outb, Cmdline, Math.Min(cl.Length, BootLineLength - 1));
        for (int i = 0; i < table.Count; i++)
        {
            var (a, b2, c, d) = table[i];
            BinaryPrimitives.WriteUInt64LittleEndian(outb.AsSpan(SegTable + 32 * i), a);
            BinaryPrimitives.WriteUInt64LittleEndian(outb.AsSpan(SegTable + 32 * i + 8), b2);
            BinaryPrimitives.WriteUInt64LittleEndian(outb.AsSpan(SegTable + 32 * i + 16), c);
            BinaryPrimitives.WriteUInt64LittleEndian(outb.AsSpan(SegTable + 32 * i + 24), d);
        }
        return (outb, new Info(virtBase, entry.Value, kernelEnd, nfix, outb.Length));
    }

    static (ulong, ulong) AdtDram(byte[] adt)
    {
        var root = AdtNode.Parse(adt);
        var chosen = root.Child("chosen") ?? throw new InvalidDataException("ADT: no /chosen");
        return (BinaryPrimitives.ReadUInt64LittleEndian(chosen.Prop("dram-base")),
                BinaryPrimitives.ReadUInt64LittleEndian(chosen.Prop("dram-size")));
    }

    static int ApplyChainedFixups(byte[] buf, List<Seg> segs, uint dataOff)
    {
        var h = buf.AsSpan((int)dataOff);
        uint startsOff = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
        uint importsCount = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
        if (importsCount != 0) throw new InvalidDataException("kernel has binds; only rebases are supported");
        ulong imageBase = segs[0].VmAddr;
        int segCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan((int)(dataOff + startsOff)));
        int nfix = 0;
        for (int i = 0; i < segCount; i++)
        {
            uint segInfo = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan((int)(dataOff + startsOff + 4 + 4 * i)));
            if (segInfo == 0) continue;
            int b = (int)(dataOff + startsOff + segInfo);
            ushort pageSize = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(b + 4));
            ushort fmt = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(b + 6));
            ushort pageCount = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(b + 20));
            var seg = segs[i];
            for (int p = 0; p < pageCount; p++)
            {
                ushort start = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(b + 22 + 2 * p));
                if (start == 0xffff) continue;
                if ((start & 0x8000) != 0) throw new InvalidDataException("DYLD_CHAINED_PTR_START_MULTI not supported");
                long loc = (long)seg.FileOff + p * pageSize + start;
                while (true)
                {
                    ulong raw = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan((int)loc));
                    var (target, next, stride) = Decode(raw, fmt, imageBase);
                    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan((int)loc), target);
                    nfix++;
                    if (next == 0) break;
                    loc += (long)next * stride;
                }
            }
        }
        return nfix;
    }

    static (ulong Target, ulong Next, int Stride) Decode(ulong raw, ushort fmt, ulong imageBase)
    {
        switch (fmt)
        {
            case 2:
            case 6:
            {
                if ((raw >> 63) != 0) throw new InvalidDataException("bind in kernel");
                ulong target = raw & ((1UL << 36) - 1);
                ulong high8 = (raw >> 36) & 0xff;
                ulong next = (raw >> 51) & 0xfff;
                if (fmt == 6) target += imageBase;
                return (target | (high8 << 56), next, 4);
            }
            case 8:
                return (imageBase + (raw & ((1UL << 30) - 1)), (raw >> 51) & 0xfff, 4);
            case 1:
            case 7:
            case 9:
            case 12:
            {
                bool auth = (raw >> 63) != 0;
                ulong next = (raw >> 51) & 0x7ff;
                int stride = fmt is 7 or 12 ? 4 : 8;
                ulong target;
                if (auth) target = raw & 0xffffffff;
                else target = (raw & ((1UL << 43) - 1)) | (((raw >> 43) & 0xff) << 56);
                if (fmt is 7 or 9 or 12 || auth) target += imageBase;
                return (target, next, stride);
            }
            default:
                throw new InvalidDataException($"unsupported chained pointer format {fmt}");
        }
    }

    // ---- guest RAM layout (tools/qemu-sweet.py build_image) ----

    public const ulong FbBase = 0x9c000000;
    public const uint FbWidth = 1080, FbHeight = 2400, FbStride = FbWidth * 4;

    public sealed record GuestImage(ulong PhysBase, byte[] Ram, ulong BootArgsPa, ulong EntryPa, ulong MemSize);

    public static GuestImage Layout(byte[] payload)
    {
        if (!payload.AsSpan(0, 8).SequenceEqual("SWEETXNU"u8) || BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8)) != 1)
            throw new InvalidDataException("not a SWEETXNU v1 payload");
        int nsegs = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12));
        ulong R(int o) => BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(o));
        ulong virtBase = R(0x10), entryVa = R(0x18), adtOff = R(0x20), adtSize = R(0x28), kernelEnd = R(0x30), physBase = R(0x38), memSize = R(0x40);
        ulong delta = physBase - virtBase;
        ulong adtPa = kernelEnd + delta;
        ulong baPa = (adtPa + adtSize + Page - 1) & ~(Page - 1);
        ulong top = baPa + Page;
        var img = new byte[top - physBase];
        for (int i = 0; i < nsegs; i++)
        {
            ulong vm = R(0x2a8 + 32 * i), foff = R(0x2a8 + 32 * i + 16), fsize = R(0x2a8 + 32 * i + 24);
            Array.Copy(payload, (long)foff, img, (long)(vm + delta - physBase), (long)fsize);
        }
        Array.Copy(payload, (long)adtOff, img, (long)(adtPa - physBase), (long)adtSize);
        // boot_args (struct.pack('<HH4x4Q6QI4xQI', ...) + cmdline + pad + memSizeActual)
        var ba = new byte[736];
        var s = ba.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], 2);
        ulong[] q = { virtBase, physBase, memSize, top, FbBase, 1, FbStride, FbWidth, FbHeight, 32 };
        for (int i = 0; i < q.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(s[(8 + 8 * i)..], q[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[88..], 0);                     // machineType
        BinaryPrimitives.WriteUInt64LittleEndian(s[96..], adtPa - delta);         // deviceTreeP (VA)
        BinaryPrimitives.WriteUInt32LittleEndian(s[104..], (uint)adtSize);
        payload.AsSpan(0x48, BootLineLength).CopyTo(s[108..]);
        BinaryPrimitives.WriteUInt64LittleEndian(s[(108 + BootLineLength + 4)..], 0);     // boot flags
        BinaryPrimitives.WriteUInt64LittleEndian(s[(108 + BootLineLength + 12)..], memSize);
        ba.CopyTo(img, (long)(baPa - physBase));
        return new GuestImage(physBase, img, baPa, entryVa + delta, memSize);
    }

    /// The ramfb pre-kernel stub with its parameter block filled in.
    public static byte[] Stub(byte[] stubBin, ulong bootArgsPa, ulong entryPa, bool ramfb = true)
    {
        const int ParamsOff = 0xf00;
        var stub = (byte[])stubBin.Clone();
        var p = stub.AsSpan(ParamsOff);
        BinaryPrimitives.WriteUInt64LittleEndian(p, bootArgsPa);
        BinaryPrimitives.WriteUInt64LittleEndian(p[8..], entryPa);
        BinaryPrimitives.WriteUInt64LittleEndian(p[16..], FbBase);
        BinaryPrimitives.WriteUInt32LittleEndian(p[24..], FbWidth);
        BinaryPrimitives.WriteUInt32LittleEndian(p[28..], FbHeight);
        BinaryPrimitives.WriteUInt32LittleEndian(p[32..], FbStride);
        BinaryPrimitives.WriteUInt32LittleEndian(p[36..], ramfb ? 1u : 0u);
        return stub;
    }
}
