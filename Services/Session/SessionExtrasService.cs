using System.Runtime.InteropServices;
using Visor.Models;
using Microsoft.Win32;
using static Visor.Services.DisplayNative;

namespace Visor.Services;

/// <summary>
/// System-only session extras: notifications, HDR, audio, monitors, DDC/CI, scaling.
/// No game process injection. Original values and "changed" flags live in the
/// <see cref="ActiveSessionSnapshot"/> (persisted), so a normal exit and a crash restart
/// use the same restore path and only what Apply changed is put back.
/// </summary>
public sealed class SessionExtrasService
{
    private const string PushNotificationsKey = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
    private const string VideoSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\VideoSettings";
    private static bool _nightLightLogged;

    /// <summary>Last non-fatal warning from Apply (DDC / isolate). Cleared at start of Apply.</summary>
    public string? LastWarning { get; private set; }

    /// <summary>
    /// Applies the profile's extras. The first time an item is changed in this session its
    /// original value is recorded in <paramref name="snap"/> together with a "changed" flag.
    /// </summary>
    public void Apply(SessionExtras extras, string? resolution, string? displayDevice, ActiveSessionSnapshot snap)
    {
        extras ??= new SessionExtras();
        LastWarning = null;

        Step("Toast notifications", () =>
        {
            if (!extras.QuietNotifications) return;
            if (!snap.ToastChanged)
            {
                snap.ToastOriginal = ReadDword(PushNotificationsKey, "ToastEnabled");
                snap.ToastChanged = true;
            }
            WriteDword(PushNotificationsKey, "ToastEnabled", 0, "Toast notifications");
        });

        Step("Auto HDR", () =>
        {
            if (!extras.DisableAutoHdr) return;
            if (!snap.AutoHdrChanged)
            {
                snap.AutoHdrOriginal = ReadDword(VideoSettingsKey, "EnableHDRForGamingApps");
                snap.AutoHdrChanged = true;
            }
            WriteDword(VideoSettingsKey, "EnableHDRForGamingApps", 0, "Auto HDR");
        });

        if (extras.DisableNightLight && !_nightLightLogged)
        {
            _nightLightLogged = true;
            AppLog.Info("\"Disable Night Light\" is not supported (no public Windows API) — setting ignored.");
        }

        Step("Audio device", () =>
        {
            if (!extras.SwitchAudioDevice || string.IsNullOrWhiteSpace(extras.AudioDeviceId)) return;
            // A stored endpoint id that no longer exists (device reinstalled / other USB port → new id)
            // is skipped with one log line instead of failing on every (re-)apply.
            var target = AudioEndpoint.ResolveUsable(extras.AudioDeviceId!);
            if (target == null) return;
            if (!snap.AudioChanged)
            {
                snap.AudioDeviceId = AudioEndpoint.GetDefaultId(AudioEndpoint.Role.Multimedia);
                snap.AudioConsoleDeviceId = AudioEndpoint.GetDefaultId(AudioEndpoint.Role.Console);
                snap.AudioChanged = true;
            }
            if (!AudioEndpoint.SetDefault(target))
                AddWarning("Audio device switch failed");
        });

        Step("Monitor brightness", () =>
        {
            if (!extras.ApplyMonitorBrightness) return;
            if (!snap.BrightnessChanged)
            {
                snap.MonitorBrightnessLevels = MonitorBrightness.GetAll();
                snap.BrightnessChanged = true;
            }
            if (!MonitorBrightness.SetAll(Math.Clamp(extras.MonitorBrightness, 0, 100)))
                AddWarning("DDC brightness failed");
        });

        Step("Monitor layout", () =>
        {
            if (!ResolveIsolate(extras) || snap.TopologySaved) return;
            switch (DisplayTopology.IsolatePrimary(out var saved))
            {
                case DisplayTopology.IsolateResult.Applied:
                    snap.TopologySaved = true;
                    snap.TopologyPathsB64 = Convert.ToBase64String(saved.Paths);
                    snap.TopologyModesB64 = Convert.ToBase64String(saved.Modes);
                    snap.TopologyPathCount = saved.PathCount;
                    snap.TopologyModeCount = saved.ModeCount;
                    break;
                case DisplayTopology.IsolateResult.Failed:
                    AddWarning("Isolate primary failed");
                    break;
            }
        });

        Step("Scaling mode", () =>
        {
            if (string.IsNullOrWhiteSpace(extras.ScalingMode)
                || string.Equals(extras.ScalingMode, "default", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(resolution))
                return;
            if (!snap.ScalingSaved && TryGetCurrentMode(displayDevice, out var cur))
            {
                snap.ScalingDevice = displayDevice;
                snap.ScalingFixedOutput = cur.dmDisplayFixedOutput;
                snap.ScalingWidth = cur.dmPelsWidth;
                snap.ScalingHeight = cur.dmPelsHeight;
                snap.ScalingSaved = true;
            }
            ScalingModeHelper.Apply(resolution!, extras.ScalingMode!, displayDevice);
        });
    }

    /// <summary>
    /// Restores every extra flagged as changed in <paramref name="snap"/> and clears the flags.
    /// </summary>
    /// <returns>False if any restore step failed (details are logged).</returns>
    public bool Restore(ActiveSessionSnapshot snap)
    {
        bool ok = true;

        // Each flag is cleared only when its restore succeeded, so a failed step is retried
        // (next restore attempt / next start) instead of being forgotten.

        // Layout first — display modes / scaling refer to the restored topology.
        if (snap.TopologySaved)
        {
            if (Step("Topology restore", () =>
                {
                    if (!DisplayTopology.Restore(snap))
                        throw new InvalidOperationException("SetDisplayConfig rejected the saved layout");
                }))
            {
                snap.TopologySaved = false;
                snap.TopologyPathsB64 = snap.TopologyModesB64 = null;
            }
            else ok = false;
        }

        if (snap.ScalingSaved)
        {
            if (!snap.ScalingWidth.HasValue || !snap.ScalingHeight.HasValue
                || Step("Scaling restore", () => ScalingModeHelper.Restore(
                    snap.ScalingWidth.Value, snap.ScalingHeight.Value, snap.ScalingFixedOutput ?? 0, snap.ScalingDevice)))
                snap.ScalingSaved = false;
            else ok = false;
        }

        if (snap.ToastChanged)
        {
            if (Step("Toast restore", () => WriteDword(PushNotificationsKey, "ToastEnabled", snap.ToastOriginal, "Toast notifications")))
                snap.ToastChanged = false;
            else ok = false;
        }

        if (snap.AutoHdrChanged)
        {
            if (Step("Auto HDR restore", () => WriteDword(VideoSettingsKey, "EnableHDRForGamingApps", snap.AutoHdrOriginal, "Auto HDR")))
                snap.AutoHdrChanged = false;
            else ok = false;
        }

        if (snap.AudioChanged)
        {
            if (Step("Audio restore", () =>
                {
                    if (!string.IsNullOrWhiteSpace(snap.AudioConsoleDeviceId))
                        AudioEndpoint.SetDefaultForRole(snap.AudioConsoleDeviceId!, AudioEndpoint.Role.Console);
                    if (!string.IsNullOrWhiteSpace(snap.AudioDeviceId))
                        AudioEndpoint.SetDefaultForRole(snap.AudioDeviceId!, AudioEndpoint.Role.Multimedia);
                }))
                snap.AudioChanged = false;
            else ok = false;
        }

        if (snap.BrightnessChanged)
        {
            if (snap.MonitorBrightnessLevels is not { Count: > 0 } levels
                || Step("Brightness restore", () =>
                {
                    if (!MonitorBrightness.RestoreAll(levels))
                        throw new InvalidOperationException("no monitor accepted the DDC/CI value");
                }))
                snap.BrightnessChanged = false;
            else ok = false;
        }

        return ok;
    }

    /// <summary>
    /// MonitorLayout isolatePrimary/primaryOnly ⇒ isolate; keepAll forces off;
    /// otherwise use IsolatePrimaryMonitor flag.
    /// </summary>
    private static bool ResolveIsolate(SessionExtras e)
    {
        var layout = e.MonitorLayout?.Trim();
        if (!string.IsNullOrWhiteSpace(layout))
        {
            if (string.Equals(layout, "keepAll", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(layout, "isolatePrimary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(layout, "primaryOnly", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return e.IsolatePrimaryMonitor;
    }

    private void AddWarning(string w) =>
        LastWarning = string.IsNullOrWhiteSpace(LastWarning) ? w : LastWarning + " · " + w;

    private static bool Step(string what, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Session extras — {what}: {ex.Message}");
            return false;
        }
    }

    private static int? ReadDword(string keyPath, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(keyPath);
        return k?.GetValue(name) is int i ? i : null;
    }

    /// <summary>Writes a DWORD, or deletes the value when <paramref name="value"/> is null (it was absent before).</summary>
    private static void WriteDword(string keyPath, string name, int? value, string label)
    {
        using var k = Registry.CurrentUser.CreateSubKey(keyPath);
        if (value.HasValue)
            k.SetValue(name, value.Value, RegistryValueKind.DWord);
        else
            k.DeleteValue(name, throwOnMissingValue: false);
        AppLog.Info($"{label} → {(value.HasValue ? value.Value.ToString() : "(default)")}");
    }
}

/// <summary>DDC/CI backlight for every physical monitor (dxva2).</summary>
internal static class MonitorBrightness
{
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, ref uint pdwMinimumBrightness, ref uint pdwCurrentBrightness, ref uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    /// <summary>Calls <paramref name="visit"/>(index, handle) for every physical monitor in a stable order.</summary>
    private static void ForEachPhysical(Action<int, IntPtr> visit)
    {
        int index = 0;
        MonitorEnumProc proc = (h, _, _, _) =>
        {
            uint n = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(h, ref n) || n == 0) return true;
            var arr = new PHYSICAL_MONITOR[n];
            if (!GetPhysicalMonitorsFromHMONITOR(h, n, arr)) { index += (int)n; return true; }
            try
            {
                foreach (var pm in arr)
                    visit(index++, pm.hPhysicalMonitor);
            }
            finally { DestroyPhysicalMonitors(n, arr); }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);
    }

    /// <summary>Current brightness (0–100) per physical monitor; null where DDC/CI is unavailable.</summary>
    public static List<int?> GetAll()
    {
        var levels = new List<int?>();
        ForEachPhysical((_, h) =>
        {
            uint min = 0, cur = 0, max = 0;
            levels.Add(GetMonitorBrightness(h, ref min, ref cur, ref max) && max > min
                ? (int)Math.Round((cur - min) * 100.0 / (max - min))
                : null);
        });
        return levels;
    }

    /// <returns>True if at least one monitor accepted the new brightness.</returns>
    public static bool SetAll(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        bool ok = false;
        ForEachPhysical((_, h) => ok |= SetPercent(h, percent));
        if (ok) AppLog.Info($"Monitor brightness → {percent}%");
        return ok;
    }

    /// <summary>Puts each monitor back to its own captured level.</summary>
    /// <returns>False when a monitor that had a readable level could not be set back.</returns>
    public static bool RestoreAll(IReadOnlyList<int?> levels)
    {
        bool ok = true;
        ForEachPhysical((i, h) =>
        {
            if (i < levels.Count && levels[i] is int pct && !SetPercent(h, pct))
                ok = false;
        });
        AppLog.Info(ok ? "Monitor brightness restored per monitor." : "Monitor brightness restore incomplete.");
        return ok;
    }

    private static bool SetPercent(IntPtr h, int percent)
    {
        uint min = 0, cur = 0, max = 0;
        if (!GetMonitorBrightness(h, ref min, ref cur, ref max) || max <= min) return false;
        uint val = min + (uint)Math.Round((max - min) * (Math.Clamp(percent, 0, 100) / 100.0));
        return val == cur || SetMonitorBrightness(h, val);
    }
}

internal static class ScalingModeHelper
{
    private const int DMDFO_DEFAULT = 0;
    private const int DMDFO_STRETCH = 1;
    private const int DMDFO_CENTER = 2;

    public static void Apply(string resolution, string mode, string? device)
    {
        if (!DisplayEngine.TryParseRes(resolution, out int w, out int h))
            return;

        int fixedOut = mode.ToLowerInvariant() switch
        {
            "stretch" or "stretched" => DMDFO_STRETCH,
            "center" or "centered" => DMDFO_CENTER,
            _ => DMDFO_DEFAULT
        };

        int r = Change(device, w, h, fixedOut);
        AppLog.Info($"Scaling mode → {mode} ({resolution}) result={r}");
    }

    public static void Restore(int width, int height, int fixedOutput, string? device)
    {
        int r = Change(device, width, height, fixedOutput);
        AppLog.Info($"Scaling mode restored ({width}x{height}) result={r}");
        if (r != DISP_CHANGE_SUCCESSFUL)
            throw new InvalidOperationException($"ChangeDisplaySettingsEx returned {r}");
    }

    /// <summary>Dynamic change (CDS flags 0) — nothing is written to the registry.</summary>
    private static int Change(string? device, int width, int height, int fixedOutput)
    {
        if (!TryGetCurrentMode(device, out var dm)) return -1;
        if (dm.dmPelsWidth == width && dm.dmPelsHeight == height && dm.dmDisplayFixedOutput == fixedOutput)
            return DISP_CHANGE_SUCCESSFUL;
        dm.dmPelsWidth = width;
        dm.dmPelsHeight = height;
        dm.dmDisplayFixedOutput = fixedOutput;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFIXEDOUTPUT;
        return ChangeDisplaySettingsEx(NullIfEmpty(device), ref dm, IntPtr.Zero, CDS_DYNAMIC, IntPtr.Zero);
    }
}

/// <summary>
/// "Primary monitor only" via CCD: keeps the active paths whose source sits at desktop (0,0)
/// (the primary display, plus its clones) and deactivates the rest — not saved to the
/// display database, and the original active configuration is kept for restore.
/// </summary>
internal static class DisplayTopology
{
    public enum IsolateResult { Applied, NothingToDo, Failed }

    public readonly record struct SavedConfig(byte[] Paths, byte[] Modes, int PathCount, int ModeCount);

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const uint SDC_TOPOLOGY_DATABASE_CURRENT = 0x0F; // SDC_USE_DATABASE_CURRENT
    private const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
    private const uint SDC_APPLY = 0x80;
    private const uint SDC_ALLOW_CHANGES = 0x400;
    private const uint DISPLAYCONFIG_PATH_ACTIVE = 0x1;
    private const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;
    private const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;

    // DISPLAYCONFIG_PATH_INFO (72 bytes): sourceInfo { LUID 0, id 8, modeInfoIdx 12, statusFlags 16 },
    // targetInfo { LUID 20, id 28, modeInfoIdx 32, … }, flags 68.
    private const int PathSize = 72;
    private const int PathSourceIdOffset = 8;
    private const int PathSourceModeIdxOffset = 12;
    private const int PathTargetModeIdxOffset = 32;
    private const int PathFlagsOffset = 68;
    // DISPLAYCONFIG_MODE_INFO (64 bytes): infoType 0, id 4, LUID 8, sourceMode { width 16, height 20, format 24, position.x 28, position.y 32 }.
    private const int ModeSize = 64;
    private const int ModeInfoTypeOffset = 0;
    private const int ModePositionXOffset = 28;
    private const int ModePositionYOffset = 32;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out int numPathArrayElements, out int numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref int numPathArrayElements, [Out] byte[] pathInfoArray,
        ref int numModeInfoArrayElements, [Out] byte[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(int numPathArrayElements, byte[]? pathArray,
        int numModeInfoArrayElements, byte[]? modeInfoArray, uint flags);

    public static IsolateResult IsolatePrimary(out SavedConfig saved)
    {
        saved = default;
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out int pc, out int mc) != 0 || pc <= 0)
                return IsolateResult.Failed;
            var paths = new byte[checked(pc * PathSize)];
            var modes = new byte[checked(Math.Max(mc, 1) * ModeSize)];
            int pcr = pc, mcr = mc;
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pcr, paths, ref mcr, modes, IntPtr.Zero) != 0 || pcr <= 0)
                return IsolateResult.Failed;

            // Primary = the source whose desktop position is (0,0).
            int primary = -1;
            for (int i = 0; i < pcr && primary < 0; i++)
            {
                uint idx = BitConverter.ToUInt32(paths, i * PathSize + PathSourceModeIdxOffset);
                if (idx >= (uint)mcr) continue;
                int m = (int)idx * ModeSize;
                if (BitConverter.ToUInt32(modes, m + ModeInfoTypeOffset) != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE) continue;
                if (BitConverter.ToInt32(modes, m + ModePositionXOffset) == 0 && BitConverter.ToInt32(modes, m + ModePositionYOffset) == 0)
                    primary = i;
            }
            if (primary < 0)
            {
                AppLog.Warn("Isolate primary: no source at (0,0) found.");
                return IsolateResult.Failed;
            }

            saved = new SavedConfig((byte[])paths.Clone(), (byte[])modes.Clone(), pcr, mcr);

            int disabled = 0;
            for (int i = 0; i < pcr; i++)
            {
                if (SameSource(paths, i, primary)) continue;
                int p = i * PathSize;
                uint flags = BitConverter.ToUInt32(paths, p + PathFlagsOffset) & ~DISPLAYCONFIG_PATH_ACTIVE;
                BitConverter.TryWriteBytes(paths.AsSpan(p + PathFlagsOffset, 4), flags);
                BitConverter.TryWriteBytes(paths.AsSpan(p + PathSourceModeIdxOffset, 4), DISPLAYCONFIG_PATH_MODE_IDX_INVALID);
                BitConverter.TryWriteBytes(paths.AsSpan(p + PathTargetModeIdxOffset, 4), DISPLAYCONFIG_PATH_MODE_IDX_INVALID);
                disabled++;
            }

            if (disabled == 0)
            {
                AppLog.Info("Isolate primary: only the primary display is active — nothing to do.");
                saved = default;
                return IsolateResult.NothingToDo;
            }

            int r = SetDisplayConfig(pcr, paths, mcr, modes, SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES);
            AppLog.Info($"Isolate primary: {disabled} display(s) turned off, result={r}");
            if (r != 0)
            {
                saved = default;
                return IsolateResult.Failed;
            }
            return IsolateResult.Applied;
        }
        catch (Exception ex)
        {
            AppLog.Error("IsolatePrimary: " + ex.Message);
            saved = default;
            return IsolateResult.Failed;
        }
    }

    /// <summary>Same adapter LUID (bytes 0..7) and source id as the primary path.</summary>
    private static bool SameSource(byte[] paths, int i, int primary) =>
        paths.AsSpan(i * PathSize, PathSourceIdOffset + 4)
            .SequenceEqual(paths.AsSpan(primary * PathSize, PathSourceIdOffset + 4));

    /// <summary>Re-applies the saved active configuration; falls back to the display database.</summary>
    public static bool Restore(ActiveSessionSnapshot snap)
    {
        if (!string.IsNullOrWhiteSpace(snap.TopologyPathsB64) && !string.IsNullOrWhiteSpace(snap.TopologyModesB64)
            && snap.TopologyPathCount > 0)
        {
            try
            {
                var paths = Convert.FromBase64String(snap.TopologyPathsB64!);
                var modes = Convert.FromBase64String(snap.TopologyModesB64!);
                int r = SetDisplayConfig(snap.TopologyPathCount, paths, snap.TopologyModeCount, modes,
                    SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES);
                AppLog.Info($"Display topology restored from snapshot, result={r}");
                if (r == 0) return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Topology restore from snapshot: " + ex.Message);
            }
        }

        // Isolate was never saved to the database, so "database current" is the user's layout.
        int db = SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_TOPOLOGY_DATABASE_CURRENT);
        AppLog.Info($"Display topology restored from database, result={db}");
        return db == 0;
    }
}

