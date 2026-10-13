using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Visor.Setup;

public partial class UninstallWindow : Window
{
    private Storyboard? _spin;
    private bool _busy;

    public UninstallWindow()
    {
        InitializeComponent();
        var dir = InstallerCore.FindInstallLocation();
        if (dir == null)
        {
            TxtPath.Text = L.NotFound;
            TxtMissing.Text = L.NothingInstalled;
            TxtMissing.Visibility = Visibility.Visible;
            BtnUninstall.IsEnabled = false;
        }
        else
        {
            TxtPath.Text = dir;
        }
        Closing += (_, e) => e.Cancel = _busy;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Chrome.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        var scale = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 }
        };
        ChromeScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        ChromeScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale.Clone());
        PanelConfirm.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380))
        {
            BeginTime = TimeSpan.FromMilliseconds(80),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        _busy = true;
        BtnUninstall.IsEnabled = false;
        BtnClose.IsEnabled = false;
        PanelConfirm.Visibility = Visibility.Collapsed;
        PanelWork.Visibility = Visibility.Visible;

        _spin = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var spinAnim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.85));
        Storyboard.SetTarget(spinAnim, SpinRotate);
        Storyboard.SetTargetProperty(spinAnim, new PropertyPath(RotateTransform.AngleProperty));
        _spin.Children.Add(spinAnim);
        _spin.Begin();

        try
        {
            var result = await App.RunSta(() => InstallerCore.Uninstall(
                msg => Dispatcher.BeginInvoke(new Action(() => TxtStatus.Text = msg))));

            _spin.Stop();
            if (result.NotFound)
                throw new InstallerException(L.NothingInstalled);

            PanelWork.Visibility = Visibility.Collapsed;
            PanelDone.Visibility = Visibility.Visible;
            PanelDone.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
            var pop = new DoubleAnimation(0.5, 1, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 }
            };
            CheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            CheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop.Clone());

            await Task.Delay(1800);
            if (result.SelfToDelete != null)
                InstallerCore.ScheduleSelfDelete(result.SelfToDelete, result.Dir!);
            _busy = false;
            Application.Current.Shutdown(App.ExitOk);
        }
        catch (Exception ex)
        {
            _spin?.Stop();
            _busy = false;
            MessageBox.Show(this, L.UninstallFailed + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            PanelWork.Visibility = Visibility.Collapsed;
            PanelConfirm.Visibility = Visibility.Visible;
            BtnUninstall.IsEnabled = InstallerCore.FindInstallLocation() != null;
            BtnClose.IsEnabled = true;
        }
    }
}
