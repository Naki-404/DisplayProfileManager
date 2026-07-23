using System.Runtime.InteropServices;
using System.Text;
using DisplayProfileManager.Models;
using Microsoft.Win32;

namespace DisplayProfileManager.Services;

/// <summary>
/// System-only session extras: notifications, HDR, audio, monitors, DDC/CI, scaling.
/// No game process injection.
/// </summary>
public sealed class SessionExtrasService : IDisposable
{
    private SessionSnapshot? _snap;

    /// <summary>Last non-fatal warning from Apply (DDC / isolate). Cleared at start of Apply.</summary>
    public string? LastWarning { get; private set; }

    public void Apply(SessionExtras extras, string? resolution, string? displayDevice)
    {
        extras ??= new SessionExtras();
        LastWarning = null;

        bool isolate = ResolveIsolate(extras);
        if (!HasAnyExtra(extras, resolution, isolate))
            return;

        try
        {
            _snap ??= Capture();
        }
        catch (Exception ex)
        {
            AppLog.Error("Session extras capture: " + ex.Message);
            return;
        }

        try
        {
            if (extras.QuietNotifications)
                SetToastEnabled(false);

            if (extras.DisableAutoHdr)
                SetAutoHdr(false);

            if (extras.DisableNightLight)
                TrySetNightLight(false);

            if (extras.SwitchAudioDevice && !string.IsNullOrWhiteSpace(extras.AudioDeviceId))
                AudioEndpoint.SetDefault(extras.AudioDeviceId!);

            if (extras.ApplyMonitorBrightness)
            {
                if (!MonitorBrightness.Set(Math.Clamp(extras.MonitorBrightness, 0, 100)))
                    LastWarning = "DDC brightness failed";
            }

            if (isolate)
            {
                // One-shot: DisplayTopology keeps the original snapshot; never re-capture after isolate.
                if (_snap != null && !_snap.TopologySaved)
                {
                    if (DisplayTopology.IsolatePrimary())
                        _snap.TopologySaved = true;
                    else
                        LastWarning = string.IsNullOrWhiteSpace(LastWarning)
                            ? "Isolate primary failed"
                            : LastWarning + " · Isolate primary failed";
                }
            }

            if (!string.IsNullOrWhiteSpace(extras.ScalingMode) &&
                !string.Equals(extras.ScalingMode, "default", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(resolution))
            {
                if (_snap != null && !_snap.ScalingSaved)
                {
                    _snap.ScalingDevice = displayDevice;
                    _snap.ScalingFixedOutput = ScalingModeHelper.CaptureFixedOutput(displayDevice);
                    _snap.ScalingWidth = ScalingModeHelper.CaptureWidth(displayDevice);
                    _snap.ScalingHeight = ScalingModeHelper.CaptureHeight(displayDevice);
                    _snap.ScalingSaved = true;
                }
                ScalingModeHelper.Apply(resolution!, extras.ScalingMode!, displayDevice);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Session extras apply: " + ex.Message);
        }
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

    private static bool HasAnyExtra(SessionExtras e, string? resolution, bool isolate) =>
        e.QuietNotifications
        || e.DisableAutoHdr
        || e.DisableNightLight
        || isolate
        || e.ApplyMonitorBrightness
        || (e.SwitchAudioDevice && !string.IsNullOrWhiteSpace(e.AudioDeviceId))
        || (!string.IsNullOrWhiteSpace(e.ScalingMode)
            && !string.Equals(e.ScalingMode, "default", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(resolution));
    public void Restore()
    {
        if (_snap == null) return;
        try
        {
            SetToastEnabled(_snap.ToastEnabled);
            SetAutoHdr(_snap.AutoHdrEnabled);
            if (_snap.NightLightKnown)
                TrySetNightLight(_snap.NightLightOn);
            if (!string.IsNullOrWhiteSpace(_snap.AudioDeviceId))
                AudioEndpoint.SetDefault(_snap.AudioDeviceId!);
            if (_snap.Brightness.HasValue)
                MonitorBrightness.Set(_snap.Brightness.Value);
            if (_snap.TopologySaved)
                DisplayTopology.Restore();
            if (_snap.ScalingSaved && _snap.ScalingWidth.HasValue && _snap.ScalingHeight.HasValue)
            {
                ScalingModeHelper.Restore(
                    _snap.ScalingWidth.Value,
                    _snap.ScalingHeight.Value,
                    _snap.ScalingFixedOutput ?? 0,
                    _snap.ScalingDevice);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Session extras restore: " + ex.Message);
        }
        finally
        {
            _snap = null;
        }
    }

    /// <summary>Copy current system extras into an ActiveSessionSnapshot (before profile apply).</summary>
    public void FillSnapshotExtras(Models.ActiveSessionSnapshot target)
    {
        var c = Capture();
        target.ToastEnabled = c.ToastEnabled;
        target.AutoHdrEnabled = c.AutoHdrEnabled;
        target.NightLightKnown = c.NightLightKnown;
        target.NightLightOn = c.NightLightOn;
        target.AudioDeviceId = c.AudioDeviceId;
        target.MonitorBrightness = c.Brightness;
    }

    /// <summary>Restore extras from persisted ActiveSessionSnapshot (crash / emergency).</summary>
    public void RestoreFromSnapshot(Models.ActiveSessionSnapshot snap)
    {
        try
        {
            SetToastEnabled(snap.ToastEnabled);
            SetAutoHdr(snap.AutoHdrEnabled);
            if (snap.NightLightKnown)
                TrySetNightLight(snap.NightLightOn);
            if (!string.IsNullOrWhiteSpace(snap.AudioDeviceId))
                AudioEndpoint.SetDefault(snap.AudioDeviceId!);
            if (snap.MonitorBrightness.HasValue)
                MonitorBrightness.Set(snap.MonitorBrightness.Value);
            if (snap.TopologySaved)
                DisplayTopology.RestoreFromSnapshot(snap);
            if (snap.ScalingSaved && snap.ScalingWidth.HasValue && snap.ScalingHeight.HasValue)
            {
                ScalingModeHelper.Restore(
                    snap.ScalingWidth.Value,
                    snap.ScalingHeight.Value,
                    snap.ScalingFixedOutput ?? 0,
                    snap.ScalingDevice);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("RestoreFromSnapshot: " + ex.Message);
        }
        finally
        {
            _snap = null;
        }
    }

    /// <summary>Merge live topology/scaling flags from in-memory apply into the disk snapshot.</summary>
    public void MergeLiveInto(Models.ActiveSessionSnapshot target)
    {
        if (_snap == null) return;
        if (_snap.TopologySaved)
        {
            target.TopologySaved = true;
            if (DisplayTopology.TryExport(
                    out var paths, out var modes, out int pc, out int mc))
            {
                target.TopologyPathsB64 = Convert.ToBase64String(paths);
                target.TopologyModesB64 = Convert.ToBase64String(modes);
                target.TopologyPathCount = pc;
                target.TopologyModeCount = mc;
            }
        }
        if (_snap.ScalingSaved)
        {
            target.ScalingSaved = true;
            target.ScalingDevice = _snap.ScalingDevice;
            target.ScalingFixedOutput = _snap.ScalingFixedOutput;
            target.ScalingWidth = _snap.ScalingWidth;
            target.ScalingHeight = _snap.ScalingHeight;
        }
    }

    private static SessionSnapshot Capture() => new()
    {
        ToastEnabled = GetToastEnabled(),
        AutoHdrEnabled = GetAutoHdr(),
        NightLightKnown = TryGetNightLight(out bool nl),
        NightLightOn = nl,
        AudioDeviceId = AudioEndpoint.GetDefaultId(),
        Brightness = MonitorBrightness.Get(),
        TopologySaved = false
    };

    private static bool GetToastEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
            var v = k?.GetValue("ToastEnabled");
            if (v is int i) return i != 0;
        }
        catch { }
        return true;
    }

    private static void SetToastEnabled(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
            k?.SetValue("ToastEnabled", on ? 1 : 0, RegistryValueKind.DWord);
            AppLog.Info($"Toast notifications → {(on ? "on" : "off")}");
        }
        catch (Exception ex)
        {
            AppLog.Error("ToastEnabled: " + ex.Message);
        }
    }

    private static bool GetAutoHdr()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\VideoSettings");
            var v = k?.GetValue("EnableHDRForGamingApps");
            if (v is int i) return i != 0;
        }
        catch { }
        return true;
    }

    private static void SetAutoHdr(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\VideoSettings");
            k?.SetValue("EnableHDRForGamingApps", on ? 1 : 0, RegistryValueKind.DWord);
            AppLog.Info($"Auto HDR → {(on ? "on" : "off")}");
        }
        catch (Exception ex)
        {
            AppLog.Error("AutoHDR: " + ex.Message);
        }
    }

