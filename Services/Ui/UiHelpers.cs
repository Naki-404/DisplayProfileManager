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
using DisplayProfileManager.Models;

namespace DisplayProfileManager;
/// <summary>Short one-shot UI motion Ã¢â‚¬â€ no continuous timers after playback.</summary>
internal static class UiMotion
{
    private static readonly IEasingFunction EaseOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseIn = new QuadraticEase { EasingMode = EasingMode.EaseIn };
    private static readonly IEasingFunction EaseInOut = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

    private static DispatcherTimer? _toastHold;
    private static int _toastGen;

    public static void FadeIn(UIElement element, double to = 1.0, int ms = 160)
    {
        if (element == null) return;
        element.Opacity = 0;
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
    }

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
            Duration = TimeSpan.FromMilliseconds(280),
            FillBehavior = FillBehavior.Stop
        };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.45, KeyTime.FromPercent(0.35), EaseOut));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), EaseOut));
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>Fade + slight upward slide for tab / panel content.</summary>
    public static void FadeSlideIn(FrameworkElement element, double fromY = 8, int ms = 180)
    {
        if (element == null) return;
        EnsureTranslate(element, out var tt);
        element.Opacity = 0;
        tt.Y = fromY;

        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(ms + 20)) { EasingFunction = EaseOut });
    }


    /// <summary>Tab content: subtle opacity only â€” no slide, no flash to zero.</summary>
    public static void SoftContentIn(FrameworkElement element, int ms = 180)
    {
        if (element == null) return;

        if (element.RenderTransform is TranslateTransform tt)
        {
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.Y = 0;
        }

        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;

        var anim = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = EaseOut
        };
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }
    /// <summary>Window open: fade + soft scale-up.</summary>
    public static void PopIn(FrameworkElement element, int ms = 200)
    {
        if (element == null) return;
        EnsureScale(element, out var scale);
        element.Opacity = 0;
        scale.ScaleX = scale.ScaleY = 0.97;

        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(ms + 30)) { EasingFunction = EaseOut });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(ms + 30)) { EasingFunction = EaseOut });
    }

    /// <summary>Window hide: fade + soft scale-down.</summary>
    public static void PopOut(FrameworkElement element, Action? onCompleted = null, int ms = 140)
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
            new DoubleAnimation(scale.ScaleX, 0.97, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseIn });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(scale.ScaleY, 0.97, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseIn });
    }

    /// <summary>Non-modal toast: rise + fade in, hold, fade out. No buttons.</summary>
    public static void ShowToast(FrameworkElement host, TextBlock? label = null, string? text = null, int holdMs = 1200)
    {
        if (host == null) return;
        if (label != null && text != null)
            label.Text = text;

        _toastHold?.Stop();
        int gen = ++_toastGen;

        EnsureTranslate(host, out var tt);
        host.Visibility = Visibility.Visible;
        host.IsHitTestVisible = false;
        host.Opacity = 0;
        tt.Y = 16;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = EaseOut };
        var slideIn = new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = EaseOut };

        fadeIn.Completed += (_, _) =>
        {
            if (gen != _toastGen) return;
            _toastHold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(holdMs) };
            _toastHold.Tick += (_, _) =>
            {
                _toastHold.Stop();
                if (gen != _toastGen) return;

                var fadeOut = new DoubleAnimation(host.Opacity, 0, TimeSpan.FromMilliseconds(220))
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
                    new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(220)) { EasingFunction = EaseIn });
            };
            _toastHold.Start();
        };

        host.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        tt.BeginAnimation(TranslateTransform.YProperty, slideIn);
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
        var brush = new System.Windows.Media.LinearGradientBrush(
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

/// <summary>Clean dark-pink tray menu Ã¢â‚¬â€ even rows, no crooked padding.</summary>
internal sealed class DarkPinkMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly System.Drawing.Color Bg = System.Drawing.Color.FromArgb(0x1A, 0x14, 0x18);
    private static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(0x3D, 0x2A, 0x34);
    private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(0x2E, 0x1F, 0x27);
    private static readonly System.Drawing.Color Accent = System.Drawing.Color.FromArgb(0xC4, 0x5C, 0x84);
    private static readonly System.Drawing.Color Text = System.Drawing.Color.FromArgb(0xF3, 0xE6, 0xEC);
    private static readonly System.Drawing.Color TextHot = System.Drawing.Color.FromArgb(0xD4, 0x74, 0x9A);

    public DarkPinkMenuRenderer() : base(new DarkPinkColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new System.Drawing.SolidBrush(Bg);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new System.Drawing.Pen(Border);
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

        if (!e.Item.Selected && !e.Item.Pressed) return;

        using var path = RoundedRect(bounds, 4);
        using var brush = new System.Drawing.SolidBrush(Hover);
        g.FillPath(brush, path);
        using var pen = new System.Drawing.Pen(Accent);
        g.DrawPath(pen, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Selected ? TextHot : Text;
        e.TextFormat = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix;
        // Keep left padding consistent
        e.TextRectangle = new System.Drawing.Rectangle(14, 0, e.Item.Width - 20, e.Item.Height);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.ContentRectangle.Top + e.Item.ContentRectangle.Height / 2;
        using var pen = new System.Drawing.Pen(Border);
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

    private sealed class DarkPinkColorTable : ProfessionalColorTable
    {
        public override System.Drawing.Color MenuBorder => Border;
        public override System.Drawing.Color MenuItemBorder => Accent;
        public override System.Drawing.Color MenuItemSelected => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override System.Drawing.Color ToolStripDropDownBackground => Bg;
        public override System.Drawing.Color ImageMarginGradientBegin => Bg;
        public override System.Drawing.Color ImageMarginGradientMiddle => Bg;
        public override System.Drawing.Color ImageMarginGradientEnd => Bg;
        public override System.Drawing.Color SeparatorDark => Border;
        public override System.Drawing.Color SeparatorLight => Border;
    }
}
