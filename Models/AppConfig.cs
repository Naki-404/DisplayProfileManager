using System.Text.Json.Serialization;

namespace DisplayProfileManager.Models;

public sealed class AppConfig
{
    public int ConfigVersion { get; set; } = 0;
    public DefaultSettings Defaults { get; set; } = new();
    /// <summary>Factory baseline captured on first run — Reset Settings restores this.</summary>
    public DefaultSettings FactoryDefaults { get; set; } = new();
    public GlobalHotkeys GlobalHotkeys { get; set; } = new();
    public List<GameProfile> Profiles { get; set; } = new();
    /// <summary>Legacy global presets — migrated into each GameProfile.Presets.</summary>
    public List<QuickPreset>? Presets { get; set; }
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; } = false;
    public bool FirstScanDone { get; set; }
    /// <summary>True after core games were seeded once — deleted cores stay deleted.</summary>
    public bool CoreProfilesSeeded { get; set; }
    public double HotkeyStep { get; set; } = 0.05;

    /// <summary>UI / first-run preferences.</summary>
    public UiPreferences Ui { get; set; } = new();
}

public sealed class UiPreferences
{
    /// <summary>en | ru</summary>
    public string Locale { get; set; } = "en";
    /// <summary>dark | light | custom</summary>
    public string Theme { get; set; } = "dark";
    public string CustomAccent { get; set; } = "#C45C84";
    public string CustomBackground { get; set; } = "#120E11";
    /// <summary>Full palette when Theme = custom.</summary>
    public ThemePalette? CustomPalette { get; set; }
    public bool SetupCompleted { get; set; }
    public bool NotifyOnGameStart { get; set; } = true;
    public bool BackupOnSave { get; set; } = true;
    public bool ConfirmDelete { get; set; } = true;
    public bool ShowActiveInHeader { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    /// <summary>null / empty = primary monitor</summary>
    public string? PreferredDisplayDevice { get; set; }

    /// <summary>Show live color overlay over games.</summary>
    public bool OverlayAutoShowOnGame { get; set; }
    public bool OverlayVisible { get; set; }
    public bool OverlayExpanded { get; set; } = true;
    /// <summary>When true, overlay stays as a compact pill (safer over exclusive fullscreen).</summary>
    public bool OverlayHotkeyOnly { get; set; }
    /// <summary>0.55..1 panel opacity</summary>
    public double OverlayPanelOpacity { get; set; } = 0.92;
    /// <summary>null = place overlay at default screen corner.</summary>
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }

    /// <summary>Soft UI chimes (open / click / save / launch).</summary>
    public bool UiSoundsEnabled { get; set; } = true;
    /// <summary>0..100 master volume for UI sounds.</summary>
    public int UiSoundVolume { get; set; } = 70;

    public UiPreferences Clone() => new()
    {
        Locale = Locale,
        Theme = Theme,
        CustomAccent = CustomAccent,
        CustomBackground = CustomBackground,
        CustomPalette = CustomPalette?.Clone(),
        SetupCompleted = SetupCompleted,
        NotifyOnGameStart = NotifyOnGameStart,
        BackupOnSave = BackupOnSave,
        ConfirmDelete = ConfirmDelete,
        ShowActiveInHeader = ShowActiveInHeader,
        MinimizeToTrayOnClose = MinimizeToTrayOnClose,
        PreferredDisplayDevice = PreferredDisplayDevice,
        OverlayAutoShowOnGame = OverlayAutoShowOnGame,
        OverlayVisible = OverlayVisible,
        OverlayExpanded = OverlayExpanded,
        OverlayHotkeyOnly = OverlayHotkeyOnly,
        OverlayPanelOpacity = OverlayPanelOpacity,
        OverlayLeft = OverlayLeft,
        OverlayTop = OverlayTop,
        UiSoundsEnabled = UiSoundsEnabled,
        UiSoundVolume = UiSoundVolume
    };
}

