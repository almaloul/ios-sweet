// UI preview only (Mono on Linux): a backend that pretends to work.
using System;
using System.IO;
using System.Threading;
using SweetEmu.Core;

namespace SweetEmu.Preview
{
    public class FakeBackend : IAppBackend
    {
        public string HomeFolder { get { return "/tmp/sweetemu-preview"; } }
        public string IpswFolder { get { return HomeFolder + "/IPSW"; } }
        public string KernelFolder { get { return HomeFolder + "/Kernel"; } }
        public string QemuDownloadUrl { get { return "https://qemu.weilnetz.de/w64/"; } }
        public string SerialLogPath { get { return HomeFolder + "/serial.txt"; } }
        public event EventHandler Exited;
        public string Scenario = Environment.GetEnvironmentVariable("PREVIEW") ?? "fresh";
        bool running;

        public BackendStatus GetStatus()
        {
            var s = new BackendStatus();
            if (Scenario != "fresh") { s.Ipsw = IpswFolder + "/iPhone10,3_16.7.16_20H392_Restore.ipsw"; s.IpswInfo = "iOS 16.7.16 (20H392)"; }
            if (Scenario == "ready" || Scenario == "running") { s.Kernel = KernelFolder + "/kernel.development.sweet"; s.Qemu = @"C:\Program Files\qemu\qemu-system-aarch64.exe"; s.RootReady = true; }
            if (s.Ipsw == null) s.Problem = "Put the iPhone X iOS 16 IPSW in " + IpswFolder;
            return s;
        }

        public void Prepare(BackendSettings settings, Action<string> log, Action<double> progress, CancellationToken cancel)
        {
            log("reading OS (098-12345-678.dmg)");
            for (int i = 0; i <= 100; i += 5) { cancel.ThrowIfCancellationRequested(); progress(i / 100.0); Thread.Sleep(50); }
        }

        public bool IsRunning { get { return running || Scenario == "running"; } }
        public void Start(BackendSettings settings) { running = true; }
        public void Stop() { running = false; if (Exited != null) Exited(this, EventArgs.Empty); }
        public void PressButton(string button) { }
        public void Screenshot(string path) { }
    }

    static class PreviewProgram
    {
        [STAThread]
        static void Main()
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.Run(new MainForm(new FakeBackend()));
        }
    }
}
