using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

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
        IsHitTestVisible = true;
        Visibility = Visibility.Visible;
        Opacity = 0;
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        _loop = FindResource("ScanLoop") as Storyboard;
        _loop?.Begin(this, true);

        if (FindResource("SpinAnim") is Storyboard spin)
            spin.Begin(this, true);
    }

    public void HideAnimated(Action? done = null)
    {
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => FinishHide(done);
        BeginAnimation(OpacityProperty, fade);

        var safety = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
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
