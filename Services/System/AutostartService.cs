using System.Diagnostics;
using Microsoft.Win32;

namespace Visor.Services;

/// <summary>
/// "Start with Windows" via HKCU\Software\Microsoft\Windows\CurrentVersion\Run (no admin,
/// no scheduled task). Starts minimized to tray.
/// </summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Visor";
    /// <summary>Command-line switch App.xaml.cs treats as "start in tray".</summary>
    public const string StartupArgument = "--minimized";

    /// <summary>Display Profile Manager used a scheduled task (and early builds a Run value).</summary>
    private const string LegacyTaskName = "DisplayProfileManager";
    private static readonly string[] LegacyRunValues = { "DisplayProfileManager", "Display Profile Manager" };

    /// <summary>Adds / removes the Run entry.</summary>
    /// <returns>True when the registry now matches <paramref name="enabled"/>.</returns>
    public static bool SetEnabled(bool enabled, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                AppLog.Error("Autostart: cannot open the Run key.");
                return false;
            }

            RemoveLegacyRunValues(key);

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                AppLog.Info("Autostart disabled.");
                return true;
            }

            if (!PathSecurity.IsSafeAutostartExe(exePath, out var full))
            {
                AppLog.Error("Autostart rejected: unsafe or missing exe path.");
                return false;
            }

            string command = $"\"{full}\" {StartupArgument}";
            if (!string.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, command, RegistryValueKind.String);
                AppLog.Info("Autostart enabled.");
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Autostart update failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// One-time cleanup after the rename: delete the old "DisplayProfileManager" scheduled task
    /// and Run values. Slow (spawns schtasks) — call off the UI thread.
    /// </summary>
    /// <returns>False when the old task exists but could not be deleted.</returns>
    public static bool MigrateLegacy()
    {
        bool ok = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key != null) RemoveLegacyRunValues(key);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Autostart legacy Run cleanup");
            ok = false;
        }

        try
        {
            if (RunSchtasks("/Query", "/TN", LegacyTaskName) != 0)
                return ok; // no legacy task

            int rc = RunSchtasks("/Delete", "/F", "/TN", LegacyTaskName);
            if (rc == 0)
            {
                AppLog.Info("Removed the old Display Profile Manager autostart task.");
            }
            else
            {
                AppLog.Warn($"Could not remove the old autostart task (schtasks exit {rc}).");
                ok = false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Autostart legacy task cleanup");
            ok = false;
        }
        return ok;
    }

    private static void RemoveLegacyRunValues(RegistryKey key)
    {
        foreach (var name in LegacyRunValues)
        {
            if (key.GetValue(name) == null) continue;
            key.DeleteValue(name, throwOnMissingValue: false);
            AppLog.Info($"Removed legacy autostart value '{name}'.");
        }
    }

    private static int RunSchtasks(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            CreateNoWindow = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        if (p == null) return -1;
        if (!p.WaitForExit(8000))
        {
            try { p.Kill(); } catch (InvalidOperationException) { /* exited */ }
            return -1;
        }
        return p.ExitCode;
    }
}
