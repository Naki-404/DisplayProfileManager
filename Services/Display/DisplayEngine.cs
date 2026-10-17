using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Visor.Models;
using static Visor.Services.DisplayNative;

namespace Visor.Services;

/// <summary>
/// System display / power APIs only (ChangeDisplaySettingsEx, per-monitor gamma ramp, power schemes).
/// Never opens game process handles.
/// </summary>
public sealed class DisplayEngine
{
    private const string PowerHighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string PowerBalanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private const string PowerUltimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string BaselineFileName = "display-baseline.json";

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static readonly JsonSerializerOptions BaselineJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string? _stateDirectory;
    private readonly DriverColorService _driverColor = new();
    /// <summary>Pre-Visor gamma ramp per display device (calibration LUT) — the "factory" color state.</summary>
    private readonly Dictionary<string, RAMP> _baselineRamps = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Guards <see cref="_baselineRamps"/>: the deferred baseline save reads it from a pool thread.</summary>
    private readonly object _rampLock = new();
    private ColorSettings _liveColor = new();
    private ColorSettings? _abPreview;
    private bool _abShowingFactory;
    private string? _colorTargetDevice;
    /// <summary>True once Visor wrote a non-baseline ramp / driver color since the last baseline reset.</summary>
    private bool _colorChanged;

    /// <param name="stateDirectory">Config folder for display-baseline.json (null = not persisted).</param>
    public DisplayEngine(string? stateDirectory = null)
    {
        _stateDirectory = stateDirectory;
    }

    public ColorSettings LiveColor => _liveColor.Clone();

    public bool IsAbShowingFactory => _abShowingFactory;

    /// <summary>
    /// Display that color (gamma + driver) is applied to. Set by ProfileMonitor to the active
    /// profile's device; null = preferred display from settings, else primary.
    /// </summary>
    public string? ColorTargetDevice
    {
        get => _colorTargetDevice;
        set
        {
            _colorTargetDevice = NullIfEmpty(value);
            _driverColor.SetTargetDevice(ResolveDeviceName(_colorTargetDevice));
        }
    }

    /// <summary>Explicit device, else the user's preferred display, else the primary display.</summary>
    public static string? ResolveDeviceName(string? device)
    {
        var d = NullIfEmpty(device) ?? NullIfEmpty(AppConfigPreferredDevice());
        return d ?? PrimaryDeviceName();
    }

