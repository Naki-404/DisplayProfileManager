using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DisplayProfileManager;

public partial class SaveOverlay : System.Windows.Controls.UserControl
{
    private DispatcherTimer? _hold;
    private int _gen;

    public SaveOverlay()
    {
        InitializeComponent();
    }

    public void ShowSaved(string title = "Saved")
    {
        TitleText.Text = title;
        _hold?.Stop();
        int gen = ++_gen;

        Visibility = Visibility.Visible;
        if (FindResource("SaveIn") is Storyboard inn)
            inn.Begin(this, true);

        _hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            if (gen != _gen) return;
            HideAnimated();
        };
        _hold.Start();
    }

    public void HideAnimated()
    {
        if (FindResource("SaveOut") is Storyboard outing)
        {
            outing.Completed += (_, _) => { Visibility = Visibility.Collapsed; };
            outing.Begin(this, true);
        }
        else Visibility = Visibility.Collapsed;
    }
}
