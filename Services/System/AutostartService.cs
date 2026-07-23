using System.Diagnostics;

namespace DisplayProfileManager.Services;

public static class AutostartService
{
    public const string TaskName = "DisplayProfileManager";

    public static bool IsEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/Query");
            psi.ArgumentList.Add("/TN");
            psi.ArgumentList.Add(TaskName);

            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled, string exePath)
    {
        try
        {
            if (enabled)
            {
                if (!PathSecurity.IsSafeAutostartExe(exePath, out var full))
                {
                    AppLog.Error("Autostart rejected: unsafe or missing exe path.");
                    return;
                }

                // /TR must be one argument; path validated to contain no quotes/metacharacters.
                string tr = $"\"{full}\" --minimized";
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("/Create");
                psi.ArgumentList.Add("/F");
                psi.ArgumentList.Add("/TN");
                psi.ArgumentList.Add(TaskName);
                psi.ArgumentList.Add("/TR");
                psi.ArgumentList.Add(tr);
                psi.ArgumentList.Add("/SC");
                psi.ArgumentList.Add("ONLOGON");
                psi.ArgumentList.Add("/RL");
                psi.ArgumentList.Add("LIMITED");

                using var p = Process.Start(psi);
                p?.WaitForExit(8000);
                if (p?.ExitCode == 0)
                    AppLog.Info("Autostart enabled.");
                else
                    AppLog.Error($"Autostart create failed (exit {p?.ExitCode}).");
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("/Delete");
                psi.ArgumentList.Add("/F");
                psi.ArgumentList.Add("/TN");
                psi.ArgumentList.Add(TaskName);

                using var p = Process.Start(psi);
                p?.WaitForExit(8000);
                AppLog.Info("Autostart disabled.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Autostart update failed: {ex.Message}");
        }
    }
}