/// <summary>All UI chrome colors as #RRGGBB hex strings.</summary>
public sealed class ThemePalette
{
    public string Bg { get; set; } = "#120E11";
    public string Panel { get; set; } = "#1A1418";
    public string Border { get; set; } = "#3D2A34";
    public string Text { get; set; } = "#F3E6EC";
    public string Muted { get; set; } = "#B08A9A";
    public string Accent { get; set; } = "#C45C84";
    public string AccentHover { get; set; } = "#D4749A";
    public string Danger { get; set; } = "#A04860";
    public string Field { get; set; } = "#241A20";
    public string Track { get; set; } = "#2E2228";
    public string GhostBg { get; set; } = "#22181D";
    public string GhostBorder { get; set; } = "#3D2A34";
    public string GhostHover { get; set; } = "#2A1E24";
    public string AccentButtonText { get; set; } = "#FFFFFF";
    public string CheckCheckedBg { get; set; } = "#2A1C23";
    public string ComboHighlight { get; set; } = "#2E1F27";
    public string ComboSelected { get; set; } = "#3A2430";
    public string TabSelected { get; set; } = "#2A1C23";
    public string TabHover { get; set; } = "#22181D";
    public string CaptionHover { get; set; } = "#2A1E24";
    public string TitleBar { get; set; } = "#160F13";
    public string PillBg { get; set; } = "#2A1C23";
    public string ToastBg { get; set; } = "#2A1820";
    public string ToastBorder { get; set; } = "#6B3A4C";

    public ThemePalette Clone() => new()
    {
        Bg = Bg, Panel = Panel, Border = Border, Text = Text, Muted = Muted,
        Accent = Accent, AccentHover = AccentHover, Danger = Danger,
        Field = Field, Track = Track, GhostBg = GhostBg, GhostBorder = GhostBorder,
        GhostHover = GhostHover, AccentButtonText = AccentButtonText,
        CheckCheckedBg = CheckCheckedBg, ComboHighlight = ComboHighlight,
        ComboSelected = ComboSelected, TabSelected = TabSelected, TabHover = TabHover,
        CaptionHover = CaptionHover, TitleBar = TitleBar, PillBg = PillBg,
        ToastBg = ToastBg, ToastBorder = ToastBorder
    };
}

public sealed class DefaultSettings
{
    public string? Resolution { get; set; } = "1920x1080";
    public int RefreshRate { get; set; }
    public string? PowerPlan { get; set; } = "balanced";
    public ColorSettings Color { get; set; } = new();
    public ColorSettings? ColorLowLevel { get; set; }
    public ColorSettings? ColorDriver { get; set; }

    public DefaultSettings Clone() => new()
    {
        Resolution = Resolution,
        RefreshRate = RefreshRate,
        PowerPlan = PowerPlan,
        Color = Color.Clone(),
        ColorLowLevel = ColorLowLevel?.Clone(),
        ColorDriver = ColorDriver?.Clone()
    };

    public void EnsureDualColorSlots()
    {
        var c = Color ?? ColorSettings.Neutral;
        var low = ColorLowLevel;
        var drv = ColorDriver;
        DualColorSlots.Ensure(ref c, ref low, ref drv);
        Color = c;
        ColorLowLevel = low;
        ColorDriver = drv;
    }

    public void SaveActiveToDualSlots()
    {
        var low = ColorLowLevel;
        var drv = ColorDriver;
        DualColorSlots.SaveActive(Color, ref low, ref drv);
        ColorLowLevel = low;
        ColorDriver = drv;
    }

    public ColorSettings ActivateBackend(ColorBackend want)
    {
        var low = ColorLowLevel;
        var drv = ColorDriver;
        var c = DualColorSlots.Activate(want, ref low, ref drv);
        ColorLowLevel = low;
        ColorDriver = drv;
        Color = c;
        return c;
    }
}

