// Starts QEMU's virt board with the SWEET payload (tools/sweet-virt.py in C#),
// and talks to it over QMP.
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SweetEmu.Core.Boot;

public sealed class QemuOptions
{
    public required string QemuExe { get; init; }
    public required byte[] Payload { get; init; }
    public string? Disk { get; init; }
    public required string WorkDir { get; init; }
    public int Cpus { get; init; } = 1;
    public string? Memory { get; init; }
    public string Display { get; init; } = "gtk";
    public string Accel { get; init; } = "tcg,tb-size=1024";
    public string Cpu { get; init; } = "cortex-a76";
    public bool Snapshot { get; init; }
    public bool Multitouch { get; init; }
    public IReadOnlyList<string> Extra { get; init; } = Array.Empty<string>();
}

public sealed class QemuSession : IDisposable
{
    const ulong StubPa = 0x40200000, VirtRamBase = 0x40000000;

    public Process Process { get; }
    public int QmpPort { get; }
    public string SerialLog { get; }
    public string CommandLine { get; }
    Qmp? qmp;

    QemuSession(Process p, int port, string serial, string cmd)
    {
        Process = p;
        QmpPort = port;
        SerialLog = serial;
        CommandLine = cmd;
    }

    public static byte[] StubBinary()
    {
        using var s = typeof(QemuSession).Assembly.GetManifestResourceStream("SweetEmu.Core.ramfb_stub.bin")
            ?? throw new InvalidOperationException("ramfb_stub.bin resource missing");
        var b = new byte[s.Length];
        s.ReadExactly(b);
        return b;
    }

    public static List<string> BuildArgs(QemuOptions o, Payload.GuestImage g, string ramBin, string stubBin, string serial, int port)
    {
        ulong ramGiB = Math.Max(4, (g.PhysBase + g.MemSize - VirtRamBase + (1UL << 30) - 1) >> 30);
        string display = o.Display == "gtk" ? "gtk,zoom-to-fit=on,show-tabs=on" : o.Display;
        string Q(string path) => path.Replace(",", ",,");
        var a = new List<string>
        {
            "-M", "virt,gic-version=3", "-cpu", o.Cpu, "-smp", o.Cpus.ToString(), "-m", o.Memory ?? $"{ramGiB}G",
            "-accel", o.Accel, "-no-reboot",
            "-display", display, "-device", "ramfb",
            "-chardev", o.Display != "none" ? $"vc,id=ser0,logfile={Q(serial)}" : $"file,id=ser0,path={Q(serial)}",
            "-serial", "chardev:ser0",
            "-qmp", $"tcp:127.0.0.1:{port},server=on,wait=off",
            "-global", "virtio-mmio.force-legacy=false",
            "-device", o.Multitouch ? "virtio-multitouch-device" : "virtio-tablet-device",
            "-device", "virtio-keyboard-device",
            "-device", $"loader,file={Q(ramBin)},addr=0x{g.PhysBase:x},force-raw=on",
            "-device", $"loader,file={Q(stubBin)},addr=0x{StubPa:x},force-raw=on",
            "-device", $"loader,addr=0x{StubPa:x},cpu-num=0",
        };
        if (o.Disk != null)
        {
            string opts = $"if=none,id=root,format=raw,cache=writeback,file={Q(Path.GetFullPath(o.Disk))}";
            if (o.Snapshot) opts += ",snapshot=on";
            a.AddRange(new[] { "-drive", opts, "-device", "virtio-blk-device,drive=root" });
        }
        a.AddRange(o.Extra);
        return a;
    }

