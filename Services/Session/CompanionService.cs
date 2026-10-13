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

    private static readonly TimeSpan DefaultStopBudget = TimeSpan.FromSeconds(6);
    private const int CloseWaitMs = 2500;
    private const int KillWaitMs = 3000;

    /// <summary>Closes / kills only the processes this companion entry started. Blocking (waits for exit) — call off the UI thread.</summary>
    public void Stop(CompanionApp companion) => Stop(companion, DefaultStopBudget);

    /// <summary>
    /// As <see cref="Stop(CompanionApp)"/>, but every wait is bounded by <paramref name="budget"/>
    /// in total (app exit has a fixed time box). Order: WM_CLOSE to every tracked process first,
    /// then one wait for all of them with the remaining budget (a recorder like OBS gets the whole
    /// budget to finish its file), then Kill only what is still alive — and only while budget
    /// remains. Once the budget is spent a companion is left to Windows: it has had its close
    /// request and is never killed late.
    /// </summary>
    public void Stop(CompanionApp companion, TimeSpan budget)
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
            var deadline = DateTime.UtcNow + (budget > TimeSpan.Zero ? budget : TimeSpan.FromMilliseconds(200));

            // Only processes Visor started that are still the same process (pid not reused).
            var live = new List<Process>();
            foreach (var (pid, started) in pids)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    if (!IsSameProcess(p, started))
                    {
                        AppLog.Info($"Companion pid {pid} was reused by another process — skipped.");
                        p.Dispose();
                        continue;
                    }
                    live.Add(p);
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

            try
            {
                if (closeFirst)
                {
                    // 1. Ask everyone to close.
                    foreach (var p in live)
                    {
                        try
                        {
                            foreach (var hwnd in FindTopWindows((uint)p.Id))
                                PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            try { p.CloseMainWindow(); } catch (InvalidOperationException) { /* exited */ }
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn(ex, $"Companion close request (pid {p.Id})");
                        }
                    }

                    // 2. One wait for all of them, bounded by the close step cap and the deadline.
                    var closeDeadline = Earliest(deadline, DateTime.UtcNow.AddMilliseconds(CloseWaitMs));
                    foreach (var p in live)
                    {
                        try { p.WaitForExit(WaitMs(closeDeadline)); }
                        catch (Exception ex) { AppLog.Warn(ex, $"Companion close wait (pid {p.Id})"); }
                    }
                }

                // 3. Kill only what is still running, and only while budget remains.
                foreach (var p in live)
                {
                    try
                    {
                        if (p.HasExited) continue;
                        if (!kill)
                        {
                            AppLog.Info($"Companion pid {p.Id} is still running after the close request (OnStop=close) — left to Windows.");
                            continue;
                        }
                        if (DateTime.UtcNow >= deadline)
                        {
                            AppLog.Warn($"Companion pid {p.Id} did not close within the stop budget — left to Windows (not killed).");
                            continue;
                        }
                        p.Kill(entireProcessTree: false);
                        if (!p.WaitForExit(WaitMs(Earliest(deadline, DateTime.UtcNow.AddMilliseconds(KillWaitMs)))))
                            AppLog.Warn($"Companion pid {p.Id} did not exit within the stop budget.");
                    }
                    catch (InvalidOperationException)
                    {
                        // exited meanwhile
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error($"Failed to stop companion pid {p.Id}: {ex.Message}");
                    }
                }
            }
            finally
            {
                foreach (var p in live) p.Dispose();
            }

            AppLog.Info("Companion stopped.");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Companion stop failed: {ex.Message}");
        }
    }

    private static DateTime Earliest(DateTime a, DateTime b) => a < b ? a : b;

    /// <summary>Milliseconds left until <paramref name="deadline"/> (at least 1 — never a blocking -1).</summary>
    private static int WaitMs(DateTime deadline)
    {
        var left = (deadline - DateTime.UtcNow).TotalMilliseconds;
        return (int)Math.Clamp(left, 1, int.MaxValue);
    }

    /// <summary>
    /// schtasks /Run by absolute System32 path (CreateProcess searches the app folder first).
    /// Nothing is redirected — no pipe can fill up — and a hung schtasks is killed after 5 s.
    /// </summary>
    private static bool StartScheduledTask(string taskName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
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
                AppLog.Error("schtasks /Run timed out — killed.");
                try { p.Kill(); p.WaitForExit(1000); } catch (Exception ex) { AppLog.Warn(ex, "schtasks kill"); }
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

    /// <summary>PIDs of every running instance of the exe — one process-list scan, no handles opened.</summary>
    private static HashSet<int> SnapshotPidsByName(string? bareName)
    {
        var set = new HashSet<int>();
        var key = ProcessNames.Key(bareName);
        if (key.Length == 0) return set;
        ProcessSnapshot.PidsOf(key, set);
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
