using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DisplayProfileManager.Models;

namespace DisplayProfileManager;

public static class GameVisuals
{
    private static readonly Dictionary<string, (string HexA, string HexB, string Glyph)> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["VALORANT-Win64-Shipping.exe"] = ("#FF4655", "#7A1020", "V"),
            ["TslGame.exe"] = ("#F2A900", "#5C3A00", "P"),
            ["aces.exe"] = ("#3D8BD4", "#16324F", "W"),
            ["EscapeFromTarkov.exe"] = ("#C4A35A", "#3A2E18", "T"),
            ["cs2.exe"] = ("#DE9B35", "#3A2508", "CS"),
            ["csgo.exe"] = ("#DE9B35", "#3A2508", "CS"),
            ["r5apex.exe"] = ("#DA2929", "#3A0A0A", "A"),
            ["Overwatch.exe"] = ("#F99E1A", "#4A2E00", "O"),
            ["GTA5.exe"] = ("#6CC24A", "#1E3A14", "G"),
            ["RainbowSix.exe"] = ("#4A90E2", "#10243A", "R6"),
            ["FortniteClient-Win64-Shipping.exe"] = ("#9D4DBB", "#2A1040", "F"),
            ["Fortnite.exe"] = ("#9D4DBB", "#2A1040", "F"),
            ["League of Legends.exe"] = ("#C89B3C", "#3A2A10", "LoL"),
            ["LeagueClient.exe"] = ("#C89B3C", "#3A2A10", "LoL"),
            ["destiny2.exe"] = ("#6B9BD1", "#1A2A3A", "D2"),
            ["Cyberpunk2077.exe"] = ("#FCEE0A", "#3A3800", "CP"),
            ["eldenring.exe"] = ("#C9A227", "#2E2408", "ER"),
            ["RocketLeague.exe"] = ("#0078F0", "#00284A", "RL"),
            ["Dota2.exe"] = ("#C23C2A", "#3A100C", "D"),
            ["cod.exe"] = ("#5A5A5A", "#1A1A1A", "CoD"),
            ["ModernWarfare.exe"] = ("#5A5A5A", "#1A1A1A", "MW"),
        };

    public static void Apply(GameProfile profile)
    {
        var process = profile.ProcessName ?? "";
        if (Known.TryGetValue(process, out var style))
        {
            profile.Glyph = style.Glyph;
            profile.TileBrush = MakeBrush(style.HexA, style.HexB);
        }
        else
        {
            var seed = Math.Abs((profile.Name + process).GetHashCode());
            var hue = seed % 360;
            profile.Glyph = string.IsNullOrWhiteSpace(profile.Name)
                ? "?"
                : profile.Name.Trim()[..1].ToUpperInvariant();
            profile.TileBrush = MakeHueBrush(hue);
        }

        var path = profile.ExePath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            profile.Icon = TryGetExeIcon(path!);
        else
            profile.Icon = null;
    }

    public static void ApplyAll(IEnumerable<GameProfile> profiles)
    {
        foreach (var p in profiles) Apply(p);
    }

    public static ImageSource? TryGetExeIcon(string exePath)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return null;
            using var bmp = icon.ToBitmap();
            var hBitmap = bmp.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }

    private static System.Windows.Media.Brush MakeBrush(string a, string b)
    {
        var brush = new LinearGradientBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(a)!,
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(b)!,
            45);
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Brush MakeHueBrush(int hue)
    {
        var c1 = Hsv(hue, 0.55, 0.55);
        var c2 = Hsv(hue, 0.65, 0.22);
        var brush = new LinearGradientBrush(c1, c2, 45);
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Color Hsv(int h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return System.Windows.Media.Color.FromRgb(
            (byte)((r + m) * 255),
            (byte)((g + m) * 255),
            (byte)((b + m) * 255));
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value == null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InvertNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value == null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
