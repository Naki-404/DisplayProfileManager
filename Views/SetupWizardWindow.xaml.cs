using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Visor.Models;
using Visor.Services;

namespace Visor;

/// <summary>
/// First-run onboarding: 1) what Visor does + language/theme, 2) behavior (autostart is opt-in),
/// 3) scan for installed games. Closable at any step (× / Skip keeps safe defaults).
/// </summary>
public partial class SetupWizardWindow : Window
{
    public sealed class FoundRow : INotifyPropertyChanged
    {
        private bool _include = true;
        public string Name { get; init; } = "";
        public string Process { get; init; } = "";
        public string? ExePath { get; init; }
        public bool Include
        {
            get => _include;
            set
            {
                _include = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Include)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private ThemePalette? _customPalette;
    private int _step = 1;
    private bool _scanned;
    private bool _scanning;
    private bool _finished;
    private readonly List<FoundRow> _found = new();

    public SetupWizardWindow()
    {
        InitializeComponent();
        Logo.Source = AssetLoader.AppIcon(64);

        // Pre-select the OS display language (RU for ru-* Windows), English otherwise.
        bool ru = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase);
        Loc.SetLocale(ru ? "ru" : "en");
        CmbLang.SelectedIndex = ru ? 1 : 0;
        CmbTheme.SelectedIndex = 0;
        FillMonitors();
        ApplyLabels();
        ApplyThemePreview();
        ShowStep(1);

        Closing += (_, _) =>
        {
            // × / Alt+F4 / Skip: keep safe defaults so the wizard doesn't come back every launch.
            if (!_finished) Commit(applyScan: false, skipped: true);
        };
    }

    private void FillMonitors()
    {
        CmbMonitor.Items.Clear();
        CmbMonitor.Items.Add(new ComboBoxItem { Content = Loc.T("setup.monitor.primary"), Tag = "" });
        foreach (var d in DisplayEngine.GetDisplays())
        {
            var label = d.Primary ? $"{d.Friendly} ★" : d.Friendly;
            CmbMonitor.Items.Add(new ComboBoxItem { Content = label, Tag = d.DeviceName, ToolTip = d.DeviceName });
        }
        CmbMonitor.SelectedIndex = 0;
    }

    private void ApplyLabels()
    {
        Title = Loc.T("setup.window");
        BtnClose.ToolTip = Loc.T("setup.skip");
        LblWhatTitle.Text = Loc.T("setup.what.title");
        LblWhat1.Text = Loc.T("setup.what.1");
        LblWhat2.Text = Loc.T("setup.what.2");
        LblWhat3.Text = Loc.T("setup.what.3");
        LblLang.Text = Loc.T("setup.language");
        LblTheme.Text = Loc.T("setup.theme");
        LblMonitor.Text = Loc.T("setup.monitor");
        ChkAutostart.Content = Loc.T("setup.autostart");
        LblAutostartHint.Text = Loc.T("setup.autostart.hint");
        ChkStartMin.Content = Loc.T("setup.startMin");
        ChkNotify.Content = Loc.T("setup.notify");
        ChkBackup.Content = Loc.T("setup.backup");
        ChkTrayClose.Content = Loc.T("setup.trayClose");
        LblScanIntro.Text = Loc.T("setup.scan.intro");
        BtnScan.Content = _scanned ? Loc.T("setup.scan.again") : Loc.T("btn.scanGames");
        LblTips.Text = Loc.T("setup.tips");
        BtnEditPalette.Content = Loc.T("setup.editPalette");
        BtnBack.Content = Loc.T("btn.back");
        BtnSkip.Content = Loc.T("setup.skip");

        if (CmbTheme.Items[0] is ComboBoxItem d) d.Content = Loc.T("setup.theme.dark");
        if (CmbTheme.Items[1] is ComboBoxItem l) l.Content = Loc.T("setup.theme.light");
        if (CmbTheme.Items[2] is ComboBoxItem c) c.Content = Loc.T("setup.theme.custom");
        if (CmbMonitor.Items.Count > 0 && CmbMonitor.Items[0] is ComboBoxItem m)
            m.Content = Loc.T("setup.monitor.primary");
        UpdateStepTexts();
    }

