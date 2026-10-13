using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Visor.Services;

namespace Visor;

public partial class BootSplashWindow : Window
{
    public BootSplashWindow()
    {
        InitializeComponent();
        AppIcon.Source = AssetLoader.AppIcon(64);
        Subtitle.Text = Loc.T("splash.starting");
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var done = false;
        void Finish()
        {
            if (done) return;
            done = true;
            Close(); // shown modeless (Show), so DialogResult is not available
        }

        bool reduced = UiMotion.Reduced;
        if (!reduced && FindResource("BootStory") is Storyboard sb)
        {
            sb.Completed += (_, _) => Finish();
            sb.Begin(this, true);
            // Hidden early by the main window's ContentRendered: stop the spin so nothing keeps ticking.
            IsVisibleChanged += (_, args) =>
            {
                if (args.NewValue is false && !done)
                    Dispatcher.BeginInvoke(() =>
                    {
                        sb.Stop(this);
                        Finish();
                    });
            };
        }
        else
        {
            // Reduced motion: static card, no spin / scale, and it goes away sooner.
            Card.Opacity = 1;
            CardScale.ScaleX = CardScale.ScaleY = 1;
            IconScale.ScaleX = IconScale.ScaleY = 1;
            BrandTitle.Opacity = 1;
            Subtitle.Opacity = 1;
        }

        var safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(reduced ? 1.2 : 2.4) };
        safety.Tick += (_, _) =>
        {
            safety.Stop();
            Finish();
        };
        safety.Start();
    }
}