public enum ColorBackend
{
    /// <summary>Windows GDI gamma ramp only.</summary>
    Gdi = 0,
    /// <summary>GDI + NVIDIA Digital Vibrance (NvAPI). Legacy; prefer Driver.</summary>
    Nvidia = 1,
    /// <summary>GDI + AMD ADL. Legacy; prefer Driver.</summary>
    Amd = 2,
    /// <summary>RivaTuner-style low-level Detonator ramp (stronger, lock-friendly).</summary>
    LowLevel = 3,
    /// <summary>Auto: NVIDIA Vibrance or AMD ADL, whichever is available.</summary>
    Driver = 4
}

/// <summary>What to restore when the last watched game exits.</summary>
public enum RestoreMode
{
    /// <summary>Restore display/power/color captured before this game session.</summary>
    PreviousSnapshot = 0,
    /// <summary>Restore AppConfig.Defaults (Global tab).</summary>
    GlobalDefaults = 1,
    /// <summary>Leave the tuned state after the game exits.</summary>
    DoNothing = 2
}

public sealed class ColorSettings
{
    /// <summary>0.0 .. 1.0, default 0.5 = neutral (maps to RT Brightness 0).</summary>
    public double Brightness { get; set; } = 0.5;

    /// <summary>0.0 .. 2.0, default 1.0 = neutral (maps to RT Contrast 0; full RT −82..82).</summary>
    public double Contrast { get; set; } = 1.0;

    /// <summary>0.5 .. 6.0, default 1.0 = neutral (RivaTuner gamma range).</summary>
    public double Gamma { get; set; } = 1.0;

    /// <summary>
    /// NVIDIA Digital Vibrance / AMD saturation, 0..100 (50 = driver default).
    /// </summary>
    public int Vibrance { get; set; } = 50;

    /// <summary>Lift dark tones after the RT curve (0..0.4). Not part of classic RivaTuner.</summary>
    public double ShadowLift { get; set; } = 0.0;

    /// <summary>How color is applied: GDI / NVIDIA / AMD / Low Level (Riva-style).</summary>
    public ColorBackend Backend { get; set; } = ColorBackend.LowLevel;

    /// <summary>Re-apply color periodically while the profile is active.</summary>
    public bool LockColor { get; set; } = true;

    public ColorSettings Clone() => new()
    {
        Brightness = Brightness,
        Contrast = Contrast,
        Gamma = Gamma,
        Vibrance = Vibrance,
        ShadowLift = ShadowLift,
        Backend = Backend,
        LockColor = LockColor
    };

    public void Clamp()
    {
        Brightness = Math.Clamp(Brightness, 0.0, 1.0);
        // Full RivaTuner contrast span (−82..82 → 0..2)
        Contrast = Math.Clamp(Contrast, 0.0, 2.0);
        bool driver = Backend is ColorBackend.Driver or ColorBackend.Nvidia or ColorBackend.Amd;
        // Driver CP gamma is 0.4..2.8; Low Level / GDI keep RivaTuner 0.5..6
        Gamma = Math.Clamp(Gamma, driver ? 0.4 : 0.5, driver ? 2.8 : 6.0);
        Vibrance = Math.Clamp(Vibrance, 0, 100);
        ShadowLift = Math.Clamp(ShadowLift, 0.0, 0.4);
        if (!Enum.IsDefined(typeof(ColorBackend), Backend))
            Backend = ColorBackend.LowLevel;
    }

    /// <summary>DPM floats → classic RivaTuner slider units (exact ints for B/C).</summary>
    public (int Brightness, int Contrast, double Gamma) ToRivaTunerUnits()
    {
        int b = (int)Math.Round((Brightness - 0.5) * 250.0);
        int c = (int)Math.Round((Contrast - 1.0) * 82.0);
        double g = Gamma;
        return (
            Math.Clamp(b, -125, 125),
            Math.Clamp(c, -82, 82),
            Math.Clamp(g, 0.5, 6.0));
    }

