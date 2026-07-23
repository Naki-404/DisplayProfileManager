using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

/// <summary>
/// Launches/stops companion tools only. Never touches game process memory or sends
/// global keyboard input (anti-cheat safe). Stop targets tracked PIDs / companion HWND.
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

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte>> _trackedPids =
        new(StringComparer.OrdinalIgnoreCase);

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

                var before = SnapshotPidsByName(Path.GetFileNameWithoutExtension(companion.Path));
                if (!StartScheduledTask(companion.TaskName!))
                    return;

                // Adopt newly appeared processes with the companion exe name (no game attach).
                Task.Run(() =>
                {
                    Thread.Sleep(800);
                    foreach (var pid in SnapshotPidsByName(Path.GetFileNameWithoutExtension(companion.Path)))
                    {
                        if (!before.Contains(pid))
                            TrackPid(key, pid);
                    }
                });
            }
            else
            {
                if (!PathSecurity.TryNormalizeExecutable(companion.Path, out var fullPath, out var error))
                {
                    AppLog.Error($"Companion rejected: {error}");
                    return;
                }

                companion.Path = fullPath;
                string name = Path.GetFileNameWithoutExtension(fullPath);

                var existing = Process.GetProcessesByName(name);
                try
                {
                    // Skip only when already running AND no launch args.
                    // Launchers like Overwolf need a second start with -launchapp ….
                    if (existing.Length > 0 && string.IsNullOrWhiteSpace(companion.Arguments))
                    {
                        foreach (var p in existing)
                        {
                            try { TrackPid(key, p.Id); }
                            catch { /* ignore */ }
                        }
                        AppLog.Info($"Companion already running — adopted {existing.Length} PID(s): {name}");
                        return;
                    }
                }
                finally
                {
                    foreach (var p in existing) p.Dispose();
                }

                var psi = new ProcessStartInfo
                {
                    FileName = fullPath,
                    WorkingDirectory = Path.GetDirectoryName(fullPath) ?? "",
                    UseShellExecute = false,
                    CreateNoWindow = false
                };

                if (!PathSecurity.IsSafeArguments(companion.Arguments))
                {
                    AppLog.Error("Companion rejected: unsafe arguments.");
                    return;
                }

                foreach (var arg in PathSecurity.SplitArguments(companion.Arguments))
                    psi.ArgumentList.Add(arg);

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    TrackPid(key, proc.Id);
                    proc.Dispose();
                }

                AppLog.Info(string.IsNullOrWhiteSpace(companion.Arguments)
                    ? $"Companion launched: {name}"
                    : $"Companion launched: {name} {companion.Arguments.Trim()}");
                // skip the generic "Companion launched." below for direct path
                if (companion.DismissDialogs || companion.MinimizeToTray)
                {
                    Task.Run(() => DismissAndTray(name, companion));
                }
                return;
            }

            AppLog.Info("Companion launched.");

            if (companion.DismissDialogs || companion.MinimizeToTray)
            {
                string name = Path.GetFileNameWithoutExtension(companion.Path);
                if (!string.IsNullOrWhiteSpace(name))
                    Task.Run(() => DismissAndTray(name, companion));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Companion launch failed: {ex.Message}");
        }
    }

    public void Stop(CompanionApp companion)
    {
        try
        {
            if (string.Equals(companion.OnStop, "none", StringComparison.OrdinalIgnoreCase))
                return;

            string key = CompanionKey(companion);
            var pids = TakeTrackedPids(key);

            // Fallback: match by full path when we have no tracked PIDs (never kill by bare name alone).
            if (pids.Count == 0 && PathSecurity.TryNormalizeExecutable(companion.Path, out var full, out _))
            {
                string bare = Path.GetFileNameWithoutExtension(full);
                foreach (var p in Process.GetProcessesByName(bare))
                {
                    try
                    {
                        string? mod = null;
                        try { mod = p.MainModule?.FileName; } catch { /* access denied — skip */ }
                        if (mod != null && string.Equals(mod, full, StringComparison.OrdinalIgnoreCase))
                            pids.Add(p.Id);
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }

            if (pids.Count == 0)
            {
                AppLog.Info("Companion already closed (no tracked PIDs).");
                return;
            }

            bool closeFirst = companion.OnStop.Contains("close", StringComparison.OrdinalIgnoreCase)
                              || companion.OnStop.Contains("hotkey", StringComparison.OrdinalIgnoreCase);
            bool kill = companion.OnStop.Contains("kill", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(companion.OnStop, "hotkeyThenKill", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(companion.OnStop, "kill", StringComparison.OrdinalIgnoreCase);

            if (!closeFirst && !kill)
                kill = true;

            foreach (int pid in pids)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (closeFirst)
                    {
                        foreach (var hwnd in FindTopWindows((uint)pid))
                            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        try { p.CloseMainWindow(); } catch { }
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
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/Run");
            psi.ArgumentList.Add("/TN");
            psi.ArgumentList.Add(taskName);

            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(5000);
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

    private void DismissAndTray(string processName, CompanionApp companion)
    {
        var deadline = DateTime.Now.AddSeconds(25);
        int noClicks = 0;
        bool trayDone = false;
        string key = CompanionKey(companion);

        while (DateTime.Now < deadline)
        {
            var procs = Process.GetProcessesByName(processName);
            if (procs.Length == 0)
            {
                Thread.Sleep(200);
                continue;
            }

            foreach (var proc in procs)
            {
                try
                {
                    TrackPid(key, proc.Id);
                    foreach (var hwnd in FindTopWindows((uint)proc.Id))
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
                finally
                {
                    proc.Dispose();
                }
            }

            if (noClicks >= 2 && trayDone) break;
            Thread.Sleep(250);
        }
    }

    private void TrackPid(string key, int pid)
    {
        if (pid <= 0) return;
        var set = _trackedPids.GetOrAdd(key, _ => new ConcurrentDictionary<int, byte>());
        set[pid] = 0;
    }

    private HashSet<int> TakeTrackedPids(string key)
    {
        var result = new HashSet<int>();
        if (_trackedPids.TryRemove(key, out var set))
        {
            foreach (var pid in set.Keys)
                result.Add(pid);
        }
        return result;
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
