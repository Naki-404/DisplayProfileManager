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

        if (FindResource("BootStory") is Storyboard sb)
        {
            sb.Completed += (_, _) => Finish();
            sb.Begin(this);
        }

        var safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        safety.Tick += (_, _) =>
        {
            safety.Stop();
            Finish();
        };
        safety.Start();
    }
}
