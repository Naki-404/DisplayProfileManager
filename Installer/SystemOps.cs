using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using Microsoft.Win32;

namespace Visor.Setup;

/// <summary>A running Visor (or legacy Display Profile Manager) process.</summary>
internal sealed class RunningApp
{
    public RunningApp(Process process, string displayName, string? path)
    {
        Process = process;
        DisplayName = displayName;
        Path = path;
    }

    public Process Process { get; }
    public string DisplayName { get; }
    public string? Path { get; }
}

internal static class AppProcesses
{
    public const string LegacyProcessName = "DisplayProfileManager";
    public const string LegacyDisplayName = "Display Profile Manager";

    /// <summary>
    /// Running Visor processes in the current session that are verifiably ours (image inside
    /// <paramref name="installDir"/> or published by Nakidev), plus, optionally, the legacy
    /// Display Profile Manager (image inside <paramref name="legacyDir"/> or a DPM binary).
    /// Processes whose image path cannot be read are never treated as ours.
    /// </summary>
    public static List<RunningApp> Find(string? installDir, bool includeLegacy, string? legacyDir = null)
    {
        var list = new List<RunningApp>();
        var me = Process.GetCurrentProcess();
        int self = me.Id, session = me.SessionId;

        foreach (var p in SafeGet("Visor"))
        {
            var path = OwnSessionPath(p, self, session);
            bool ours = path != null
                && ((installDir != null && PathGuard.IsInsideOrSame(path, installDir)) || IsNakidev(path));
            if (ours) list.Add(new RunningApp(p, Product.Name, path));
            else p.Dispose();
        }

        if (includeLegacy)
        {
            foreach (var p in SafeGet(LegacyProcessName))
            {
                var path = OwnSessionPath(p, self, session);
                bool ours = path != null
                    && ((legacyDir != null && PathGuard.IsInsideOrSame(path, legacyDir)) || IsLegacyBinary(path));
                if (ours) list.Add(new RunningApp(p, LegacyDisplayName, path));
                else p.Dispose();
            }
        }
        return list;
    }

    /// <summary>Image path of a process in our session (not ourselves); null otherwise.</summary>
    private static string? OwnSessionPath(Process p, int self, int session)
    {
        try
        {
            if (p.Id == self || p.SessionId != session) return null;
            return Native.GetProcessPath(p.Id);
        }
        catch { return null; }
    }

    public static string Names(IEnumerable<RunningApp> apps) =>
        string.Join(" / ", apps.Select(a => a.DisplayName).Distinct());

    /// <summary>
    /// Closes the given processes: CloseMainWindow, then the app's exit event, then kill after a timeout.
    /// Returns true if any process had to be killed. Throws <see cref="InstallerException"/> if a process
    /// survives (e.g. an elevated instance we may not terminate) — callers must not touch files then.
    /// </summary>
    public static bool CloseAll(IReadOnlyList<RunningApp> apps, Action<string>? log)
    {
        if (apps.Count == 0) return false;
        log?.Invoke(L.StClosing);
        try
        {
            foreach (var a in apps)
            {
                try { if (IsRunning(a.Process)) a.Process.CloseMainWindow(); } catch { }
            }

            // Graceful signal understood by Visor (exits after restoring the display).
            try
            {
                if (EventWaitHandle.TryOpenExisting(Product.ExitEventName, out var ev))
                    using (ev) ev.Set();
            }
            catch { }

            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline && apps.Any(a => IsRunning(a.Process)))
                Thread.Sleep(200);

            bool killed = false;
            foreach (var a in apps)
            {
                if (!IsRunning(a.Process)) continue;
                try { a.Process.Kill(); killed = true; } catch { }
                var killDeadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < killDeadline && IsRunning(a.Process))
                    Thread.Sleep(100);
                if (IsRunning(a.Process))
                    throw new InstallerException(L.CannotClose(a.DisplayName));
            }

