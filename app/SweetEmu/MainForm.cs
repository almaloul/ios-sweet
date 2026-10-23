// SweetEmu main window. Plain WinForms in conservative C# so the same file
// builds for .NET 8 (the shipped app) and with Mono (UI preview on Linux).
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SweetEmu.Core;

namespace SweetEmu
{
    public class MainForm : Form
    {
        readonly IAppBackend backend;
        readonly BackendSettings settings = new BackendSettings();

        readonly Label ipswState = new Label(), kernelState = new Label(), qemuState = new Label(), rootState = new Label();
        readonly Label ipswText = new Label(), kernelText = new Label(), qemuText = new Label(), rootText = new Label();
        readonly Button ipswButton = new Button(), kernelButton = new Button(), qemuButton = new Button(), prepareButton = new Button();
        readonly CheckBox discard = new CheckBox();
        readonly Button startButton = new Button();
        readonly Button homeButton = new Button(), volUpButton = new Button(), volDownButton = new Button(), lockButton = new Button(), shotButton = new Button();
        readonly ProgressBar progress = new ProgressBar();
        readonly Label statusLine = new Label();
        readonly TextBox log = new TextBox();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        CancellationTokenSource prepareCancel;
        bool preparing;
        long serialPos;
        BackendStatus status;

        static readonly Color Good = Color.FromArgb(0x1E, 0x8E, 0x3E);
        static readonly Color Bad = Color.FromArgb(0xC5, 0x22, 0x1F);
        static readonly Color Muted = Color.FromArgb(0x5F, 0x63, 0x68);

        public MainForm(IAppBackend backend)
        {
            this.backend = backend;
            Text = "SweetEmu - iOS 16 on QEMU";
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.White;
            ClientSize = new Size(760, 640);
            MinimumSize = new Size(640, 560);
            StartPosition = FormStartPosition.CenterScreen;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // title
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // setup
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // run
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // progress
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // log
            Controls.Add(root);

            var title = new Label
            {
                Text = "SweetEmu",
                Font = new Font("Segoe UI", 18F, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 0),
            };
            var subtitle = new Label
            {
                Text = "iOS 16 (iPhone X userland) on QEMU, from the ios-sweet XNU port. Personal use only.",
                ForeColor = Muted,
                AutoSize = true,
                Margin = new Padding(2, 0, 0, 12),
            };
            var head = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Dock = DockStyle.Top };
            head.Controls.Add(title);
            head.Controls.Add(subtitle);
            root.Controls.Add(head);

            // Setup checklist
            var setup = new GroupBox { Text = "Setup", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 6, 10, 10) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            AddRow(grid, 0, ipswState, ipswText, ipswButton, "Open IPSW folder", OpenIpswFolder);
            AddRow(grid, 1, kernelState, kernelText, kernelButton, "Open Kernel folder", OpenKernelFolder);
            AddRow(grid, 2, qemuState, qemuText, qemuButton, "Get QEMU", GetQemu);
            AddRow(grid, 3, rootState, rootText, prepareButton, "Prepare", PrepareClicked);
            setup.Controls.Add(grid);
            root.Controls.Add(setup);