    private void UpdateStepTexts()
    {
        (TxtTitle.Text, TxtSub.Text) = _step switch
        {
            1 => (Loc.T("setup.title"), Loc.T("setup.subtitle")),
            2 => (Loc.T("setup.step2.title"), Loc.T("setup.step2.sub")),
            _ => (Loc.T("setup.step3.title"), Loc.T("setup.step3.sub"))
        };
        BtnNext.Content = _step == 3 ? Loc.T("btn.finish") : Loc.T("btn.next");
    }

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 1, 3);
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        BtnBack.Visibility = _step > 1 ? Visibility.Visible : Visibility.Hidden;
        Dot2.SetResourceReference(Border.BackgroundProperty, _step >= 2 ? "BrandGradientBrush" : "TrackBrush");
        Dot3.SetResourceReference(Border.BackgroundProperty, _step >= 3 ? "BrandGradientBrush" : "TrackBrush");
        UpdateStepTexts();
        var content = _step switch { 1 => (FrameworkElement)Step1, 2 => Step2, _ => Step3 };
        UiMotion.FadeSlideIn(content, fromY: 6, ms: 180);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning) return;
        if (_step < 3)
        {
            ShowStep(_step + 1);
            return;
        }
        Commit(applyScan: true, skipped: false);
        _finished = true;
        DialogResult = true;
        Close();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning) return;
        DialogResult = false;
        Close();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning) return;
        _scanning = true;
        BtnScan.IsEnabled = false;
        BtnNext.IsEnabled = false;
        TxtScanStatus.Text = Loc.T("scan.status");
        UiSound.BeginWork();
        try
        {
            var found = await Task.Run(() => GameScanner.ScanAll(msg =>
                Dispatcher.BeginInvoke(() => TxtScanStatus.Text = msg)));

            var existing = App.Services.Config.Current.Profiles
                .Select(p => p.ProcessName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _found.Clear();
            foreach (var g in found
                         .Where(g => !existing.Contains(g.Process))
                         .GroupBy(g => g.Process, StringComparer.OrdinalIgnoreCase)
                         .Select(grp => grp.First()))
            {
                _found.Add(new FoundRow { Name = g.Name, Process = g.Process, ExePath = g.ExePath });
            }
            FoundList.ItemsSource = null;
            FoundList.ItemsSource = _found;
            FoundList.Visibility = _found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtScanStatus.Text = found.Count == 0
                ? Loc.T("scan.none")
                : _found.Count == 0 ? Loc.T("scan.allListed") : Loc.Tf("setup.scan.found", _found.Count);
            _scanned = true;
            UiSound.EndWork(found.Count == 0 ? UiWorkResult.Empty : UiWorkResult.Done);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Setup scan");
            UiSound.EndWork(UiWorkResult.Cancel);
            TxtScanStatus.Text = Loc.T("scan.failed") + " " + ex.Message;
        }
        finally
        {
            _scanning = false;
            BtnScan.IsEnabled = true;
            BtnNext.IsEnabled = true;
            BtnScan.Content = _scanned ? Loc.T("setup.scan.again") : Loc.T("btn.scanGames");
        }
    }

    private void Lang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CmbLang.SelectedItem is ComboBoxItem it && it.Tag is string tag)
        {
            Loc.SetLocale(tag);
            if (IsInitialized && TxtTitle != null)
                ApplyLabels();
        }
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BtnEditPalette == null) return;
        BtnEditPalette.Visibility = CmbTheme.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (CmbTheme.SelectedIndex == 2)
            _customPalette ??= ThemeService.DefaultCustom();
        ApplyThemePreview();
    }

    private void EditPalette_Click(object sender, RoutedEventArgs e)
    {
        _customPalette ??= ThemeService.DefaultCustom();
        var win = new CustomThemeWindow(_customPalette) { Owner = this };
        if (win.ShowDialog() == true && win.ResultPalette != null)
        {
            _customPalette = win.ResultPalette;
            ThemeService.ApplyPalette(_customPalette);
        }
    }

    private void ApplyThemePreview()
    {
        var theme = (CmbTheme.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "dark";
        if (theme == "custom")
        {
            _customPalette ??= ThemeService.DefaultCustom();
            ThemeService.ApplyPalette(_customPalette);
        }
        else
            ThemeService.Apply(new UiPreferences { Theme = theme });
    }

    /// <summary>Writes the choices to config. Skipping keeps the defaults (autostart stays OFF).</summary>
    private void Commit(bool applyScan, bool skipped)
    {
        try
        {
            var cfg = App.Services.Config.Current;
            string theme = (CmbTheme.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "dark";
            string? mon = (CmbMonitor.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var ui = (cfg.Ui ?? new UiPreferences()).Clone();
            ui.Locale = (CmbLang.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "en";
            ui.Theme = theme;
            ui.NotifyOnGameStart = ChkNotify.IsChecked == true;
            ui.BackupOnSave = ChkBackup.IsChecked == true;
            ui.MinimizeToTrayOnClose = ChkTrayClose.IsChecked == true;
            ui.PreferredDisplayDevice = string.IsNullOrWhiteSpace(mon) ? null : mon;
            ui.SetupCompleted = true;
            ui.ShowActiveInHeader = true;
            ui.ConfirmDelete = true;
            if (theme == "custom")
            {
                ui.CustomPalette = (_customPalette ?? ThemeService.DefaultCustom()).Clone();
                ui.CustomAccent = ui.CustomPalette.Accent;
                ui.CustomBackground = ui.CustomPalette.Bg;
            }
            cfg.Ui = ui;

            cfg.StartWithWindows = !skipped && ChkAutostart.IsChecked == true;
            cfg.StartMinimized = !skipped && ChkStartMin.IsChecked == true;

            if (applyScan)
            {
                foreach (var row in _found.Where(r => r.Include))
                {
                    if (cfg.Profiles.Any(p => string.Equals(p.ProcessName, row.Process, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    cfg.Profiles.Add(GameCatalog.CreateProfileSkeleton(row.Process, row.Name, row.ExePath));
                }
                // Reaching Finish means the user saw the scan step — don't ask again from the main window.
                cfg.FirstScanDone = true;
            }

            Loc.SetLocale(ui.Locale);
            ThemeService.Apply(ui);
            App.Services.Config.Save(cfg);

            var exe = Environment.ProcessPath ?? "";
            if (!string.IsNullOrWhiteSpace(exe) && !AutostartService.SetEnabled(cfg.StartWithWindows, exe))
                AppLog.Warn("Setup wizard: could not change Windows startup entry.");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Setup wizard commit");
        }
    }
}