    private static string? AppConfigPreferredDevice()
    {
        try
        {
            return Visor.App.Services?.Config?.Current?.Ui?.PreferredDisplayDevice;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ baseline (calibration) state

    /// <summary>
    /// Call once at startup before any color apply. Captures each display's gamma ramp and the driver
    /// vibrance as the baseline, and saves it to display-baseline.json.
    /// </summary>
    /// <param name="useSavedBaseline">
    /// After a dirty exit with no session snapshot the live ramp may still be a Visor curve: re-apply
    /// the baseline saved by the last clean start instead (if there is none, gamma is left alone).
    /// </param>
    /// <param name="deferDriverBaseline">
    /// True at app start: only the (fast, GDI) gamma ramps are captured here; the driver color
    /// baseline (WMI vendor detect + NvAPI / ADL init, slow) and the baseline file save are left to
    /// <see cref="CompleteBaselineCapture"/>, which the app runs on a background thread once the
    /// window is up. Safe because a Driver apply captures a missing baseline itself before writing.
    /// </param>
    /// <returns>True when the saved baseline was re-applied.</returns>
    public bool InitializeBaseline(bool useSavedBaseline, bool deferDriverBaseline = false)
    {
        bool restored = false;
        if (useSavedBaseline && TryLoadBaselineFile(out var saved))
        {
            var attached = new HashSet<string>(EnumerateDisplays().Select(d => d.DeviceName), StringComparer.OrdinalIgnoreCase);
            foreach (var (device, channels) in saved.Ramps)
            {
                if (!attached.Contains(device) || channels is not { Length: 3 }) continue;
                if (!TryDecodeRamp(channels[0], channels[1], channels[2], out var ramp)) continue;
                if (TrySetGammaRamp(device, ramp, out var err))
                {
                    lock (_rampLock) _baselineRamps[device] = ramp;
                    restored = true;
                }
                else
                {
                    AppLog.Warn($"Saved baseline ramp for {device} rejected: {err}");
                }
            }

            foreach (var drv in saved.Drivers ?? new List<DriverColorSnapshot>())
            {
                if (_driverColor.RestoreFromSnapshot(drv))
                    restored = true;
            }

            if (restored)
                AppLog.Info("Dirty restart without a session snapshot — re-applied the saved display baseline.");
        }
        else if (useSavedBaseline)
        {
            AppLog.Info("Dirty restart without a session snapshot or saved baseline — gamma left as is.");
        }

        foreach (var (device, _, _) in EnumerateDisplays())
            CaptureBaselineRamp(device);

        if (!deferDriverBaseline)
            CompleteBaselineCapture();
        return restored;
    }

    /// <summary>
    /// Second half of <see cref="InitializeBaseline"/>: driver color baseline (may init NvAPI /
    /// ADL) and the display-baseline.json save. Thread-safe — intended for a background thread.
    /// </summary>
    public void CompleteBaselineCapture()
    {
        try { _driverColor.CaptureBaselineIfNeeded(); }
        catch (Exception ex) { AppLog.Error(ex, "Driver color baseline capture"); }
        SaveBaselineFile();
    }

    private void CaptureBaselineRamp(string device)
    {
        lock (_rampLock)
        {
            if (_baselineRamps.ContainsKey(device)) return;
        }
        if (TryGetGammaRamp(device, out var ramp))
        {
            lock (_rampLock) _baselineRamps.TryAdd(device, ramp.Clone());
            AppLog.Info($"Baseline gamma ramp captured for {device}.");
        }
        else
        {
            AppLog.Warn($"Could not read gamma ramp for {device}.");
        }
    }

    private KeyValuePair<string, RAMP>[] BaselineRampsSnapshot()
    {
        lock (_rampLock) return _baselineRamps.ToArray();
    }

    private sealed class BaselineFile
    {
        public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
        /// <summary>device → [red, green, blue] base64.</summary>
        public Dictionary<string, string[]> Ramps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<DriverColorSnapshot>? Drivers { get; set; }
    }

    private string? BaselinePath => _stateDirectory == null ? null : Path.Combine(_stateDirectory, BaselineFileName);

    private void SaveBaselineFile()
    {
        var path = BaselinePath;
        if (path == null) return;
        try
        {
            var file = new BaselineFile { Drivers = _driverColor.Baselines.ToList() };
            foreach (var (device, ramp) in BaselineRampsSnapshot())
                file.Ramps[device] = new[] { EncodeChannel(ramp.Red), EncodeChannel(ramp.Green), EncodeChannel(ramp.Blue) };
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, BaselineJson));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Save display baseline: " + ex.Message);
        }
    }

    private bool TryLoadBaselineFile(out BaselineFile file)
    {
        file = new BaselineFile();
        var path = BaselinePath;
        if (path == null || !File.Exists(path)) return false;
        try
        {
            var loaded = JsonSerializer.Deserialize<BaselineFile>(File.ReadAllText(path), BaselineJson);
            if (loaded == null) return false;
            loaded.Ramps ??= new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            file = loaded;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Load display baseline");
            return false;
        }
    }

    /// <summary>
    /// Re-apply the captured baseline gamma (every display Visor knows) and driver color.
    /// Never touches resolution or power plan. Displays without a captured baseline are left alone.
    /// </summary>
    /// <param name="onlyIfChanged">Skip when Visor has not changed color since start / the last reset (app exit).</param>
    public bool RestoreBaselineColor(bool onlyIfChanged = false)
    {
        if (onlyIfChanged && !_colorChanged)
            return true;
        bool ok = true;
        try { ok &= _driverColor.RestoreBaseline(); }
        catch (Exception ex) { ok = false; AppLog.Error("Driver baseline restore: " + ex.Message); }

        var ramps = BaselineRampsSnapshot();
        foreach (var (device, ramp) in ramps)
        {
            if (!TrySetGammaRamp(device, ramp, out var err))
            {
                ok = false;
                AppLog.Warn($"Baseline ramp restore on {device}: {err}");
            }
        }

        _liveColor = ColorSettings.Neutral;
        _abPreview = null;
        _abShowingFactory = false;
        _colorChanged = false;
        AppLog.Info($"Color reset to baseline ({ramps.Length} display(s)).");
        return ok;
    }

    // ------------------------------------------------------------------ color

    /// <summary>Apply color for UI live preview (does not touch profiles).</summary>
    public void PreviewColor(ColorSettings color)
    {
        _abPreview = color.Clone();
        _abShowingFactory = false;
        ApplyColor(color);
    }

