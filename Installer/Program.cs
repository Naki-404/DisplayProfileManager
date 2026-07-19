using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DisplayProfileManager.Setup;

internal static class Program
{
    private const string AppName = "Display Profile Manager";
    private const string AppId = "DisplayProfileManager";
    private const string Version = "1.4.0";
    private const string ExeName = "DisplayProfileManager.exe";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            Uninstall(silent: args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase)));
            return;
        }

        Application.Run(new SetupForm());
    }

    internal static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppId);

    internal static bool HasDesktopRuntime()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "shared", "Microsoft.WindowsDesktop.App")
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith("6.", StringComparison.Ordinal) ||
                    name.StartsWith("7.", StringComparison.Ordinal) ||
                    name.StartsWith("8.", StringComparison.Ordinal) ||
                    name.StartsWith("9.", StringComparison.Ordinal) ||
                    name.StartsWith("10.", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    internal static void OpenRuntimeDownload()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://aka.ms/dotnet/6.0/windowsdesktop-runtime-win-x64.exe",
            UseShellExecute = true
        });
    }

    internal static void Install(string dir, bool startMenu, bool desktop, bool launch, Action<string> log)
    {
        Directory.CreateDirectory(dir);
        log("Extracting files…");
        ExtractPayload(dir);

        var exe = Path.Combine(dir, ExeName);
        if (!File.Exists(exe))
            throw new InvalidOperationException("Payload missing DisplayProfileManager.exe");

        log("Registering uninstall…");
        WriteUninstallKey(dir, exe);

        if (startMenu)
        {
            log("Start Menu shortcut…");
            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", AppName + ".lnk"),
                exe, dir);
        }

        if (desktop)
        {
            log("Desktop shortcut…");
            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"),
                exe, dir);
        }

        log("Done.");
        if (launch)
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
    }

    internal static void Uninstall(bool silent)
    {
        string? dir = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}");
            dir = key?.GetValue("InstallLocation") as string;
        }
        catch { }

        dir ??= DefaultInstallDir;

        try
        {
            // stop running instance
            foreach (var p in Process.GetProcessesByName("DisplayProfileManager"))
            {
                try { p.CloseMainWindow(); p.WaitForExit(1500); } catch { }
                try { if (!p.HasExited) p.Kill(); } catch { }
            }
        }
        catch { }

        try
        {
            var start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", AppName + ".lnk");
            var desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");
            if (File.Exists(start)) File.Delete(start);
            if (File.Exists(desk)) File.Delete(desk);
        }
        catch { }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}", false);
        }
        catch { }

        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        catch (Exception ex)
        {
            if (!silent)
                MessageBox.Show("Could not remove all files:\n" + ex.Message, AppName);
            return;
        }

        if (!silent)
            MessageBox.Show(AppName + " was uninstalled.", AppName);
    }

    private static void ExtractPayload(string dir)
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames()
            .Where(n => n.Contains("payload", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (names.Count == 0)
            throw new InvalidOperationException("Installer payload is empty. Rebuild with build-release.ps1");

        foreach (var name in names)
        {
            // Embedded as Installer.payload.DisplayProfileManager.exe etc.
            var marker = ".payload.";
            var idx = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            var relative = idx >= 0 ? name[(idx + marker.Length)..] : Path.GetFileName(name);
            relative = relative.Replace('/', Path.DirectorySeparatorChar);
            var dest = Path.Combine(dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var src = asm.GetManifestResourceStream(name)!;
            using var dst = File.Create(dest);
            src.CopyTo(dst);
        }
    }

    private static void WriteUninstallKey(string dir, string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}")!;
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", AppName);
        key.SetValue("InstallLocation", dir);
        key.SetValue("DisplayIcon", exe);
        key.SetValue("UninstallString", $"\"{Path.Combine(dir, "Uninstall.exe")}\" /uninstall");
        key.SetValue("QuietUninstallString", $"\"{Path.Combine(dir, "Uninstall.exe")}\" /uninstall /silent");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try
        {
            var sizeKb = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length) / 1024;
            key.SetValue("EstimatedSize", (int)sizeKb, RegistryValueKind.DWord);
        }
        catch { }

        // Copy this setup as Uninstall.exe beside the app
        var self = Environment.ProcessPath!;
        var uninst = Path.Combine(dir, "Uninstall.exe");
        File.Copy(self, uninst, true);
    }

    private static void CreateShortcut(string lnkPath, string target, string workDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        // WScript.Shell COM — no extra deps
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        var sc = shell.CreateShortcut(lnkPath);
        sc.TargetPath = target;
        sc.WorkingDirectory = workDir;
        sc.Description = AppName;
        sc.Save();
        Marshal.FinalReleaseComObject(sc);
        Marshal.FinalReleaseComObject(shell);
    }
}
