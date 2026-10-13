using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Visor.Setup;

public partial class SetupWindow : Window
{
    private readonly LegacyInstall? _legacy;
    private Storyboard? _spin;
    private Storyboard? _progress;
    private bool _busy;

    public SetupWindow()
    {
        InitializeComponent();
        TxtVersion.Text = L.Version(Product.Version);
        TxtPath.Text = InstallerCore.FindInstallLocation() ?? Product.DefaultInstallDir;
        TxtPath.TextChanged += (_, _) => UpdateResolvedHint();

        _legacy = LegacyInstall.Detect();
        if (_legacy != null)
        {
            TxtLegacy.Text = L.RemoveLegacy(_legacy.Dir ?? "");
            ChkLegacy.Visibility = Visibility.Visible;
        }

        UpdateResolvedHint();
        Closing += (_, e) => e.Cancel = _busy;
    }

    private void UpdateResolvedHint()
    {
        var state = InstallerCore.Inspect(InstallerCore.ResolveInstallDir(TxtPath.Text));
        bool ok = state.Kind is TargetKind.Fresh or TargetKind.Upgrade;
        TxtResolved.Foreground = (Brush)FindResource(ok ? "AccentHoverBrush" : "DangerBrush");
        TxtResolved.Text = state.Kind switch
        {
            TargetKind.Upgrade => L.WillUpdate(state.Dir!, state.ExistingVersion),
            TargetKind.Fresh => L.WillInstallTo(state.Dir!),
            TargetKind.Foreign => state.Dir + "\n" + L.FolderNotEmptyShort,
            TargetKind.Protected => state.Dir + "\n" + L.ProtectedFolderShort,
            _ => L.InvalidPath,
        };
        BtnInstall.Content = state.Kind == TargetKind.Upgrade ? L.Update : L.Install;
        BtnInstall.IsEnabled = ok && !_busy;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Chrome.BeginAnimation(OpacityProperty, fade);

        var scale = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(460))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 }
        };
        ChromeScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        ChromeScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale.Clone());

        var contentFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
        {
            BeginTime = TimeSpan.FromMilliseconds(90),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ContentRoot.BeginAnimation(OpacityProperty, contentFade);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Start from the parent of the current "...\Visor" target.
            var resolved = InstallerCore.ResolveInstallDir(TxtPath.Text);
            var start = resolved == null ? null : Path.GetDirectoryName(resolved);
            if (start == null || !Directory.Exists(start))
                start = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            var picked = Native.PickFolder(new WindowInteropHelper(this).Handle, L.BrowseTitle, start);
            if (picked != null)
                TxtPath.Text = InstallerCore.ResolveInstallDir(picked) ?? picked;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SetStatus(string text) =>
        Dispatcher.BeginInvoke(new Action(() => TxtStatus.Text = text));

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var target = InstallerCore.ResolveInstallDir(TxtPath.Text);
        if (target == null) { ShowError(L.InvalidPath); return; }

        var options = new InstallOptions
        {
            TargetDir = target,
            StartMenuShortcut = ChkStartMenu.IsChecked == true,
            DesktopShortcut = ChkDesktop.IsChecked == true,
            RemoveLegacy = _legacy != null && ChkLegacy.IsChecked == true,
        };
        bool launch = ChkLaunch.IsChecked == true;

        // Ask before closing anything that is running.
        var running = AppProcesses.Find(target, options.RemoveLegacy, _legacy?.Dir);
        if (running.Count > 0)
        {
            var answer = MessageBox.Show(this, L.AppsRunning(AppProcesses.Names(running)), Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            foreach (var r in running) r.Process.Dispose();
            if (answer != MessageBoxResult.Yes) return;
        }

        SetBusy(true);
        try
        {
            var result = await App.RunSta(() => InstallerCore.Install(options, _legacy, SetStatus));
            StopAnimations();

            if (launch)
            {
                try { LaunchApp(result); }
                catch (Exception ex) { result.Warnings.Add(ex.Message); }
            }

            _busy = false;
            await ShowSuccessAsync(result);
        }
        catch (Exception ex)
        {
            StopAnimations();
            SetBusy(false);
            ShowError(ex.Message);
            MessageBox.Show(this, L.InstallFailed + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Starts Visor as the normal user. If Setup itself runs elevated (e.g. "Run as administrator"),
    /// Visor must not inherit the admin token: hand the launch to the shell (explorer.exe), which
    /// starts it un-elevated in the user's session.
    /// </summary>
    private static void LaunchApp(InstallResult result)
    {
        if (Native.IsElevated())
        {
            Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                "\"" + result.ExePath + "\"")
            {
                UseShellExecute = false
            })?.Dispose();
            return;
        }

        Process.Start(new ProcessStartInfo(result.ExePath)
        {
            WorkingDirectory = result.Dir,
            UseShellExecute = true
        })?.Dispose();
    }

    private void ShowError(string text)
    {
        TxtError.Text = text;
        TxtError.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnCancel.IsEnabled = !busy;
        BtnClose.IsEnabled = !busy;
        TxtPath.IsEnabled = !busy;
        ChkStartMenu.IsEnabled = ChkDesktop.IsEnabled = ChkLegacy.IsEnabled = ChkLaunch.IsEnabled = !busy;
        if (busy)
        {
            BtnInstall.IsEnabled = false;
            TxtError.Visibility = Visibility.Collapsed;
            StartAnimations();
        }
        else
        {
            UpdateResolvedHint();
        }
    }

    private void StartAnimations()
    {
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));

        _spin = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var spinAnim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.85));
        Storyboard.SetTarget(spinAnim, SpinRotate);
        Storyboard.SetTargetProperty(spinAnim, new PropertyPath(RotateTransform.AngleProperty));
        _spin.Children.Add(spinAnim);
        _spin.Begin();

        _progress = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var slide = new DoubleAnimation(-110, 460, TimeSpan.FromSeconds(1.25))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(slide, ProgressSlide);
        Storyboard.SetTargetProperty(slide, new PropertyPath(TranslateTransform.XProperty));
        _progress.Children.Add(slide);
        _progress.Begin();
    }

    private void StopAnimations()
    {
        _spin?.Stop();
        _progress?.Stop();
        ProgressPanel.Visibility = Visibility.Collapsed;
    }

    private async Task ShowSuccessAsync(InstallResult result)
    {
        TxtSuccessTitle.Text = result.Upgraded ? L.UpdatedOk : L.InstalledOk;
        if (result.Warnings.Count > 0)
        {
            TxtWarnings.Text = L.Warnings + "\n• " + string.Join("\n• ", result.Warnings);
            TxtWarnings.Visibility = Visibility.Visible;
            BtnDone.Visibility = Visibility.Visible;
        }
        SuccessOverlay.Visibility = Visibility.Visible;

        SuccessOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(480))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }
        };
        SuccessCheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        SuccessCheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop.Clone());

        if (result.Warnings.Count == 0)
        {
            await Task.Delay(2400);
            Close();
        }
    }
}
