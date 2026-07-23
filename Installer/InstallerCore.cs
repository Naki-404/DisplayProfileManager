using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace DisplayProfileManager.Setup;

internal static class InstallerCore
{
    private const string AppName = "Display Profile Manager";
    private const string AppId = "DisplayProfileManager";
    private const string Version = "1.8.0";
    private const string Publisher = "Nakidev";
    private const string ExeName = "DisplayProfileManager.exe";
    private const string PayloadResourceHint = "payload.zip";

    private const int MoveFileDelayUntilReboot = 0x4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

    internal static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppId);

    /// <summary>
    /// Always install into a dedicated DisplayProfileManager folder.
    /// If the user picks Desktop / a parent path, append the app folder name.
    /// </summary>
    internal static string ResolveInstallDir(string selectedPath)
    {
        var path = (selectedPath ?? "").Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(path))
            return DefaultInstallDir;

        var name = Path.GetFileName(path);
        if (name.Equals(AppId, StringComparison.OrdinalIgnoreCase) ||
            name.Equals(AppName, StringComparison.OrdinalIgnoreCase))
            return path;

        return Path.Combine(path, AppId);
    }

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

    /// <summary>File IO only — safe on a background thread. No COM / Registry / UI.</summary>
    internal static void ExtractAppFiles(string installDir, Action<string>? log = null)
    {
        Directory.CreateDirectory(installDir);
        log?.Invoke("Preparing folder…");

        foreach (var file in Directory.GetFiles(installDir))
        {
            var name = Path.GetFileName(file);
            if (!name.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch { /* ignore */ }
            }
        }
        foreach (var sub in Directory.GetDirectories(installDir))
        {
            try { Directory.Delete(sub, true); } catch { /* ignore */ }
        }

        log?.Invoke("Extracting application…");
        ExtractPayloadZip(installDir);
        var exe = Path.Combine(installDir, ExeName);
        if (!File.Exists(exe))
            throw new InvalidOperationException("Installer payload is missing DisplayProfileManager.exe. Rebuild with build-release.ps1");

        TryUnblockTree(installDir);
        log?.Invoke("Files ready.");
    }

    /// <summary>Registry + shortcuts — must run on STA / UI thread.</summary>
    internal static void RegisterInstallation(string installDir, bool startMenu, bool desktop, Action<string>? log = null)
    {
        var exe = Path.Combine(installDir, ExeName);
        if (!File.Exists(exe))
            throw new FileNotFoundException("Application exe not found after extract.", exe);

        log?.Invoke("Writing Uninstall.exe…");
        var self = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate setup executable path.");
        var uninst = Path.Combine(installDir, "Uninstall.exe");
        File.Copy(self, uninst, true);
        TryUnblock(uninst);

        log?.Invoke("Registering in Apps & Features…");
        WriteUninstallKey(installDir, exe, uninst);

        if (startMenu)
        {
            log?.Invoke("Start Menu shortcut…");
            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", AppName + ".lnk"),
                exe, installDir);
        }

        if (desktop)
        {
            log?.Invoke("Desktop shortcut…");
            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"),
                exe, installDir);
        }

        log?.Invoke("Installation complete.");
    }

    internal static string? FindInstallLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}");
            var dir = key?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                return dir;
        }
        catch { }

        if (Directory.Exists(DefaultInstallDir))
            return DefaultInstallDir;

        try
        {
            var beside = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrWhiteSpace(beside) &&
                File.Exists(Path.Combine(beside, ExeName)))
                return beside;
        }
        catch { }

        return null;
    }

    /// <summary>Remove app files, shortcuts, ARP entry. Restores display first. Does not show UI.</summary>
    internal static void PerformUninstall(bool confirmUi, bool silent, Action<string>? log = null)
    {
        _ = confirmUi;
        var dir = FindInstallLocation() ?? DefaultInstallDir;

        log?.Invoke("Closing Display Profile Manager…");
        try
        {
            foreach (var p in Process.GetProcessesByName("DisplayProfileManager"))
            {
                try { p.CloseMainWindow(); p.WaitForExit(2000); } catch { }
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { try { p.Kill(); } catch { } }
            }
            Thread.Sleep(400);
        }
        catch { }

        DisplayRestore.RestoreNeutralDisplay(log);

        log?.Invoke("Removing autostart task…");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                ArgumentList = { "/Delete", "/F", "/TN", "DisplayProfileManager" },
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
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

        log?.Invoke("Unregistering from Apps & Features…");
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}", false);
        }
        catch { }

        log?.Invoke("Deleting application files…");
        if (Directory.Exists(dir))
        {
            var self = Path.GetFullPath(Environment.ProcessPath ?? "");
            foreach (var file in Directory.GetFiles(dir))
            {
                try
                {
                    if (string.Equals(Path.GetFullPath(file), self, StringComparison.OrdinalIgnoreCase))
                        continue;
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch { }
            }
            foreach (var sub in Directory.GetDirectories(dir))
            {
                try { Directory.Delete(sub, true); } catch { }
            }
        }

        log?.Invoke("Finishing…");
        _ = silent;
    }

    /// <summary>
    /// Schedule remaining locked files (Uninstall.exe + folder) for deletion on reboot.
    /// Avoids spawning hidden cmd cleanup scripts — those look like droppers to AV heuristics.
    /// </summary>
    internal static void ScheduleSelfCleanup(string? installDir)
    {
        try
        {
            var self = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(self) && File.Exists(self))
                MoveFileEx(self, null, MoveFileDelayUntilReboot);
        }
        catch { }

        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            return;

        try
        {
            foreach (var file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
            {
                try { MoveFileEx(file, null, MoveFileDelayUntilReboot); } catch { }
            }
            try { MoveFileEx(installDir, null, MoveFileDelayUntilReboot); } catch { }
        }
        catch { }
    }

    private static void ExtractPayloadZip(string dir)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(PayloadResourceHint, StringComparison.OrdinalIgnoreCase))
            ?? asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("payload", StringComparison.OrdinalIgnoreCase));

        if (resName == null)
            throw new InvalidOperationException("Installer payload is empty. Rebuild with build-release.ps1");

        using var src = asm.GetManifestResourceStream(resName)
            ?? throw new InvalidOperationException("Cannot open installer payload stream.");
        using var zip = new ZipArchive(src, ZipArchiveMode.Read);

        var rootFull = Path.GetFullPath(dir).TrimEnd('\\') + '\\';
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName) || entry.FullName.EndsWith('/'))
                continue;

            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                continue;
            if (!IsAllowedPayloadPath(relative))
                continue;

            var dest = Path.GetFullPath(Path.Combine(dir, relative));
            if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(dest.TrimEnd('\\'), rootFull.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                continue;

            var parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            using var entryStream = entry.Open();
            using var dst = File.Create(dest);
            entryStream.CopyTo(dst);
        }
    }

    private static bool IsAllowedPayloadPath(string relative)
    {
        var name = Path.GetFileName(relative);
        if (name.Equals(ExeName, StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("QRes.exe", StringComparison.OrdinalIgnoreCase)) return true;

        var norm = relative.Replace('/', '\\');
        return norm.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteUninstallKey(string dir, string exe, string uninst)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}")!;
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("InstallLocation", dir);
        key.SetValue("DisplayIcon", exe);
        key.SetValue("UninstallString", $"\"{uninst}\" /uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninst}\" /uninstall /silent");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try
        {
            var sizeKb = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length) / 1024;
            key.SetValue("EstimatedSize", (int)Math.Min(sizeKb, int.MaxValue), RegistryValueKind.DWord);
        }
        catch { }
    }

    private static void CreateShortcut(string lnkPath, string target, string workDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            var sc = shell.CreateShortcut(lnkPath);
            sc.TargetPath = target;
            sc.WorkingDirectory = workDir;
            sc.Description = AppName + " by " + Publisher;
            sc.IconLocation = target + ",0";
            sc.Save();
            Marshal.FinalReleaseComObject(sc);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void TryUnblockTree(string dir)
    {
        try
        {
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                TryUnblock(file);
        }
        catch { }
    }

    private static void TryUnblock(string path)
    {
        try
        {
            var zone = path + ":Zone.Identifier";
            if (File.Exists(zone))
                File.Delete(zone);
        }
        catch { /* ignore */ }
    }
}