            // Run controls
            var run = new GroupBox { Text = "Emulator", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 6, 10, 10), Margin = new Padding(3, 10, 3, 3) };
            var runPanel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            discard.Text = "Discard changes when the emulator closes (keeps the iOS image pristine)";
            discard.AutoSize = true;
            discard.Margin = new Padding(3, 3, 3, 8);
            runPanel.Controls.Add(discard);
            var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Dock = DockStyle.Top };
            Style(startButton, "Start", 120, true);
            startButton.Click += StartClicked;
            buttons.Controls.Add(startButton);
            Style(homeButton, "Home", 80, false);
            Style(volUpButton, "Volume +", 80, false);
            Style(volDownButton, "Volume -", 80, false);
            Style(lockButton, "Lock", 80, false);
            Style(shotButton, "Screenshot", 90, false);
            homeButton.Click += delegate { Press("home"); };
            volUpButton.Click += delegate { Press("volup"); };
            volDownButton.Click += delegate { Press("voldown"); };
            lockButton.Click += delegate { Press("power"); };
            shotButton.Click += ScreenshotClicked;
            buttons.Controls.Add(homeButton);
            buttons.Controls.Add(volUpButton);
            buttons.Controls.Add(volDownButton);
            buttons.Controls.Add(lockButton);
            buttons.Controls.Add(shotButton);
            runPanel.Controls.Add(buttons);
            var hint = new Label
            {
                Text = "In the QEMU window the mouse is your finger. Keys: Esc = Home, PgUp/PgDn = volume, End = lock.",
                ForeColor = Muted,
                AutoSize = true,
                Margin = new Padding(3, 6, 3, 0),
            };
            runPanel.Controls.Add(hint);
            run.Controls.Add(runPanel);
            root.Controls.Add(run);

            var progressRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 8, 0, 4) };
            progress.Dock = DockStyle.Fill;
            progress.Height = 14;
            progress.Visible = false;
            statusLine.AutoSize = true;
            statusLine.ForeColor = Muted;
            progressRow.Controls.Add(statusLine);
            progressRow.Controls.Add(progress);
            root.Controls.Add(progressRow);

            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.Font = new Font(FontFamily.GenericMonospace, 8.5F);
            log.BackColor = Color.FromArgb(0xF8, 0xF9, 0xFA);
            log.Dock = DockStyle.Fill;
            log.WordWrap = false;
            root.Controls.Add(log);

            backend.Exited += delegate { BeginInvoke(new Action(SessionEnded)); };
            timer.Interval = 1000;
            timer.Tick += delegate { Refresh2(); TailSerial(); };
            timer.Start();
            Refresh2();
            Log("Home folder: " + backend.HomeFolder);
        }

        static void Style(Button b, string text, int width, bool primary)
        {
            b.Text = text;
            b.Width = width;
            b.Height = primary ? 34 : 30;
            b.FlatStyle = FlatStyle.System;
            b.Margin = new Padding(3, 3, 8, 3);
            if (primary) b.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        }

        void AddRow(TableLayoutPanel grid, int row, Label state, Label text, Button button, string caption, EventHandler click)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            state.AutoSize = false;
            state.Size = new Size(24, 24);
            state.TextAlign = ContentAlignment.MiddleCenter;
            state.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            state.Anchor = AnchorStyles.Left;
            text.AutoSize = true;
            text.Anchor = AnchorStyles.Left;
            text.Margin = new Padding(3, 8, 3, 8);
            text.MaximumSize = new Size(520, 0);
            button.Text = caption;
            button.Width = 140;
            button.Height = 28;
            button.FlatStyle = FlatStyle.System;
            button.Anchor = AnchorStyles.Right;
            button.Click += click;
            grid.Controls.Add(state, 0, row);
            grid.Controls.Add(text, 1, row);
            grid.Controls.Add(button, 2, row);
        }

        static void Mark(Label state, bool ok)
        {
            state.Text = ok ? "✔" : "✖";
            state.ForeColor = ok ? Good : Bad;
        }

        // Re-check the folders (files may have been dropped in) and update the controls.
        void Refresh2()
        {
            try
            {
                status = backend.GetStatus();
            }
            catch (Exception e)
            {
                statusLine.Text = "Status check failed: " + e.Message;
                return;
            }
            bool running = backend.IsRunning;

            Mark(ipswState, status.Ipsw != null || status.RootReady);
            ipswText.Text = status.Ipsw != null
                ? "IPSW: " + Path.GetFileName(status.Ipsw) + (status.IpswInfo != null ? "  -  " + status.IpswInfo : "")
                : status.RootReady ? "IPSW: not needed any more (root image already built)"
                : "IPSW: put the iPhone X (iPhone10,3) iOS 16.7.16 IPSW in the IPSW folder";

            Mark(kernelState, status.Kernel != null);
            kernelText.Text = status.Kernel != null
                ? "Kernel: " + Path.GetFileName(status.Kernel)
                : "Kernel: put kernel.development.sweet (from the GitHub build) in the Kernel folder";

            Mark(qemuState, status.Qemu != null);
            qemuText.Text = status.Qemu != null ? "QEMU: " + status.Qemu : "QEMU: not found. Install it, or put it in a \"qemu\" folder next to this app";

            Mark(rootState, status.RootReady);
            rootText.Text = status.RootReady
                ? "iOS root image: ready"
                : preparing ? "iOS root image: building..." : "iOS root image: not built yet (Prepare reads the IPSW, takes a while)";
            prepareButton.Text = preparing ? "Cancel" : status.RootReady ? "Rebuild" : "Prepare";
            prepareButton.Enabled = preparing || (!running && status.Ipsw != null);

            bool canStart = status.RootReady && status.Kernel != null && status.Qemu != null && !preparing;
            startButton.Text = running ? "Stop" : "Start";
            startButton.Enabled = running || canStart;
            homeButton.Enabled = volUpButton.Enabled = volDownButton.Enabled = lockButton.Enabled = shotButton.Enabled = running;
            discard.Enabled = !running;
            if (!preparing && !running)
                statusLine.Text = canStart ? "Ready to start." : (status.Problem ?? "");
        }

        void Log(string line)
        {
            if (log.TextLength > 400000) log.Text = log.Text.Substring(log.TextLength - 200000);
            log.AppendText(line.Replace("\n", Environment.NewLine) + Environment.NewLine);
        }

        void OpenFolder(string path)
        {
            Directory.CreateDirectory(path);
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "Could not open " + path + ": " + e.Message, Text);
            }
        }

        void OpenIpswFolder(object sender, EventArgs e) { OpenFolder(backend.IpswFolder); }

        void OpenKernelFolder(object sender, EventArgs e) { OpenFolder(backend.KernelFolder); }

        void GetQemu(object sender, EventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = backend.QemuDownloadUrl, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Open " + backend.QemuDownloadUrl + " in your browser (" + ex.Message + ")", Text);
            }
        }

        void PrepareClicked(object sender, EventArgs e)
        {
            if (preparing)
            {
                if (prepareCancel != null) prepareCancel.Cancel();
                return;
            }
            if (status != null && status.RootReady &&
                MessageBox.Show(this, "Rebuild the iOS root image from the IPSW? Everything changed inside iOS is lost.",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            preparing = true;
            prepareCancel = new CancellationTokenSource();
            var token = prepareCancel.Token;
            progress.Value = 0;
            progress.Visible = true;
            statusLine.Text = "Preparing: reading the IPSW and building the iOS root image...";
            Log("--- Prepare ---");
            Refresh2();
            var worker = new Thread(delegate ()
            {
                string error = null;
                try
                {
                    backend.Prepare(settings,
                        s => BeginInvoke(new Action(delegate { Log(s); })),
                        f => BeginInvoke(new Action(delegate
                        {
                            progress.Value = Math.Max(0, Math.Min(1000, (int)(f * 1000)));
                            statusLine.Text = string.Format("Preparing: copying files, {0:0}%", f * 100);
                        })),
                        token);
                }
                catch (OperationCanceledException)
                {
                    error = "cancelled";
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                BeginInvoke(new Action(delegate { PrepareDone(error); }));
            });
            worker.IsBackground = true;
            progress.Maximum = 1000;
            worker.Start();
        }

        void PrepareDone(string error)
        {
            preparing = false;
            progress.Visible = false;
            if (error == null)
            {
                Log("Prepare finished.");
                statusLine.Text = "iOS root image ready.";
            }
            else
            {
                Log("Prepare failed: " + error);
                statusLine.Text = "Prepare failed: " + error;
                if (error != "cancelled") MessageBox.Show(this, "Prepare failed:\n\n" + error, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            Refresh2();
        }

        void StartClicked(object sender, EventArgs e)
        {
            if (backend.IsRunning)
            {
                statusLine.Text = "Stopping...";
                backend.Stop();
                SessionEnded();
                return;
            }
            settings.DiscardChanges = discard.Checked;
            try
            {
                Log("--- Start" + (settings.DiscardChanges ? " (discarding changes)" : "") + " ---");
                backend.Start(settings);
                serialPos = 0;
                statusLine.Text = "Running. The first boot sits on the Apple logo for a long time under emulation.";
            }
            catch (Exception ex)
            {
                Log("Start failed: " + ex.Message);
                MessageBox.Show(this, "Could not start:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            Refresh2();
        }

        void SessionEnded()
        {
            TailSerial();
            Log("--- Emulator stopped ---");
            statusLine.Text = "Emulator stopped.";
            Refresh2();
        }

        void Press(string button)
        {
            try
            {
                backend.PressButton(button);
            }
            catch (Exception ex)
            {
                Log("Button " + button + " failed: " + ex.Message);
            }
        }

        void ScreenshotClicked(object sender, EventArgs e)
        {
            string dir = Path.Combine(backend.HomeFolder, "Screenshots");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "iOS-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
            try
            {
                backend.Screenshot(path);
                Log("Screenshot: " + path);
            }
            catch (Exception ex)
            {
                Log("Screenshot failed: " + ex.Message);
            }
        }

        // Show new kernel console output (QEMU writes it to the serial log).
        void TailSerial()
        {
            string path = backend.SerialLogPath;
            if (!backend.IsRunning && serialPos == 0) return;
            try
            {
                if (!File.Exists(path)) return;
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (f.Length < serialPos) serialPos = 0;
                    if (f.Length == serialPos) return;
                    f.Position = serialPos;
                    var buf = new byte[Math.Min(256 * 1024, f.Length - serialPos)];
                    int n = f.Read(buf, 0, buf.Length);
                    serialPos += n;
                    string text = Encoding.UTF8.GetString(buf, 0, n).Replace("\r", "");
                    if (text.EndsWith("\n")) text = text.Substring(0, text.Length - 1);
                    Log(text);
                }
            }
            catch (IOException)
            {
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (preparing &&
                MessageBox.Show(this, "Prepare is still running. Stop it and quit?", Text, MessageBoxButtons.YesNo) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            if (prepareCancel != null) prepareCancel.Cancel();
            if (backend.IsRunning) backend.Stop();
            base.OnFormClosing(e);
        }
    }
}