    public static QemuSession Start(QemuOptions o)
    {
        Directory.CreateDirectory(o.WorkDir);
        var g = Payload.Layout(o.Payload);
        if (g.PhysBase < 0xa0000000)
            throw new InvalidDataException("payload RAM overlaps the framebuffer: build the ADT for virt (high memory)");
        string ram = Path.Combine(o.WorkDir, "ram.bin"), stub = Path.Combine(o.WorkDir, "stub.bin");
        string serial = Path.Combine(o.WorkDir, "serial.txt");
        File.WriteAllBytes(ram, g.Ram);
        File.WriteAllBytes(stub, Payload.Stub(StubBinary(), g.BootArgsPa, g.EntryPa));
        if (File.Exists(serial)) File.Delete(serial);
        int port = FreePort();
        var args = BuildArgs(o, g, ram, stub, serial, port);
        var psi = new ProcessStartInfo(o.QemuExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = o.WorkDir };
        foreach (var x in args) psi.ArgumentList.Add(x);
        var p = Process.Start(psi) ?? throw new IOException("could not start QEMU");
        string cmd = o.QemuExe + " " + string.Join(' ', args.Select(x => x.Contains(' ') ? $"\"{x}\"" : x));
        return new QemuSession(p, port, serial, cmd);
    }

    static int FreePort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public Qmp Monitor => qmp ??= Qmp.Connect(QmpPort);

    /// Press and release a key by QEMU qcode (e.g. "esc", "pgup", "pgdn", "end").
    public void TapKey(string qcode, int holdMs = 120)
    {
        Monitor.SendKey(qcode, true);
        Thread.Sleep(holdMs);
        Monitor.SendKey(qcode, false);
    }

    public void Screenshot(string path) => Monitor.Command("screendump", new JsonObject { ["filename"] = path, ["format"] = "png" });

    public void Quit()
    {
        try
        {
            Monitor.Command("quit");
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        qmp?.Dispose();
        if (!Process.HasExited)
        {
            try { Process.Kill(); } catch (InvalidOperationException) { }
        }
        Process.Dispose();
    }
}

public sealed class Qmp : IDisposable
{
    readonly TcpClient client;
    readonly StreamReader reader;
    readonly StreamWriter writer;
    readonly object gate = new();

    Qmp(TcpClient c)
    {
        client = c;
        var s = c.GetStream();
        reader = new StreamReader(s, new UTF8Encoding(false));
        writer = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    public static Qmp Connect(int port, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var c = new TcpClient();
                c.Connect("127.0.0.1", port);
                var q = new Qmp(c);
                q.reader.ReadLine(); // greeting
                q.Command("qmp_capabilities");
                return q;
            }
            catch (SocketException) when (sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(100);
            }
        }
    }

    public JsonNode? Command(string execute, JsonObject? args = null)
    {
        lock (gate)
        {
            var msg = new JsonObject { ["execute"] = execute };
            if (args != null) msg["arguments"] = args;
            writer.WriteLine(msg.ToJsonString());
            while (true)
            {
                var line = reader.ReadLine() ?? throw new IOException("QEMU monitor closed");
                var r = JsonNode.Parse(line)!.AsObject();
                if (r.ContainsKey("return")) return r["return"];
                if (r.ContainsKey("error")) throw new InvalidOperationException($"{execute}: {r["error"]!["desc"]}");
                // asynchronous events are skipped
            }
        }
    }

    public void SendKey(string qcode, bool down)
    {
        var ev = new JsonObject
        {
            ["type"] = "key",
            ["data"] = new JsonObject
            {
                ["down"] = down,
                ["key"] = new JsonObject { ["type"] = "qcode", ["data"] = qcode },
            },
        };
        Command("input-send-event", new JsonObject { ["events"] = new JsonArray(ev) });
    }

    public void Dispose() => client.Dispose();
}

public static class QemuLocator
{
    /// qemu-system-aarch64 next to the app, under Program Files, or on PATH.
    public static string? Find(string appDir)
    {
        string exe = OperatingSystem.IsWindows() ? "qemu-system-aarch64.exe" : "qemu-system-aarch64";
        var candidates = new List<string>
        {
            Path.Combine(appDir, "qemu", exe),
            Path.Combine(appDir, exe),
        };
        if (OperatingSystem.IsWindows())
        {
            foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432") })
                if (pf != null) candidates.Add(Path.Combine(pf, "qemu", exe));
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0) candidates.Add(Path.Combine(dir, exe));
        return candidates.FirstOrDefault(File.Exists);
    }
}
