using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace DisplayProfileManager.Setup;

public partial class SetupWindow : Window
{
    private Storyboard? _spin;
    private Storyboard? _progress;

    public SetupWindow()
    {
        InitializeComponent();
        TxtPath.Text = InstallerCore.DefaultInstallDir;
        TxtPath.TextChanged += (_, _) => UpdateResolvedHint();
        UpdateResolvedHint();
        if (!InstallerCore.HasDesktopRuntime())
            TxtRuntime.Text = ".NET Desktop Runtime not found — Install will open the download page first.";
    }

    private void UpdateResolvedHint()
    {
        var resolved = InstallerCore.ResolveInstallDir(TxtPath.Text);
        TxtResolved.Text = "Will install to: " + resolved;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Chrome.BeginAnimation(OpacityProperty, fade);

        var scaleX = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 }
        };
        var scaleY = scaleX.Clone();
        ChromeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleX);
        ChromeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleY);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var d = new System.Windows.Forms.FolderBrowserDialog
        {
            SelectedPath = DirectoryExistsOrParent(TxtPath.Text),
            Description = "Choose parent folder — a DisplayProfileManager folder will be created inside"
        };
        if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TxtPath.Text = d.SelectedPath;
    }

    private static string DirectoryExistsOrParent(string path)
    {
        try
        {
            if (Directory.Exists(path)) return path;
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) return parent;
        }
        catch { }
        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    private void StartInstallAnimations()
    {
        ProgressPanel.Visibility = Visibility.Visible;
        TxtRuntime.Visibility = Visibility.Collapsed;

        _spin = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var spinAnim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9));
        Storyboard.SetTarget(spinAnim, SpinRotate);
        Storyboard.SetTargetProperty(spinAnim, new PropertyPath(System.Windows.Media.RotateTransform.AngleProperty));
        _spin.Children.Add(spinAnim);
        _spin.Begin();

        _progress = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var slide = new DoubleAnimation(-90, 420, TimeSpan.FromSeconds(1.35))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(slide, ProgressSlide);
        Storyboard.SetTargetProperty(slide, new PropertyPath(System.Windows.Media.TranslateTransform.XProperty));
        _progress.Children.Add(slide);
        _progress.Begin();
    }

    private void StopInstallAnimations()
    {
        _spin?.Stop();
        _progress?.Stop();
    }

    private void SetStatus(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(text));
            return;
        }
        TxtStatus.Text = text;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        BtnInstall.IsEnabled = false;
        BtnCancel.IsEnabled = false;
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

            var target = InstallerCore.ResolveInstallDir(TxtPath.Text);
            TxtPath.Text = target;
            UpdateResolvedHint();

            var startMenu = ChkStartMenu.IsChecked == true;
            var desktop = ChkDesktop.IsChecked == true;
            var launch = ChkLaunch.IsChecked == true;

            StartInstallAnimations();
            SetStatus("Installing…");

            // Background: file extract only (safe across drives / folders)
            await Task.Run(() => InstallerCore.ExtractAppFiles(target, SetStatus)).ConfigureAwait(true);

            // UI / STA thread: registry + shortcuts (fixes cross-thread crash)
            InstallerCore.RegisterInstallation(target, startMenu, desktop, SetStatus);

            StopInstallAnimations();
            SetStatus("Done — registered in Apps & Features.");

            if (launch)
            {
                var exe = Path.Combine(target, "DisplayProfileManager.exe");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = target,
                    UseShellExecute = true
                });
            }

            await ShowSuccessAndCloseAsync(
                "Installed successfully",
                "Thank you for installing Display Profile Manager.\nEnjoy — made by Nakidev");
        }
        catch (Exception ex)
        {
            StopInstallAnimations();
            System.Windows.MessageBox.Show(this, "Install failed:\n" + ex.Message, Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnInstall.IsEnabled = true;
            BtnCancel.IsEnabled = true;
        }
    }

    private async Task ShowSuccessAndCloseAsync(string title, string body)
    {
        TxtSuccessTitle.Text = title;
        TxtSuccessBody.Text = body;
        SuccessOverlay.Visibility = Visibility.Visible;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        SuccessOverlay.BeginAnimation(OpacityProperty, fade);

        var pop = new DoubleAnimation(0.5, 1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 }
        };
        SuccessCheckScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pop);
        SuccessCheckScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pop.Clone());

        await Task.Delay(2400);
        Close();
    }
}