/// <summary>Outcome of one default-endpoint switch (see <see cref="AudioEndpoint.TrySetDefaultForRole"/>).</summary>
public enum AudioSwitchResult
{
    Ok,
    /// <summary>The endpoint id is unknown to Windows (device removed / reinstalled → new id) or not active.</summary>
    DeviceUnavailable,
    /// <summary>The same id failed recently — not retried until <see cref="AudioSwitchMemo.RetryAfter"/> elapses.</summary>
    Suppressed,
    /// <summary>PolicyConfig returned a failure HRESULT (logged with the code).</summary>
    Failed
}

/// <summary>
/// Pure helpers around MMDevice endpoint ids ("{0.0.0.00000000}.{guid}") and the HRESULTs the
/// PolicyConfig call returns — unit-tested without touching the audio stack.
/// </summary>
public static class AudioEndpointId
{
    public const int E_NOTFOUND = unchecked((int)0x80070490);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);

    /// <summary>Data-flow part of an endpoint id: "0.0.0" = render, "0.0.1" = capture.</summary>
    public enum Flow { Unknown, Render, Capture }

    /// <summary>
    /// Parses "{0.0.0.00000000}.{3c662cc2-4bcf-43ee-9fc0-127246e3bb71}". False for names or malformed ids.
    /// </summary>
    public static bool TryParse(string? id, out Flow flow, out Guid endpointGuid)
    {
        flow = Flow.Unknown;
        endpointGuid = Guid.Empty;
        if (string.IsNullOrWhiteSpace(id)) return false;
        var s = id.Trim();
        int sep = s.IndexOf("}.{", StringComparison.Ordinal);
        if (!s.StartsWith('{') || !s.EndsWith('}') || sep < 0) return false;
        string head = s[1..sep];
        string tail = s[(sep + 2)..];
        if (!Guid.TryParseExact(tail, "B", out endpointGuid)) return false;
        var parts = head.Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiHexDigit)))
            return false;
        flow = (parts[0], parts[1], parts[2]) switch
        {
            ("0", "0", "0") => Flow.Render,
            ("0", "0", "1") => Flow.Capture,
            _ => Flow.Unknown
        };
        return true;
    }

    /// <summary>True when <paramref name="idOrName"/> looks like an endpoint id rather than a friendly-name fragment.</summary>
    public static bool LooksLikeId(string? idOrName) =>
        !string.IsNullOrWhiteSpace(idOrName) && idOrName.TrimStart().StartsWith('{');

    /// <summary>Log text for a PolicyConfig / MMDevice HRESULT: "0x80070490 E_NOTFOUND (…)".</summary>
    public static string DescribeHResult(int hr)
    {
        string hex = "0x" + hr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        string? name = hr switch
        {
            E_NOTFOUND => "E_NOTFOUND (endpoint id unknown — device removed or reinstalled; re-select it in the profile)",
            E_INVALIDARG => "E_INVALIDARG (malformed endpoint id or role)",
            E_NOINTERFACE => "E_NOINTERFACE (PolicyConfig interface not available on this Windows build)",
            E_ACCESSDENIED => "E_ACCESSDENIED",
            E_NOTIMPL => "E_NOTIMPL",
            AUDCLNT_E_DEVICE_INVALIDATED => "AUDCLNT_E_DEVICE_INVALIDATED (device unplugged / disabled)",
            _ => null
        };
        return name == null ? hex : hex + " " + name;
    }

    /// <summary>MMDevice DEVICE_STATE_* bits → short text for logs.</summary>
    public static string DescribeState(int state) => state switch
    {
        1 => "active",
        2 => "disabled",
        4 => "not present",
        8 => "unplugged",
        _ => "state 0x" + state.ToString("X", System.Globalization.CultureInfo.InvariantCulture)
    };
}

