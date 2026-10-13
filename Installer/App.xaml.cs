using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Visor.Setup;

/// <summary>
/// Command line:
///   Visor-Setup.exe                         interactive install / update
///   Visor-Setup.exe /silent [/dir="D:\X"]   silent install (default folder unless /dir; "Visor" is appended)
///   Uninstall.exe  |  Visor-Setup.exe /uninstall [/silent]
/// Exit codes: 0 success, 1 failure, 2 nothing installed (uninstall).
/// </summary>
public partial class App : Application
{
    public const int ExitOk = 0, ExitFailed = 1, ExitNotInstalled = 2;

    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        var args = e.Args ?? new string[0];
        bool Has(params string[] names) =>
            args.Any(a => names.Any(n => a.Equals(n, StringComparison.OrdinalIgnoreCase)));

        bool silent = Has("/silent", "/s", "/quiet", "-silent");
        bool uninstall = Has("/uninstall", "-uninstall", "/remove") ||
            Path.GetFileNameWithoutExtension(Product.SelfPath)
                .Equals("Uninstall", StringComparison.OrdinalIgnoreCase);
        string? dirArg = args
            .Where(a => a.StartsWith("/dir=", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Substring(5).Trim('"'))
            .LastOrDefault();

        if (silent)
        {
            Shutdown(uninstall ? SilentUninstall() : SilentInstall(dirArg));
            return;
        }

        Window win = uninstall ? new UninstallWindow() : new SetupWindow();
        MainWindow = win;
        win.Show();
    }

    private static int SilentInstall(string? dir)
    {
        try
        {
            var o = new InstallOptions { TargetDir = dir ?? InstallerCore.FindInstallLocation() ?? Product.DefaultInstallDir };
            InstallerCore.Install(o, LegacyInstall.Detect(), null);
            return ExitOk;
        }
        catch { return ExitFailed; }
    }

    private static int SilentUninstall()
    {
        try
        {
            var r = InstallerCore.Uninstall(null);
            if (r.NotFound) return ExitNotInstalled;
            if (r.SelfToDelete != null) InstallerCore.ScheduleSelfDelete(r.SelfToDelete, r.Dir!);
            return ExitOk;
        }
        catch { return ExitFailed; }
    }

    /// <summary>Runs work on a background STA thread (COM shell objects are used for shortcuts).</summary>
    public static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}
