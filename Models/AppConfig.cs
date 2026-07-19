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
        PreferredDisplayDevice = PreferredDisplayDevice
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

    public DefaultSettings Clone() => new()
    {
        Resolution = Resolution,
        RefreshRate = RefreshRate,
        PowerPlan = PowerPlan,
        Color = Color.Clone()
    };
}

public sealed class ColorSettings
{
    /// <summary>0.0 .. 1.0, default 0.5 = neutral</summary>
    public double Brightness { get; set; } = 0.5;

    /// <summary>0.5 .. 2.0, default 1.0 = neutral</summary>
    public double Contrast { get; set; } = 1.0;

    /// <summary>0.3 .. 3.0, default 1.0 = neutral</summary>
    public double Gamma { get; set; } = 1.0;

    public ColorSettings Clone() => new()
    {
        Brightness = Brightness,
        Contrast = Contrast,
        Gamma = Gamma
    };

    public void Clamp()
    {
        Brightness = Math.Clamp(Brightness, 0.0, 1.0);
        Contrast = Math.Clamp(Contrast, 0.5, 2.0);
        Gamma = Math.Clamp(Gamma, 0.3, 3.0);
    }

    public static ColorSettings Neutral => new() { Brightness = 0.5, Contrast = 1.0, Gamma = 1.0 };
}

public sealed class GlobalHotkeys
{
    public string? BrightnessUp { get; set; } = "Ctrl+Alt+Up";
    public string? BrightnessDown { get; set; } = "Ctrl+Alt+Down";
    public string? ContrastUp { get; set; } = "Ctrl+Alt+Right";
    public string? ContrastDown { get; set; } = "Ctrl+Alt+Left";
    public string? GammaUp { get; set; } = "Ctrl+Alt+Add";
    public string? GammaDown { get; set; } = "Ctrl+Alt+Subtract";
    public string? ResetColor { get; set; } = "Ctrl+Alt+NumPad9";
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

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (ApplyResolution && !string.IsNullOrWhiteSpace(Resolution)) parts.Add(Resolution!);
            if (ApplyColor) parts.Add($"B{Color.Brightness:0.##}/C{Color.Contrast:0.##}/G{Color.Gamma:0.##}");
            if (!string.IsNullOrWhiteSpace(Hotkey)) parts.Add(Hotkey!);
            return parts.Count == 0 ? "—" : string.Join(" · ", parts);
        }
    }
}

public sealed class GameProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Profile";
    public bool Enabled { get; set; } = true;
    public string ProcessName { get; set; } = "";
    /// <summary>Optional full path to the game exe — used for icons and discovery.</summary>
    public string? ExePath { get; set; }
    public string? Resolution { get; set; }
    public int RefreshRate { get; set; }
    /// <summary>Win32 device name (\\\\.\\DISPLAY1) or null for primary / app default.</summary>
    public string? DisplayDevice { get; set; }
    public string? PowerPlan { get; set; } = "highPerformance";
    public ColorSettings Color { get; set; } = ColorSettings.Neutral;
    public bool ApplyColor { get; set; }
    public bool ApplyResolution { get; set; }
    public bool ApplyPowerPlan { get; set; } = true;
    public List<CompanionApp> Companions { get; set; } = new();
    public List<QuickPreset> Presets { get; set; } = new();

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

public sealed class CompanionApp
{
    public string Path { get; set; } = "";
    public string LaunchMode { get; set; } = "direct";
    public string? TaskName { get; set; }
    /// <summary>none | kill | close | closeThenKill</summary>
    public string OnStop { get; set; } = "closeThenKill";
    public string? StopHotkey { get; set; }
    public bool DismissDialogs { get; set; }
    public bool MinimizeToTray { get; set; }
}
