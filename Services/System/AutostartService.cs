using System.Diagnostics;
using System.IO;
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

    /// <summary>Marker in the config folder: the legacy task removal was attempted (success or not).</summary>
    private const string LegacyTaskMarkerName = "legacy-autostart-task.done";
    private const int SchtasksTimeoutMs = 5000;
    /// <summary>Inconclusive runs (schtasks timed out / did not start) are retried on later starts, this many times in all.</summary>
    internal const int MaxLegacyMigrationAttempts = 3;
    /// <summary>schtasks exit code <see cref="RunSchtasks"/> reports when the process could not start or finish.</summary>
    private const int SchtasksNotRun = -1;

    /// <summary>Outcome of one legacy-task cleanup run.</summary>
    internal enum LegacyMigration
    {
        /// <summary>Old task removed, or it never existed. Final.</summary>
        Done,
        /// <summary>schtasks answered and refused (needs elevation). Final — Visor will not try again.</summary>
        Failed,
        /// <summary>schtasks did not start or did not finish within the timeout — nothing is known; retry later.</summary>
        Inconclusive
    }

    /// <summary>
    /// One-time cleanup after the rename: delete the old "DisplayProfileManager" scheduled task
    /// and Run values. A marker file in the config folder records the outcome: "removed-or-absent"
    /// and "failed-final" (schtasks refused — the old task may have been created with highest
    /// privileges, and Visor never asks for elevation) end the matter; a timed-out / unstartable
    /// schtasks is recorded as "failed" with the attempt count and retried on a later start, at
    /// most <see cref="MaxLegacyMigrationAttempts"/> times in all. The schtasks error text is
    /// logged once at WARN. Slow (spawns schtasks, 5 s timeout) — call off the UI thread.
    /// </summary>
    /// <returns>False when the old task exists but could not be deleted (or it is still unknown).</returns>
    public static bool MigrateLegacyOnce(string configDirectory)
    {
        string marker = Path.Combine(configDirectory, LegacyTaskMarkerName);
        string? markerText = null;
        try
        {
            if (File.Exists(marker)) markerText = File.ReadAllText(marker);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Autostart legacy marker check");
        }

        if (!ShouldAttemptLegacyMigration(markerText, out int attempts))
            return markerText?.Contains("removed-or-absent", StringComparison.Ordinal) == true;

        var outcome = MigrateLegacyCore();
        attempts++;

        string status = outcome switch
        {
            LegacyMigration.Done => "removed-or-absent",
            LegacyMigration.Failed => "failed-final",
            _ => attempts >= MaxLegacyMigrationAttempts ? "failed-final" : "failed"
        };
        if (outcome == LegacyMigration.Inconclusive)
        {
            AppLog.Warn(attempts >= MaxLegacyMigrationAttempts
                ? $"Legacy autostart task check gave no answer {attempts} times — giving up."
                : $"Legacy autostart task check gave no answer (attempt {attempts}/{MaxLegacyMigrationAttempts}) — will retry on a later start.");
        }

        try
        {
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(marker, $"{DateTime.UtcNow:o} {status} attempt={attempts}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Autostart legacy marker write"); // next start retries once more — acceptable
        }
        return outcome == LegacyMigration.Done;
    }

    /// <summary>
    /// Reads the marker: no marker → attempt 0; "removed-or-absent" / "failed-final" → never again;
    /// "failed attempt=N" → again while N is below <see cref="MaxLegacyMigrationAttempts"/>.
    /// Unreadable / legacy markers ("failed" without a count) count as one attempt.
    /// </summary>
    internal static bool ShouldAttemptLegacyMigration(string? markerText, out int attempts)
    {
        attempts = 0;
        if (string.IsNullOrWhiteSpace(markerText)) return true;
        var tokens = markerText.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        string status = tokens.Length > 1 ? tokens[1] : tokens[0];
        if (status is "removed-or-absent" or "failed-final") return false;

        attempts = 1;
        foreach (var t in tokens)
        {
            if (t.StartsWith("attempt=", StringComparison.Ordinal) && int.TryParse(t[8..], out int n) && n > 0)
                attempts = n;
        }
        return attempts < MaxLegacyMigrationAttempts;
    }

    /// <summary>The cleanup itself (no marker). Prefer <see cref="MigrateLegacyOnce"/>.</summary>
    public static bool MigrateLegacy() => MigrateLegacyCore() == LegacyMigration.Done;

    internal static LegacyMigration MigrateLegacyCore()
    {
        var outcome = LegacyMigration.Done;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key != null) RemoveLegacyRunValues(key);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Autostart legacy Run cleanup");
            outcome = LegacyMigration.Failed;
        }

        try
        {
            var query = RunSchtasks("/Query", "/TN", LegacyTaskName);
            if (query.ExitCode == SchtasksNotRun)
                return LegacyMigration.Inconclusive; // timed out / did not start — we know nothing yet
            if (query.ExitCode != 0)
                return outcome; // schtasks answered: no legacy task

            var del = RunSchtasks("/Delete", "/F", "/TN", LegacyTaskName);
            if (del.ExitCode == 0)
            {
                AppLog.Info("Removed the old Display Profile Manager autostart task.");
            }
            else if (del.ExitCode == SchtasksNotRun)
            {
                AppLog.Warn($"Deleting the old '{LegacyTaskName}' scheduled task gave no answer"
                            + (del.Error.Length > 0 ? $" ({del.Error})" : "") + ".");
                return LegacyMigration.Inconclusive;
            }
            else
            {
                AppLog.Warn($"Could not remove the old '{LegacyTaskName}' scheduled task (schtasks exit {del.ExitCode}"
                            + (del.Error.Length > 0 ? $": {del.Error}" : "")
                            + "). It may require elevation — delete it in Task Scheduler if it bothers you; Visor will not try again.");
                outcome = LegacyMigration.Failed;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Autostart legacy task cleanup");
            outcome = LegacyMigration.Failed;
        }
        return outcome;
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

    /// <summary>
    /// Runs schtasks (absolute System32 path — CreateProcess would otherwise look in the app
    /// folder first). stdout / stderr are drained asynchronously so a full pipe can never block
    /// the child; a schtasks that does not finish within 5 s is killed.
    /// </summary>
    /// <returns>Exit code (-1 when it could not start / timed out) and the trimmed stderr+stdout text.</returns>
    private static (int ExitCode, string Error) RunSchtasks(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (-1, "schtasks did not start");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(SchtasksTimeoutMs))
            {
                try { p.Kill(); p.WaitForExit(1000); } catch (Exception ex) { AppLog.Warn(ex, "schtasks kill"); }
                return (-1, $"schtasks did not finish within {SchtasksTimeoutMs / 1000} s (killed)");
            }
            // Pipes close with the process; bounded wait so a stray child holding them cannot hang us.
            Task.WaitAll(new Task[] { stdout, stderr }, 1000);
            string text = Condense(stderr.IsCompletedSuccessfully ? stderr.Result : "");
            if (text.Length == 0)
                text = Condense(stdout.IsCompletedSuccessfully ? stdout.Result : "");
            return (p.ExitCode, text);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "schtasks start");
            return (-1, ex.Message);
        }
    }

    /// <summary>schtasks output as one short line (it pads with blank lines and CRLFs).</summary>
    private static string Condense(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var line = string.Join(" | ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0));
        return line.Length > 300 ? line[..300] + "…" : line;
    }
}
