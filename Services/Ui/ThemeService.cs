using System.Windows;
using System.Windows.Media;
using Visor.Models;
using Color = System.Windows.Media.Color;
using WpfApp = System.Windows.Application;

namespace Visor.Services;

public static class ThemeService
{
    /// <summary>Visor brand accents (icon gradient).</summary>
    public const string BrandCyan = "#22D3EE";
    public const string BrandVioletDark = "#8B5CF6";
    public const string BrandVioletLight = "#7C3AED";
    public const string BrandBackground = "#0A0C10";

    /// <summary>True when the effective theme has a dark background (used by tray menu / DWM caption).</summary>
    public static bool IsDarkTheme(UiPreferences? ui) => IsDark(Hex(Resolve(ui ?? new UiPreferences()).Bg));

    /// <summary>Reads a themed color from the live application resources (falls back to the dark palette).</summary>
    public static Color CurrentColor(string brushKey, string fallbackHex)
    {
        try
        {
            if (WpfApp.Current?.Resources[brushKey] is SolidColorBrush b)
                return b.Color;
        }
        catch { }
        return Hex(fallbackHex);
    }

    public static ThemePalette Resolve(UiPreferences ui)
    {
        ui ??= new UiPreferences();
        return (ui.Theme ?? "dark").ToLowerInvariant() switch
        {
            "light" => LightPalette(),
            "custom" => ui.CustomPalette?.Clone() ?? SeedCustom(ui.CustomAccent, ui.CustomBackground),
            _ => DarkPalette()
        };
    }

    public static void Apply(UiPreferences ui) => ApplyPalette(Resolve(ui));

