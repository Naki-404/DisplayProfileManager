using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Visor.Models;

namespace Visor;
/// <summary>Short one-shot UI motion — Cubic/Back ease, no continuous timers after playback.</summary>
internal static class UiMotion
{
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };
    private static readonly IEasingFunction EaseInOut = new CubicEase { EasingMode = EasingMode.EaseInOut };
    private static readonly IEasingFunction BackOut = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };

    private static DispatcherTimer? _toastHold;
    private static int _toastGen;

    public static void FadeTo(UIElement element, double to, Action? onCompleted = null, int ms = 160)
    {
        if (element == null) return;
        var anim = new DoubleAnimation(element.Opacity, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = EaseInOut
        };
        if (onCompleted != null)
            anim.Completed += (_, _) => onCompleted();
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    public static void PulseOpacity(UIElement element)
    {
        if (element == null) return;
        var anim = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(320),
            FillBehavior = FillBehavior.Stop
        };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.55, KeyTime.FromPercent(0.4), EaseOut));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), EaseOut));
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>Fade + soft upward slide for tab / panel content.</summary>
    public static void FadeSlideIn(FrameworkElement element, double fromY = 10, int ms = 220)
    {
        if (element == null) return;
        EnsureTranslate(element, out var tt);
        element.Opacity = 0;
        tt.Y = fromY;

        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(ms + 30)) { EasingFunction = BackOut });
    }

    /// <summary>Tab content: soft brightness nudge — no hard flash.</summary>
    public static void SoftContentIn(FrameworkElement element, int ms = 200)
    {
        if (element == null) return;

        if (element.RenderTransform is TranslateTransform tt)
        {
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.Y = 0;
        }

        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;

        var anim = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = EaseOut
        };
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>Window open: fade + soft overshoot scale.</summary>
    public static void PopIn(FrameworkElement element, int ms = 240)
    {
        if (element == null) return;
        EnsureScale(element, out var scale);
        element.Opacity = 0;
        scale.ScaleX = scale.ScaleY = 0.94;

        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(ms + 40)) { EasingFunction = BackOut });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(ms + 40)) { EasingFunction = BackOut });
    }

    /// <summary>Window hide: fade + soft scale-down.</summary>
    public static void PopOut(FrameworkElement element, Action? onCompleted = null, int ms = 150)
    {
        if (element == null)
        {
            onCompleted?.Invoke();
            return;
        }

        EnsureScale(element, out var scale);
        var fade = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = EaseIn
        };
        if (onCompleted != null)
            fade.Completed += (_, _) => onCompleted();

        element.BeginAnimation(UIElement.OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(scale.ScaleX, 0.96, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseIn });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(scale.ScaleY, 0.96, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseIn });
    }

    /// <summary>Reading time for a toast: ~60 ms per character, clamped to 1.5–5 s.</summary>
    public static int ToastHoldMs(string? text)
    {
        int len = string.IsNullOrEmpty(text) ? 0 : text.Length;
        return Math.Clamp(len * 60, 1500, 5000);
    }

    /// <summary>Non-modal toast: rise + fade in, hold (scaled by text length unless given), fade out.</summary>
    public static void ShowToast(FrameworkElement host, TextBlock? label = null, string? text = null, int? holdMs = null)
    {
        if (host == null) return;
        if (label != null && text != null)
            label.Text = text;
        int hold = holdMs ?? ToastHoldMs(text ?? label?.Text);

        _toastHold?.Stop();
        int gen = ++_toastGen;

        EnsureTranslate(host, out var tt);
        host.Visibility = Visibility.Visible;
        host.Opacity = 0;
        tt.Y = 14;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = EaseOut };
        var slideIn = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = BackOut };

        fadeIn.Completed += (_, _) =>
        {
            if (gen != _toastGen) return;
            _toastHold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(hold) };
            _toastHold.Tick += (_, _) =>
            {
                _toastHold.Stop();
                if (gen != _toastGen) return;

                var fadeOut = new DoubleAnimation(host.Opacity, 0, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = EaseIn
                };
                fadeOut.Completed += (_, _) =>
                {
                    if (gen == _toastGen)
                        host.Visibility = Visibility.Collapsed;
                };
                host.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                tt.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(0, 8, TimeSpan.FromMilliseconds(200)) { EasingFunction = EaseIn });
            };
            _toastHold.Start();
        };

        host.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        tt.BeginAnimation(TranslateTransform.YProperty, slideIn);
    }

    /// <summary>Dismisses the current toast immediately (e.g. after Undo was clicked).</summary>
    public static void HideToast(FrameworkElement host)
    {
        if (host == null) return;
        _toastHold?.Stop();
        int gen = ++_toastGen;
        var fadeOut = new DoubleAnimation(host.Opacity, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = EaseIn };
        fadeOut.Completed += (_, _) =>
        {
            if (gen == _toastGen)
                host.Visibility = Visibility.Collapsed;
        };
        host.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private static void EnsureTranslate(FrameworkElement element, out TranslateTransform tt)
    {
        if (element.RenderTransform is TranslateTransform existing)
        {
            tt = existing;
            return;
        }

        tt = new TranslateTransform();
        element.RenderTransform = tt;
        element.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
    }

    private static void EnsureScale(FrameworkElement element, out ScaleTransform scale)
    {
        if (element.RenderTransform is ScaleTransform existing)
        {
            scale = existing;
            element.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            return;
        }

        scale = new ScaleTransform(1, 1);
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
    }
}

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
            // Deterministic across runs (string.GetHashCode is randomized per process).
            uint seed = StableHash(profile.Name + "|" + process);
            // Cool blue-gray band (slate accent family) instead of full rainbow hue.
            var hue = 195 + (int)(seed % 50);
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

    /// <summary>FNV-1a over the lower-cased string — stable between launches, never negative.</summary>
    public static uint StableHash(string? text)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        uint hash = offset;
        foreach (char ch in (text ?? "").ToLowerInvariant())
        {
            hash ^= ch;
            hash *= prime;
        }
        return hash;
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
        var brush = new System.Windows.Media.LinearGradientBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(a)!,
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(b)!,
            45);
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Brush MakeHueBrush(int hue)
    {
        var c1 = Hsv(hue, 0.34, 0.40);
        var c2 = Hsv(hue, 0.42, 0.16);
        var brush = new System.Windows.Media.LinearGradientBrush(c1, c2, 45);
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

/// <summary>
/// Picks readable text (light or dark) for a game tile based on the tile brush luminance.
/// The left part of the 45° gradient sits under the text, so the first stop is weighted more.
/// </summary>
public sealed class TileForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush Light = Frozen(System.Windows.Media.Color.FromRgb(0xF5, 0xF8, 0xFC));
    private static readonly SolidColorBrush Dark = Frozen(System.Windows.Media.Color.FromRgb(0x0B, 0x0F, 0x14));
    private static readonly SolidColorBrush LightMuted = Frozen(System.Windows.Media.Color.FromArgb(0xC8, 0xF5, 0xF8, 0xFC));
    private static readonly SolidColorBrush DarkMuted = Frozen(System.Windows.Media.Color.FromArgb(0xC8, 0x0B, 0x0F, 0x14));

    public bool Muted { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool darkText = value is System.Windows.Media.Brush b && PrefersDarkText(b);
        if (Muted) return darkText ? DarkMuted : LightMuted;
        return darkText ? Dark : Light;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public static bool PrefersDarkText(System.Windows.Media.Brush brush)
    {
        System.Windows.Media.Color c;
        switch (brush)
        {
            case SolidColorBrush sc:
                c = sc.Color;
                break;
            case GradientBrush gb when gb.GradientStops.Count > 0:
                var a = gb.GradientStops[0].Color;
                var z = gb.GradientStops[gb.GradientStops.Count - 1].Color;
                c = System.Windows.Media.Color.FromRgb(
                    (byte)(a.R * 0.65 + z.R * 0.35),
                    (byte)(a.G * 0.65 + z.G * 0.35),
                    (byte)(a.B * 0.65 + z.B * 0.35));
                break;
            default:
                return false;
        }
        double l = RelativeLuminance(c);
        double contrastWhite = 1.05 / (l + 0.05);
        double contrastDark = (l + 0.05) / 0.055;
        return contrastDark > contrastWhite;
    }

    public static double RelativeLuminance(System.Windows.Media.Color c)
    {
        static double Lin(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static SolidColorBrush Frozen(System.Windows.Media.Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>QuickPreset → gamer-readable summary, e.g. "Brightness 55 · Gamma 1.10 · 1440p" (hotkey shown separately).</summary>
public sealed class PresetSummaryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is QuickPreset p ? Visor.Services.ColorUiHelper.Describe(p) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>CompanionApp → friendly list label ("Overwolf.exe · close, then force-quit").</summary>
public sealed class CompanionLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is CompanionApp c ? CompanionEditWindow.Describe(c) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Tray menu renderer that follows the current app theme (dark / light / custom).</summary>
internal sealed class SlateMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly ThemeColors _t;

    public SlateMenuRenderer() : this(ThemeColors.Read()) { }

    private SlateMenuRenderer(ThemeColors t) : base(new SlateMenuColorTable(t))
    {
        RoundedEdges = false;
        _t = t;
    }

    internal readonly record struct ThemeColors(
        System.Drawing.Color Bg, System.Drawing.Color Border, System.Drawing.Color Hover,
        System.Drawing.Color Accent, System.Drawing.Color Text, System.Drawing.Color Muted)
    {
        public static ThemeColors Read()
        {
            static System.Drawing.Color C(string key, string fallback)
            {
                var c = Visor.Services.ThemeService.CurrentColor(key, fallback);
                return System.Drawing.Color.FromArgb(c.R, c.G, c.B);
            }
            return new ThemeColors(
                C("PanelBrush", "#121821"),
                C("BorderBrush", "#243041"),
                C("ComboHighlightBrush", "#1A2330"),
                C("AccentBrush", "#22D3EE"),
                C("TextBrush", "#E6EDF5"),
                C("MutedBrush", "#8A98AB"));
        }
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new System.Drawing.SolidBrush(_t.Bg);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new System.Drawing.Pen(_t.Border);
        var r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // no image margin gutter
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = new System.Drawing.Rectangle(6, 1, e.Item.Width - 12, e.Item.Height - 2);

        if (!e.Item.Enabled || (!e.Item.Selected && !e.Item.Pressed)) return;

        using var path = RoundedRect(bounds, 4);
        using var brush = new System.Drawing.SolidBrush(_t.Hover);
        g.FillPath(brush, path);
        using var pen = new System.Drawing.Pen(_t.Accent);
        g.DrawPath(pen, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? _t.Muted : e.Item.Selected ? _t.Accent : _t.Text;
        e.TextFormat = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix
                       | TextFormatFlags.EndEllipsis;
        // Keep left padding consistent
        e.TextRectangle = new System.Drawing.Rectangle(14, 0, e.Item.Width - 28, e.Item.Height);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Selected == true ? _t.Accent : _t.Muted;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.ContentRectangle.Top + e.Item.ContentRectangle.Height / 2;
        using var pen = new System.Drawing.Pen(_t.Border);
        e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(System.Drawing.Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class SlateMenuColorTable : ProfessionalColorTable
    {
        private readonly ThemeColors _t;
        public SlateMenuColorTable(ThemeColors t) => _t = t;

        public override System.Drawing.Color MenuBorder => _t.Border;
        public override System.Drawing.Color MenuItemBorder => _t.Accent;
        public override System.Drawing.Color MenuItemSelected => _t.Hover;
        public override System.Drawing.Color MenuItemSelectedGradientBegin => _t.Hover;
        public override System.Drawing.Color MenuItemSelectedGradientEnd => _t.Hover;
        public override System.Drawing.Color ToolStripDropDownBackground => _t.Bg;
        public override System.Drawing.Color ImageMarginGradientBegin => _t.Bg;
        public override System.Drawing.Color ImageMarginGradientMiddle => _t.Bg;
        public override System.Drawing.Color ImageMarginGradientEnd => _t.Bg;
        public override System.Drawing.Color SeparatorDark => _t.Border;
        public override System.Drawing.Color SeparatorLight => _t.Border;
    }
}
