using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Visor;

public partial class ScanOverlay : System.Windows.Controls.UserControl
{
    private Storyboard? _loop;
    private Storyboard? _spin;
    private bool _loopsRunning;

    public ScanOverlay()
    {
        InitializeComponent();
        IsHitTestVisible = false;
        // The two "Forever" loops only run while actually on screen: pause when the host window is
        // hidden to the tray mid-scan, resume when it comes back, stop for good on unload.
        IsVisibleChanged += (_, e) =>
        {
            if (!_loopsRunning) return;
            if (e.NewValue is true)
            {
                _loop?.Resume(this);
                _spin?.Resume(this);
            }
            else
            {
                _loop?.Pause(this);
                _spin?.Pause(this);
            }
        };
        Unloaded += (_, _) => StopLoops();
    }

    public void SetStatus(string text) => StatusText.Text = text;

    public void ShowAnimated()
    {
        TitleText.Text = Visor.Services.Loc.T("scan.title");
        StatusText.Text = Visor.Services.Loc.T("scan.status");
        IsHitTestVisible = true;
        Visibility = Visibility.Visible;
        if (UiMotion.Reduced)
        {
            // Static indicator: no fade, no spinning ring, no sweeping bar.
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            BarFill.Width = 160;
            RingPulse.Opacity = 0.85;
            return;
        }

        Opacity = 0;
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        _loop = FindResource("ScanLoop") as Storyboard;
        _spin = FindResource("SpinAnim") as Storyboard;
        _loop?.Begin(this, true);
        _spin?.Begin(this, true);
        _loopsRunning = true;
    }

    private void StopLoops()
    {
        if (!_loopsRunning) return;
        _loopsRunning = false;
        _spin?.Stop(this);
        _loop?.Stop(this);
    }

    public void HideAnimated(Action? done = null)
    {
        if (UiMotion.Reduced)
        {
            FinishHide(done);
            return;
        }
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
        StopLoops();
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        done?.Invoke();
        Dispatcher.BeginInvoke(() => _hideDone = false);
    }
}