/// <summary>
/// Remembers endpoint ids whose switch just failed so a profile re-apply (deferred apply, focus
/// changes, …) does not hammer PolicyConfig and the log with the same error. Pure (clock passed in).
/// </summary>
public sealed class AudioSwitchMemo
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, (DateTime Utc, int HResult)> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reportedUnavailable = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>False while <paramref name="id"/> is inside its back-off window after a failure.</summary>
    public bool ShouldAttempt(string id, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_failures.TryGetValue(id, out var f)) return true;
            if (nowUtc - f.Utc >= RetryAfter)
            {
                _failures.Remove(id);
                return true;
            }
            return false;
        }
    }

    public void RecordFailure(string id, int hresult, DateTime nowUtc)
    {
        lock (_gate) _failures[id] = (nowUtc, hresult);
    }

    public void RecordSuccess(string id)
    {
        lock (_gate)
        {
            _failures.Remove(id);
            _reportedUnavailable.Remove(id);
        }
    }

    /// <summary>True the first time an unavailable id is seen (log once per id per run, not on every re-apply).</summary>
    public bool FirstUnavailableReport(string id)
    {
        lock (_gate) return _reportedUnavailable.Add(id);
    }

    public int? LastFailure(string id)
    {
        lock (_gate) return _failures.TryGetValue(id, out var f) ? f.HResult : null;
    }
}

