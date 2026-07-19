using System.Windows;
using System.Windows.Media;
using DisplayProfileManager.Models;
using Color = System.Windows.Media.Color;
using WpfApp = System.Windows.Application;

namespace DisplayProfileManager.Services;

public static class ThemeService
{
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

        SetSystem(System.Windows.SystemColors.WindowBrushKey, Hex(p.Bg));
        SetSystem(System.Windows.SystemColors.ControlBrushKey, Hex(p.Panel));
        SetSystem(System.Windows.SystemColors.WindowTextBrushKey, Hex(p.Text));
        SetSystem(System.Windows.SystemColors.ControlTextBrushKey, Hex(p.Text));
    }

    public static ThemePalette DarkPalette() => new();

    public static ThemePalette LightPalette() => new()
    {
        Bg = "#F4F0F2",
        Panel = "#FFFFFF",
        Border = "#D4C4CC",
        Text = "#2A1A22",
        Muted = "#7A5A68",
        Accent = "#B84A72",
        AccentHover = "#C45C84",
        Danger = "#B04050",
        Field = "#FFFFFF",
        Track = "#E4D8DE",
        GhostBg = "#F8F4F6",
        GhostBorder = "#D4C4CC",
        GhostHover = "#EFE6EA",
        AccentButtonText = "#FFFFFF",
        CheckCheckedBg = "#F3E4EA",
        ComboHighlight = "#F0E4EA",
        ComboSelected = "#E8D4DC",
        TabSelected = "#F0E4EA",
        TabHover = "#F8F0F4",
        CaptionHover = "#EDE4E8",
        TitleBar = "#EDE6EA",
        PillBg = "#F3E4EA",
        ToastBg = "#FFFFFF",
        ToastBorder = "#D4C4CC"
    };

    public static ThemePalette SeedCustom(string? accentHex, string? bgHex)
    {
        var accent = Hex(accentHex, Hex("#C45C84"));
        var bg = Hex(bgHex, Hex("#120E11"));
        bool dark = IsDark(bg);
        var text = dark ? Hex("#F3E6EC") : Hex("#1E1218");
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
            Danger = "#A04860",
            Field = ToHex(field),
            Track = ToHex(Blend(bg, accent, 0.22)),
            GhostBg = ToHex(Blend(panel, text, dark ? 0.06 : 0.04)),
            GhostBorder = ToHex(border),
            GhostHover = ToHex(Blend(panel, accent, 0.15)),
            AccentButtonText = "#FFFFFF",
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

    private static bool IsDark(Color c) => (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) < 140;

    private static Color Blend(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}
