using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using DisplayProfileManager.Models;
using DisplayProfileManager.Services;

namespace DisplayProfileManager;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20 = 19;

    private readonly ObservableCollection<GameProfile> _profiles = new();
    private readonly ObservableCollection<QuickPreset> _presets = new();
    private GameProfile? _selected;
    private QuickPreset? _selectedPreset;
    private bool _suppressEditorEvents;
    private bool _dirty;

    public MainWindow()
    {
        _suppressEditorEvents = true;
        InitializeComponent();
        ProfileList.ItemsSource = _profiles;
        PresetGameList.ItemsSource = _profiles;
        PresetList.ItemsSource = _presets;
        FooterText.Text = "Config: " + App.Services.Config.ConfigPath;
        LoadResolutions();
        FillDisplays();
        ReloadFromConfig();
        ApplyLocalization();
        UpdateActiveHeader(App.Services.Monitor.CurrentProfile);
        _suppressEditorEvents = false;

        Loaded += (_, _) => MaybeFirstScan();
        Loc.Changed += () => Dispatcher.Invoke(ApplyLocalization);

        MainTabs.SelectionChanged += MainTabs_SelectionChanged;
        MainTabs.PreviewMouseLeftButtonDown += MainTabs_ProfilesHeaderDown;
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs) return;
        if (MainTabs.SelectedContent is FrameworkElement fe)
            UiMotion.SoftContentIn(fe);
    }

    private void MainTabs_ProfilesHeaderDown(object sender, MouseButtonEventArgs e)
    {
        // Count only clicks on the Profiles tab header (TabPanel), never content area
        if (e.OriginalSource is not DependencyObject d) return;
        if (FindAncestor<TabPanel>(d) == null) return;
        var tab = FindAncestor<TabItem>(d);
        if (tab == TabProfiles || string.Equals(tab?.Header?.ToString(), Loc.T("tab.profiles"), StringComparison.OrdinalIgnoreCase)
            || string.Equals(tab?.Header?.ToString(), "Profiles", StringComparison.OrdinalIgnoreCase))
            EggBusy?.NotifyProfilesClick();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
                return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    public void ShowWithFade()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        UiMotion.PopIn(ChromeRoot);
        UiMotion.FadeSlideIn(ContentRoot, fromY: 8, ms: 200);
    }

    public void HideWithFade()
    {
        UiMotion.PopOut(ChromeRoot, () =>
        {
            Hide();
            ChromeRoot.BeginAnimation(UIElement.OpacityProperty, null);
            ChromeRoot.Opacity = 1;
            if (ChromeRoot.RenderTransform is System.Windows.Media.ScaleTransform st)
            {
                st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
                st.ScaleX = st.ScaleY = 1;
            }
        });
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            bool light = string.Equals(App.Services.Config.Current.Ui?.Theme, "light", StringComparison.OrdinalIgnoreCase);
            int useDark = light ? 0 : 1;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20, ref useDark, sizeof(int));
        }
        catch { /* older Windows */ }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Maximize_Click(sender, e);
            return;
        }
        try { DragMove(); } catch { }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        UiMotion.FadeTo(ChromeRoot, 0, () =>
        {
            WindowState = WindowState.Minimized;
            ChromeRoot.BeginAnimation(UIElement.OpacityProperty, null);
            ChromeRoot.Opacity = 1;
        }, ms: 120);
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow { Owner = this };
        win.ShowDialog();
        ApplyLocalization();
        UpdateActiveHeader(App.Services.Monitor.CurrentProfile);
        if (win.Imported)
            ReloadFromConfig();
        if (System.Windows.Application.Current is App app)
            app.RebuildTrayMenu();
    }

    public void ApplyLocalization()
    {
        TitleAppName.Text = Loc.T("app.name");
        Title = Loc.T("app.name");
        BtnApply.Content = Loc.T("btn.apply");
        BtnSave.Content = Loc.T("btn.save");
        BtnReset.Content = Loc.T("btn.reset");
        BtnScan.Content = Loc.T("btn.scan");
        BtnAddManual.Content = Loc.T("btn.addManual");
        BtnRestore.Content = Loc.T("btn.restore");
        BtnDup.Content = Loc.T("btn.dup");
        BtnDelete.Content = Loc.T("btn.delete");
        TabProfiles.Header = Loc.T("tab.profiles");
        TabPresets.Header = Loc.T("tab.presets");
        TabGlobal.Header = Loc.T("tab.global");
        TabLog.Header = Loc.T("tab.log");
        TxtSettingsCaption.Text = Loc.T("btn.settings");
        BtnSettingsCaption.ToolTip = Loc.T("btn.settings");
        TxtSearch.ToolTip = Loc.T("search.placeholder");
        LblDisplay.Text = Loc.T("display");
        UpdateActiveHeader(App.Services.Monitor.CurrentProfile);
    }

    public void UpdateActiveHeader(GameProfile? profile)
    {
        bool show = App.Services.Config.Current.Ui?.ShowActiveInHeader != false;
        ActivePill.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (profile == null)
            ActiveHeaderText.Text = Loc.T("active.none");
        else
        {
            var bits = new List<string> { Loc.T("active.prefix") + " " + profile.Name };
            if (profile.ApplyResolution && !string.IsNullOrWhiteSpace(profile.Resolution))
                bits.Add(profile.Resolution!);
            ActiveHeaderText.Text = string.Join(" · ", bits);
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        var q = TxtSearch.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(q))
        {
            ProfileList.ItemsSource = _profiles;
            return;
        }
        var filtered = _profiles.Where(p =>
            (p.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (p.ProcessName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        ProfileList.ItemsSource = filtered;
    }

    private void FillDisplays()
    {
        CmbDisplay.Items.Clear();
        CmbDisplay.Items.Add(new ComboBoxItem { Content = Loc.T("setup.monitor.primary") + " / app default", Tag = "" });
        foreach (var d in DisplayEngine.GetDisplays())
        {
            var label = d.Primary ? $"{d.Friendly} ★" : d.Friendly;
            CmbDisplay.Items.Add(new ComboBoxItem { Content = $"{label}", Tag = d.DeviceName });
        }
        CmbDisplay.SelectedIndex = 0;
    }

    public void SetStatus(string status)
    {
        StatusText.Text = status;
        UiMotion.PulseOpacity(StatusText);
    }

    public void ShowToast(string message)
    {
        UiMotion.ShowToast(ToastHost, ToastText, message);
        SetStatus(message);
    }

    public void ReloadFromConfig()
    {
        _suppressEditorEvents = true;
        var cfg = App.Services.Config.Current;

        _profiles.Clear();
        foreach (var p in cfg.Profiles)
            _profiles.Add(CloneProfile(p));
        GameVisuals.ApplyAll(_profiles);

        _presets.Clear();

        CmbDefaultRes.Items.Clear();
        CmbPresetRes.Items.Clear();
        foreach (var r in DisplayEngine.GetAvailableResolutions())
        {
            CmbDefaultRes.Items.Add(r);
            CmbPresetRes.Items.Add(r);
        }
        CmbDefaultRes.SelectedItem = cfg.Defaults.Resolution ?? DisplayEngine.GetCurrentResolution();
        SelectComboByContent(CmbDefaultPower, cfg.Defaults.PowerPlan ?? "balanced");

        SldDefBrightness.Value = cfg.Defaults.Color.Brightness;
        SldDefContrast.Value = cfg.Defaults.Color.Contrast;
        SldDefGamma.Value = cfg.Defaults.Color.Gamma;
        UpdateDefLabels();

        HkBrightUp.Text = cfg.GlobalHotkeys.BrightnessUp ?? "";
        HkBrightDown.Text = cfg.GlobalHotkeys.BrightnessDown ?? "";
        HkContrastUp.Text = cfg.GlobalHotkeys.ContrastUp ?? "";
        HkContrastDown.Text = cfg.GlobalHotkeys.ContrastDown ?? "";
        HkGammaUp.Text = cfg.GlobalHotkeys.GammaUp ?? "";
        HkGammaDown.Text = cfg.GlobalHotkeys.GammaDown ?? "";
        HkReset.Text = cfg.GlobalHotkeys.ResetColor ?? "";

        ChkAutostart.IsChecked = cfg.StartWithWindows;
        ChkStartMin.IsChecked = cfg.StartMinimized;

        if (_profiles.Count > 0)
        {
            ProfileList.SelectedIndex = 0;
            PresetGameList.SelectedIndex = 0;
        }
        else
            ClearEditor();

        _suppressEditorEvents = false;
        _dirty = false;
        RefreshLog_Click(this, new RoutedEventArgs());
    }

    private void MaybeFirstScan()
    {
        var cfg = App.Services.Config.Current;
        if (cfg.FirstScanDone) return;
        cfg.FirstScanDone = true;
        App.Services.Config.Save(cfg);

        if (_profiles.Count == 0)
            _ = RunGameScanAsync(confirmIfAny: true);
    }

    private void LoadResolutions()
    {
        CmbResolution.Items.Clear();
        CmbResolution.Items.Add("(don't change)");
        foreach (var r in DisplayEngine.GetAvailableResolutions())
            CmbResolution.Items.Add(r);
    }

    private async void ScanGames_Click(object sender, RoutedEventArgs e)
        => await RunGameScanAsync(confirmIfAny: false);

    private async Task RunGameScanAsync(bool confirmIfAny)
    {
        ScanBusy.ShowAnimated();
        ScanBusy.SetStatus("Looking through disks…");

        var minDelay = Task.Delay(1600);
        List<GameScanner.FoundGame> found;
        try
        {
            found = await Task.Run(() =>
                GameScanner.ScanAll(msg => Dispatcher.Invoke(() => ScanBusy.SetStatus(msg))));
        }
        catch (Exception ex)
        {
            ScanBusy.HideAnimated();
            ThemedDialog.Show(this, "Scan failed:\n" + ex.Message, "Scan");
            return;
        }

        await minDelay;
        ScanBusy.HideAnimated();

        if (found.Count == 0)
        {
            ThemedDialog.Show(this,
                "No known games found on this PC.\nUse Add manually or Running… to pick any .exe.",
                "Scan");
            return;
        }

        if (confirmIfAny)
        {
            var names = string.Join(", ", found.Select(f => f.Name).Distinct());
            if (!ThemedDialog.Show(this,
                    $"Found games:\n{names}\n\nAdd them as profiles?",
                    "Scan games",
                    confirm: true))
                return;
        }

        var added = AddFoundGames(found);
        ShowToast(added == 0
            ? "All detected games already listed"
            : $"Added {added} game(s)");
    }

    private int AddFoundGames(List<GameScanner.FoundGame> found)
    {
        int added = 0;
        foreach (var g in found)
        {
            var existing = _profiles.FirstOrDefault(p =>
                string.Equals(p.ProcessName, g.Process, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (string.IsNullOrWhiteSpace(existing.ExePath) && !string.IsNullOrWhiteSpace(g.ExePath))
                {
                    existing.ExePath = g.ExePath;
                    GameVisuals.Apply(existing);
                    _dirty = true;
                }
                continue;
            }

            var profile = GameCatalog.CreateProfileSkeleton(g.Process, g.Name, g.ExePath);
            GameVisuals.Apply(profile);
            _profiles.Add(profile);
            added++;
        }
        if (added > 0)
        {
            _dirty = true;
            ProfileList.SelectedItem = _profiles.Last();
            ProfileList.Items.Refresh();
            PresetGameList.Items.Refresh();
        }
        return added;
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_dirty && _selected != null) PushEditorToSelected();
        _selected = ProfileList.SelectedItem as GameProfile;
        LoadEditor(_selected);
    }

    private void LoadEditor(GameProfile? p)
    {
        _suppressEditorEvents = true;
        if (p == null) { ClearEditor(); _suppressEditorEvents = false; return; }

        ChkEnabled.IsChecked = p.Enabled;
        TxtName.Text = p.Name;
        TxtProcess.Text = p.ProcessName;
        ChkApplyRes.IsChecked = p.ApplyResolution;
        ChkApplyPower.IsChecked = p.ApplyPowerPlan;
        ChkApplyColor.IsChecked = p.ApplyColor;

        if (string.IsNullOrWhiteSpace(p.Resolution)) CmbResolution.SelectedIndex = 0;
        else
        {
            if (!CmbResolution.Items.Contains(p.Resolution)) CmbResolution.Items.Add(p.Resolution);
            CmbResolution.SelectedItem = p.Resolution;
        }

        SelectComboByTag(CmbPower, p.PowerPlan ?? "");
        SelectComboByTag(CmbDisplay, p.DisplayDevice ?? "");
        SldBrightness.Value = p.Color.Brightness;
        SldContrast.Value = p.Color.Contrast;
        SldGamma.Value = p.Color.Gamma;
        UpdateColorLabels();
        CompanionList.ItemsSource = null;
        CompanionList.ItemsSource = p.Companions;
        _suppressEditorEvents = false;
        _dirty = false;
    }

    private void ClearEditor()
    {
        TxtName.Text = "";
        TxtProcess.Text = "";
        CompanionList.ItemsSource = null;
    }

    private void EditorChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEditorEvents || _selected == null) return;
        _dirty = true;
        PushEditorToSelected();
        ProfileList.Items.Refresh();
    }

    private void ColorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEditorEvents) return;
        UpdateColorLabels();
        if (_selected == null) return;
        _dirty = true;
        PushEditorToSelected();
    }

    private void DefColor_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEditorEvents) return;
        UpdateDefLabels();
    }

    private void UpdateColorLabels()
    {
        if (LblBrightness == null) return;
        LblBrightness.Text = $"Brightness: {SldBrightness.Value:F2}";
        LblContrast.Text = $"Contrast: {SldContrast.Value:F2}";
        LblGamma.Text = $"Gamma: {SldGamma.Value:F2}";
    }

    private void UpdateDefLabels()
    {
        if (LblDefBrightness == null) return;
        LblDefBrightness.Text = $"Brightness: {SldDefBrightness.Value:F2}";
        LblDefContrast.Text = $"Contrast: {SldDefContrast.Value:F2}";
        LblDefGamma.Text = $"Gamma: {SldDefGamma.Value:F2}";
    }

    private void PushEditorToSelected()
    {
        if (_selected == null) return;
        _selected.Enabled = ChkEnabled.IsChecked == true;
        _selected.Name = TxtName.Text.Trim();
        _selected.ProcessName = TxtProcess.Text.Trim();
        _selected.ApplyResolution = ChkApplyRes.IsChecked == true;
        _selected.ApplyPowerPlan = ChkApplyPower.IsChecked == true;
        _selected.ApplyColor = ChkApplyColor.IsChecked == true;

        var res = CmbResolution.SelectedItem?.ToString();
        _selected.Resolution = res == "(don't change)" || string.IsNullOrWhiteSpace(res) ? null : res;

        if (CmbPower.SelectedItem is ComboBoxItem pi)
            _selected.PowerPlan = string.IsNullOrWhiteSpace(pi.Tag?.ToString()) ? null : pi.Tag!.ToString();

        if (CmbDisplay.SelectedItem is ComboBoxItem di)
            _selected.DisplayDevice = string.IsNullOrWhiteSpace(di.Tag?.ToString()) ? null : di.Tag!.ToString();

        _selected.Color.Brightness = SldBrightness.Value;
        _selected.Color.Contrast = SldContrast.Value;
        _selected.Color.Gamma = SldGamma.Value;
        _selected.Color.Clamp();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_selected != null) PushEditorToSelected();
        if (_selectedPreset != null) PushPresetEditor();
        SyncPresetsBackToGame();

        var cfg = App.Services.Config.Current;
        cfg.Profiles = _profiles.Select(CloneProfile).ToList();
        cfg.Presets = null;
        cfg.Defaults.Resolution = CmbDefaultRes.SelectedItem?.ToString();
        cfg.Defaults.PowerPlan = (CmbDefaultPower.SelectedItem as ComboBoxItem)?.Content?.ToString()
                                 ?? CmbDefaultPower.SelectedItem?.ToString()
                                 ?? "balanced";
        cfg.Defaults.Color.Brightness = SldDefBrightness.Value;
        cfg.Defaults.Color.Contrast = SldDefContrast.Value;
        cfg.Defaults.Color.Gamma = SldDefGamma.Value;
        cfg.Defaults.Color.Clamp();

        // Keep factory defaults as the true baseline for Reset settings
        if (cfg.FactoryDefaults == null || string.IsNullOrWhiteSpace(cfg.FactoryDefaults.Resolution))
            cfg.FactoryDefaults = ConfigService.CaptureFactoryDefaults();
        cfg.FactoryDefaults.Color = ColorSettings.Neutral;

        cfg.GlobalHotkeys.BrightnessUp = HkBrightUp.Text.Trim();
        cfg.GlobalHotkeys.BrightnessDown = HkBrightDown.Text.Trim();
        cfg.GlobalHotkeys.ContrastUp = HkContrastUp.Text.Trim();
        cfg.GlobalHotkeys.ContrastDown = HkContrastDown.Text.Trim();
        cfg.GlobalHotkeys.GammaUp = HkGammaUp.Text.Trim();
        cfg.GlobalHotkeys.GammaDown = HkGammaDown.Text.Trim();
        cfg.GlobalHotkeys.ResetColor = HkReset.Text.Trim();

        cfg.StartWithWindows = ChkAutostart.IsChecked == true;
        cfg.StartMinimized = ChkStartMin.IsChecked == true;
        cfg.ConfigVersion = ConfigService.CurrentVersion;

        App.Services.Config.Save(cfg);
        App.Services.Hotkeys.RegisterFromConfig(cfg, App.Services.Monitor.CurrentProfile);

        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(exe))
            AutostartService.SetEnabled(cfg.StartWithWindows, exe!);

        _dirty = false;
        AppLog.Info("Configuration saved from UI.");
        SaveBusy.ShowSaved();
        SetStatus("Saved");
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_selected != null) PushEditorToSelected();
        if (_selectedPreset != null) PushPresetEditor();
        Save_Click_Silent();

        if (MainTabs.SelectedIndex == 1 && _selectedPreset != null)
        {
            App.Services.Monitor.ApplyPreset(_selectedPreset);
            ShowToast($"Applied: {_selectedPreset.Name}");
            return;
        }

        if (_selected != null)
        {
            App.Services.Display.ApplyProfile(_selected, App.Services.Config.Current.Defaults);
            ShowToast($"Applied: {_selected.Name}");
            return;
        }

        App.Services.Display.RestoreDefaults(App.Services.Config.Current.Defaults);
        ShowToast("Applied defaults");
    }

    private void Save_Click_Silent()
    {
        if (_selected != null) PushEditorToSelected();
        if (_selectedPreset != null) PushPresetEditor();

        var cfg = App.Services.Config.Current;
        cfg.Profiles = _profiles.Select(CloneProfile).ToList();
        cfg.Presets = null;
        cfg.Defaults.Resolution = CmbDefaultRes.SelectedItem?.ToString();
        cfg.Defaults.PowerPlan = (CmbDefaultPower.SelectedItem as ComboBoxItem)?.Content?.ToString()
                                 ?? CmbDefaultPower.SelectedItem?.ToString()
                                 ?? "balanced";
        cfg.Defaults.Color.Brightness = SldDefBrightness.Value;
        cfg.Defaults.Color.Contrast = SldDefContrast.Value;
        cfg.Defaults.Color.Gamma = SldDefGamma.Value;
        cfg.Defaults.Color.Clamp();

        if (cfg.FactoryDefaults == null || string.IsNullOrWhiteSpace(cfg.FactoryDefaults.Resolution))
            cfg.FactoryDefaults = ConfigService.CaptureFactoryDefaults();
        cfg.FactoryDefaults.Color = ColorSettings.Neutral;

        cfg.GlobalHotkeys.BrightnessUp = HkBrightUp.Text.Trim();
        cfg.GlobalHotkeys.BrightnessDown = HkBrightDown.Text.Trim();
        cfg.GlobalHotkeys.ContrastUp = HkContrastUp.Text.Trim();
        cfg.GlobalHotkeys.ContrastDown = HkContrastDown.Text.Trim();
        cfg.GlobalHotkeys.GammaUp = HkGammaUp.Text.Trim();
        cfg.GlobalHotkeys.GammaDown = HkGammaDown.Text.Trim();
        cfg.GlobalHotkeys.ResetColor = HkReset.Text.Trim();

        cfg.StartWithWindows = ChkAutostart.IsChecked == true;
        cfg.StartMinimized = ChkStartMin.IsChecked == true;
        cfg.ConfigVersion = ConfigService.CurrentVersion;

        App.Services.Config.Save(cfg);
        App.Services.Hotkeys.RegisterFromConfig(cfg, App.Services.Monitor.CurrentProfile);
        _dirty = false;
    }

    private void RestoreCoreGames_Click(object sender, RoutedEventArgs e)
    {
        int added = 0;
        foreach (var core in GameCatalog.CreateCoreProfiles())
        {
            if (_profiles.Any(p => string.Equals(p.ProcessName, core.ProcessName, StringComparison.OrdinalIgnoreCase)))
                continue;
            _profiles.Add(core);
            GameVisuals.Apply(core);
            added++;
        }
        if (added > 0)
        {
            _dirty = true;
            ProfileList.SelectedItem = _profiles.Last();
            ProfileList.Items.Refresh();
            PresetGameList.Items.Refresh();
        }
        ThemedDialog.Show(this, added == 0
            ? "Valorant / PUBG / War Thunder / Tarkov already in the list."
            : $"Restored {added} game profile(s).", "Restore my games");
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var cfg = App.Services.Config.Current;
        var factory = cfg.FactoryDefaults ?? ConfigService.CaptureFactoryDefaults();
        factory.Color = ColorSettings.Neutral;
        cfg.FactoryDefaults = factory.Clone();
        cfg.Defaults = factory.Clone();
        App.Services.Config.Save(cfg);

        _suppressEditorEvents = true;
        CmbDefaultRes.SelectedItem = factory.Resolution;
        SelectComboByContent(CmbDefaultPower, factory.PowerPlan ?? "balanced");
        SldDefBrightness.Value = 0.5;
        SldDefContrast.Value = 1.0;
        SldDefGamma.Value = 1.0;
        UpdateDefLabels();
        _suppressEditorEvents = false;

        App.Services.Monitor.ResetDisplayNow();
        ShowToast("Factory settings restored");
        ThemedDialog.Show(this,
            "Restored initial settings:\n• Resolution: " + factory.Resolution + "\n• Color: neutral\n• Power: " + factory.PowerPlan,
            "Reset settings");
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var p = new GameProfile
        {
            Name = "New Profile",
            ProcessName = "",
            ApplyResolution = false,
            ApplyColor = false,
            ApplyPowerPlan = true,
            Color = ColorSettings.Neutral
        };
        _profiles.Add(p);
        GameVisuals.Apply(p);
        ProfileList.SelectedItem = p;
        _dirty = true;
    }

    private void DupProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var copy = CloneProfile(_selected);
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name += " Copy";
        _profiles.Add(copy);
        GameVisuals.Apply(copy);
        ProfileList.SelectedItem = copy;
        _dirty = true;
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        if (App.Services.Config.Current.Ui?.ConfirmDelete != false)
        {
            if (!ThemedDialog.Show(this, Loc.T("confirm.delete") + $"\n{_selected.Name}", Loc.T("btn.delete"), confirm: true))
                return;
        }
        _profiles.Remove(_selected);
        _selected = null;
        _dirty = true;
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            Title = "Select game executable"
        };
        if (dlg.ShowDialog() == true)
        {
            TxtProcess.Text = System.IO.Path.GetFileName(dlg.FileName);
            if (string.IsNullOrWhiteSpace(TxtName.Text) || TxtName.Text == "New Profile")
                TxtName.Text = GameCatalog.GetFriendlyName(TxtProcess.Text)
                               ?? System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
            if (_selected != null)
            {
                _selected.ExePath = dlg.FileName;
                PushEditorToSelected();
                GameVisuals.Apply(_selected);
                ProfileList.Items.Refresh();
                PresetGameList.Items.Refresh();
            }
            _dirty = true;
        }
    }

    private void PickRunning_Click(object sender, RoutedEventArgs e)
    {
        var procs = ProcessWatcher.GetRunningExeNames().ToList();
        var pick = new ProcessPickerWindow(procs) { Owner = this };
        if (pick.ShowDialog() == true && !string.IsNullOrWhiteSpace(pick.SelectedProcess))
        {
            TxtProcess.Text = pick.SelectedProcess;
            if (string.IsNullOrWhiteSpace(TxtName.Text) || TxtName.Text == "New Profile")
                TxtName.Text = GameCatalog.GetFriendlyName(pick.SelectedProcess)
                               ?? System.IO.Path.GetFileNameWithoutExtension(pick.SelectedProcess);
            _dirty = true;
            PushEditorToSelected();
        }
    }

    private void AddCompanion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var c = new CompanionApp();
        var dlg = new CompanionEditWindow(c) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _selected.Companions.Add(c);
            CompanionList.ItemsSource = null;
            CompanionList.ItemsSource = _selected.Companions;
            _dirty = true;
        }
    }

    private void EditCompanion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        if (CompanionList.SelectedItem is not CompanionApp c) return;
        var dlg = new CompanionEditWindow(c) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            CompanionList.ItemsSource = null;
            CompanionList.ItemsSource = _selected.Companions;
            _dirty = true;
        }
    }

    private void RemoveCompanion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        if (CompanionList.SelectedItem is not CompanionApp c) return;
        _selected.Companions.Remove(c);
        CompanionList.ItemsSource = null;
        CompanionList.ItemsSource = _selected.Companions;
        _dirty = true;
    }

    // -- Presets (per-game) ---------------------------------------------------

    private GameProfile? _presetGame;

    private void PresetGameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedPreset != null) PushPresetEditor();
        SyncPresetsBackToGame();

        _presetGame = PresetGameList.SelectedItem as GameProfile;
        _presets.Clear();
        _selectedPreset = null;

        if (_presetGame != null)
        {
            _presetGame.Presets ??= new List<QuickPreset>();
            foreach (var p in _presetGame.Presets)
                _presets.Add(p);
            LblPresetGame.Text = $"Presets — {_presetGame.Name}";
            if (_presets.Count > 0) PresetList.SelectedIndex = 0;
            else LoadPresetEditor(null);
        }
        else
        {
            LblPresetGame.Text = "Presets";
            LoadPresetEditor(null);
        }
    }

    private void SyncPresetsBackToGame()
    {
        if (_presetGame == null) return;
        _presetGame.Presets = _presets.Select(ClonePreset).ToList();
    }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedPreset != null) PushPresetEditor();
        _selectedPreset = PresetList.SelectedItem as QuickPreset;
        LoadPresetEditor(_selectedPreset);
    }

    private void LoadPresetEditor(QuickPreset? p)
    {
        _suppressEditorEvents = true;
        if (p == null)
        {
            TxtPresetName.Text = "";
            TxtPresetHotkey.Text = "";
            _suppressEditorEvents = false;
            return;
        }
        TxtPresetName.Text = p.Name;
        TxtPresetHotkey.Text = p.Hotkey ?? "";
        ChkPresetRes.IsChecked = p.ApplyResolution;
        ChkPresetColor.IsChecked = p.ApplyColor;
        if (!string.IsNullOrWhiteSpace(p.Resolution) && CmbPresetRes.Items.Contains(p.Resolution))
            CmbPresetRes.SelectedItem = p.Resolution;
        else if (CmbPresetRes.Items.Count > 0)
            CmbPresetRes.SelectedIndex = 0;
        SldPresetB.Value = p.Color.Brightness;
        SldPresetC.Value = p.Color.Contrast;
        SldPresetG.Value = p.Color.Gamma;
        UpdatePresetLabels();
        _suppressEditorEvents = false;
    }

    private void PresetEditorChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEditorEvents || _selectedPreset == null) return;
        PushPresetEditor();
        PresetList.Items.Refresh();
    }

    private void PresetColor_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEditorEvents) return;
        UpdatePresetLabels();
        if (_selectedPreset == null) return;
        PushPresetEditor();
        PresetList.Items.Refresh();
    }

    private void UpdatePresetLabels()
    {
        if (LblPresetB == null) return;
        LblPresetB.Text = $"Brightness: {SldPresetB.Value:F2}";
        LblPresetC.Text = $"Contrast: {SldPresetC.Value:F2}";
        LblPresetG.Text = $"Gamma: {SldPresetG.Value:F2}";
    }

    private void PushPresetEditor()
    {
        if (_selectedPreset == null) return;
        _selectedPreset.Name = TxtPresetName.Text.Trim();
        _selectedPreset.Hotkey = string.IsNullOrWhiteSpace(TxtPresetHotkey.Text) ? null : TxtPresetHotkey.Text.Trim();
        _selectedPreset.ApplyResolution = ChkPresetRes.IsChecked == true;
        _selectedPreset.ApplyColor = ChkPresetColor.IsChecked == true;
        _selectedPreset.Resolution = CmbPresetRes.SelectedItem?.ToString();
        _selectedPreset.Color.Brightness = SldPresetB.Value;
        _selectedPreset.Color.Contrast = SldPresetC.Value;
        _selectedPreset.Color.Gamma = SldPresetG.Value;
        _selectedPreset.Color.Clamp();
        SyncPresetsBackToGame();
    }

    private void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_presetGame == null)
        {
            ThemedDialog.Show(this, "Select a game first.", "Presets");
            return;
        }
        var p = new QuickPreset
        {
            Id = _presetGame.Id + "_" + Guid.NewGuid().ToString("N")[..8],
            Name = "New preset",
            ApplyColor = true,
            Color = ColorSettings.Neutral,
            Hotkey = ""
        };
        _presets.Add(p);
        SyncPresetsBackToGame();
        PresetList.SelectedItem = p;
        _dirty = true;
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPreset == null) return;
        _presets.Remove(_selectedPreset);
        _selectedPreset = null;
        SyncPresetsBackToGame();
        _dirty = true;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        if (key == Key.Escape) { box.Text = ""; return; }
        box.Text = HotkeyService.GestureFromKeys(Keyboard.Modifiers, key);
        if (box == TxtPresetHotkey && _selectedPreset != null)
            PushPresetEditor();
    }

    private void RefreshLog_Click(object sender, RoutedEventArgs e)
    {
        TxtLog.Text = string.Join(Environment.NewLine, AppLog.Tail(300));
        TxtLog.ScrollToEnd();
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = App.Services.Config.ConfigDirectory;
        if (!Directory.Exists(dir)) return;
        // explorer.exe + ArgumentList — avoid UseShellExecute on a path string
        var psi = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(dir);
        Process.Start(psi);
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (App.Current is App app && !app.IsExitRequested)
        {
            if (App.Services.Config.Current.Ui?.MinimizeToTrayOnClose == false)
            {
                app.ExitApp();
                return;
            }
            e.Cancel = true;
            HideWithFade();
        }
    }

    private static GameProfile CloneProfile(GameProfile p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Enabled = p.Enabled,
        ProcessName = p.ProcessName,
        ExePath = p.ExePath,
        Resolution = p.Resolution,
        RefreshRate = p.RefreshRate,
        DisplayDevice = p.DisplayDevice,
        PowerPlan = p.PowerPlan,
        ApplyColor = p.ApplyColor,
        ApplyResolution = p.ApplyResolution,
        ApplyPowerPlan = p.ApplyPowerPlan,
        Color = p.Color.Clone(),
        Companions = p.Companions.Select(c => new CompanionApp
        {
            Path = c.Path,
            LaunchMode = c.LaunchMode,
            TaskName = c.TaskName,
            OnStop = c.OnStop,
            StopHotkey = c.StopHotkey,
            DismissDialogs = c.DismissDialogs,
            MinimizeToTray = c.MinimizeToTray
        }).ToList(),
        Presets = (p.Presets ?? new List<QuickPreset>()).Select(ClonePreset).ToList()
    };

    private static QuickPreset ClonePreset(QuickPreset p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Hotkey = p.Hotkey,
        ApplyResolution = p.ApplyResolution,
        Resolution = p.Resolution,
        ApplyColor = p.ApplyColor,
        Color = p.Color.Clone()
    };

    private static void SelectComboByContent(System.Windows.Controls.ComboBox cmb, string value)
    {
        foreach (var item in cmb.Items)
        {
            var text = (item as ComboBoxItem)?.Content?.ToString() ?? item?.ToString();
            if (string.Equals(text, value, StringComparison.OrdinalIgnoreCase))
            {
                cmb.SelectedItem = item;
                return;
            }
        }
    }

    private static void SelectComboByTag(System.Windows.Controls.ComboBox cmb, string tag)
    {
        foreach (var item in cmb.Items)
        {
            if (item is ComboBoxItem ci && string.Equals(ci.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                cmb.SelectedItem = item;
                return;
            }
        }
        if (cmb.Items.Count > 0) cmb.SelectedIndex = 0;
    }
}