    public static ColorSettings Neutral => new()
    {
        Brightness = 0.5,
        Contrast = 1.0,
        Gamma = 1.0,
        Vibrance = 50,
        ShadowLift = 0.0,
        Backend = ColorBackend.LowLevel,
        LockColor = false
    };

    /// <summary>
    /// NVIDIA Control Panel–style defaults (B/C mid, G=1, Vibrance at driver baseline).
    /// </summary>
    public static ColorSettings DriverNeutral => new()
    {
        Brightness = 0.5,
        Contrast = 1.0,
        Gamma = 1.0,
        Vibrance = 50,
        ShadowLift = 0.0,
        Backend = ColorBackend.Driver,
        LockColor = false
    };

    /// <summary>
    /// Classic RivaTuner B (−125..125), C (−82..82), G (0.5..6) → DPM floats.
    /// Same encoding as RT registry schemes (gamma-1 = 0/10/1.5, etc.).
    /// </summary>
    public static ColorSettings FromRivaTuner(double bRt, double cRt, double gRt, ColorBackend backend = ColorBackend.LowLevel)
    {
        var c = new ColorSettings
        {
            Brightness = 0.5 + bRt / 250.0,
            Contrast = 1.0 + cRt / 82.0,
            Gamma = gRt,
            Vibrance = 50,
            ShadowLift = 0.0,
            Backend = backend,
            LockColor = true
        };
        c.Clamp();
        return c;
    }
}

public sealed class GlobalHotkeys
{
    public string? ToggleOverlay { get; set; }
    public string? EmergencyRestore { get; set; }
    public string? NextPreset { get; set; }
    public string? PreviousPreset { get; set; }
    public string? CompareAb { get; set; }
}

public sealed class QuickPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Preset";
    public string? Hotkey { get; set; }
    public bool ApplyResolution { get; set; }
    public string? Resolution { get; set; }
    public bool ApplyColor { get; set; } = true;
    public ColorSettings Color { get; set; } = ColorSettings.Neutral;
    public ColorSettings? ColorLowLevel { get; set; }
    public ColorSettings? ColorDriver { get; set; }

    public void EnsureDualColorSlots()
    {
        var c = Color ?? ColorSettings.Neutral;
        var low = ColorLowLevel;
        var drv = ColorDriver;
        DualColorSlots.Ensure(ref c, ref low, ref drv);
        Color = c;
        ColorLowLevel = low;
        ColorDriver = drv;
    }

    public void SaveActiveToDualSlots()
    {
        var low = ColorLowLevel;
        var drv = ColorDriver;
        DualColorSlots.SaveActive(Color, ref low, ref drv);
        ColorLowLevel = low;
        ColorDriver = drv;
    }

    public ColorSettings ActivateBackend(ColorBackend want)
    {
        var low = ColorLowLevel;
        var drv = ColorDriver;
        var c = DualColorSlots.Activate(want, ref low, ref drv);
        ColorLowLevel = low;
        ColorDriver = drv;
        Color = c;
        return c;
    }

    /// <summary>Deep clone including dual color slots.</summary>
    public static QuickPreset CloneOf(QuickPreset p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Hotkey = p.Hotkey,
        ApplyResolution = p.ApplyResolution,
        Resolution = p.Resolution,
        ApplyColor = p.ApplyColor,
        Color = p.Color.Clone(),
        ColorLowLevel = p.ColorLowLevel?.Clone(),
        ColorDriver = p.ColorDriver?.Clone()
    };

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (ApplyResolution && !string.IsNullOrWhiteSpace(Resolution)) parts.Add(Resolution!);
            if (ApplyColor)
            {
                var s = $"B{Color.Brightness:0.##}/C{Color.Contrast:0.##}/G{Color.Gamma:0.##}";
                if (Color.Vibrance != 50) s += $"/V{Color.Vibrance}";
                parts.Add(s);
            }
            if (!string.IsNullOrWhiteSpace(Hotkey)) parts.Add(Hotkey!);
            return parts.Count == 0 ? "—" : string.Join(" · ", parts);
        }
    }
}

