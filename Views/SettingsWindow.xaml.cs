using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Visor.Models;
using Visor.Services;

namespace Visor;

public partial class SettingsWindow : Window
{
    public bool Imported { get; private set; }
    private ThemePalette? _customPalette;
    /// <summary>True once Save committed — any other way of closing (Esc, ×, Alt+F4) reverts the live preview.</summary>
    private bool _committed;

    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            OverlayWin32.EnsureInsideWorkArea(this); // 560 DIP default: fits 1366×768 @125%; still clamps if needed
            UiMotion.PopIn(ChromeRoot);
        };
        Closing += (_, _) =>
        {
            if (!_committed) RevertPreview();
        };
        LoadFromConfig();
        ApplyLabels();
        UpdatePaletteButton();
    }

    /// <summary>Puts language, theme and sound back to the saved config (preview was live while the dialog was open).</summary>
    private void RevertPreview()
    {
        var ui = App.Services.Config.Current.Ui ?? new UiPreferences();
        // Loc.Changed relabels the main window (once) when the locale actually changed.
        Loc.SetLocale(ui.Locale);
        ThemeService.Apply(ui);
        UiSound.ApplyFromConfig(ui);
        if (System.Windows.Application.Current is App app)
            app.RebuildTrayMenu();
    }

    private void LoadFromConfig()
    {
        var cfg = App.Services.Config.Current;
        var ui = cfg.Ui ?? new UiPreferences();
        _customPalette = ui.CustomPalette?.Clone()
                         ?? ThemeService.SeedCustom(ui.CustomAccent, ui.CustomBackground);

        SelectByTag(CmbLang, ui.Locale);
        SelectByTag(CmbTheme, ui.Theme);

        CmbMonitor.Items.Clear();
        CmbMonitor.Items.Add(new ComboBoxItem { Content = Loc.T("setup.monitor.primary"), Tag = "" });
        int sel = 0;
        int i = 1;
        foreach (var d in DisplayEngine.GetDisplays())
        {
            var label = d.Primary ? $"{d.Friendly} ★" : d.Friendly;
            CmbMonitor.Items.Add(new ComboBoxItem { Content = $"{label} ({d.DeviceName})", Tag = d.DeviceName });
            if (string.Equals(d.DeviceName, ui.PreferredDisplayDevice, StringComparison.OrdinalIgnoreCase))
                sel = i;
            i++;
        }
        CmbMonitor.SelectedIndex = sel;

        ChkAutostart.IsChecked = cfg.StartWithWindows;
        ChkStartMin.IsChecked = cfg.StartMinimized;
        ChkNotify.IsChecked = ui.NotifyOnGameStart;
        ChkBackup.IsChecked = ui.BackupOnSave;
        ChkTrayClose.IsChecked = ui.MinimizeToTrayOnClose;
        ChkConfirmDelete.IsChecked = ui.ConfirmDelete;
        ChkShowActive.IsChecked = ui.ShowActiveInHeader;
        ChkOverlayAuto.IsChecked = ui.OverlayAutoShowOnGame;
        if (ChkOverlayHotkeyOnly != null) ChkOverlayHotkeyOnly.IsChecked = ui.OverlayHotkeyOnly;
        ChkUiSounds.IsChecked = ui.UiSoundsEnabled;
        SldSoundVol.Value = Math.Clamp(ui.UiSoundVolume, 0, 100);
        LblSoundVolVal.Text = $"{(int)SldSoundVol.Value}%";
        SldSoundVol.IsEnabled = ui.UiSoundsEnabled;
        BtnPreviewSound.IsEnabled = ui.UiSoundsEnabled;
        if (ChkMuteInGame != null) ChkMuteInGame.IsChecked = ui.MuteSoundsInGame;
        if (CmbSoundPreview != null && CmbSoundPreview.SelectedIndex < 0)
            CmbSoundPreview.SelectedIndex = 0;
        if (LblConfigPath != null) LblConfigPath.Text = Loc.Tf("settings.configPath", App.Services.Config.ConfigPath);
    }

    private void AboutContacts_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://t.me/nakidev",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void AboutSite_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/Naki-404/DisplayProfileManager",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void ApplyLabels()
    {
        Title = Loc.T("settings.title");
        if (BtnCaptionClose != null) BtnCaptionClose.ToolTip = Loc.T("btn.close");
        System.Windows.Automation.AutomationProperties.SetName(BtnCaptionClose, Loc.T("btn.close"));
        if (LblConfigPath != null) LblConfigPath.Text = Loc.Tf("settings.configPath", App.Services.Config.ConfigPath);
        ChkAutostart.ToolTip = Loc.T("global.autostart.tip");
        ChkStartMin.ToolTip = Loc.T("global.startmin.tip");
        ChkTrayClose.ToolTip = Loc.T("setup.trayClose.tip");
        BtnExport.ToolTip = Loc.T("settings.export.tip");
        BtnImport.ToolTip = Loc.T("settings.import.tip");
        TxtTitle.Text = Loc.T("settings.title");
        LblAppearance.Text = Loc.T("settings.appearance");
        LblBehavior.Text = Loc.T("settings.behavior");
        LblData.Text = Loc.T("settings.data");
        LblLang.Text = Loc.T("setup.language");
        LblTheme.Text = Loc.T("setup.theme");
        LblMonitor.Text = Loc.T("setup.monitor");
        ChkAutostart.Content = Loc.T("setup.autostart");
        ChkStartMin.Content = Loc.T("setup.startMin");
        ChkNotify.Content = Loc.T("setup.notify");
        ChkBackup.Content = Loc.T("setup.backup");
        ChkTrayClose.Content = Loc.T("setup.trayClose");
        ChkConfirmDelete.Content = Loc.T("settings.confirmDelete");
        ChkShowActive.Content = Loc.T("settings.showActive");
        if (LblOverlay != null) LblOverlay.Text = Loc.T("overlay.open");
        if (ChkOverlayAuto != null) ChkOverlayAuto.Content = Loc.T("overlay.auto");
        if (ChkOverlayHotkeyOnly != null) ChkOverlayHotkeyOnly.Content = Loc.T("overlay.hotkeyOnly");
        if (LblOverlayHint != null) LblOverlayHint.Text = Loc.T("overlay.auto.hint");
        if (LblSound != null) LblSound.Text = Loc.T("settings.sound");
        if (ChkUiSounds != null) ChkUiSounds.Content = Loc.T("settings.sound.enable");
        if (LblSoundVol != null) LblSoundVol.Text = Loc.T("settings.sound.volume");
        if (BtnPreviewSound != null) BtnPreviewSound.Content = Loc.T("settings.sound.preview.btn");
        if (ChkMuteInGame != null) ChkMuteInGame.Content = Loc.T("settings.sound.muteInGame");
        if (ItmSoundOpen != null) ItmSoundOpen.Content = Loc.T("sound.cat.open");
        if (ItmSoundClick != null) ItmSoundClick.Content = Loc.T("sound.cat.click");
        if (ItmSoundSave != null) ItmSoundSave.Content = Loc.T("sound.cat.save");
        if (ItmSoundLaunch != null) ItmSoundLaunch.Content = Loc.T("sound.cat.launch");
        if (ItmSoundDone != null) ItmSoundDone.Content = Loc.T("sound.cat.done");
        if (ItmSoundEmpty != null) ItmSoundEmpty.Content = Loc.T("sound.cat.empty");
        if (ItmSoundArmed != null) ItmSoundArmed.Content = Loc.T("sound.cat.armed");
        if (ItmSoundWarn != null) ItmSoundWarn.Content = Loc.T("sound.cat.warn");
        if (ItmSoundError != null) ItmSoundError.Content = Loc.T("sound.cat.error");
        if (ItmSoundPreset != null) ItmSoundPreset.Content = Loc.T("sound.cat.preset");
        BtnEditPalette.Content = Loc.T("setup.editPalette");
        BtnExport.Content = Loc.T("btn.export");
        BtnImport.Content = Loc.T("btn.import");
        BtnCancel.Content = Loc.T("btn.cancel");
        BtnSave.Content = Loc.T("btn.save");
        if (CmbTheme.Items.Count > 0 && CmbTheme.Items[0] is ComboBoxItem d) d.Content = Loc.T("setup.theme.dark");
        if (CmbTheme.Items.Count > 1 && CmbTheme.Items[1] is ComboBoxItem light) light.Content = Loc.T("setup.theme.light");
        if (CmbTheme.Items.Count > 2 && CmbTheme.Items[2] is ComboBoxItem c) c.Content = Loc.T("setup.theme.custom");

        // Refresh "Primary" label in monitor list without losing selection
        if (CmbMonitor.Items.Count > 0 && CmbMonitor.Items[0] is ComboBoxItem primary)
            primary.Content = Loc.T("setup.monitor.primary");

        if (LblAbout != null) LblAbout.Text = Loc.T("about.title");
        if (LblAboutVersion != null) LblAboutVersion.Text = Loc.Tf("about.version", AppVersion());
        if (LblAboutCreator != null) LblAboutCreator.Text = Loc.T("about.creator");
        if (LblAboutContacts != null) LblAboutContacts.Text = Loc.T("about.contacts");
        if (LblAboutSite != null) LblAboutSite.Text = Loc.T("about.site");
    }

    private static string AppVersion()
    {
        var asm = typeof(SettingsWindow).Assembly;
        var info = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
            asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
        var v = info?.InformationalVersion ?? asm.GetName().Version?.ToString(3) ?? "";
        int plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }

    private void Lang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (CmbLang.SelectedItem is ComboBoxItem lang)
        {
            // Loc.Changed relabels the main window while the dialog is open (deferred, once per change).
            Loc.SetLocale(lang.Tag?.ToString());
            ApplyLabels();
            if (System.Windows.Application.Current is App app)
                app.RebuildTrayMenu();
        }
    }

    private void UpdatePaletteButton()
    {
        BtnEditPalette.Visibility = CmbTheme.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SelectByTag(System.Windows.Controls.ComboBox cmb, string? tag)
    {
        for (int i = 0; i < cmb.Items.Count; i++)
        {
            if (cmb.Items[i] is ComboBoxItem it &&
                string.Equals(it.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                cmb.SelectedIndex = i;
                return;
            }
        }
        cmb.SelectedIndex = 0;
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdatePaletteButton();
        var theme = (CmbTheme.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "dark";
        if (theme == "custom")
        {
            _customPalette ??= ThemeService.DefaultCustom();
            ThemeService.ApplyPalette(_customPalette);
        }
        else
        {
            ThemeService.Apply(new UiPreferences { Theme = theme, Locale = Loc.Locale });
        }
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

    /// <summary>Starts from the saved preferences so fields this dialog doesn't show (zoom factor, overlay state…) survive.</summary>
    private UiPreferences BuildUi()
    {
        string? mon = (CmbMonitor.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        string theme = (CmbTheme.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "dark";
        var ui = (App.Services.Config.Current.Ui ?? new UiPreferences()).Clone();
        ui.Locale = (CmbLang.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "en";
        ui.Theme = theme;
        ui.SetupCompleted = true;
        ui.NotifyOnGameStart = ChkNotify.IsChecked == true;
        ui.BackupOnSave = ChkBackup.IsChecked == true;
        ui.MinimizeToTrayOnClose = ChkTrayClose.IsChecked == true;
        ui.ConfirmDelete = ChkConfirmDelete.IsChecked == true;
        ui.ShowActiveInHeader = ChkShowActive.IsChecked == true;
        ui.PreferredDisplayDevice = string.IsNullOrWhiteSpace(mon) ? null : mon;
        ui.OverlayAutoShowOnGame = ChkOverlayAuto.IsChecked == true;
        ui.OverlayHotkeyOnly = ChkOverlayHotkeyOnly?.IsChecked == true;
        ui.UiSoundsEnabled = ChkUiSounds.IsChecked == true;
        ui.UiSoundVolume = (int)Math.Round(SldSoundVol.Value);
        ui.MuteSoundsInGame = ChkMuteInGame?.IsChecked == true;
        if (theme == "custom")
        {
            ui.CustomPalette = (_customPalette ?? ThemeService.DefaultCustom()).Clone();
            ui.CustomAccent = ui.CustomPalette.Accent;
            ui.CustomBackground = ui.CustomPalette.Bg;
        }
        return ui;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var cfg = App.Services.Config.Current;
        cfg.Ui = BuildUi();
        cfg.StartWithWindows = ChkAutostart.IsChecked == true;
        cfg.StartMinimized = ChkStartMin.IsChecked == true;
        Loc.SetLocale(cfg.Ui.Locale);
        ThemeService.Apply(cfg.Ui);
        UiSound.ApplyFromConfig(cfg.Ui);
        App.Services.Config.Save(cfg);
        UiSound.Save();

        var exe = Environment.ProcessPath ?? "";
        if (!string.IsNullOrWhiteSpace(exe) && !AutostartService.SetEnabled(cfg.StartWithWindows, exe))
            ThemedDialog.Show(this, Loc.T("autostart.failed"), Loc.T("settings.behavior"));

        _committed = true;
        DialogResult = true;
        Close();
    }

    private void UiSound_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool on = ChkUiSounds.IsChecked == true;
        SldSoundVol.IsEnabled = on;
        BtnPreviewSound.IsEnabled = on;
        UiSound.ApplyFromConfig(new UiPreferences
        {
            UiSoundsEnabled = on,
            UiSoundVolume = (int)Math.Round(SldSoundVol.Value),
            MuteSoundsInGame = ChkMuteInGame?.IsChecked == true
        });
    }

    private void SoundVol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblSoundVolVal == null) return;
        LblSoundVolVal.Text = $"{(int)Math.Round(SldSoundVol.Value)}%";
        if (!IsLoaded) return;
        UiSound.ApplyFromConfig(new UiPreferences
        {
            UiSoundsEnabled = ChkUiSounds.IsChecked == true,
            UiSoundVolume = (int)Math.Round(SldSoundVol.Value),
            MuteSoundsInGame = ChkMuteInGame?.IsChecked == true
        });
    }

    private void MuteInGame_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UiSound.ApplyFromConfig(new UiPreferences
        {
            UiSoundsEnabled = ChkUiSounds.IsChecked == true,
            UiSoundVolume = (int)Math.Round(SldSoundVol.Value),
            MuteSoundsInGame = ChkMuteInGame?.IsChecked == true
        });
    }

    private void PreviewSound_Click(object sender, RoutedEventArgs e)
    {
        UiSound.ApplyFromConfig(new UiPreferences
        {
            UiSoundsEnabled = true,
            UiSoundVolume = (int)Math.Round(SldSoundVol.Value),
            MuteSoundsInGame = false
        });

        string tag = (CmbSoundPreview?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "open";
        switch (tag)
        {
            case "click": UiSound.Click(); break;
            case "save": UiSound.Save(); break;
            case "launch": UiSound.Launch(); break;
            case "done": UiSound.Done(); break;
            case "empty": UiSound.Empty(); break;
            case "armed": UiSound.Armed(); break;
            case "warn": UiSound.Warn(); break;
            case "error": UiSound.Error(); break;
            case "preset": UiSound.PresetApply(); break;
            default: UiSound.Open(); break;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Loc.T("filter.json"),
            FileName = "visor-config.json"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.Copy(App.Services.Config.ConfigPath, dlg.FileName, true);
            ThemedDialog.Show(this, Loc.T("toast.exported"), Loc.T("settings.data"));
        }
        catch (Exception ex)
        {
            ThemedDialog.Show(this, ex.Message, Loc.T("settings.data"));
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("filter.json") };
        if (dlg.ShowDialog() != true) return;
        if (!ThemedDialog.Confirm(this, Loc.T("confirm.importConfig"), Loc.T("confirm.importConfig.title"),
                Loc.T("btn.importReplace"), danger: true))
            return;
        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var imported = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (imported == null)
            {
                ThemedDialog.Show(this, Loc.T("settings.import.invalid"), Loc.T("settings.data"));
                return;
            }

            imported.Ui ??= App.Services.Config.Current.Ui ?? new UiPreferences();
            imported.Ui.SetupCompleted = true;
            imported.ConfigVersion = Math.Max(imported.ConfigVersion, ConfigService.CurrentVersion);
            App.Services.Config.Save(imported);
            Imported = true;
            Loc.SetLocale(App.Services.Config.Current.Ui!.Locale);
            ThemeService.Apply(App.Services.Config.Current.Ui!);
            LoadFromConfig();
            ApplyLabels();
            ThemedDialog.Show(this, Loc.T("toast.imported"), Loc.T("settings.data"));
        }
        catch (Exception ex)
        {
            ThemedDialog.Show(this, ex.Message, Loc.T("settings.data"));
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // Closing handler reverts the preview.
        DialogResult = false;
        Close();
    }
}
