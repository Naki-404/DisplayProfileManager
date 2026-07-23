using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DisplayProfileManager;

/// <summary>Centered easter-egg banner. Trigger: 5× Library/Profiles clicks.</summary>
public partial class EggOverlay : System.Windows.Controls.UserControl
{
    private DispatcherTimer? _hold;
    private int _gen;
    private int _profilesClicks;
    private DateTime _streakStartUtc = DateTime.MinValue;
    private bool _busy;

    public EggOverlay()
    {
        InitializeComponent();
        Unloaded += (_, _) => _hold?.Stop();
    }

    public void NotifyProfilesClick()
    {
        var now = DateTime.UtcNow;
        if (_streakStartUtc == DateTime.MinValue || (now - _streakStartUtc).TotalSeconds > 5)
        {
            _streakStartUtc = now;
            _profilesClicks = 0;
        }

        _profilesClicks++;
        if (_profilesClicks < 5) return;

        _profilesClicks = 0;
        _streakStartUtc = DateTime.MinValue;
        _busy = false;
        ShowRandom();
    }

    public void ShowRandom()
    {
        if (_busy) return;
        _busy = true;
        int gen = ++_gen;

        if (FindResource("EggIn") is Storyboard innOld) innOld.Stop(this);
        if (FindResource("EggOut") is Storyboard outOld) outOld.Stop(this);

        Root.Opacity = 1;
        Visibility = Visibility.Visible;
        IsHitTestVisible = false;

        if (FindResource("EggIn") is Storyboard inn)
            inn.Begin(this, true);

        _hold?.Stop();
        _hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            if (gen != _gen) return;
            Hide(gen);
        };
        _hold.Start();
    }

    private void Hide(int gen)
    {
        void Finish()
        {
            if (gen != _gen) return;
            Visibility = Visibility.Collapsed;
            IsHitTestVisible = false;
            Card.BeginAnimation(OpacityProperty, null);
            Root.BeginAnimation(OpacityProperty, null);
            Card.Opacity = 0;
            Root.Opacity = 1;
            _busy = false;
        }

        if (FindResource("EggOut") is Storyboard outing)
        {
            EventHandler? handler = null;
            handler = (_, _) =>
            {
                outing.Completed -= handler;
                Finish();
            };
            outing.Completed += handler;
            outing.Begin(this, true);
            var safety = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            safety.Tick += (_, _) =>
            {
                safety.Stop();
                Finish();
            };
            safety.Start();
        }
        else Finish();
    }
}
