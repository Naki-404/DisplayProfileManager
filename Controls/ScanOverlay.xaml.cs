using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DisplayProfileManager.Services;

namespace DisplayProfileManager;

public partial class ScanOverlay : System.Windows.Controls.UserControl
{
    private Storyboard? _loop;

    public ScanOverlay()
    {
        InitializeComponent();
        IsHitTestVisible = false;
    }

    public void SetStatus(string text) => StatusText.Text = text;

    public void ShowAnimated()
    {
        try
        {
            var bmp = AssetLoader.Image("scan-shelf.jpg");
            if (bmp != null) Art.Source = bmp;
        }
        catch { /* optional */ }

        IsHitTestVisible = true;
        Visibility = Visibility.Visible;
        Opacity = 0;
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });

        _loop = FindResource("ScanLoop") as Storyboard;
        _loop?.Begin(this, true);

        if (FindResource("SpinAnim") is Storyboard spin)
            spin.Begin(this, true);
    }

    public void HideAnimated(Action? done = null)
    {
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => FinishHide(done);
        BeginAnimation(OpacityProperty, fade);

        var safety = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        safety.Tick += (_, _) =>
        {
            safety.Stop();
            FinishHide(done);
        };
        safety.Start();
    }

    private bool _hideDone;
    private void FinishHide(Action? done)
    {
        if (_hideDone) return;
        _hideDone = true;
        if (FindResource("SpinAnim") is Storyboard spin) spin.Stop(this);
        _loop?.Stop(this);
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        done?.Invoke();
        Dispatcher.BeginInvoke(() => _hideDone = false);
    }
}
