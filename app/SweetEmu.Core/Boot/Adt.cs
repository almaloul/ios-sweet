// Apple Device Tree: parse/serialize (tools/adt.py) and the QEMU virt tree
// (tools/mkadt.py --virt). Keep property order identical to mkadt.py so the
// outputs can be compared byte for byte.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SweetEmu.Core.Boot;

public sealed class AdtNode
{
    public List<(string Name, uint Flags, byte[] Value)> Props { get; } = new();
    public List<AdtNode> Children { get; } = new();

    public string Name
    {
        get
        {
            var v = Prop("name");
            if (v == null) return "?";
            int z = Array.IndexOf(v, (byte)0);
            return Encoding.ASCII.GetString(v, 0, z < 0 ? v.Length : z);
        }
    }

    public byte[]? Prop(string name) => Props.FirstOrDefault(p => p.Name == name).Value;

    public AdtNode? Child(string name) => Children.FirstOrDefault(c => c.Name == name);

    public static AdtNode Parse(byte[] buf)
    {
        int off = 0; // fresh cursor per parse (a shared one broke the second parse in a process)
        return Parse(buf, ref off);
    }

    public static AdtNode Parse(byte[] buf, ref int off)
    {
        var node = new AdtNode();
        int nprops = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off));
        int nchildren = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + 4));
        off += 8;
        for (int i = 0; i < nprops; i++)
        {
            int z = Array.IndexOf(buf, (byte)0, off, 32);
            string name = Encoding.ASCII.GetString(buf, off, (z < 0 ? off + 32 : z) - off);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + 32));
            uint flags = size >> 31;
            int len = (int)(size & 0x7fffffff);
            node.Props.Add((name, flags, buf.AsSpan(off + 36, len).ToArray()));
            off += 36 + ((len + 3) & ~3);
        }
        for (int i = 0; i < nchildren; i++) node.Children.Add(Parse(buf, ref off));
        return node;
    }

    public byte[] Serialize()
    {
        var ms = new MemoryStream();
        Write(ms);
        return ms.ToArray();
    }

    void Write(Stream s)
    {
        Span<byte> u = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u, (uint)Props.Count);
        s.Write(u);
        BinaryPrimitives.WriteUInt32LittleEndian(u, (uint)Children.Count);
        s.Write(u);
        foreach (var (name, flags, val) in Props)
        {
            var nb = new byte[32];
            var enc = Encoding.ASCII.GetBytes(name);
            Array.Copy(enc, nb, Math.Min(32, enc.Length));
            s.Write(nb);
            BinaryPrimitives.WriteUInt32LittleEndian(u, (uint)val.Length | (flags << 31));
            s.Write(u);
            s.Write(val);
            s.Write(new byte[(4 - val.Length % 4) % 4]);
        }
        foreach (var c in Children) c.Write(s);
    }
}

public static class VirtAdt
{
    // tools/mkadt.py constants (the --virt tree: --qemu + --highmem + virtio + uart0)
    const ulong HighPhysBase = 0x100000000, HighMemSize = 0xfcc00000, PanicLogSize = 0x100000;
    const ulong SocBase = 0x01000000;
    const ulong GicdSize = 0x10000, GicrSize = 0x100000;
    const ulong Fb = 0x9c000000, FbSize = 0x1700000;
    const ulong SweetEcid = 0x5357454554000001;
    const uint FbW = 1080, FbH = 2400, FbStride = 1080 * 4;
    const ulong QemuGicd = 0x08000000, QemuGicr = 0x080a0000;
    const uint QemuTimebase = 62500000, QemuVtimerIntid = 27;
    const ulong QemuVirtio = 0x0a000000, QemuVirtioSize = 0x4000;
    const ulong QemuUart = 0x09000000, QemuUartSize = 0x1000;

