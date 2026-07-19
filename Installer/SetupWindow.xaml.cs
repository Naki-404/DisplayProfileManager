using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace DisplayProfileManager.Setup;

public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
        TxtPath.Text = InstallerCore.DefaultInstallDir;
        if (!InstallerCore.HasDesktopRuntime())
            TxtRuntime.Text = ".NET Desktop Runtime not found — Install will open the download page first.";
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Chrome.BeginAnimation(OpacityProperty, fade);

        var slide = new ThicknessAnimation(
            new Thickness(0, 12, 0, -12),
            new Thickness(0),
            TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Chrome.BeginAnimation(MarginProperty, slide);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        // WPF folder picker via WinForms dialog (simple, no extra package)
        using var d = new System.Windows.Forms.FolderBrowserDialog
        {
            SelectedPath = TxtPath.Text,
            Description = "Choose install folder"
        };
        if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TxtPath.Text = d.SelectedPath;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        BtnInstall.IsEnabled = false;
        try
        {
            if (!InstallerCore.HasDesktopRuntime())
            {
                InstallerCore.OpenRuntimeDownload();
                System.Windows.MessageBox.Show(this,
                    "Install the .NET Desktop Runtime, then click Install again.",
                    "Display Profile Manager",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dir = TxtPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(dir))
            {
                System.Windows.MessageBox.Show(this, "Choose an install folder.", Title);
                return;
            }

            await Task.Run(() => InstallerCore.Install(
                dir,
                ChkStartMenu.IsChecked == true,
                ChkDesktop.IsChecked == true,
                false,
                s => Dispatcher.Invoke(() => TxtStatus.Text = s)));

            // Pulse success
            TxtStatus.Text = "Installation complete.";
            var pulse = new DoubleAnimation(1, 0.55, TimeSpan.FromMilliseconds(160))
            {
                AutoReverse = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            BtnInstall.BeginAnimation(OpacityProperty, pulse);

            if (ChkLaunch.IsChecked == true)
            {
                var exe = System.IO.Path.Combine(dir, "DisplayProfileManager.exe");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true
                });
            }

            await Task.Delay(350);
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "Install failed:\n" + ex.Message, Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnInstall.IsEnabled = true;
        }
    }
}
