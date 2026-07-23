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

    public static ThemePalette DarkPalette() => new()
    {
        Bg = "#0F1216",
        Panel = "#161B22",
        Border = "#2A3340",
        Text = "#E8EEF4",
        Muted = "#8B9AAB",
        Accent = "#7EB8D4",
        AccentHover = "#93C7DF",
        Danger = "#C1554F",
        Field = "#1B212B",
        Track = "#232B37",
        GhostBg = "#1A212B",
        GhostBorder = "#2A3340",
        GhostHover = "#212938",
        AccentButtonText = "#0F1216",
        CheckCheckedBg = "#1E2732",
        ComboHighlight = "#1E2733",
        ComboSelected = "#24303D",
        TabSelected = "#1C2430",
        TabHover = "#171E27",
        CaptionHover = "#1E2732",
        TitleBar = "#10141A",
        PillBg = "#1C2430",
        ToastBg = "#161B22",
        ToastBorder = "#2A3340"
    };

    public static ThemePalette LightPalette() => new()
    {
        Bg = "#F1F4F7",
        Panel = "#FFFFFF",
        Border = "#D7DEE6",
        Text = "#1B232C",
        Muted = "#667180",
        Accent = "#3B7E9D",
        AccentHover = "#4C93B4",
        Danger = "#B3453F",
        Field = "#FFFFFF",
        Track = "#E1E7ED",
        GhostBg = "#F5F7FA",
        GhostBorder = "#D7DEE6",
        GhostHover = "#EAEFF3",
        AccentButtonText = "#FFFFFF",
        CheckCheckedBg = "#E4EEF3",
        ComboHighlight = "#EAF1F5",
        ComboSelected = "#DCE9EF",
        TabSelected = "#EAF1F5",
        TabHover = "#F2F6F9",
        CaptionHover = "#E7EDF1",
        TitleBar = "#E9EDF1",
        PillBg = "#E4EEF3",
        ToastBg = "#FFFFFF",
        ToastBorder = "#D7DEE6"
    };

    public static ThemePalette SeedCustom(string? accentHex, string? bgHex)
    {
        var accent = Hex(accentHex, Hex("#7EB8D4"));
        var bg = Hex(bgHex, Hex("#0F1216"));
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
            Danger = "#C1554F",
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
