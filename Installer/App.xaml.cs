using System.IO;
using System.Windows;

namespace DisplayProfileManager.Setup;

public partial class App : System.Windows.Application
{
    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        var args = e.Args ?? Array.Empty<string>();
        bool silent = args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase));
        bool uninstallArg = args.Any(a =>
            a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/remove", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-uninstall", StringComparison.OrdinalIgnoreCase));

        // Copied beside the app as Uninstall.exe — always open uninstall UI (not setup)
        bool isUninstallExe = false;
        try
        {
            var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
            isUninstallExe = name.Equals("Uninstall", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("DisplayProfileManager-Uninstall", StringComparison.OrdinalIgnoreCase);
        }
        catch { }

        if (uninstallArg || isUninstallExe)
        {
            if (silent)
            {
                var dir = InstallerCore.FindInstallLocation();
                InstallerCore.PerformUninstall(confirmUi: false, silent: true);
                InstallerCore.ScheduleSelfCleanup(dir);
                Shutdown();
                return;
            }

            var win = new UninstallWindow();
            MainWindow = win;
            win.Show();
            return;
        }

        var setup = new SetupWindow();
        MainWindow = setup;
        setup.Show();
    }
}