    static byte[] U32(params uint[] v)
    {
        var b = new byte[4 * v.Length];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4 * i), v[i]);
        return b;
    }

    static byte[] U64(params ulong[] v)
    {
        var b = new byte[8 * v.Length];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8 * i), v[i]);
        return b;
    }

    static byte[] CStr(params string[] s) => s.SelectMany(x => Encoding.ASCII.GetBytes(x).Append((byte)0)).ToArray();

    sealed class Builder
    {
        uint next = 1;
        public (string, byte[]) Ph() => ("AAPL,phandle", U32(next++));

        public AdtNode Node(string name, IEnumerable<(string, byte[])> props, IEnumerable<AdtNode>? children = null)
        {
            var n = new AdtNode();
            n.Props.Add(("name", 0, CStr(name)));
            foreach (var (k, v) in props) n.Props.Add((k, 0, v));
            if (children != null) n.Children.AddRange(children);
            return n;
        }
    }

    /// /product properties of the real D22AP tree, without syscfg placeholders and bookkeeping.
    public static List<(string, byte[])>? ProductProps(byte[]? d22apAdt)
    {
        if (d22apAdt == null) return null;
        var root = AdtNode.Parse(d22apAdt);
        var product = root.Children.FirstOrDefault(c => c.Name == "product")
            ?? throw new InvalidDataException("device tree has no /product node");
        return product.Props.Where(p => p.Flags == 0 && p.Name != "name" && p.Name != "AAPL,phandle")
            .Select(p => (p.Name, p.Value)).ToList();
    }

    /// Kernel limit (SWEET_SMP_MAX_CPUS).
    public const int MaxCpus = 8;

    /// The --virt tree. d22apAdt: the IPSW's DeviceTree.d22ap payload (for /product).
    /// cpus: one cpu node per core, QEMU virt MPIDR numbering (Aff0 = core),
    /// started through PSCI over HVC (psci-method, kernel patch 0086).
    public static byte[] Build(byte[]? d22apAdt, byte[]? seed = null, int cpus = 1)
    {
        if (cpus < 1 || cpus > MaxCpus) throw new ArgumentOutOfRangeException(nameof(cpus));
        var b = new Builder();
        ulong physBase = HighPhysBase, memSize = HighMemSize;
        ulong panicBase = physBase + memSize - PanicLogSize;
        memSize -= PanicLogSize;
        seed ??= RandomNumberGenerator.GetBytes(64);
        var bootManifestHash = SHA384.HashData(Encoding.ASCII.GetBytes("sweet boot manifest"));

        var chosen = b.Node("chosen", new[]
        {
            b.Ph(),
            ("dram-base", U64(physBase)),
            ("dram-size", U64(memSize)),
            ("random-seed", seed),
            ("debug-enabled", U32(1)),
            ("embedded-panic-log-size", U32((uint)PanicLogSize)),
            ("firmware-version", CStr("sweet-shim")),
            ("system-firmware-version", CStr("sweet-shim")),
            ("boot-manifest-hash", bootManifestHash),
            ("crypto-hash-method", CStr("sha2-384")),
            ("display-scale", U32(3)),
            ("unique-chip-id", U64(SweetEcid)),
            ("chip-id", U32(0x8015)),
            ("certificate-production-status", U32(0)),
            ("effective-production-status-ap", U32(0)),
            ("certificate-security-mode", U32(1)),
            ("effective-security-mode-sep", U32(1)),
        }, new[]
        {
            b.Node("memory-map", new[] { b.Ph() }),
            b.Node("asmb", new[] { b.Ph(), ("lp-sip0", U64(0x6f)), ("lp-sip1", U32(1)) }),
        });

        // QEMU virt: cpu n has MPIDR n (Aff0). cpu0 keeps mkadt.py's --virt properties.
        var cpuNodes = new List<AdtNode>();
        for (uint n = 0; n < cpus; n++)
        {
            var props = new List<(string, byte[])>
            {
                b.Ph(),
                ("device_type", CStr("cpu")),
                ("compatible", CStr("qcom,kryo470", "ARM,v8")),
                ("reg", U32(n)),
                ("cpu-id", U32(n)),
                ("cluster-id", U32(0)),
                ("cluster-type", U32(0)),
                ("die-cluster-id", U32(0)),
                ("cluster-core-id", U32(n)),
                ("l2-cache-id", U32(n)),
                ("l3-cache-id", U32(0)),
                ("state", CStr(n == 0 ? "running" : "stopped")),
                ("timebase-frequency", U32(QemuTimebase)),
                ("clock-frequency", U32(1804800000)),
                ("bus-frequency", U32(100000000)),
                ("memory-frequency", U32(1866000000)),
                ("peripheral-frequency", U32(QemuTimebase)),
                ("fixed-frequency", U32(QemuTimebase)),
            };
            if (cpus > 1) props.Add(("psci-method", CStr("hvc")));
            cpuNodes.Add(b.Node($"cpu{n}", props));
        }
        var cpusNode = b.Node("cpus", new[] { b.Ph(), ("#address-cells", U32(1)), ("#size-cells", U32(0)) }, cpuNodes);

        var gic = b.Node("gic", new[]
        {
            b.Ph(),
            ("compatible", CStr("arm,gic-v3")),
            ("interrupt-controller", Array.Empty<byte>()),
            ("virtual-timer-intid", U32(QemuVtimerIntid)),
            ("reg", U64(QemuGicd - SocBase, GicdSize, QemuGicr - SocBase, GicrSize)),
        });
        var armIo = b.Node("arm-io", new[]
        {
            b.Ph(),
            ("device_type", CStr("sweet-io")),
            ("compatible", CStr("sweet-io", "arm-io")),
            ("#address-cells", U32(2)),
            ("#size-cells", U32(2)),
            ("ranges", U64(0, SocBase, 0x100000000 - SocBase)),
        }, new[]
        {
            gic,
            b.Node("wdt", new[] { b.Ph(), ("no-pmu", U32(1)) }),
            b.Node("disp0", new[]
            {
                b.Ph(),
                ("device_type", CStr("display-subsystem")),
                ("compatible", CStr("sweet,display")),
                ("canvas-width", U32(1125)),
                ("canvas-height", U32(2436)),
            }),
            b.Node("uart0", new[]
            {
                b.Ph(),
                ("compatible", CStr("arm,pl011")),
                ("reg", U64(QemuUart - SocBase, QemuUartSize)),
            }),
        });

        var fbcon = b.Node("sweet-fbcon", new[]
        {
            b.Ph(),
            ("reg", U64(Fb, FbSize)),
            ("width", U32(FbW)),
            ("height", U32(FbH)),
            ("stride", U32(FbStride)),
        });
        // mkadt.py creates the ufs node (consuming a phandle) even when it is left out.
        b.Node("sweet-ufs", new[] { b.Ph() });
        var virtio = b.Node("sweet-virtio", new[]
        {
            b.Ph(),
            ("compatible", CStr("virtio,mmio")),
            ("reg", U64(QemuVirtio, QemuVirtioSize)),
        });
        var defaults = b.Node("defaults", new[] { b.Ph(), ("no-effaceable-storage", U32(1)) });
        var buttons = b.Node("buttons", new[]
        {
            b.Ph(),
            ("button-names", CStr("volup", "voldown", "hold")),
            ("home-button-type", U32(2)),
            ("opposed-power-vol-buttons", U32(1)),
        });
        var productProps = ProductProps(d22apAdt);
        var product = productProps != null ? b.Node("product", new[] { b.Ph() }.Concat(productProps)) : null;
        var pram = b.Node("pram", new[] { b.Ph(), ("reg", U64(panicBase).Concat(U64(PanicLogSize)).ToArray()) });

        var children = new List<AdtNode> { chosen, defaults, cpusNode, armIo, fbcon, buttons, pram };
        if (product != null) children.Add(product);
        children.Add(virtio);
        var root = b.Node("device-tree", new[]
        {
            b.Ph(),
            ("compatible", CStr("D22AP", "iPhone10,3", "xiaomi,sweet", "qcom,sm7150", "AppleARM")),
            ("model", CStr("iPhone10,3")),
            ("target-type", CStr("D22")),
            ("target-sub-type", CStr("D22AP")),
            ("manufacturer", CStr("Apple Inc.")),
            ("#address-cells", U32(2)),
            ("#size-cells", U32(2)),
        }, children);
        return root.Serialize();
    }
}
