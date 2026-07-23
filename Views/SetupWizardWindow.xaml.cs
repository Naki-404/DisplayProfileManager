using System.Windows;
using System.Windows.Controls;
using DisplayProfileManager.Models;
using DisplayProfileManager.Services;

namespace DisplayProfileManager;

public partial class SetupWizardWindow : Window
{
    private ThemePalette? _customPalette;

    public SetupWizardWindow()
    {
        InitializeComponent();
        CmbLang.SelectedIndex = 0;
        CmbTheme.SelectedIndex = 0;
        FillMonitors();
        ApplyLabels();
        ApplyThemePreview();
    }

    private void FillMonitors()
    {
        CmbMonitor.Items.Clear();
        CmbMonitor.Items.Add(new ComboBoxItem { Content = Loc.T("setup.monitor.primary"), Tag = "" });
        foreach (var d in DisplayEngine.GetDisplays())
        {
            var label = d.Primary ? $"{d.Friendly} ★" : d.Friendly;
            CmbMonitor.Items.Add(new ComboBoxItem { Content = $"{label} ({d.DeviceName})", Tag = d.DeviceName });
        }
        CmbMonitor.SelectedIndex = 0;
    }

    private void ApplyLabels()
    {
        TxtTitle.Text = Loc.T("setup.title");
        TxtSub.Text = Loc.T("setup.subtitle");
        LblLang.Text = Loc.T("setup.language");
        LblTheme.Text = Loc.T("setup.theme");
        LblMonitor.Text = Loc.T("setup.monitor");
        ChkAutostart.Content = Loc.T("setup.autostart");
        ChkStartMin.Content = Loc.T("setup.startMin");
        ChkNotify.Content = Loc.T("setup.notify");
        ChkBackup.Content = Loc.T("setup.backup");
        ChkTrayClose.Content = Loc.T("setup.trayClose");
        BtnFinish.Content = Loc.T("btn.finish");
        BtnEditPalette.Content = Loc.T("setup.editPalette");

        if (CmbTheme.Items[0] is ComboBoxItem d) d.Content = Loc.T("setup.theme.dark");
        if (CmbTheme.Items[1] is ComboBoxItem l) l.Content = Loc.T("setup.theme.light");
        if (CmbTheme.Items[2] is ComboBoxItem c) c.Content = Loc.T("setup.theme.custom");
        if (CmbMonitor.Items.Count > 0 && CmbMonitor.Items[0] is ComboBoxItem m)
            m.Content = Loc.T("setup.monitor.primary");
    }

    private void Lang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CmbLang.SelectedItem is ComboBoxItem it && it.Tag is string tag)
        {
            Loc.SetLocale(tag);
            ApplyLabels();
        }
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        BtnEditPalette.Visibility = CmbTheme.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (CmbTheme.SelectedIndex == 2)
            _customPalette ??= ThemeService.SeedCustom("#C45C84", "#120E11");
        ApplyThemePreview();
    }

    private void EditPalette_Click(object sender, RoutedEventArgs e)
    {
        _customPalette ??= ThemeService.SeedCustom("#C45C84", "#120E11");
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
            _customPalette ??= ThemeService.SeedCustom("#C45C84", "#120E11");
            ThemeService.ApplyPalette(_customPalette);
        }
        else
            ThemeService.Apply(new UiPreferences { Theme = theme });
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        var cfg = App.Services.Config.Current;
        string theme = (CmbTheme.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "dark";
        string? mon = (CmbMonitor.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        cfg.Ui = new UiPreferences
        {
            Locale = (CmbLang.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "en",
            Theme = theme,
            NotifyOnGameStart = ChkNotify.IsChecked == true,
            BackupOnSave = ChkBackup.IsChecked == true,
            MinimizeToTrayOnClose = ChkTrayClose.IsChecked == true,
            PreferredDisplayDevice = string.IsNullOrWhiteSpace(mon) ? null : mon,
            SetupCompleted = true,
            ShowActiveInHeader = true,
            ConfirmDelete = true
        };
        if (theme == "custom")
        {
            cfg.Ui.CustomPalette = (_customPalette ?? ThemeService.SeedCustom("#C45C84", "#120E11")).Clone();
            cfg.Ui.CustomAccent = cfg.Ui.CustomPalette.Accent;
            cfg.Ui.CustomBackground = cfg.Ui.CustomPalette.Bg;
        }

        cfg.StartWithWindows = ChkAutostart.IsChecked == true;
        cfg.StartMinimized = ChkStartMin.IsChecked == true;
        Loc.SetLocale(cfg.Ui.Locale);
        ThemeService.Apply(cfg.Ui);
        App.Services.Config.Save(cfg);

        var exe = Environment.ProcessPath ?? "";
        if (!string.IsNullOrWhiteSpace(exe))
            AutostartService.SetEnabled(cfg.StartWithWindows, exe);

        DialogResult = true;
        Close();
    }
}