/// <summary>
/// Default audio endpoint via PolicyConfig (undocumented COM, widely used). The switch first checks
/// that the stored endpoint id still exists and is active (endpoint ids change when a device is
/// reinstalled or moved to another port), tries <c>IPolicyConfig</c> (Win7+) and falls back to
/// <c>IPolicyConfigVista</c>, logs the HRESULT, and backs off instead of retrying on every re-apply.
/// </summary>
public static class AudioEndpoint
{
    private const int DEVICE_STATE_ACTIVE = 1;

    /// <summary>Process-wide back-off memo (see <see cref="AudioSwitchMemo"/>).</summary>
    public static AudioSwitchMemo Memo { get; } = new();

    public static List<(string Id, string Name)> ListDevices()
    {
        var list = new List<(string, string)>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE_ACTIVE, out var coll);
            if (hr != 0 || coll == null) return list;

            coll.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (coll.Item(i, out var dev) != 0 || dev == null) continue;
                try
                {
                    if (dev.GetId(out string id) != 0 || string.IsNullOrWhiteSpace(id)) continue;
                    string name = id;
                    if (dev.OpenPropertyStore(0, out var store) == 0 && store != null)
                    {
                        try
                        {
                            var key = new PROPERTYKEY { fmtid = Guids.PKEY_Device_FriendlyName, pid = 14 };
                            if (store.GetValue(ref key, out var pv) == 0)
                            {
                                try { name = pv.GetValue() as string ?? id; }
                                finally { PropVariantClear(ref pv); }
                            }
                        }
                        finally { Marshal.ReleaseComObject(store); }
                    }
                    list.Add((id, name));
                }
                finally { Marshal.ReleaseComObject(dev); }
            }
            Marshal.ReleaseComObject(coll);
            Marshal.ReleaseComObject(enumerator);
        }
        catch (Exception ex)
        {
            AppLog.Error("List audio devices: " + ex.Message);
        }
        return list;
    }

    /// <summary>Default render roles Visor reads / writes (communications is never touched).</summary>
    public enum Role { Console = 0, Multimedia = 1 }

    public static string? GetDefaultId(Role role)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, (ERole)(int)role, out var dev);
            if (hr != 0 || dev == null)
            {
                Marshal.ReleaseComObject(enumerator);
                return null;
            }
            try
            {
                if (dev.GetId(out string id) != 0) return null;
                return id;
            }
            finally
            {
                Marshal.ReleaseComObject(dev);
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("GetDefault audio: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Endpoint id the profile setting refers to, only when that endpoint exists and is active now;
    /// otherwise null (logged once per id — not on every re-apply).
    /// </summary>
    public static string? ResolveUsable(string idOrName)
    {
        var id = ResolveId(idOrName);
        if (id == null) return null;
        int? state = ReadState(id, out int hr);
        if (state == DEVICE_STATE_ACTIVE) return id;
        if (Memo.FirstUnavailableReport(id))
        {
            string why = state.HasValue ? AudioEndpointId.DescribeState(state.Value) : AudioEndpointId.DescribeHResult(hr);
            AppLog.Warn($"Audio device {id} is not available ({why}) — default device left unchanged. Re-select the device in the game profile.");
        }
        return null;
    }

    /// <summary>
    /// Makes the device the default for playback (console + multimedia roles). The communications
    /// role is left alone so a separate headset / chat device setup keeps working.
    /// </summary>
    /// <returns>True when both roles were switched (or already set).</returns>
    public static bool SetDefault(string idOrName)
    {
        var id = ResolveUsable(idOrName);
        if (id == null) return false;
        var console = TrySetDefaultForRole(id, Role.Console);
        if (console is AudioSwitchResult.DeviceUnavailable or AudioSwitchResult.Suppressed) return false;
        var multimedia = TrySetDefaultForRole(id, Role.Multimedia);
        return console == AudioSwitchResult.Ok && multimedia == AudioSwitchResult.Ok;
    }

    /// <summary>
    /// Restore helper: throws only for a failure worth retrying later. A device that no longer exists
    /// cannot be restored — that is logged once and treated as done (no retry on every start).
    /// </summary>
    public static void SetDefaultForRole(string id, Role role)
    {
        var r = TrySetDefaultForRole(id, role);
        if (r is AudioSwitchResult.Failed or AudioSwitchResult.Suppressed)
        {
            int hr = Memo.LastFailure(id) ?? 0;
            throw new COMException($"SetDefaultEndpoint({role}) " +
                (r == AudioSwitchResult.Suppressed ? "skipped after a recent failure: " : "failed: ") +
                AudioEndpointId.DescribeHResult(hr), hr);
        }
    }

    /// <summary>
    /// One role switch with existence check, IPolicyConfig → IPolicyConfigVista fallback, HRESULT logging
    /// and a back-off after a failure (<see cref="AudioSwitchMemo.RetryAfter"/>).
    /// </summary>
    public static AudioSwitchResult TrySetDefaultForRole(string id, Role role)
    {
        if (string.IsNullOrWhiteSpace(id)) return AudioSwitchResult.DeviceUnavailable;
        var now = DateTime.UtcNow;
        if (!Memo.ShouldAttempt(id, now))
            return AudioSwitchResult.Suppressed;

        int? state = ReadState(id, out int stateHr);
        if (state != DEVICE_STATE_ACTIVE)
        {
            if (Memo.FirstUnavailableReport(id))
            {
                string why = state.HasValue ? AudioEndpointId.DescribeState(state.Value) : AudioEndpointId.DescribeHResult(stateHr);
                AppLog.Warn($"Default audio ({role}) not switched: device {id} is not available ({why}).");
            }
            return AudioSwitchResult.DeviceUnavailable;
        }

        int hr;
        string via;
        try
        {
            hr = CallSetDefaultEndpoint(id, role, out via);
        }
        catch (Exception ex)
        {
            hr = ex.HResult != 0 ? ex.HResult : AudioEndpointId.E_NOINTERFACE;
            via = "PolicyConfig";
        }

        if (hr == 0)
        {
            Memo.RecordSuccess(id);
            AppLog.Info($"Default audio ({role}) → {id}");
            return AudioSwitchResult.Ok;
        }

        Memo.RecordFailure(id, hr, now);
        AppLog.Error($"Default audio ({role}) → {id} failed via {via}: {AudioEndpointId.DescribeHResult(hr)}. " +
                     $"Not retried for {AudioSwitchMemo.RetryAfter.TotalMinutes:0} min.");
        return AudioSwitchResult.Failed;
    }

    /// <summary>IPolicyConfig (Win7 – Win11) first; IPolicyConfigVista only when the Win7 interface is not exposed.</summary>
    private static int CallSetDefaultEndpoint(string id, Role role, out string via)
    {
        object client = new CPolicyConfigClient();
        try
        {
            if (client is IPolicyConfig policy)
            {
                via = "IPolicyConfig";
                return policy.SetDefaultEndpoint(id, (ERole)(int)role);
            }
            if (client is IPolicyConfigVista vista)
            {
                via = "IPolicyConfigVista";
                return vista.SetDefaultEndpoint(id, (ERole)(int)role);
            }
            via = "PolicyConfig";
            return AudioEndpointId.E_NOINTERFACE;
        }
        finally
        {
            Marshal.ReleaseComObject(client);
        }
    }

    /// <summary>DEVICE_STATE_* of the endpoint, or null when Windows does not know the id (<paramref name="hr"/> says why).</summary>
    private static int? ReadState(string id, out int hr)
    {
        hr = 0;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? dev = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            hr = enumerator.GetDevice(id, out dev);
            if (hr != 0 || dev == null) return null;
            hr = dev.GetState(out int state);
            return hr == 0 ? state : null;
        }
        catch (Exception ex)
        {
            hr = ex.HResult;
            return null;
        }
        finally
        {
            if (dev != null) Marshal.ReleaseComObject(dev);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Endpoint id, or the first active device whose friendly name contains the text.</summary>
    private static string? ResolveId(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;
        if (AudioEndpointId.LooksLikeId(idOrName)) return idOrName.Trim();
        var match = ListDevices().FirstOrDefault(d =>
            d.Name.Contains(idOrName, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(match.Id))
        {
            if (Memo.FirstUnavailableReport(idOrName))
                AppLog.Warn("Audio device not found: " + idOrName);
            return null;
        }
        return match.Id;
    }

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    // CLSID_PolicyConfigClient (AudioSes.dll, ThreadingModel=Both).
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class CPolicyConfigClient { }

    // Correct vtable order for IMMDeviceEnumerator (after IUnknown).
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore propertyStore);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out int pdwState);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int cProps);
        [PreserveSig] int GetAt(int iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PropVariant propvar);
        [PreserveSig] int Commit();
    }

    // IPolicyConfig (Windows 7 – 11): 10 methods precede SetDefaultEndpoint (incl. ResetDeviceFormat).
    // Only SetDefaultEndpoint is called; the placeholders keep the vtable slots aligned.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility(IntPtr a, IntPtr b);
    }

    // IPolicyConfigVista: no ResetDeviceFormat → SetDefaultEndpoint is the 10th method.
    [ComImport, Guid("568B9108-44BF-40B4-9006-86AFE5B5A620"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigVista
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility(IntPtr a, IntPtr b);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr pointerValue;
        public object? GetValue()
        {
            if (vt == 31) return Marshal.PtrToStringUni(pointerValue); // VT_LPWSTR
            return null;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    private static class Guids
    {
        public static readonly Guid PKEY_Device_FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    }
}