/// <summary>Keeps independent B/C/G/V sets for Low Level vs NVIDIA/AMD driver backends.</summary>
public static class DualColorSlots
{
    public static void Ensure(ref ColorSettings color, ref ColorSettings? low, ref ColorSettings? driver)
    {
        color ??= ColorSettings.Neutral;
        color.Clamp();

        if (low == null || LooksCorrupt(low))
        {
            low = IsDriver(color.Backend)
                ? ColorSettings.Neutral.Clone()
                : color.Clone();
            low.Backend = ColorBackend.LowLevel;
            low.Clamp();
        }

        if (driver == null || LooksCorrupt(driver))
        {
            driver = IsDriver(color.Backend)
                ? color.Clone()
                : ColorSettings.DriverNeutral.Clone();
            driver.Backend = ColorBackend.Driver;
            driver.ShadowLift = 0;
            driver.Clamp();
        }

        // Active Color itself can be a corrupt leftover from reading unloaded sliders (0/0/0/0).
        if (LooksCorrupt(color))
        {
            color = IsDriver(color.Backend)
                ? ColorSettings.DriverNeutral.Clone()
                : ColorSettings.Neutral.Clone();
            color.Clamp();
        }
    }

    public static void SaveActive(ColorSettings color, ref ColorSettings? low, ref ColorSettings? driver)
    {
        var active = color.Clone();
        // Never persist an unloaded-slider zero dump into a dual slot.
        if (LooksCorrupt(active))
        {
            Ensure(ref active, ref low, ref driver);
            return;
        }

        Ensure(ref active, ref low, ref driver);
        if (IsDriver(active.Backend))
        {
            active.Backend = ColorBackend.Driver;
            active.ShadowLift = 0;
            active.Clamp();
            driver = active;
        }
        else
        {
            active.Backend = ColorBackend.LowLevel;
            active.Clamp();
            low = active;
        }
    }

    public static ColorSettings Activate(ColorBackend want, ref ColorSettings? low, ref ColorSettings? driver)
    {
        ColorSettings seed = ColorSettings.Neutral;
        Ensure(ref seed, ref low, ref driver);
        if (IsDriver(want))
        {
            var c = driver!.Clone();
            if (LooksCorrupt(c))
                c = ColorSettings.DriverNeutral.Clone();
            c.Backend = ColorBackend.Driver;
            c.ShadowLift = 0;
            c.Clamp();
            return c;
        }
        else
        {
            var c = low!.Clone();
            if (LooksCorrupt(c))
                c = ColorSettings.Neutral.Clone();
            c.Backend = ColorBackend.LowLevel;
            c.Clamp();
            return c;
        }
    }

    /// <summary>
    /// Unloaded WPF sliders default to 0 — if that was saved, backend switch shows 0/0/0/0.
    /// Treat near-zero B/C with V=0 as corrupt (real profiles almost never look like this).
    /// </summary>
    private static bool LooksCorrupt(ColorSettings c) =>
        c.Brightness <= 0.02 && c.Contrast <= 0.02 && c.Vibrance == 0;

    private static bool IsDriver(ColorBackend b) =>
        b is ColorBackend.Driver or ColorBackend.Nvidia or ColorBackend.Amd;
}

