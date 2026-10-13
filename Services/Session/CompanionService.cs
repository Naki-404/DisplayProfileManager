using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Visor.Models;

namespace Visor.Services;

/// <summary>
/// Launches/stops companion tools only. Never touches game process memory or sends
/// global keyboard input (anti-cheat safe). Only processes Visor started are tracked,
/// stopped or have their dialogs dismissed — pre-existing instances are never touched.
/// </summary>
public sealed class CompanionService
{
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr hDlg, int nIDDlgItem);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const uint BM_CLICK = 0x00F5;
    private const uint WM_CLOSE = 0x0010;
    private const int IDNO = 7;
    private const int SW_HIDE = 0;

    /// <summary>companion key → PID → process start time (guards against PID reuse).</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, DateTime?>> _trackedPids =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Starts a companion. Only processes Visor itself starts are tracked (and later stopped);
    /// an instance that was already running is left alone.
    /// </summary>
    public void Launch(CompanionApp companion)
    {
        try
        {
            string key = CompanionKey(companion);

            if (string.Equals(companion.LaunchMode, "scheduledTask", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(companion.TaskName))
            {
                if (!PathSecurity.IsSafeScheduledTaskName(companion.TaskName))
                {
                    AppLog.Error("Rejected unsafe scheduled task name.");
                    return;
                }

                string bare = Path.GetFileNameWithoutExtension(companion.Path);
                var before = SnapshotPidsByName(bare);
                if (!StartScheduledTask(companion.TaskName!))
                    return;

                // The task's process is not our child: track only PIDs of the companion exe that
                // appeared after we ran the task (instances that existed before are never touched).
                Thread.Sleep(800);
                int adopted = 0;
                foreach (var pid in SnapshotPidsByName(bare))
                {
                    if (before.Contains(pid)) continue;
                    TrackPid(key, pid);
                    adopted++;
                }
                AppLog.Info($"Companion task started: {companion.TaskName} ({adopted} new process(es) tracked).");
            }
            else
            {
                if (!PathSecurity.TryNormalizeExecutable(companion.Path, out var fullPath, out var error))
                {
                    AppLog.Error($"Companion rejected: {error}");
                    return;
                }
                if (!PathSecurity.IsSafeArguments(companion.Arguments))
                {
                    AppLog.Error("Companion rejected: unsafe arguments.");
                    return;
                }

                string name = Path.GetFileNameWithoutExtension(fullPath);
                bool hasArgs = !string.IsNullOrWhiteSpace(companion.Arguments);

                // Already running and nothing to pass on: leave the user's instance alone (not managed).
                // Launchers like Overwolf need a second start with -launchapp …, so args always launch.
                if (!hasArgs && SnapshotPidsByName(name).Count > 0)
                {
                    AppLog.Info($"Companion already running (not started by Visor, left alone): {name}");
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = fullPath,
                    WorkingDirectory = Path.GetDirectoryName(fullPath) ?? "",
                    UseShellExecute = false,
                    CreateNoWindow = false
                };
                foreach (var arg in PathSecurity.SplitArguments(companion.Arguments))
                    psi.ArgumentList.Add(arg);

                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                        TrackPid(key, proc.Id);
                }

                // Arguments are never logged (they can carry account / launch tokens).
                AppLog.Info(hasArgs
                    ? $"Companion launched: {name} (with arguments)"
                    : $"Companion launched: {name}");
            }

            if (companion.DismissDialogs || companion.MinimizeToTray)
                Task.Run(() => DismissAndTray(key, companion));
        }
        catch (Exception ex)
        {
            AppLog.Error($"Companion launch failed: {ex.Message}");
        }
    }

    /// <summary>Closes / kills only the processes this companion entry started. Blocking (waits for exit) — call off the UI thread.</summary>
    public void Stop(CompanionApp companion)
    {
        try
        {
            if (string.Equals(companion.OnStop, "none", StringComparison.OrdinalIgnoreCase))
                return;

            var pids = TakeTrackedPids(CompanionKey(companion));
            if (pids.Count == 0)
            {
                AppLog.Info("Companion: nothing started by Visor to stop.");
                return;
            }

            var mode = companion.OnStop ?? "";
            bool closeFirst = !string.Equals(mode, "kill", StringComparison.OrdinalIgnoreCase);
            bool kill = !string.Equals(mode, "close", StringComparison.OrdinalIgnoreCase);

            foreach (var (pid, started) in pids)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (!IsSameProcess(p, started))
                    {
                        AppLog.Info($"Companion pid {pid} was reused by another process — skipped.");
                        continue;
                    }

                    if (closeFirst)
                    {
                        foreach (var hwnd in FindTopWindows((uint)pid))
                            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        try { p.CloseMainWindow(); } catch (InvalidOperationException) { /* exited */ }
                        if (p.WaitForExit(2500))
                            continue;
                    }

                    if (kill && !p.HasExited)
                    {
                        p.Kill(entireProcessTree: false);
                        p.WaitForExit(3000);
                    }
                }
                catch (ArgumentException)
                {
                    // already exited
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Failed to stop companion pid {pid}: {ex.Message}");
                }
            }

            AppLog.Info("Companion stopped.");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Companion stop failed: {ex.Message}");
        }
    }

    private static bool StartScheduledTask(string taskName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("/Run");
            psi.ArgumentList.Add("/TN");
            psi.ArgumentList.Add(taskName);

            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(5000))
            {
                AppLog.Error("schtasks /Run timed out.");
                return false;
            }
            if (p.ExitCode != 0)
            {
                AppLog.Error($"schtasks /Run exit {p.ExitCode}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"schtasks failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Answers "No" on startup dialogs / hides the main window — only for windows of PIDs Visor started.</summary>
    private void DismissAndTray(string key, CompanionApp companion)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        int noClicks = 0;
        bool trayDone = false;

        while (DateTime.UtcNow < deadline)
        {
            if (!_trackedPids.TryGetValue(key, out var set) || set.IsEmpty)
                return; // stopped meanwhile, or nothing of ours

            foreach (var pid in set.Keys)
            {
                foreach (var hwnd in FindTopWindows((uint)pid))
                {
                    if (!IsWindow(hwnd)) continue;
                    string cls = GetClass(hwnd);

                    if (companion.DismissDialogs && cls == "#32770")
                    {
                        if (ClickButtonByText(hwnd, new[] { "Нет", "No", "&Нет", "&No" })
                            || ClickButtonById(hwnd, IDNO))
                        {
                            noClicks++;
                            Thread.Sleep(300);
                            continue;
                        }
                    }

                    if (companion.MinimizeToTray && !trayDone)
                    {
                        string title = GetText(hwnd);
                        if (cls != "#32770" && (cls.StartsWith("T", StringComparison.Ordinal)
                            || title.Contains("RivaTuner", StringComparison.OrdinalIgnoreCase)))
                        {
                            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            Thread.Sleep(200);
                            if (IsWindow(hwnd) && IsWindowVisible(hwnd))
                                ShowWindow(hwnd, SW_HIDE);
                            trayDone = true;
                        }
                    }
                }
            }

            if ((noClicks >= 2 || !companion.DismissDialogs) && (trayDone || !companion.MinimizeToTray)) break;
            Thread.Sleep(250);
        }
    }

    private void TrackPid(string key, int pid)
    {
        if (pid <= 0) return;
        var set = _trackedPids.GetOrAdd(key, _ => new ConcurrentDictionary<int, DateTime?>());
        set[pid] = TryGetStartTime(pid);
    }

    private List<(int Pid, DateTime? Started)> TakeTrackedPids(string key)
    {
        var result = new List<(int, DateTime?)>();
        if (_trackedPids.TryRemove(key, out var set))
        {
            foreach (var kv in set)
                result.Add((kv.Key, kv.Value));
        }
        return result;
    }

    private static DateTime? TryGetStartTime(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSameProcess(Process p, DateTime? started)
    {
        if (started == null) return true;
        try { return p.StartTime == started.Value; }
        catch { return true; }
    }

    private static string CompanionKey(CompanionApp c)
    {
        if (!string.IsNullOrWhiteSpace(c.Path))
            return c.Path.Trim().ToLowerInvariant();
        return (c.TaskName ?? "unknown").Trim().ToLowerInvariant();
    }

    private static HashSet<int> SnapshotPidsByName(string? bareName)
    {
        var set = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(bareName)) return set;
        foreach (var p in Process.GetProcessesByName(bareName))
        {
            try { set.Add(p.Id); }
            finally { p.Dispose(); }
        }
        return set;
    }

    private static List<IntPtr> FindTopWindows(uint pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint wpid);
            if (wpid == pid && IsWindowVisible(h))
                list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string GetClass(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetText(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool ClickButtonById(IntPtr dlg, int id)
    {
        IntPtr btn = GetDlgItem(dlg, id);
        if (btn == IntPtr.Zero) return false;
        return PostMessage(btn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
    }

    private static bool ClickButtonByText(IntPtr dlg, string[] texts)
    {
        bool clicked = false;
        EnumChildWindows(dlg, (h, _) =>
        {
            if (clicked) return false;
            if (!GetClass(h).Equals("Button", StringComparison.OrdinalIgnoreCase)) return true;
            string text = GetText(h);
            foreach (var t in texts)
            {
                if (string.Equals(text, t, StringComparison.OrdinalIgnoreCase))
                {
                    PostMessage(h, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    clicked = true;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return clicked;
    }
}
