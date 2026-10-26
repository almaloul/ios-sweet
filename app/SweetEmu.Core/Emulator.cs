// The app's folder layout and the two user-level operations: Prepare and Start.
//
//   <home>\IPSW\     put the iPhone X iOS 16 IPSW here (iPhone10,3_16.7.16_20H392_Restore.ipsw)
//   <home>\Kernel\   kernel.development.sweet (the SWEET kernel from the CI build)
//   <home>\qemu\     optional bundled QEMU (else Program Files\qemu or PATH)
//   <home>\Data\     generated: ios16-root.img, virt.adt, payload-virt.bin, logs
using System.Text;
using SweetEmu.Core.Boot;

namespace SweetEmu.Core;

public sealed class EmulatorSettings
{
    /// Emulated cores; QEMU runs each on its own host thread. Default: up to 4,
    /// leaving at least two host threads for QEMU's I/O and the rest of Windows.
    public int Cpus { get; set; } = Math.Clamp(Environment.ProcessorCount - 2, 1, 4);
    public string? Memory { get; set; }
    public bool DiscardChanges { get; set; }
    public string Display { get; set; } = "gtk";
    public string BootArgs { get; set; } = Emulator.DefaultBootArgs;
    public long ImageSizeGiB { get; set; }
}

public sealed class Emulator
{
    public const string DefaultBootArgs =
        "debug=0xc serial=3 keepsyms=1 -enable_kprintf_spam -noprogress rd=vblk0 rootrw=1 " +
        "unentitled_ios_sim_launch=1 ignition_level=1 wdt_test=no_user_checkin launchd_unsecure_cache=1 " +
        "wdt=-1 sweetfb_noconsole=1 sweetfb_hz=30 sweetsample=SpringBoard,backboardd,lsd";

    public string Home { get; }
    public string IpswDir => Path.Combine(Home, "IPSW");
    public string KernelDir => Path.Combine(Home, "Kernel");
    public string DataDir => Path.Combine(Home, "Data");
    public string RootImage => Path.Combine(DataDir, "ios16-root.img");
    public string AdtPath => Path.Combine(DataDir, "virt.adt");
    public string D22apPath => Path.Combine(DataDir, "d22ap.adt");
    public string TempDir => Path.Combine(DataDir, "temp");
    public string RunDir => Path.Combine(DataDir, "run");

    public Emulator(string home)
    {
        Home = Path.GetFullPath(home);
        Directory.CreateDirectory(IpswDir);
        Directory.CreateDirectory(KernelDir);
        Directory.CreateDirectory(DataDir);
    }

    public string? FindIpsw() =>
        Directory.EnumerateFiles(IpswDir, "*.ipsw").OrderByDescending(f => Path.GetFileName(f).Contains("iPhone10,3")).FirstOrDefault();

    public string? FindKernel()
    {
        var preferred = Path.Combine(KernelDir, "kernel.development.sweet");
        if (File.Exists(preferred)) return preferred;
        return Directory.EnumerateFiles(KernelDir, "kernel*").FirstOrDefault(f => !f.EndsWith(".txt") && !f.EndsWith(".dSYM"));
    }

    public string? FindQemu() => QemuLocator.Find(Home) ?? QemuLocator.Find(AppContext.BaseDirectory);

    public bool RootReady => File.Exists(RootImage) && File.Exists(D22apPath);

    public sealed record Status(string? Ipsw, string? Kernel, string? Qemu, bool RootReady, string? Problem);

    public Status Check()
    {
        string? ipsw = FindIpsw(), kernel = FindKernel(), qemu = FindQemu();
        string? problem = null;
        if (!RootReady && ipsw == null) problem = $"Put the iPhone X iOS 16 IPSW in {IpswDir}";
        else if (kernel == null) problem = $"Put kernel.development.sweet in {KernelDir}";
        else if (qemu == null) problem = "Install QEMU (https://qemu.weilnetz.de/w64/) or put it in the qemu folder";
        return new Status(ipsw, kernel, qemu, RootReady, problem);
    }

    /// Build the root image and device tree from the IPSW (once; takes a while).
    public void Prepare(EmulatorSettings s, Action<string> log, Action<double> progress, CancellationToken cancel)
    {
        string ipswPath = FindIpsw() ?? throw new FileNotFoundException($"no .ipsw in {IpswDir}");
        using var ipsw = new Ipsw(ipswPath);
        log($"device tree: {ipsw.DeviceTreePath}");
        File.WriteAllBytes(D22apPath, ipsw.ReadDeviceTree());
        RootBuilder.Build(ipsw, new RootBuildOptions
        {
            OutputImage = RootImage,
            ImageSize = s.ImageSizeGiB << 30,
            TempDir = TempDir,
            Log = log,
            Progress = progress,
        }, cancel);
        if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true);
        log("prepared");
    }

    public byte[] BuildPayload(EmulatorSettings s)
    {
        string kernel = FindKernel() ?? throw new FileNotFoundException($"no kernel in {KernelDir}");
        byte[]? d22 = File.Exists(D22apPath) ? File.ReadAllBytes(D22apPath) : null;
        int cpus = Math.Clamp(s.Cpus, 1, VirtAdt.MaxCpus);
        var adt = VirtAdt.Build(d22, cpus: cpus);
        File.WriteAllBytes(AdtPath, adt);
        var (payload, _) = Payload.Build(File.ReadAllBytes(kernel), adt, $"cpus={cpus} {s.BootArgs}");
        File.WriteAllBytes(Path.Combine(DataDir, "payload-virt.bin"), payload);
        return payload;
    }

    public QemuSession Start(EmulatorSettings s, IReadOnlyList<string>? extra = null)
    {
        if (!RootReady) throw new InvalidOperationException("Prepare first: the root image does not exist yet");
        string qemu = FindQemu() ?? throw new FileNotFoundException("QEMU not found");
        return QemuSession.Start(new QemuOptions
        {
            QemuExe = qemu,
            Payload = BuildPayload(s),
            Disk = RootImage,
            WorkDir = RunDir,
            Cpus = s.Cpus,
            Memory = s.Memory,
            Snapshot = s.DiscardChanges,
            Display = s.Display,
            Extra = extra ?? Array.Empty<string>(),
        });
    }
}
