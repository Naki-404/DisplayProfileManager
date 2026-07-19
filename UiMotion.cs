using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DisplayProfileManager;

/// <summary>Short one-shot UI motion â€” no continuous timers after playback.</summary>
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


    /// <summary>Tab content: subtle opacity only — no slide, no flash to zero.</summary>
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

