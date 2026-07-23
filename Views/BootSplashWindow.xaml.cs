using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DisplayProfileManager;

public partial class BootSplashWindow : Window
{
    public BootSplashWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var done = false;
        void Finish()
        {
            if (done) return;
            done = true;
            try { DialogResult = true; }
            catch { Close(); }
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
