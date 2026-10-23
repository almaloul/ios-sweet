using SweetEmu.Core.Boot;

namespace SweetEmu.Core;

/// The real engine behind the window.
public sealed class AppBackend : IAppBackend
{
    readonly Emulator emu;
    QemuSession? session;
    string? lastIpswPath, lastIpswInfo;

    public AppBackend(string home) => emu = new Emulator(home);

    public string HomeFolder => emu.Home;
    public string IpswFolder => emu.IpswDir;
    public string KernelFolder => emu.KernelDir;
    public string QemuDownloadUrl => "https://qemu.weilnetz.de/w64/";
    public string SerialLogPath => Path.Combine(emu.RunDir, "serial.txt");
    public event EventHandler? Exited;

    public BackendStatus GetStatus()
    {
        var s = emu.Check();
        if (s.Ipsw != null && s.Ipsw != lastIpswPath)
        {
            lastIpswPath = s.Ipsw;
            try
            {
                using var ipsw = new Ipsw(s.Ipsw);
                lastIpswInfo = $"iOS {ipsw.ProductVersion} ({ipsw.BuildVersion})";
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                lastIpswInfo = "not usable: " + e.Message;
            }
        }
        return new BackendStatus
        {
            Ipsw = s.Ipsw!,
            IpswInfo = (s.Ipsw != null ? lastIpswInfo : null)!,
            Kernel = s.Kernel!,
            Qemu = s.Qemu!,
            RootReady = s.RootReady,
            Problem = s.Problem!,
        };
    }

    public void Prepare(BackendSettings settings, Action<string> log, Action<double> progress, CancellationToken cancel) =>
        emu.Prepare(new EmulatorSettings { ImageSizeGiB = settings.ImageSizeGiB }, log, progress, cancel);

    public bool IsRunning => session != null && !session.Process.HasExited;

    public void Start(BackendSettings settings)
    {
        if (IsRunning) return;
        session?.Dispose();
        var s = emu.Start(new EmulatorSettings { DiscardChanges = settings.DiscardChanges });
        session = s;
        s.Process.EnableRaisingEvents = true;
        s.Process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        if (session == null) return;
        if (!session.Process.HasExited)
        {
            session.Quit();
            if (!session.Process.WaitForExit(5000)) session.Process.Kill();
        }
        session.Dispose();
        session = null;
    }

    public void PressButton(string button)
    {
        if (!IsRunning) return;
        // Keys the kernel's virtio-input driver maps to the iPhone buttons (bsd/dev/sweet_virtio.c).
        string qcode = button switch
        {
            "home" => "esc",
            "volup" => "pgup",
            "voldown" => "pgdn",
            "power" => "end",
            _ => throw new ArgumentException("unknown button " + button),
        };
        session!.TapKey(qcode);
    }

    public void Screenshot(string path)
    {
        if (IsRunning) session!.Screenshot(Path.GetFullPath(path));
    }
}