    // Night Light: one-way best-effort disable only (OS state cannot be read reliably).
    private static bool TryGetNightLight(out bool on)
    {
        on = false;
        return false;
    }

    private static void TrySetNightLight(bool on)
    {
        if (on)
        {
            // We never captured prior state — refuse to guess "enable".
            AppLog.Info("Night Light re-enable skipped (state unknown).");
            return;
        }
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\$$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate");
            if (k != null)
            {
                byte[] off =
                {
                    0x43, 0x42, 0x01, 0x00, 0x0A, 0x02, 0x01, 0x00, 0x2A, 0x06,
                    0x24, 0xA9, 0x8A, 0xE5, 0xD5, 0x01, 0x00
                };
                k.SetValue("Data", off, RegistryValueKind.Binary);
                AppLog.Info("Night Light disable attempted (best-effort, one-way).");
            }
        }
        catch (Exception ex)
        {
            AppLog.Info("Night Light tweak skipped: " + ex.Message);
        }
    }

    public void Dispose() => _snap = null; // restore already done by EmergencyRestore / ApplyRestoreMode

    private sealed class SessionSnapshot
    {
        public bool ToastEnabled { get; set; } = true;
        public bool AutoHdrEnabled { get; set; } = true;
        public bool NightLightKnown { get; set; }
        public bool NightLightOn { get; set; }
        public string? AudioDeviceId { get; set; }
        public int? Brightness { get; set; }
        public bool TopologySaved { get; set; }
        public bool ScalingSaved { get; set; }
        public string? ScalingDevice { get; set; }
        public int? ScalingFixedOutput { get; set; }
        public int? ScalingWidth { get; set; }
        public int? ScalingHeight { get; set; }
    }
}

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    public static int? Get()
    {
        int? result = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            uint n = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(h, ref n) || n == 0) return true;
            var arr = new PHYSICAL_MONITOR[n];
            if (!GetPhysicalMonitorsFromHMONITOR(h, n, arr)) return true;
            try
            {
                uint min = 0, cur = 0, max = 0;
                if (GetMonitorBrightness(arr[0].hPhysicalMonitor, ref min, ref cur, ref max) && max > min)
                    result = (int)Math.Round((cur - min) * 100.0 / (max - min));
            }
            finally { DestroyPhysicalMonitors(n, arr); }
            return false; // first only
        }, IntPtr.Zero);
        return result;
    }

    /// <returns>True if at least one monitor accepted the new brightness.</returns>
    public static bool Set(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        bool ok = false;
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
            {
                uint n = 0;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(h, ref n) || n == 0) return true;
                var arr = new PHYSICAL_MONITOR[n];
                if (!GetPhysicalMonitorsFromHMONITOR(h, n, arr)) return true;
                try
                {
                    uint min = 0, cur = 0, max = 0;
                    if (GetMonitorBrightness(arr[0].hPhysicalMonitor, ref min, ref cur, ref max) && max > min)
                    {
                        uint val = min + (uint)Math.Round((max - min) * (percent / 100.0));
                        if (SetMonitorBrightness(arr[0].hPhysicalMonitor, val))
                        {
                            ok = true;
                            AppLog.Info($"Monitor brightness → {percent}%");
                        }
                    }
                }
                finally { DestroyPhysicalMonitors(n, arr); }
                return true; // all monitors
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            AppLog.Error("MonitorBrightness.Set: " + ex.Message);
            return false;
        }
        return ok;
    }
}

