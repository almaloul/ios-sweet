// The window (SweetEmu/MainForm.cs) talks to the engine only through this
// interface. Kept to plain C# 7 so the same UI source also compiles with Mono
// (used to preview the UI on Linux with a fake backend).
using System;
using System.Threading;

namespace SweetEmu.Core
{
    public class BackendStatus
    {
        public string Ipsw;          // path, or null
        public string IpswInfo;      // "iOS 16.7.16 (20H392)" when readable
        public string Kernel;        // path, or null
        public string Qemu;          // path, or null
        public bool RootReady;
        public string Problem;       // first missing piece, in words, or null
    }

    public class BackendSettings
    {
        public bool DiscardChanges;
        public int ImageSizeGiB;     // 0 = automatic
    }

    public interface IAppBackend
    {
        string HomeFolder { get; }
        string IpswFolder { get; }
        string KernelFolder { get; }
        string QemuDownloadUrl { get; }

        BackendStatus GetStatus();

        /// Blocking; run it on a worker thread.
        void Prepare(BackendSettings settings, Action<string> log, Action<double> progress, CancellationToken cancel);

        bool IsRunning { get; }
        void Start(BackendSettings settings);
        void Stop();
        /// "home", "volup", "voldown", "power"
        void PressButton(string button);
        void Screenshot(string path);
        /// Kernel console log of the running (or last) session.
        string SerialLogPath { get; }
        event EventHandler Exited;
    }
}
