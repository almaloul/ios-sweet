// SweetEmu: Windows app entry point (.NET 8 Windows Forms).
using System;
using System.IO;
using System.Windows.Forms;
using SweetEmu.Core;

namespace SweetEmu
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(new AppBackend(HomeFolder())));
        }

        /// The folder next to SweetEmu.exe (IPSW, Kernel, qemu, Data live there),
        /// or %LOCALAPPDATA%\SweetEmu when the app sits somewhere read-only.
        static string HomeFolder()
        {
            string exeDir = AppContext.BaseDirectory;
            try
            {
                string probe = Path.Combine(exeDir, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return exeDir;
            }
            catch (Exception e) when (e is UnauthorizedAccessException || e is IOException)
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SweetEmu");
            }
        }
    }
}