    /// <summary>Toggle between last preview and the baseline ramp. Returns false if no preview yet.</summary>
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
            ApplyBaselineRampToTarget();
            _abShowingFactory = true;
        }
        return true;
    }

    public void ClearAbCompare()
    {
        _abPreview = null;
        _abShowingFactory = false;
    }

    public void ApplyColor(ColorSettings color)
    {
        color.Clamp();
        _liveColor = color.Clone();
        _colorChanged = true;
        var device = ResolveDeviceName(_colorTargetDevice);
        if (device != null) CaptureBaselineRamp(device);

        if (color.Backend is ColorBackend.LowLevel or ColorBackend.Gdi)
        {
            // Restore NVIDIA/AMD only if a previous Driver session left tweaks active — no NvAPI init.
            try { _driverColor.ClearDriverTweaksIfActive(); }
            catch (Exception ex) { AppLog.Error("Driver restore on LowLevel: " + ex.Message); }

            ApplyRamp(device, BuildRamp(color));
            var (b, c, g) = color.ToRivaTunerUnits();
            AppLog.Info($"Color applied (Low Level): B={b} C={c} G={g:F2}" + (device != null ? $" on {device}" : ""));
        }
        else
        {
            // NVIDIA Control Panel–style B/C/G via SetDeviceGammaRamp + Digital Vibrance / ADL sat.
            ApplyRamp(device, BuildRamp(color));
            try
            {
                _driverColor.SetTargetDevice(device);
                _driverColor.Apply(color);
            }
            catch (Exception ex) { AppLog.Error("Driver color apply: " + ex.Message); }
            AppLog.Info($"Color applied (Driver): V={color.Vibrance} {_driverColor.ActiveVendor}" + (device != null ? $" on {device}" : ""));
        }
    }

    /// <summary>Re-apply live ramp only (lock timer). Driver backends re-push vibrance lightly.</summary>
    public void ReapplyLiveColor()
    {
        var device = ResolveDeviceName(_colorTargetDevice);
        TrySetGammaRamp(device, BuildRamp(_liveColor), out _);
        if (_liveColor.Backend is not (ColorBackend.LowLevel or ColorBackend.Gdi))
        {
            try { _driverColor.Apply(_liveColor); }
            catch (Exception ex) { AppLog.Error("Driver lock reapply: " + ex.Message); }
        }
    }

    /// <summary>
    /// Puts the color target back to its baseline (calibration) ramp and undoes driver tweaks —
    /// e.g. when a desktop preview ends. Identity only if no baseline was ever captured.
    /// </summary>
    public void ApplyNeutralOrFactoryRamp()
    {
        ApplyBaselineRampToTarget();
        _liveColor = ColorSettings.Neutral;
    }

    /// <summary>Baseline ramp of the color target (A/B "factory" side). Identity only if nothing was captured.</summary>
    private void ApplyBaselineRampToTarget()
    {
        _driverColor.ClearDriverTweaksIfActive();
        var device = ResolveDeviceName(_colorTargetDevice);
        bool hasBaseline = false;
        RAMP baseline = default;
        if (device != null)
            lock (_rampLock) hasBaseline = _baselineRamps.TryGetValue(device, out baseline);
        if (hasBaseline)
        {
            ApplyRamp(device, baseline);
            return;
        }

        var ramp = RAMP.Create();
        LowLevelColorRamp.FillIdentity(ramp.Red, ramp.Green, ramp.Blue);
        ApplyRamp(device, ramp);
    }

    public DriverColorSnapshot? CaptureDriverColorCurrent() => _driverColor.CaptureCurrent();

    public bool RestoreDriverColorSnapshot(DriverColorSnapshot snap) =>
        _driverColor.RestoreFromSnapshot(snap);

    public void ClearDriverTweaksIfActive() => _driverColor.ClearDriverTweaksIfActive();

    private static bool ApplyRamp(string? device, RAMP ramp)
    {
        if (TrySetGammaRamp(device, ramp, out var err)) return true;
        AppLog.Error((device != null ? $"[{device}] " : "") + err + " (Windows may clip extreme curves)");
        return false;
    }

    private static RAMP BuildRamp(ColorSettings color)
    {
        var ramp = RAMP.Create();

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

    /// <summary>Capture a display's current gamma ramp as three 256-entry arrays.</summary>
    public bool TryCaptureGammaRamp(string? device, out ushort[] red, out ushort[] green, out ushort[] blue)
    {
        red = green = blue = Array.Empty<ushort>();
        if (!TryGetGammaRamp(ResolveDeviceName(device), out var ramp)) return false;
        red = ramp.Red;
        green = ramp.Green;
        blue = ramp.Blue;
        return true;
    }

    public bool ApplyCapturedGammaRamp(string? device, ushort[] red, ushort[] green, ushort[] blue)
    {
        if (red.Length != 256 || green.Length != 256 || blue.Length != 256) return false;
        var ramp = new RAMP
        {
            Red = (ushort[])red.Clone(),
            Green = (ushort[])green.Clone(),
            Blue = (ushort[])blue.Clone()
        };
        return ApplyRamp(ResolveDeviceName(device), ramp);
    }

    // ------------------------------------------------------------------ profile / defaults

    /// <summary>Manual (non-session) apply of a profile's power plan + resolution. Color is presets-only.</summary>
    public void ApplyProfile(GameProfile profile, DefaultSettings defaults)
    {
        if (profile.ApplyPowerPlan && !string.IsNullOrWhiteSpace(profile.PowerPlan))
            SetPowerPlan(profile.PowerPlan);

        if (profile.ApplyResolution && !string.IsNullOrWhiteSpace(profile.Resolution))
            SetResolution(profile.Resolution!, profile.RefreshRate, profile.DisplayDevice ?? AppConfigPreferredDevice());
    }

    /// <summary>Global defaults: power plan / resolution when set, then color (neutral = baseline ramp, never identity).</summary>
    public void RestoreDefaults(DefaultSettings defaults)
    {
        if (!string.IsNullOrWhiteSpace(defaults.PowerPlan))
            SetPowerPlan(defaults.PowerPlan!);

        if (!string.IsNullOrWhiteSpace(defaults.Resolution))
            SetResolution(defaults.Resolution!, defaults.RefreshRate, AppConfigPreferredDevice());

        var color = (defaults.Color ?? ColorSettings.Neutral).Clone();
        color.Clamp();
        if (IsNeutral(color))
        {
            RestoreBaselineColor();
        }
        else
        {
            _driverColor.RestoreBaseline();
            ApplyColor(color);
        }
        AppLog.Info($"Restored defaults: res={defaults.Resolution}, power={defaults.PowerPlan ?? "(unchanged)"}");
    }

    private static bool IsNeutral(ColorSettings c) =>
        Math.Abs(c.Brightness - 0.5) < 0.005 && Math.Abs(c.Contrast - 1.0) < 0.005
        && Math.Abs(c.Gamma - 1.0) < 0.005 && c.ShadowLift < 0.0001 && c.Vibrance == 50 && c.Hue == 0;

    // ------------------------------------------------------------------ resolution

    /// <summary>
    /// Dynamic mode change (not written to the registry, so a power loss mid-game boots back to the
    /// desktop mode). No-op when the display already runs the requested mode.
    /// </summary>
    public bool SetResolution(string resolution, int refreshRate = 0, string? deviceName = null)
    {
        if (!TryParseRes(resolution, out int width, out int height) || width > 7680 || height > 4320)
        {
            AppLog.Error($"Invalid resolution: {resolution}");
            return false;
        }

        string? device = NullIfEmpty(deviceName);
        if (!TryGetCurrentMode(device, out var mode))
        {
            AppLog.Error($"EnumDisplaySettings failed for {device ?? "primary display"}.");
            return false;
        }

        if (mode.dmPelsWidth == width && mode.dmPelsHeight == height
            && (refreshRate <= 0 || mode.dmDisplayFrequency == refreshRate))
        {
            AppLog.Info($"Resolution already {width}x{height}" + (refreshRate > 0 ? $"@{refreshRate}" : "")
                        + (device != null ? $" on {device}" : "") + " — skipped.");
            return true;
        }

        mode.dmPelsWidth = width;
        mode.dmPelsHeight = height;
        mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL;
        if (mode.dmBitsPerPel == 0) mode.dmBitsPerPel = 32;
        if (refreshRate > 0)
        {
            mode.dmDisplayFrequency = refreshRate;
            mode.dmFields |= DM_DISPLAYFREQUENCY;
        }

        int result = ChangeDisplaySettingsEx(device, ref mode, IntPtr.Zero, CDS_DYNAMIC, IntPtr.Zero);
        if (result != DISP_CHANGE_SUCCESSFUL && refreshRate > 0)
        {
            AppLog.Warn($"ChangeDisplaySettingsEx {width}x{height}@{refreshRate} failed ({result}) — retrying without refresh rate.");
            mode.dmFields &= ~DM_DISPLAYFREQUENCY;
            result = ChangeDisplaySettingsEx(device, ref mode, IntPtr.Zero, CDS_DYNAMIC, IntPtr.Zero);
        }

        if (result != DISP_CHANGE_SUCCESSFUL)
        {
            AppLog.Error($"ChangeDisplaySettingsEx failed ({result}) for {width}x{height}" + (device != null ? $" on {device}" : ""));
            return false;
        }

        AppLog.Info($"Resolution → {width}x{height}" + (refreshRate > 0 ? $"@{refreshRate}" : "")
                    + (device != null ? $" on {device}" : ""));
        return true;
    }

    public static List<(string DeviceName, string Friendly, bool Primary)> GetDisplays() => EnumerateDisplays();

    /// <summary>Distinct WxH modes (32-bit, ≥800 wide), largest first (numeric, not string order).</summary>
    public static List<string> GetAvailableResolutions()
    {
        var set = new HashSet<(int W, int H)>();
        var mode = NewDevMode();
        int i = 0;
        while (EnumDisplaySettings(null, i, ref mode))
        {
            if (mode.dmPelsWidth >= 800 && mode.dmBitsPerPel >= 32)
                set.Add((mode.dmPelsWidth, mode.dmPelsHeight));
            i++;
        }
        return SortResolutions(set);
    }

    /// <summary>Distinct WxH strings, largest first by width then height (numeric, not string order).</summary>
    internal static List<string> SortResolutions(IEnumerable<(int W, int H)> modes) =>
        modes
            .Distinct()
            .OrderByDescending(r => r.W)
            .ThenByDescending(r => r.H)
            .Select(r => $"{r.W}x{r.H}")
            .ToList();

    /// <summary>Refresh rates available for a WxH mode (descending).</summary>
    public static List<int> GetAvailableRefreshRates(string? resolution, string? deviceName = null)
    {
        var set = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        if (!TryParseRes(resolution, out int w, out int h))
            return set.ToList();

        string? device = NullIfEmpty(deviceName);
        var mode = NewDevMode();
        int i = 0;
        while (EnumDisplaySettings(device, i, ref mode))
        {
            if (mode.dmPelsWidth == w && mode.dmPelsHeight == h && mode.dmDisplayFrequency > 0)
                set.Add(mode.dmDisplayFrequency);
            i++;
        }
        return set.ToList();
    }

    public static bool TryParseRes(string? resolution, out int width, out int height)
    {
        width = height = 0;
        if (string.IsNullOrWhiteSpace(resolution)) return false;
        var parts = resolution.Trim().ToLowerInvariant().Split('x', '×');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0].Trim(), out width)
               && int.TryParse(parts[1].Trim(), out height)
               && width >= 640 && height >= 480;
    }

    public static string GetCurrentResolution()
    {
        if (TryGetCurrentMode(null, out var mode))
            return $"{mode.dmPelsWidth}x{mode.dmPelsHeight}";
        return "1920x1080";
    }

    public static (string Resolution, int RefreshRate, string? Device) GetCurrentMode(string? deviceName = null)
    {
        string? device = NullIfEmpty(deviceName);
        if (!TryGetCurrentMode(device, out var mode))
            return (GetCurrentResolution(), 0, device);
        return ($"{mode.dmPelsWidth}x{mode.dmPelsHeight}", mode.dmDisplayFrequency, device);
    }

    // ------------------------------------------------------------------ power plan

    /// <summary>
    /// Finds the installed Ultimate Performance scheme. e9a42b02-… is only the hidden template: on standard
    /// installs the usable scheme is a duplicate with its own GUID, so activating the template fails with
    /// "scheme not installed". Set at startup (UltimatePlanService.FindInstalled); null in tests.
    /// </summary>
    public static Func<string?>? UltimateResolver { get; set; }

    private static string? InstalledUltimate()
    {
        try { return UltimateResolver?.Invoke(); }
        catch (Exception ex) { AppLog.Warn(ex, "Ultimate plan resolve"); return null; }
    }

    /// <summary>Plan alias (highPerformance / balanced / powerSaver / ultimate) or GUID → scheme GUID; null if unknown.</summary>
    public static string? ResolvePowerPlanGuid(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan)) return null;
        var p = plan.Trim();
        if (Guid.TryParse(p, out var g)) return g.ToString("D");
        return p.ToLowerInvariant() switch
        {
            "highperformance" or "high" or "high_performance" => PowerHighPerf,
            "balanced" => PowerBalanced,
            "powersaver" or "power_saver" or "saver" => PowerSaver,
            "ultimate" or "ultimateperformance" or "ultimate_performance" => InstalledUltimate() ?? PowerUltimate,
            _ => null
        };
    }

    /// <summary>UI alias for a stock scheme GUID ("balanced" / "highPerformance" / …), else null.</summary>
    public static string? PowerPlanAlias(string guid)
    {
        var g = guid.ToLowerInvariant();
        switch (g)
        {
            case PowerBalanced: return "balanced";
            case PowerHighPerf: return "highPerformance";
            case PowerSaver: return "powerSaver";
            case PowerUltimate: return "ultimate";
        }
        return string.Equals(g, InstalledUltimate(), StringComparison.OrdinalIgnoreCase) ? "ultimate" : null;
    }

    /// <summary>Activates a power scheme via PowerSetActiveScheme (no powercfg child process).</summary>
    /// <returns>True when the scheme is verified active afterwards. Unknown plan names are rejected, never mapped to Balanced.</returns>
    public bool SetPowerPlan(string plan) => TrySetPowerPlan(plan) == PowerPlanResult.Applied;

    /// <summary>
    /// As <see cref="SetPowerPlan"/>, but distinguishes "the API accepted the change, the read-back
    /// disagreed or failed" (<see cref="PowerPlanResult.AppliedUnverified"/>) from a real failure —
    /// a session must still record the plan as changed in the first case so it is restored later.
    /// </summary>
    public PowerPlanResult TrySetPowerPlan(string plan)
    {
        var guidText = ResolvePowerPlanGuid(plan);
        if (guidText == null)
        {
            AppLog.Error($"Unknown power plan '{plan}' — left unchanged.");
            return PowerPlanResult.Failed;
        }

        if (string.Equals(GetActivePowerPlanGuid(), guidText, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Info($"Power plan already {plan}.");
            return PowerPlanResult.Applied;
        }

        try
        {
            var guid = Guid.Parse(guidText);
            uint rc = PowerSetActiveScheme(IntPtr.Zero, ref guid);
            if (rc != 0)
            {
                AppLog.Error($"Power plan {plan} not applied (PowerSetActiveScheme error {rc}" +
                             (rc == 2 ? " — scheme not installed" : "") + ").");
                return PowerPlanResult.Failed;
            }
            var active = GetActivePowerPlanGuid();
            if (!string.Equals(active, guidText, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Error($"Power plan {plan} reported success but active scheme reads back as {active ?? "(null)"} — treated as changed.");
                return PowerPlanResult.AppliedUnverified;
            }
            AppLog.Info($"Power plan → {plan}");
            return PowerPlanResult.Applied;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Power plan change failed: {ex.Message}");
            return PowerPlanResult.Failed;
        }
    }

    /// <summary>Active power scheme GUID (lower-case "D" format) or null.</summary>
    public static string? GetActivePowerPlanGuid()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero)
                return null;
            try
            {
                return Marshal.PtrToStructure<Guid>(ptr).ToString("D");
            }
            finally
            {
                LocalFree(ptr);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("GetActivePowerPlanGuid: " + ex.Message);
            return null;
        }
    }

    /// <summary>App shutdown: undo driver tweaks still active, then release NvAPI (once).</summary>
    /// <param name="quick">Logoff: do not wait for a driver-settings call to finish before unloading.</param>
    public void DisposeDriverColor(bool quick = false)
    {
        try { _driverColor.Dispose(); }
        catch (Exception ex) { AppLog.Warn(ex, "Driver color dispose"); }
        NvApiHost.Shutdown(quick ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(1));
    }
}

/// <summary>Outcome of <see cref="DisplayEngine.TrySetPowerPlan"/>.</summary>
public enum PowerPlanResult
{
    /// <summary>Nothing changed (unknown plan, API error, exception).</summary>
    Failed,
    /// <summary>PowerSetActiveScheme succeeded and the read-back confirms the scheme (or it was already active).</summary>
    Applied,
    /// <summary>PowerSetActiveScheme succeeded but the read-back failed or disagreed — assume the plan changed.</summary>
    AppliedUnverified
}