internal static class ScalingModeHelper
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int CDS_UPDATEREGISTRY = 0x01;
    private const int DM_DISPLAYFIXEDOUTPUT = 0x20000000;
    private const int DM_PELSWIDTH = 0x80000;
    private const int DM_PELSHEIGHT = 0x100000;
    private const int DMDFO_DEFAULT = 0;
    private const int DMDFO_STRETCH = 1;
    private const int DMDFO_CENTER = 2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    }

    public static int? CaptureFixedOutput(string? device)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm)) return null;
        return dm.dmDisplayFixedOutput;
    }

    public static int? CaptureWidth(string? device)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm)) return null;
        return dm.dmPelsWidth;
    }

    public static int? CaptureHeight(string? device)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm)) return null;
        return dm.dmPelsHeight;
    }

    public static void Apply(string resolution, string mode, string? device)
    {
        var parts = resolution.ToLowerInvariant().Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h))
            return;

        int fixedOut = mode.ToLowerInvariant() switch
        {
            "stretch" or "stretched" => DMDFO_STRETCH,
            "center" or "centered" => DMDFO_CENTER,
            _ => DMDFO_DEFAULT
        };

        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm)) return;
        dm.dmPelsWidth = w;
        dm.dmPelsHeight = h;
        dm.dmDisplayFixedOutput = fixedOut;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFIXEDOUTPUT;
        int r = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        AppLog.Info($"Scaling mode → {mode} ({resolution}) result={r}");
    }

    public static void Restore(int width, int height, int fixedOutput, string? device)
    {
        try
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm)) return;
            dm.dmPelsWidth = width;
            dm.dmPelsHeight = height;
            dm.dmDisplayFixedOutput = fixedOutput;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFIXEDOUTPUT;
            int r = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            AppLog.Info($"Scaling mode restored ({width}x{height}) result={r}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Scaling restore: " + ex.Message);
        }
    }
}

