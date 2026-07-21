using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

/// <summary>
/// System display / power APIs only (ChangeDisplaySettings, gamma ramp, powercfg).
/// Never opens game process handles.
/// </summary>
public sealed class DisplayEngine
{
    private const string PowerHighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string PowerBalanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private static string ResolveQResPath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "QRes.exe");
        if (File.Exists(beside)) return beside;
        var tools = @"C:\Tools\QRes\QRes.exe";
        if (File.Exists(tools)) return tools;
        return beside;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP ramp);

    [DllImport("gdi32.dll")]
    private static extern bool GetDeviceGammaRamp(IntPtr hDC, ref RAMP ramp);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int CDS_UPDATEREGISTRY = 0x01;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DM_PELSWIDTH = 0x80000;
    private const int DM_PELSHEIGHT = 0x100000;
    private const int DM_DISPLAYFREQUENCY = 0x400000;
    private const int DM_BITSPERPEL = 0x40000;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAMP
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Blue;
    }

    private ColorSettings _liveColor = new();
    private RAMP? _factoryRamp;
    private bool _hasFactoryRamp;
    private readonly DriverColorService _driverColor = new();

    public ColorSettings LiveColor => _liveColor.Clone();

    private ColorSettings? _abPreview;
    private bool _abShowingFactory;

    /// <summary>Identity gamma + driver reset after a dirty shutdown (do not capture bad ramp as factory).</summary>
    public void RestoreCrashSafe()
    {
        try { _driverColor.ResetDriverToNeutral(); }
        catch (Exception ex) { AppLog.Error("Crash-safe driver reset: " + ex.Message); }

        var ramp = new RAMP
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };
        LowLevelColorRamp.FillIdentity(ramp.Red, ramp.Green, ramp.Blue);
        ApplyRamp(ramp);
        _liveColor = ColorSettings.Neutral;
        _abPreview = null;
        _abShowingFactory = false;
        AppLog.Info("Crash-safe restore: identity gamma + driver neutral.");
    }

    /// <summary>Apply color for UI live preview (does not touch profiles).</summary>
    public void PreviewColor(ColorSettings color)
    {
        _abPreview = color.Clone();
        _abShowingFactory = false;
        ApplyColor(color);
    }

    /// <summary>Toggle between last preview and factory/identity ramp. Returns false if no preview yet.</summary>
    public bool ToggleAbCompare()
    {
        if (_abPreview == null) return false;
        if (_abShowingFactory)
        {
            ApplyColor(_abPreview);
            _abShowingFactory = false;
        }
        else
        {
            ApplyNeutralOrFactoryRamp();
            _abShowingFactory = true;
        }
        return true;
    }

    public bool IsAbShowingFactory => _abShowingFactory;

    /// <summary>Call once at startup before any ApplyColor — stores OS gamma for RestoreFactory.</summary>
    public void CaptureFactoryGammaRamp()
    {
        var ramp = new RAMP
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDeviceGammaRamp(hdc, ref ramp))
            {
                _factoryRamp = CloneRamp(ramp);
                _hasFactoryRamp = true;
                AppLog.Info("Factory gamma ramp captured.");
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        // Baseline for vibrance restore — only at startup / after crash-safe, not on Low Level applies.
        _driverColor.CaptureBaselineIfNeeded();
    }

    public void ApplyProfile(GameProfile profile, DefaultSettings defaults)
    {
        if (profile.ApplyPowerPlan && !string.IsNullOrWhiteSpace(profile.PowerPlan))
            SetPowerPlan(profile.PowerPlan);

        if (profile.ApplyResolution && !string.IsNullOrWhiteSpace(profile.Resolution))
        {
            var device = profile.DisplayDevice
                         ?? AppConfigPreferredDevice();
            SetResolution(profile.Resolution!, profile.RefreshRate, device);
        }

        if (profile.ApplyColor)
        {
            _liveColor = profile.Color.Clone();
            _liveColor.Clamp();
            ApplyColor(_liveColor);
        }
    }

    private static string? AppConfigPreferredDevice()
    {
        try
        {
            return DisplayProfileManager.App.Services?.Config?.Current?.Ui?.PreferredDisplayDevice;
        }
        catch
        {
            return null;
        }
    }

    public void RestoreDefaults(DefaultSettings defaults)
    {
        if (!string.IsNullOrWhiteSpace(defaults.PowerPlan))
            SetPowerPlan(defaults.PowerPlan!);

        if (!string.IsNullOrWhiteSpace(defaults.Resolution))
            SetResolution(defaults.Resolution!, defaults.RefreshRate);

        _driverColor.RestoreBaseline();
        _liveColor = defaults.Color.Clone();
        _liveColor.Clamp();
        ApplyColor(_liveColor);
        AppLog.Info($"Restored defaults: res={defaults.Resolution}, power={defaults.PowerPlan}");
    }

    public void ApplyColor(ColorSettings color)
    {
        color.Clamp();
        _liveColor = color.Clone();

        bool lowLevel = color.Backend is ColorBackend.LowLevel or ColorBackend.Gdi;

        if (lowLevel)
        {
            // Restore NVIDIA/AMD only if a previous Driver session left tweaks active — no NvAPI init.
            try { _driverColor.ClearDriverTweaksIfActive(); }
            catch (Exception ex) { AppLog.Error("Driver restore on LowLevel: " + ex.Message); }

            var ramp = BuildRamp(color);
            if (!ApplyRamp(ramp))
                AppLog.Error("SetDeviceGammaRamp rejected or failed (Windows may clip extreme curves).");

            var (b, c, g) = color.ToRivaTunerUnits();
            AppLog.Info($"Color applied (Low Level): B={b} C={c} G={g:F2}");
        }
        else
        {
            // NVIDIA Control Panel–style B/C/G via SetDeviceGammaRamp + Digital Vibrance / ADL sat.
            var ramp = new RAMP
            {
                Red = new ushort[256],
                Green = new ushort[256],
                Blue = new ushort[256]
            };
            NvidiaControlPanelRamp.Fill(ramp.Red, ramp.Green, ramp.Blue, color);
            if (!ApplyRamp(ramp))
                AppLog.Error("NVIDIA CP-style gamma ramp rejected.");

            try { _driverColor.Apply(color); }
            catch (Exception ex) { AppLog.Error("Driver color apply: " + ex.Message); }
            AppLog.Info($"Color applied (Driver): V={color.Vibrance} {_driverColor.ActiveVendor}");
        }
    }

    /// <summary>Identity or captured factory ramp — clears any previous Low Level curve.</summary>
    public void ApplyNeutralOrFactoryRamp()
    {
        _driverColor.ClearDriverTweaksIfActive();
        if (_hasFactoryRamp && _factoryRamp.HasValue)
        {
            ApplyRamp(_factoryRamp.Value);
            return;
        }

        var ramp = new RAMP
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };
        LowLevelColorRamp.FillIdentity(ramp.Red, ramp.Green, ramp.Blue);
        ApplyRamp(ramp);
    }

    /// <summary>Re-apply live ramp only (lock timer). Driver backends re-push vibrance lightly.</summary>
    public void ReapplyLiveColor()
    {
        if (_liveColor.Backend is ColorBackend.LowLevel or ColorBackend.Gdi)
        {
            var ramp = BuildRamp(_liveColor);
            ApplyRamp(ramp);
        }
        else
        {
            var ramp = new RAMP
            {
                Red = new ushort[256],
                Green = new ushort[256],
                Blue = new ushort[256]
            };
            NvidiaControlPanelRamp.Fill(ramp.Red, ramp.Green, ramp.Blue, _liveColor);
            ApplyRamp(ramp);
            try { _driverColor.Apply(_liveColor); }
            catch (Exception ex) { AppLog.Error("Driver lock reapply: " + ex.Message); }
        }
    }

    public DriverColorSnapshot? CaptureDriverColorCurrent() => _driverColor.CaptureCurrent();

    public void RestoreDriverColorSnapshot(DriverColorSnapshot snap) =>
        _driverColor.RestoreFromSnapshot(snap);

    public void ClearDriverTweaksIfActive() => _driverColor.ClearDriverTweaksIfActive();

    public void AdjustBrightness(double delta)
    {
        var c = _liveColor.Clone();
        c.Brightness += delta;
        c.Clamp();
        ApplyColor(c);
    }

    public void AdjustContrast(double delta)
    {
        var c = _liveColor.Clone();
        c.Contrast += delta;
        c.Clamp();
        ApplyColor(c);
    }

    public void AdjustGamma(double delta)
    {
        var c = _liveColor.Clone();
        c.Gamma += delta;
        c.Clamp();
        ApplyColor(c);
    }

    /// <summary>Adjust ShadowLift (0..0.4). Delta is in ShadowLift units, not 0..100 UI.</summary>
    public void AdjustShadowLift(double delta)
    {
        var c = _liveColor.Clone();
        if (c.Backend is not ColorBackend.LowLevel and not ColorBackend.Gdi)
            return;
        c.ShadowLift = Math.Clamp(c.ShadowLift + delta, 0.0, 0.4);
        ApplyColor(c);
    }

    public void ClearAbCompare()
    {
        _abPreview = null;
        _abShowingFactory = false;
    }

    public void ResetColor(ColorSettings defaults)
    {
        var c = defaults?.Clone() ?? ColorSettings.Neutral;
        if (c.Brightness < 0.05 || c.Brightness > 0.95)
            c = ColorSettings.Neutral;
        ApplyColor(c);
    }

    public void ResetColorToFactoryOrNeutral(ColorSettings? configured)
    {
        _driverColor.RestoreBaseline();
        if (_hasFactoryRamp && _factoryRamp.HasValue)
        {
            ApplyRamp(_factoryRamp.Value);
            _liveColor = ColorSettings.Neutral;
            AppLog.Info("Color reset to captured factory ramp.");
            return;
        }
        ResetColor(configured ?? ColorSettings.Neutral);
    }

    public void ApplyIdentityColor() => ApplyColor(ColorSettings.Neutral);

    public void RestoreFactory(DefaultSettings factory)
    {
        var f = factory ?? new DefaultSettings();
        if (string.IsNullOrWhiteSpace(f.Resolution))
            f.Resolution = GetCurrentResolution();

        if (!string.IsNullOrWhiteSpace(f.PowerPlan))
            SetPowerPlan(f.PowerPlan!);
        if (!string.IsNullOrWhiteSpace(f.Resolution))
            SetResolution(f.Resolution!, f.RefreshRate);

        _driverColor.RestoreBaseline();
        if (_hasFactoryRamp && _factoryRamp.HasValue)
        {
            ApplyRamp(_factoryRamp.Value);
            _liveColor = ColorSettings.Neutral;
        }
        else
        {
            var c = f.Color ?? ColorSettings.Neutral;
            if (c.Brightness < 0.05 || c.Brightness > 0.95)
                c = ColorSettings.Neutral;
            ApplyColor(c);
        }

        AppLog.Info($"Factory restored: res={f.Resolution}");
    }

    private bool ApplyRamp(RAMP ramp)
    {
        // Prefer CreateDC("DISPLAY") — same path many gamma tools / RT GDI mode use.
        IntPtr hdc = CreateDC("DISPLAY", null, null, IntPtr.Zero);
        bool created = hdc != IntPtr.Zero;
        if (!created)
            hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            AppLog.Error("No HDC for gamma ramp.");
            return false;
        }

        try
        {
            bool ok = SetDeviceGammaRamp(hdc, ref ramp);
            if (!ok)
            {
                AppLog.Error("SetDeviceGammaRamp returned false.");
                return false;
            }

            // Detect silent reject: Windows can return TRUE but leave identity in place.
            var check = new RAMP
            {
                Red = new ushort[256],
                Green = new ushort[256],
                Blue = new ushort[256]
            };
            if (GetDeviceGammaRamp(hdc, ref check))
            {
                int expect = LowLevelColorRamp.UnpackRampByte(ramp.Red[128]);
                int actual = LowLevelColorRamp.UnpackRampByte(check.Red[128]);
                if (Math.Abs(expect - actual) > 8)
                {
                    AppLog.Error($"Gamma ramp mismatch at mid: set≈{expect} got≈{actual} (driver may have clamped).");
                    return false;
                }
            }
            return true;
        }
        finally
        {
            if (created) DeleteDC(hdc);
            else ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private static RAMP CloneRamp(RAMP src) => new()
    {
        Red = (ushort[])src.Red.Clone(),
        Green = (ushort[])src.Green.Clone(),
        Blue = (ushort[])src.Blue.Clone()
    };

    private static RAMP BuildRamp(ColorSettings color)
    {
        var ramp = new RAMP
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };

        if (color.Backend == ColorBackend.LowLevel)
        {
            LowLevelColorRamp.Fill(ramp.Red, ramp.Green, ramp.Blue, color);
            return ramp;
        }

        if (GpuVendorDetect.IsDriverSide(color.Backend))
        {
            NvidiaControlPanelRamp.Fill(ramp.Red, ramp.Green, ramp.Blue, color);
            return ramp;
        }

        // Legacy soft GDI curve (non-RT). Uses float domain + full 16-bit precision.
        double brightnessOffset = (color.Brightness - 0.5) * 2.0;
        double contrast = Math.Max(0.01, color.Contrast);
        double gamma = Math.Max(0.01, color.Gamma);
        double shadowLift = Math.Clamp(color.ShadowLift, 0.0, 0.4);

        for (int i = 0; i < 256; i++)
        {
            double normalized = i / 255.0;
            double value = (normalized - 0.5) * contrast + 0.5;
            value += brightnessOffset * 0.5;
            value = Math.Pow(Math.Clamp(value, 0.0, 1.0), 1.0 / gamma);

            if (shadowLift > 0.0001)
            {
                double lift = shadowLift * (1.0 - value) * (1.0 - value);
                value = Math.Clamp(value + lift, 0.0, 1.0);
            }

            value = Math.Clamp(value, 0.0, 1.0);
            // Keep MSB packing consistent with Low Level / GDI docs
            int b = (int)Math.Clamp(Math.Round(value * 255.0), 0, 255);
            ushort word = LowLevelColorRamp.PackRampByte(b);
            ramp.Red[i] = word;
            ramp.Green[i] = word;
            ramp.Blue[i] = word;
        }

        return ramp;
    }

    public bool SetResolution(string resolution, int refreshRate = 0, string? deviceName = null)
    {
        var parts = resolution.ToLowerInvariant().Split('x');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out int width) ||
            !int.TryParse(parts[1], out int height))
        {
            AppLog.Error($"Invalid resolution: {resolution}");
            return false;
        }

        if (width < 640 || height < 480 || width > 7680 || height > 4320)
        {
            AppLog.Error($"Resolution out of range: {resolution}");
            return false;
        }

        string? device = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim();

        var mode = new DEVMODE();
        mode.dmSize = (short)Marshal.SizeOf<DEVMODE>();
        if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref mode))
            return SetResolutionViaQRes(width, height);

        mode.dmPelsWidth = width;
        mode.dmPelsHeight = height;
        mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL;
        if (mode.dmBitsPerPel == 0) mode.dmBitsPerPel = 32;

        if (refreshRate > 0)
        {
            mode.dmDisplayFrequency = refreshRate;
            mode.dmFields |= DM_DISPLAYFREQUENCY;
        }

        int result = ChangeDisplaySettingsEx(device, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (result != DISP_CHANGE_SUCCESSFUL)
        {
            AppLog.Error($"ChangeDisplaySettingsEx failed ({result}), trying QRes.");
            return SetResolutionViaQRes(width, height);
        }

        AppLog.Info($"Resolution → {resolution}" + (refreshRate > 0 ? $"@{refreshRate}" : "")
                    + (device != null ? $" on {device}" : ""));
        return true;
    }

    public static List<(string DeviceName, string Friendly, bool Primary)> GetDisplays()
    {
        var list = new List<(string, string, bool)>();
        uint i = 0;
        while (true)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            i++;
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            bool primary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
            string friendly = string.IsNullOrWhiteSpace(dd.DeviceString) ? dd.DeviceName : dd.DeviceString;
            list.Add((dd.DeviceName, friendly, primary));
        }
        return list;
    }

    private static bool SetResolutionViaQRes(int width, int height)
    {
        var qres = ResolveQResPath();
        if (!File.Exists(qres))
        {
            AppLog.Error("QRes.exe not found.");
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = qres,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add($"/x:{width}");
            psi.ArgumentList.Add($"/y:{height}");

            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { }
                AppLog.Error("QRes timed out.");
                return false;
            }

            if (p.ExitCode != 0)
            {
                AppLog.Error($"QRes exit code {p.ExitCode}");
                return false;
            }

            AppLog.Info($"Resolution via QRes → {width}x{height}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"QRes failed: {ex.Message}");
            return false;
        }
    }

    public void SetPowerPlan(string plan)
    {
        string guid = plan.ToLowerInvariant() switch
        {
            "highperformance" or "high" or "high_performance" => PowerHighPerf,
            "balanced" => PowerBalanced,
            _ when Guid.TryParse(plan, out _) => plan,
            _ => PowerBalanced
        };

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/setactive");
            psi.ArgumentList.Add(guid);

            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            AppLog.Info($"Power plan → {plan}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"powercfg failed: {ex.Message}");
        }
    }

    public static List<string> GetAvailableResolutions()
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        var mode = new DEVMODE();
        mode.dmSize = (short)Marshal.SizeOf<DEVMODE>();
        int i = 0;
        while (EnumDisplaySettings(null, i, ref mode))
        {
            if (mode.dmPelsWidth >= 800 && mode.dmBitsPerPel >= 32)
                set.Add($"{mode.dmPelsWidth}x{mode.dmPelsHeight}");
            i++;
        }
        return set.ToList();
    }

    public static string GetCurrentResolution()
    {
        var mode = new DEVMODE();
        mode.dmSize = (short)Marshal.SizeOf<DEVMODE>();
        if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref mode))
            return $"{mode.dmPelsWidth}x{mode.dmPelsHeight}";
        return "1920x1080";
    }

    public static (string Resolution, int RefreshRate, string? Device) GetCurrentMode(string? deviceName = null)
    {
        string? device = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim();
        var mode = new DEVMODE();
        mode.dmSize = (short)Marshal.SizeOf<DEVMODE>();
        if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref mode))
            return (GetCurrentResolution(), 0, device);
        return ($"{mode.dmPelsWidth}x{mode.dmPelsHeight}", mode.dmDisplayFrequency, device);
    }

    /// <summary>Capture current gamma ramp as three 256-entry ushort arrays.</summary>
    public bool TryCaptureGammaRamp(out ushort[] red, out ushort[] green, out ushort[] blue)
    {
        red = new ushort[256];
        green = new ushort[256];
        blue = new ushort[256];
        var ramp = new RAMP { Red = red, Green = green, Blue = blue };
        IntPtr hdc = CreateDC("DISPLAY", null, null, IntPtr.Zero);
        bool created = hdc != IntPtr.Zero;
        if (!created) hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        try
        {
            if (!GetDeviceGammaRamp(hdc, ref ramp)) return false;
            red = (ushort[])ramp.Red.Clone();
            green = (ushort[])ramp.Green.Clone();
            blue = (ushort[])ramp.Blue.Clone();
            return true;
        }
        finally
        {
            if (created) DeleteDC(hdc);
            else ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    public bool ApplyCapturedGammaRamp(ushort[] red, ushort[] green, ushort[] blue)
    {
        if (red.Length != 256 || green.Length != 256 || blue.Length != 256) return false;
        var ramp = new RAMP
        {
            Red = (ushort[])red.Clone(),
            Green = (ushort[])green.Clone(),
            Blue = (ushort[])blue.Clone()
        };
        return ApplyRamp(ramp);
    }

    /// <summary>Active power scheme GUID via powercfg /getactivescheme (best-effort).</summary>
    public static string? GetActivePowerPlanGuid()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = "/getactivescheme",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            // GUID: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
            var m = System.Text.RegularExpressions.Regex.Match(
                output,
                @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            return m.Success ? m.Value : null;
        }
        catch (Exception ex)
        {
            AppLog.Error("GetActivePowerPlanGuid: " + ex.Message);
            return null;
        }
    }

    public void DisposeDriverColor()
    {
        // Restore driver vibrance baseline only — do not force factory gamma here.
        // Exit / EmergencyRestore already restored the intended display state.
        try { _driverColor.RestoreBaseline(); } catch { }
        try { _driverColor.Dispose(); } catch { }
    }
}
