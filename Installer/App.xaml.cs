using System.Windows;

namespace DisplayProfileManager.Setup;

public partial class App : System.Windows.Application
{
    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        var args = e.Args;
        if (args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            InstallerCore.Uninstall(silent: args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase)));
            Shutdown();
            return;
        }

        var win = new SetupWindow();
        MainWindow = win;
        win.Show();
    }
}