public sealed class GameProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Profile";
    public bool Enabled { get; set; } = true;
    public string ProcessName { get; set; } = "";
    /// <summary>Extra exe names that also activate this profile (launchers, anti-cheat helpers).</summary>
    public List<string> ProcessAliases { get; set; } = new();
    /// <summary>Optional full path to the game exe — used for icons and discovery.</summary>
    public string? ExePath { get; set; }
    public string? Resolution { get; set; }
    public int RefreshRate { get; set; }
    /// <summary>Preset Id to apply automatically when the game starts (null = none).</summary>
    public string? StartupPresetId { get; set; }
    /// <summary>Wait N seconds after process start before first apply (0 = immediate).</summary>
    public int ApplyDelaySeconds { get; set; }
    /// <summary>When true, wait until a window for this process is foreground before applying.</summary>
    public bool ApplyOnFocus { get; set; }
    /// <summary>Win32 device name (\\\\.\\DISPLAY1) or null for primary / app default.</summary>
    public string? DisplayDevice { get; set; }
    public string? PowerPlan { get; set; } = "highPerformance";
    public ColorSettings Color { get; set; } = ColorSettings.Neutral;
    /// <summary>Independent Low Level (RivaTuner) B/C/G slot — kept when switching to NVIDIA/AMD.</summary>
    public ColorSettings? ColorLowLevel { get; set; }
    /// <summary>Independent Driver (NVIDIA CP / AMD) slot — kept when switching to Low Level.</summary>
    public ColorSettings? ColorDriver { get; set; }
    public bool ApplyColor { get; set; }
    public bool ApplyResolution { get; set; }
    public bool ApplyPowerPlan { get; set; } = true;
    public List<CompanionApp> Companions { get; set; } = new();
    public List<QuickPreset> Presets { get; set; } = new();

    /// <summary>Extra session tweaks (Focus Assist, audio, monitors, deferred apply…).</summary>
    public SessionExtras Session { get; set; } = new();

    /// <summary>How to restore the PC when this game (last watched) exits.</summary>
    public RestoreMode RestoreMode { get; set; } = RestoreMode.PreviousSnapshot;

    public void EnsureDualColorSlots()
    {
        var c = Color ?? ColorSettings.Neutral;
        var low = ColorLowLevel;
        var drv = ColorDriver;
        DualColorSlots.Ensure(ref c, ref low, ref drv);
        Color = c;
        ColorLowLevel = low;
        ColorDriver = drv;
    }

    public void SaveActiveToDualSlots()
    {
        var low = ColorLowLevel;
        var drv = ColorDriver;
        DualColorSlots.SaveActive(Color, ref low, ref drv);
        ColorLowLevel = low;
        ColorDriver = drv;
    }

    public ColorSettings ActivateBackend(ColorBackend want)
    {
        var low = ColorLowLevel;
        var drv = ColorDriver;
        var c = DualColorSlots.Activate(want, ref low, ref drv);
        ColorLowLevel = low;
        ColorDriver = drv;
        Color = c;
        return c;
    }

    [JsonIgnore]
    public string ResolutionDisplay =>
        string.IsNullOrWhiteSpace(Resolution) ? "—" : Resolution;

    [JsonIgnore]
    public System.Windows.Media.Brush? TileBrush { get; set; }

    [JsonIgnore]
    public System.Windows.Media.ImageSource? Icon { get; set; }

    [JsonIgnore]
    public string Glyph { get; set; } = "?";
}

/// <summary>System-level extras applied with a game profile (no game injection).</summary>
public sealed class SessionExtras
{
    /// <summary>Re-apply display/color N seconds after game start (0 = off).</summary>
    public int DeferredApplySeconds { get; set; }

    public bool DisableNightLight { get; set; }
    public bool DisableAutoHdr { get; set; }
    /// <summary>Mute Windows toast notifications while game runs.</summary>
    public bool QuietNotifications { get; set; }

    public bool SwitchAudioDevice { get; set; }
    /// <summary>WASAPI device id / friendly name match.</summary>
    public string? AudioDeviceId { get; set; }

    /// <summary>Keep only primary monitor active while game runs.</summary>
    public bool IsolatePrimaryMonitor { get; set; }