            Thread.Sleep(300); // let the OS release file handles
            return killed;
        }
        finally
        {
            foreach (var a in apps) a.Process.Dispose();
        }
    }

    /// <summary>
    /// Conservative liveness check: anything we cannot prove has exited counts as running
    /// (an access-denied answer for an elevated process must not look like "exited").
    /// </summary>
    private static bool IsRunning(Process p)
    {
        try { return !p.HasExited; }
        catch { return Native.IsProcessRunning(p.Id); }
    }

    private static Process[] SafeGet(string name)
    {
        try { return Process.GetProcessesByName(name); } catch { return Array.Empty<Process>(); }
    }

    private static bool IsLegacyBinary(string path)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            bool Dpm(string? s) => s != null &&
                (s.IndexOf("DisplayProfileManager", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 s.IndexOf("Display Profile Manager", StringComparison.OrdinalIgnoreCase) >= 0);
            return Dpm(vi.ProductName) || Dpm(vi.FileDescription) || Dpm(vi.InternalName) ||
                   string.Equals(vi.CompanyName, Product.Publisher, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    private static bool IsNakidev(string path)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            return string.Equals(vi.CompanyName, Product.Publisher, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

/// <summary>Display restore on uninstall. The app owns the real restore logic; this only drives it.</summary>
internal static class DisplayRestore
{
    /// <summary>True if Visor left an active-session snapshot (a profile is/was applied).</summary>
    public static bool HasActiveSession() =>
        File.Exists(Path.Combine(Product.AppDataDir, "active-session.json"));

    /// <summary>True if the legacy app has a game profile applied right now (its own snapshot).</summary>
    public static bool HasLegacyActiveSession() =>
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                 "DisplayProfileManager", "active-session.json"));

    /// <summary>Runs "&lt;exe&gt; --emergency" hidden and waits up to 30 s (best effort).</summary>
    public static void RunEmergency(string exe, Action<string>? log)
    {
        try
        {
            if (!File.Exists(exe)) return;
            log?.Invoke(L.StRestore);
            var psi = new ProcessStartInfo(exe, "--emergency")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(exe)!
            };
            using var p = Process.Start(psi);
            if (p != null && !p.WaitForExit(30000))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Restores the display after the app was closed:
    /// snapshot present → run "Visor.exe --emergency" (the app restores from its snapshot);
    /// no snapshot but the app had to be killed → only reset gamma on all monitors;
    /// otherwise nothing (the app already restored on a clean exit). Never touches the power plan.
    /// </summary>
    public static void AfterAppClosed(string? appExe, bool appWasKilled, Action<string>? log)
    {
        try
        {
            if (HasActiveSession())
            {
                // During install/upgrade there is no app exe to drive; the app restores from its
                // snapshot on the next start, so leave the ramp alone (keeps ICC calibration intact).
                if (appExe != null) RunEmergency(appExe, log);
            }
            else if (appWasKilled)
            {
                log?.Invoke(L.StRestore);
                Native.ResetGammaAllMonitors();
            }
        }
        catch { /* best effort */ }
    }
}

/// <summary>Shortcut, autostart and Apps &amp; Features helpers.</summary>
internal static class SystemOps
{
    /// <summary>Deletes a shortcut only if it points at <paramref name="matches"/>.</summary>
    public static void DeleteShortcutIf(string lnk, Func<string, bool> matches)
    {
        try
        {
            var target = Native.GetShortcutTarget(lnk);
            if (target != null && matches(target)) File.Delete(lnk);
        }
        catch { }
    }

    /// <summary>Deletes scheduled task <paramref name="taskName"/> only if its definition mentions <paramref name="needle"/>.</summary>
    public static void DeleteTaskIfReferences(string taskName, string needle)
    {
        try
        {
            var (code, xml) = Run("schtasks.exe", "/Query /TN \"" + taskName + "\" /XML");
            if (code != 0 || string.IsNullOrEmpty(xml)) return;
            xml = xml.Replace("\0", "");
            if (xml.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0 &&
                xml.IndexOf(SecurityElement.Escape(needle) ?? needle, StringComparison.OrdinalIgnoreCase) < 0)
                return;
            Run("schtasks.exe", "/Delete /F /TN \"" + taskName + "\"");
        }
        catch { }
    }

    /// <summary>Removes HKCU Run values whose command launches exactly <paramref name="exePath"/>.</summary>
    public static void DeleteRunValuesFor(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Product.RunKey, writable: true);
            if (key == null) return;
            foreach (var name in key.GetValueNames())
            {
                var target = key.GetValue(name) is string cmd ? CommandExe(cmd) : null;
                if (target != null && PathGuard.SamePath(target, exePath))
                {
                    try { key.DeleteValue(name, false); } catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>Executable part of a command line: the quoted first token, or the text up to ".exe".</summary>
    private static string? CommandExe(string cmd)
    {
        var s = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (s.StartsWith("\""))
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s.Substring(1, end - 1) : null;
        }
        int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? s.Substring(0, exe + 4) : null;
    }

    /// <summary>Deletes an Apps and Features key only if its InstallLocation is <paramref name="dir"/>.</summary>
    public static void DeleteArpKeyIfFor(string keyName, string dir)
    {
        try
        {
            var loc = PathGuard.Normalize(ReadArpInstallLocation(keyName));
            if (loc != null && PathGuard.SamePath(loc, dir)) DeleteArpKey(keyName);
        }
        catch { }
    }

    public static string? ReadArpInstallLocation(string keyName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Product.UninstallRoot + keyName);
            return key?.GetValue("InstallLocation") as string;
        }
        catch { return null; }
    }

    public static bool ArpKeyExists(string keyName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Product.UninstallRoot + keyName);
            return key != null;
        }
        catch { return false; }
    }

    public static void DeleteArpKey(string keyName)
    {
        Registry.CurrentUser.DeleteSubKeyTree(Product.UninstallRoot + keyName, false);
    }

    public static void WriteArpKey(string dir)
    {
        var exe = Path.Combine(dir, Product.ExeName);
        var uninst = Path.Combine(dir, Product.UninstallerName);
        using var key = Registry.CurrentUser.CreateSubKey(Product.UninstallRoot + Product.ArpKeyName)
            ?? throw new InvalidOperationException("Registry key could not be created.");
        key.SetValue("DisplayName", Product.Name);
        key.SetValue("DisplayVersion", Product.Version);
        key.SetValue("Publisher", Product.Publisher);
        key.SetValue("InstallLocation", dir);
        key.SetValue("DisplayIcon", exe + ",0");
        key.SetValue("UninstallString", "\"" + uninst + "\" /uninstall");
        key.SetValue("QuietUninstallString", "\"" + uninst + "\" /uninstall /silent");
        key.SetValue("URLInfoAbout", Product.Url);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try
        {
            long bytes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            key.SetValue("EstimatedSize", (int)Math.Min(bytes / 1024, int.MaxValue), RegistryValueKind.DWord);
        }
        catch { }
    }

    private static (int code, string output) Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return (-1, ""); }
        return (p.ExitCode, stdoutTask.Result);
    }
}