    public static void ApplyPalette(ThemePalette p)
    {
        Set("BgBrush", Hex(p.Bg));
        Set("PanelBrush", Hex(p.Panel));
        Set("BorderBrush", Hex(p.Border));
        Set("TextBrush", Hex(p.Text));
        Set("MutedBrush", Hex(p.Muted));
        Set("AccentBrush", Hex(p.Accent));
        Set("AccentHoverBrush", Hex(p.AccentHover));
        Set("DangerBrush", Hex(p.Danger));
        bool darkSurface = IsDark(Hex(p.Bg));
        var success = Hex(string.IsNullOrWhiteSpace(p.Success)
            ? (darkSurface ? "#22C55E" : "#15803D")
            : p.Success);
        var warning = Hex(string.IsNullOrWhiteSpace(p.Warning)
            ? (darkSurface ? "#F59E0B" : "#B45309")
            : p.Warning);
        Set("SuccessBrush", success);
        Set("WarningBrush", warning);
        // Pick white vs near-black by WCAG contrast (IsDark threshold alone fails on #22C55E).
        Set("SuccessButtonTextBrush", BestContrastingLabel(success));
        // Soft hover tint for danger fills (close caption, DangerButton).
        Set("DangerHoverBrush", Blend(Hex(p.Danger), Hex("#FFFFFF"), 0.14));
        Set("FieldBrush", Hex(p.Field));
        Set("TrackBrush", Hex(p.Track));
        Set("GhostBgBrush", Hex(p.GhostBg));
        Set("GhostBorderBrush", Hex(p.GhostBorder));
        Set("GhostHoverBrush", Hex(p.GhostHover));
        Set("AccentButtonTextBrush", Hex(p.AccentButtonText));
        Set("CheckCheckedBgBrush", Hex(p.CheckCheckedBg));
        Set("ComboHighlightBrush", Hex(p.ComboHighlight));
        Set("ComboSelectedBrush", Hex(p.ComboSelected));
        Set("TabSelectedBrush", Hex(p.TabSelected));
        Set("TabHoverBrush", Hex(p.TabHover));
        Set("CaptionHoverBrush", Hex(p.CaptionHover));
        Set("TitleBarBrush", Hex(p.TitleBar));
        Set("PillBgBrush", Hex(p.PillBg));
        Set("ToastBgBrush", Hex(p.ToastBg));
        Set("ToastBorderBrush", Hex(p.ToastBorder));

        // Derived brand brushes (not part of the persisted palette).
        var bg = Hex(p.Bg);
        bool dark = IsDark(bg);
        var accent = Hex(p.Accent);
        var accentAlt = dark ? Hex(BrandVioletDark) : Hex(BrandVioletLight);
        var muted = Hex(p.Muted);
        Set("AccentAltBrush", accentAlt);
        Set("ScrimBrush", Color.FromArgb(0xCC, bg.R, bg.G, bg.B));
        Set("ScrollThumbBrush", Color.FromArgb(0x66, muted.R, muted.G, muted.B));
        Set("ScrollThumbHoverBrush", Color.FromArgb(0xCC, muted.R, muted.G, muted.B));
        var gradient = new LinearGradientBrush(accent, accentAlt, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
        gradient.Freeze();
        WpfApp.Current.Resources["BrandGradientBrush"] = gradient;
        // Dashboard cards (Stage 1 shell): border + hover border, horizontal cyan → violet accent.
        var (cardBorder, cardHover) = CardColors(p);
        Set("CardBorderBrush", cardBorder);
        Set("CardHoverBrush", cardHover);
        var accentGradient = new LinearGradientBrush(accent, accentAlt, new System.Windows.Point(0, 0), new System.Windows.Point(1, 0));
        accentGradient.Freeze();
        WpfApp.Current.Resources["AccentGradientBrush"] = accentGradient;

        SetSystem(System.Windows.SystemColors.WindowBrushKey, Hex(p.Bg));
        SetSystem(System.Windows.SystemColors.ControlBrushKey, Hex(p.Panel));
        SetSystem(System.Windows.SystemColors.WindowTextBrushKey, Hex(p.Text));
        SetSystem(System.Windows.SystemColors.ControlTextBrushKey, Hex(p.Text));

        Visor.UiMotion.SyncChromeDurations();
    }

    public static ThemePalette DarkPalette() => new()
    {
        Bg = "#0A0C10",
        Panel = "#12161C",
        Border = "#1E2630",
        Text = "#E8EEF4",
        Muted = "#8B9AAB",
        Accent = BrandCyan,
        AccentHover = "#67E8F9",
        Danger = "#E05252",
        Success = "#22C55E",
        Warning = "#F59E0B",
        Field = "#161B22",
        Track = "#1F2835",
        GhostBg = "#151A22",
        GhostBorder = "#1E2630",
        GhostHover = "#1B2431",
        AccentButtonText = "#06121A",
        CheckCheckedBg = "#0E2A33",
        ComboHighlight = "#1A2330",
        ComboSelected = "#1C2C3A",
        TabSelected = "#162230",
        TabHover = "#131A24",
        CaptionHover = "#1B2431",
        TitleBar = "#0D1218",
        PillBg = "#10242E",
        ToastBg = "#151A22",
        ToastBorder = "#2A3A4F"
    };

    public static ThemePalette LightPalette() => new()
    {
        // Cyan-700 accent keeps >= 4.5:1 contrast for white button text on a light background.
        Bg = "#F3F6FA",
        Panel = "#FFFFFF",
        Border = "#D5DDE7",
        Text = "#111827",
        Muted = "#5B6676",
        Accent = "#0E7490",
        AccentHover = "#0891B2",
        Danger = "#C0392B",
        Success = "#15803D",
        Warning = "#B45309",
        Field = "#FFFFFF",
        Track = "#E2E8F0",
        GhostBg = "#F6F8FB",
        GhostBorder = "#D5DDE7",
        GhostHover = "#EAF6F9",
        AccentButtonText = "#FFFFFF",
        CheckCheckedBg = "#E0F5F9",
        ComboHighlight = "#EEF6F9",
        ComboSelected = "#DDF0F5",
        TabSelected = "#E3F3F7",
        TabHover = "#EEF3F7",
        CaptionHover = "#E6ECF2",
        TitleBar = "#E9EEF4",
        PillBg = "#E0F2F7",
        ToastBg = "#FFFFFF",
        ToastBorder = "#D5DDE7"
    };

    /// <summary>Starting point for a custom palette: the Visor brand colors.</summary>
    public static ThemePalette DefaultCustom() => SeedCustom(BrandCyan, BrandBackground);

    public static ThemePalette SeedCustom(string? accentHex, string? bgHex)
    {
        var accent = Hex(accentHex, Hex(BrandCyan));
        var bg = Hex(bgHex, Hex(BrandBackground));
        bool dark = IsDark(bg);
        var text = dark ? Hex("#E8EEF4") : Hex("#1B232C");
        var panel = Blend(bg, dark ? Hex("#FFFFFF") : Hex("#000000"), dark ? 0.07 : 0.04);
        var border = Blend(accent, bg, 0.55);
        var muted = Blend(text, bg, 0.42);
        var field = Blend(bg, dark ? Hex("#FFFFFF") : Hex("#000000"), dark ? 0.1 : 0.03);
        var hover = Blend(accent, Hex("#FFFFFF"), 0.18);

        return new ThemePalette
        {
            Bg = ToHex(bg),
            Panel = ToHex(panel),
            Border = ToHex(border),
            Text = ToHex(text),
            Muted = ToHex(muted),
            Accent = ToHex(accent),
            AccentHover = ToHex(hover),
            // Like Success/Warning: the light-surface red needs more depth to keep AA contrast for red text at rest.
            Danger = dark ? "#E05252" : "#C0392B",
            Success = dark ? "#22C55E" : "#15803D",
            Warning = dark ? "#F59E0B" : "#B45309",
            Field = ToHex(field),
            Track = ToHex(Blend(bg, accent, 0.22)),
            GhostBg = ToHex(Blend(panel, text, dark ? 0.06 : 0.04)),
            GhostBorder = ToHex(border),
            GhostHover = ToHex(Blend(panel, accent, 0.15)),
            AccentButtonText = IsDark(accent) ? "#FFFFFF" : "#06121A",
            CheckCheckedBg = ToHex(Blend(panel, accent, 0.2)),
            ComboHighlight = ToHex(Blend(panel, accent, 0.18)),
            ComboSelected = ToHex(Blend(panel, accent, 0.28)),
            TabSelected = ToHex(Blend(panel, accent, 0.2)),
            TabHover = ToHex(Blend(panel, accent, 0.1)),
            CaptionHover = ToHex(Blend(panel, text, 0.08)),
            TitleBar = ToHex(Blend(bg, panel, 0.5)),
            PillBg = ToHex(Blend(panel, accent, 0.18)),
            ToastBg = ToHex(panel),
            ToastBorder = ToHex(border)
        };
    }

    /// <summary>
    /// Card border at rest and on hover, derived from the palette: the rest border is the palette border, the
    /// hover border leans toward the accent so a card "lifts" without changing its fill (pure, unit-tested).
    /// </summary>
    public static (Color Border, Color Hover) CardColors(ThemePalette p)
    {
        var border = Hex(p.Border);
        var accent = Hex(p.Accent);
        return (border, Blend(border, accent, 0.35));
    }

    private static void Set(string key, Color c) =>
        WpfApp.Current.Resources[key] = new SolidColorBrush(c);

    private static void SetSystem(ResourceKey key, Color c) =>
        WpfApp.Current.Resources[key] = new SolidColorBrush(c);

    public static Color Hex(string? hex, Color? fallback = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback ?? Colors.Gray;
            hex = hex.Trim();
            if (!hex.StartsWith("#")) hex = "#" + hex;
            return (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
        }
        catch
        {
            return fallback ?? Colors.Gray;
        }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static bool IsDark(Color c) => (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) < 140;

    /// <summary>White or near-black label — whichever has higher WCAG contrast on <paramref name="bg"/>.</summary>
    public static Color BestContrastingLabel(Color bg)
    {
        var white = Hex("#FFFFFF");
        var ink = Hex("#052E16");
        return ContrastRatio(bg, white) >= ContrastRatio(bg, ink) ? white : ink;
    }

    private static double RelativeLuminance(Color c)
    {
        static double Lin(byte u)
        {
            double s = u / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    /// <summary>WCAG 2.x contrast ratio (1..21).</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        double l1 = RelativeLuminance(a);
        double l2 = RelativeLuminance(b);
        double hi = Math.Max(l1, l2);
        double lo = Math.Min(l1, l2);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static Color Blend(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}