    /// <summary>
    /// Monitor layout preference: keepAll | isolatePrimary | primaryOnly.
    /// isolatePrimary mirrors IsolatePrimaryMonitor for newer UI.
    /// </summary>
    public string? MonitorLayout { get; set; }

    public bool ApplyMonitorBrightness { get; set; }
    /// <summary>0–100 DDC/CI backlight.</summary>
    public int MonitorBrightness { get; set; } = 80;

    /// <summary>default | stretch | center — DEVMODE fixed output.</summary>
    public string? ScalingMode { get; set; }

    public SessionExtras Clone() => new()
    {
        DeferredApplySeconds = DeferredApplySeconds,
        DisableNightLight = DisableNightLight,
        DisableAutoHdr = DisableAutoHdr,
        QuietNotifications = QuietNotifications,
        SwitchAudioDevice = SwitchAudioDevice,
        AudioDeviceId = AudioDeviceId,
        IsolatePrimaryMonitor = IsolatePrimaryMonitor,
        MonitorLayout = MonitorLayout,
        ApplyMonitorBrightness = ApplyMonitorBrightness,
        MonitorBrightness = MonitorBrightness,
        ScalingMode = ScalingMode
    };
}

public sealed class CompanionApp
{
    public string Path { get; set; } = "";
    /// <summary>Command-line args for direct launch, e.g. -launchapp appid</summary>
    public string? Arguments { get; set; }
    public string LaunchMode { get; set; } = "direct";
    public string? TaskName { get; set; }
    /// <summary>none | kill | close | closeThenKill</summary>
    public string OnStop { get; set; } = "closeThenKill";
    public string? StopHotkey { get; set; }
    public bool DismissDialogs { get; set; }
    public bool MinimizeToTray { get; set; }

    /// <summary>List row text: path + args.</summary>
    public string ListLabel =>
        string.IsNullOrWhiteSpace(Arguments) ? Path : $"{Path}  {Arguments.Trim()}";
}

/// <summary>
/// Pre-game PC state persisted to active-session.json for crash-safe restore.
/// </summary>
public sealed class ActiveSessionSnapshot
{
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public string? ProfileId { get; set; }
    public string? ProfileName { get; set; }

    public string? Resolution { get; set; }
    public int RefreshRate { get; set; }
    public string? DisplayDevice { get; set; }
    public string? PowerPlanGuid { get; set; }

    /// <summary>Base64 of 256 ushorts (little-endian) per channel.</summary>
    public string? GammaRedB64 { get; set; }
    public string? GammaGreenB64 { get; set; }
    public string? GammaBlueB64 { get; set; }
    public bool HasGammaRamp { get; set; }

    public bool ToastEnabled { get; set; } = true;
    public bool AutoHdrEnabled { get; set; } = true;
    public bool NightLightKnown { get; set; }
    public bool NightLightOn { get; set; }
    public string? AudioDeviceId { get; set; }
    public int? MonitorBrightness { get; set; }

    public bool TopologySaved { get; set; }
    /// <summary>CCD path array (base64) so crash restart can restore isolate.</summary>
    public string? TopologyPathsB64 { get; set; }
    public string? TopologyModesB64 { get; set; }
    public int TopologyPathCount { get; set; }
    public int TopologyModeCount { get; set; }

    public bool ScalingSaved { get; set; }
    public string? ScalingDevice { get; set; }
    public int? ScalingFixedOutput { get; set; }
    public int? ScalingWidth { get; set; }
    public int? ScalingHeight { get; set; }

    /// <summary>Pre-game GPU driver vibrance/sat (crash-safe).</summary>
    public bool HasDriverColor { get; set; }
    public string? DriverVendor { get; set; }
    public int? DriverVibranceLevel { get; set; }
    public float? DriverNormalizedVibrance { get; set; }
    public int? DriverBrightness { get; set; }
    public int? DriverContrast { get; set; }
    public int? DriverSaturation { get; set; }
}
