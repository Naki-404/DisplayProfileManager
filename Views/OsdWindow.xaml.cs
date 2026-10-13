using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Visor.Services;

namespace Visor;

/// <summary>
/// Small topmost on-screen banner shown over the game (e.g. "Preset: Night"). Click-through,
/// never takes focus, appears on the monitor the game is on and hides itself after ~1.5 s.
/// </summary>
public partial class OsdWindow : Window
{
    private static OsdWindow? _instance;
    private readonly DispatcherTimer _hold;

    private OsdWindow()
    {
        InitializeComponent();
        Logo.Source = AssetLoader.AppIcon(32);
        _hold = new DispatcherTimer();
        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            FadeOut();
        };
        SourceInitialized += (_, _) =>
        {
            OverlayWin32.SetClickThrough(this);
            OverlayWin32.StayTopmost(this);
        };
    }

    /// <summary>Shows (or refreshes) the banner. Safe to call from the UI thread at any time.</summary>
    public static void Flash(string title, string? subtitle = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        try
        {
            _instance ??= new OsdWindow();
            _instance.ShowMessage(title.Trim(), subtitle);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "OSD banner");
        }
    }

    private void ShowMessage(string title, string? subtitle)
    {
        TitleText.Text = title;
        SubText.Text = subtitle ?? "";
        SubText.Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible;

        if (!IsVisible)
            Show();
        UpdateLayout();
        OverlayWin32.PlaceTopCenter(this, 48);
        OverlayWin32.StayTopmost(this);

        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(Card.Opacity, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Slide.Y, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
        });

        int chars = title.Length + (subtitle?.Length ?? 0);
        _hold.Stop();
        _hold.Interval = TimeSpan.FromMilliseconds(Math.Clamp(1500 + Math.Max(0, chars - 24) * 40, 1500, 3000));
        _hold.Start();
    }

    private void FadeOut()
    {
        var fade = new DoubleAnimation(Card.Opacity, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            if (_hold.IsEnabled) return; // a new message arrived meanwhile
            Slide.BeginAnimation(TranslateTransform.YProperty, null);
            Slide.Y = -8;
            Hide();
        };
        Card.BeginAnimation(OpacityProperty, fade);
    }
}
