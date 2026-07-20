using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace DisplayProfileManager.Setup;

internal static class InstallerCore
{
    private const string AppName = "Display Profile Manager";
    private const string AppId = "DisplayProfileManager";
    private const string Version = "1.6.0";
    private const string Publisher = "Nakidev";
    private const string ExeName = "DisplayProfileManager.exe";

    /// <summary>Only these files are ever installed (no screenshots, docs, extras).</summary>
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "DisplayProfileManager.exe",
        "QRes.exe"
    };

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

        // Remove leftover junk from older broken installs in this folder
        foreach (var file in Directory.GetFiles(installDir))
        {
            var name = Path.GetFileName(file);
            if (!AllowedFiles.Contains(name) &&
                !name.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(file); } catch { /* ignore */ }
            }
        }
        foreach (var sub in Directory.GetDirectories(installDir))
        {
            try { Directory.Delete(sub, true); } catch { /* ignore */ }
        }

        log?.Invoke("Extracting application…");
        var extracted = ExtractPayloadWhitelisted(installDir);
        if (!extracted.Any(f => f.Equals(ExeName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Installer payload is missing DisplayProfileManager.exe. Rebuild with build-release.ps1");

        foreach (var name in extracted)
            TryUnblock(Path.Combine(installDir, name));

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

        // Fallback: folder next to this Uninstall.exe
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
            // brief pause so gamma APIs aren't fighting a dying process
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
        // Delete everything except the running Uninstall.exe (locked). Rest cleaned by ScheduleSelfCleanup.
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

    /// <summary>After Uninstall.exe exits, remove remaining folder (including itself).</summary>
    internal static void ScheduleSelfCleanup(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
            return;

        try
        {
            var self = Environment.ProcessPath;
            var bat = Path.Combine(Path.GetTempPath(), $"dpm-cleanup-{Guid.NewGuid():N}.cmd");
            // ping delay is more reliable than timeout under some locales
            var lines = new List<string>
            {
                "@echo off",
                "ping 127.0.0.1 -n 3 >nul"
            };
            if (!string.IsNullOrWhiteSpace(self))
            {
                lines.Add($"del /f /q \"{self}\" >nul 2>&1");
            }
            lines.Add($"rmdir /s /q \"{installDir}\" >nul 2>&1");
            // second pass if first failed due to locks
            lines.Add("ping 127.0.0.1 -n 2 >nul");
            if (!string.IsNullOrWhiteSpace(self))
                lines.Add($"del /f /q \"{self}\" >nul 2>&1");
            lines.Add($"rmdir /s /q \"{installDir}\" >nul 2>&1");
            lines.Add("del \"%~f0\" >nul 2>&1");

            File.WriteAllLines(bat, lines);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{bat}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { }
    }

    private static List<string> ExtractPayloadWhitelisted(string dir)
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames()
            .Where(n => n.Contains(".payload.", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (names.Count == 0)
            throw new InvalidOperationException("Installer payload is empty. Rebuild with build-release.ps1");

        var written = new List<string>();
        foreach (var resName in names)
        {
            var marker = ".payload.";
            var idx = resName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            var relative = idx >= 0 ? resName[(idx + marker.Length)..] : Path.GetFileName(resName);
            relative = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

            // Prefer exact whitelist match at end of resource name (handles nested junk)
            string? fileName = AllowedFiles.FirstOrDefault(a =>
                relative.Equals(a, StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(Path.DirectorySeparatorChar + a, StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith("." + a, StringComparison.OrdinalIgnoreCase));

            if (fileName == null)
                continue;

            var dest = Path.Combine(dir, fileName);
            using var src = asm.GetManifestResourceStream(resName)!;
            using var dst = File.Create(dest);
            src.CopyTo(dst);
            written.Add(fileName);
        }

        return written.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
