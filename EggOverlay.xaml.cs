using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DisplayProfileManager.Services;

namespace DisplayProfileManager;

/// <summary>Centered half-screen easter-egg banner. Trigger: 5× Profiles clicks.</summary>
public partial class EggOverlay : System.Windows.Controls.UserControl
{
    private readonly Random _rng = new();
    private readonly ConcurrentDictionary<string, BitmapImage> _cache = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _hold;
    private int _gen;
    private int _profilesClicks;
    private bool _busy;
    private string? _last;

    private static readonly string[] Scenes = AssetLoader.KnownImages;

    public EggOverlay()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _hold?.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SizeChanged += (_, _) => FitCard();
        FitCard();
    }

    public void NotifyProfilesClick()
    {
        _profilesClicks++;
        if (_profilesClicks < 5) return;
        _profilesClicks = 0;
        _busy = false;
        ShowRandom();
    }

    public void ShowRandom()
    {
        if (_busy) return;

        var pool = Scenes.ToList();
        if (pool.Count == 0) return;

        var choices = pool.Where(p => !string.Equals(p, _last, StringComparison.OrdinalIgnoreCase)).ToList();
        if (choices.Count == 0) choices = pool;
        var name = choices[_rng.Next(choices.Count)];
        _last = name;

        BitmapImage? bmp;
        if (!_cache.TryGetValue(name, out bmp))
        {
            bmp = AssetLoader.Image(name);
            if (bmp == null) return;
            // Cap cache to known scenes — avoid unbounded growth if asset list grows later.
            if (_cache.Count >= Math.Max(8, Scenes.Length + 2))
                _cache.Clear();
            _cache[name] = bmp;
        }

        int gen = ++_gen;
        _busy = true;
        FitCard();

        if (FindResource("EggIn") is Storyboard innOld) innOld.Stop(this);
        if (FindResource("EggOut") is Storyboard outOld) outOld.Stop(this);

        Art.Source = bmp;
        Root.Opacity = 1;
        Visibility = Visibility.Visible;
        IsHitTestVisible = false;
        Card.Opacity = 1;
        CardScale.ScaleX = 1;
        CardScale.ScaleY = 1;
        ArtFloat.Y = 0;

        if (FindResource("EggIn") is Storyboard inn)
            inn.Begin(this, true);

        _hold?.Stop();
        _hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3200) };
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
            var safety = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            safety.Tick += (_, _) =>
            {
                safety.Stop();
                Finish();
            };
            safety.Start();
        }
        else Finish();
    }

    private void FitCard()
    {
        double w = ActualWidth > 80 ? ActualWidth * 0.55 : 560;
        double h = ActualHeight > 80 ? ActualHeight * 0.5 : 320;
        Card.Width = Math.Clamp(w, 420, 780);
        Card.Height = Math.Clamp(h, 240, 440);
    }
}