/// <summary>Best-effort: save path array and disable non-primary outputs via CCD.</summary>
internal static class DisplayTopology
{
    private static byte[]? _savedPaths;
    private static byte[]? _savedModes;
    private static int _pathCount;
    private static int _modeCount;

    private const uint QDC_ALL_PATHS = 1;
    private const uint SDC_APPLY = 0x00000080;
    private const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    private const uint SDC_SAVE_TO_DATABASE = 0x00000200;
    private const uint SDC_ALLOW_CHANGES = 0x00000400;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out int numPathArrayElements, out int numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref int numPathArrayElements, [Out] byte[] pathInfoArray,
        ref int numModeInfoArrayElements, [Out] byte[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(int numPathArrayElements, byte[]? pathArray,
        int numModeInfoArrayElements, byte[]? modeInfoArray, uint flags);

    // DISPLAYCONFIG_PATH_INFO is 72 bytes on x64 (flags UINT32 at offset 68).
    // On 32-bit layouts differ — isolate is skipped rather than corrupting topology.
    private static readonly int PathSize = IntPtr.Size == 8 ? 72 : 0;
    private static readonly int ModeSize = IntPtr.Size == 8 ? 64 : 0;
    private const int PathFlagsOffset = 68;

    /// <returns>True if isolate was applied (or already active).</returns>
    public static bool IsolatePrimary()
    {
        try
        {
            if (PathSize == 0)
            {
                AppLog.Info("Isolate primary skipped — unsupported process architecture.");
                return false;
            }

            if (_savedPaths != null)
            {
                AppLog.Info("Isolate primary already active — skip re-capture.");
                return true;
            }

            if (GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out int pc, out int mc) != 0) return false;
            if (pc <= 0 || mc < 0) return false;
            var paths = new byte[checked(pc * PathSize)];
            var modes = new byte[checked(Math.Max(mc, 1) * ModeSize)];
            int pcr = pc, mcr = mc;
            if (QueryDisplayConfig(QDC_ALL_PATHS, ref pcr, paths, ref mcr, modes, IntPtr.Zero) != 0) return false;
            if (pcr <= 0 || paths.Length < pcr * PathSize) return false;

            _savedPaths = (byte[])paths.Clone();
            _savedModes = (byte[])modes.Clone();
            _pathCount = pcr;
            _modeCount = mcr;

            // Clear PATH_ACTIVE (0x1) on non-first paths via flags at offset 68
            for (int i = 1; i < pcr; i++)
            {
                int off = i * PathSize + PathFlagsOffset;
                if (off + 4 <= paths.Length)
                {
                    paths[off] = 0;
                    paths[off + 1] = 0;
                    paths[off + 2] = 0;
                    paths[off + 3] = 0;
                }
            }

            int r = SetDisplayConfig(pcr, paths, mcr, modes, SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES);
            AppLog.Info($"Isolate primary monitors result={r}");
            if (r != 0)
            {
                // Failed apply — drop snapshot so Restore is a no-op
                _savedPaths = null;
                _savedModes = null;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("IsolatePrimary: " + ex.Message);
            _savedPaths = null;
            _savedModes = null;
            return false;
        }
    }

    public static void Restore()
    {
        if (_savedPaths == null || _savedModes == null) return;
        try
        {
            SetDisplayConfig(_pathCount, _savedPaths, _modeCount, _savedModes,
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES);
            AppLog.Info("Display topology restored.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Topology restore: " + ex.Message);
        }
        finally
        {
            _savedPaths = null;
            _savedModes = null;
        }
    }

    public static bool TryExport(out byte[] paths, out byte[] modes, out int pathCount, out int modeCount)
    {
        paths = Array.Empty<byte>();
        modes = Array.Empty<byte>();
        pathCount = 0;
        modeCount = 0;
        if (_savedPaths == null || _savedModes == null) return false;
        paths = (byte[])_savedPaths.Clone();
        modes = (byte[])_savedModes.Clone();
        pathCount = _pathCount;
        modeCount = _modeCount;
        return true;
    }

    /// <summary>
    /// Restore from in-memory isolate snapshot, or from persisted CCD blobs after crash.
    /// </summary>
    public static void RestoreFromSnapshot(Models.ActiveSessionSnapshot snap)
    {
        if (_savedPaths != null && _savedModes != null)
        {
            Restore();
            return;
        }

        if (string.IsNullOrWhiteSpace(snap.TopologyPathsB64)
            || string.IsNullOrWhiteSpace(snap.TopologyModesB64)
            || snap.TopologyPathCount <= 0
            || snap.TopologyModeCount <= 0)
        {
            AppLog.Info("TopologySaved but no CCD blob — cannot restore after crash.");
            return;
        }

        try
        {
            var paths = Convert.FromBase64String(snap.TopologyPathsB64!);
            var modes = Convert.FromBase64String(snap.TopologyModesB64!);
            int r = SetDisplayConfig(
                snap.TopologyPathCount, paths,
                snap.TopologyModeCount, modes,
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES);
            AppLog.Info($"Display topology restored from snapshot blob result={r}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Topology restore from blob: " + ex.Message);
        }
    }
}

/// <summary>Default audio endpoint via PolicyConfig (undocumented COM, widely used).</summary>
public static class AudioEndpoint
{
    public static List<(string Id, string Name)> ListDevices()
    {
        var list = new List<(string, string)>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.EnumAudioEndpoints(EDataFlow.eRender, 1 /* DEVICE_STATE_ACTIVE */, out var coll);
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

    public static string? GetDefaultId()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var dev);
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

    public static void SetDefault(string idOrName)
    {
        try
        {
            string? id = idOrName;
            // Allow friendly-name match
            if (!idOrName.StartsWith("{", StringComparison.Ordinal))
            {
                var match = ListDevices().FirstOrDefault(d =>
                    d.Name.Contains(idOrName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(match.Id)) id = match.Id;
            }
            if (string.IsNullOrWhiteSpace(id)) return;

            var policy = (IPolicyConfigVista)new CPolicyConfigClient();
            policy.SetDefaultEndpoint(id, (int)ERole.eConsole);
            policy.SetDefaultEndpoint(id, (int)ERole.eMultimedia);
            policy.SetDefaultEndpoint(id, (int)ERole.eCommunications);
            Marshal.ReleaseComObject(policy);
            AppLog.Info("Default audio → " + id);
        }
        catch (Exception ex)
        {
            AppLog.Error("SetDefault audio: " + ex.Message);
        }
    }

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    // CLSID_PolicyConfigClient
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

    // IPolicyConfig (Vista+) — only SetDefaultEndpoint used; placeholders keep vtable alignment.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigVista
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
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
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
