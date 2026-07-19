using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace DisplayProfileManager.Setup;

public partial class UninstallWindow : Window
{
    private readonly string? _installDir;
    private Storyboard? _spin;

    public UninstallWindow()
    {
        InitializeComponent();
        _installDir = InstallerCore.FindInstallLocation();
        if (string.IsNullOrWhiteSpace(_installDir))
        {
            TxtPath.Text = "(not found — nothing to remove)";
            TxtMissing.Text = "No installation was found in Apps & Features.";
            TxtMissing.Visibility = Visibility.Visible;
            BtnUninstall.IsEnabled = false;
        }
        else
        {
            TxtPath.Text = _installDir;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Chrome.BeginAnimation(OpacityProperty, fade);
        var scale = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 }
        };
        ChromeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scale);
        ChromeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scale.Clone());
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        BtnUninstall.IsEnabled = false;
        PanelConfirm.Visibility = Visibility.Collapsed;
        PanelWork.Visibility = Visibility.Visible;

        _spin = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var spinAnim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9));
        Storyboard.SetTarget(spinAnim, SpinRotate);
        Storyboard.SetTargetProperty(spinAnim, new PropertyPath(System.Windows.Media.RotateTransform.AngleProperty));
        _spin.Children.Add(spinAnim);
        _spin.Begin();

        try
        {
            TxtStatus.Text = "Closing app…";
            await Task.Delay(150);

            // Must reset gamma on UI/STA-friendly context; run whole uninstall sequence with live status
            await Task.Run(() =>
            {
                InstallerCore.PerformUninstall(confirmUi: false, silent: true, log: msg =>
                {
                    Dispatcher.Invoke(() => TxtStatus.Text = msg);
                });
            }).ConfigureAwait(true);

            _spin.Stop();
            PanelWork.Visibility = Visibility.Collapsed;
            PanelDone.Visibility = Visibility.Visible;

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280));
            PanelDone.BeginAnimation(OpacityProperty, fade);
            var pop = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }
            };
            CheckScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pop);
            CheckScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pop.Clone());

            await Task.Delay(2000);
            // Delete Uninstall.exe + install folder after this process exits
            InstallerCore.ScheduleSelfCleanup(_installDir);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _spin?.Stop();
            System.Windows.MessageBox.Show(this, "Uninstall failed:\n" + ex.Message, Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
            PanelWork.Visibility = Visibility.Collapsed;
            PanelConfirm.Visibility = Visibility.Visible;
            BtnUninstall.IsEnabled = true;
        }
    }
}
