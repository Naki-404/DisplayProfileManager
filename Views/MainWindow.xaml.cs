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
using System.Windows.Threading;
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
    private readonly DispatcherTimer _autoSaveTimer;
    private GameProfile? _selected;
    private QuickPreset? _selectedPreset;
    private bool _suppressEditorEvents;
    private bool _dirty;
    private bool _ignoreTabChange;
    private int _lastTabIndex;
    private ColorBackend _presetEditorBackend = ColorBackend.LowLevel;

    private GameProfile? _playProfile;
    private QuickPreset? _playPreset;
    private ColorBackend _playBackend = ColorBackend.LowLevel;
    private bool _suppressPlayEvents;
    private bool _suppressGameListSync;

    public MainWindow()
    {
        _suppressEditorEvents = true;
        InitializeComponent();
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            if (!_dirty) return;
            // Always push editors first — otherwise dual-slot / slider state can save as 0/0/0/0.
            FlushAutosave();
            SetStatus("Autosaved");
        };
        ProfileList.ItemsSource = _profiles;
        PlayGameList.ItemsSource = _profiles;
        PresetGameList.ItemsSource = _profiles;
        PresetList.ItemsSource = _presets;
        FooterText.Text = "Config: " + App.Services.Config.ConfigPath;
        LoadResolutions();
        FillDisplays();
        FillAudioDevices();
        ReloadFromConfig();
        ApplyLocalization();
        UpdateActiveHeader(App.Services.Monitor.CurrentProfile);
        _suppressEditorEvents = false;
        RefreshPlayTab();

        Loaded += (_, _) => MaybeFirstScan();
        Loc.Changed += () => Dispatcher.Invoke(ApplyLocalization);

        MainTabs.SelectionChanged += MainTabs_SelectionChanged;
        MainTabs.PreviewMouseLeftButtonDown += MainTabs_ProfilesHeaderDown;
        _lastTabIndex = MainTabs.SelectedIndex;

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        };
    }

    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointApi
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectApi
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointApi Reserved;
        public PointApi MaxSize;
        public PointApi MaxPosition;
        public PointApi MinTrackSize;
        public PointApi MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int CbSize;
        public RectApi RcMonitor;
        public RectApi RcWork;
        public int DwFlags;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            ClampMaximizedToWorkArea(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void ClampMaximizedToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                RectApi work = info.RcWork;
                RectApi monitorRect = info.RcMonitor;
                mmi.MaxPosition.X = Math.Abs(work.Left - monitorRect.Left);
                mmi.MaxPosition.Y = Math.Abs(work.Top - monitorRect.Top);
                mmi.MaxSize.X = Math.Abs(work.Right - work.Left);
                mmi.MaxSize.Y = Math.Abs(work.Bottom - work.Top);
            }
        }
        Marshal.StructureToPtr(mmi, lParam, fDeleteOld: true);
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs) return;
        if (_ignoreTabChange)
        {
            if (MainTabs.SelectedContent is FrameworkElement fe0)
                UiMotion.SoftContentIn(fe0);
            return;
        }

        int newIndex = MainTabs.SelectedIndex;
        if (newIndex == _lastTabIndex)
        {
            if (MainTabs.SelectedContent is FrameworkElement feSame)
                UiMotion.SoftContentIn(feSame);
            return;
        }

        if (_dirty)
            FlushAutosave();

        _lastTabIndex = MainTabs.SelectedIndex;
        if (ReferenceEquals(MainTabs.SelectedItem, TabPresets))
            FocusPresetsForActiveOrSelectedGame();
        if (ReferenceEquals(MainTabs.SelectedItem, TabPlay))
            RefreshPlayTab();
        if (ReferenceEquals(MainTabs.SelectedItem, TabLog))
            RefreshLog_Click(this, new RoutedEventArgs());
        if (MainTabs.SelectedContent is FrameworkElement fe)
            UiMotion.SoftContentIn(fe);
    }

    /// <summary>
    /// Presets are per-game. When opening the Presets tab (or when a game starts),
    /// select that game in the list so hotkeyed Tarkov presets are visible — not "ghost" switches.
    /// </summary>
    public void FocusPresetsForActiveOrSelectedGame(GameProfile? prefer = null)
    {
        prefer ??= App.Services.Monitor.CurrentProfile
                   ?? _selected
                   ?? PresetGameList.SelectedItem as GameProfile
                   ?? _profiles.FirstOrDefault();
        if (prefer == null) return;

        var match = _profiles.FirstOrDefault(p => p.Id == prefer.Id)
                    ?? _profiles.FirstOrDefault(p =>
                        string.Equals(p.ProcessName, prefer.ProcessName, StringComparison.OrdinalIgnoreCase));
        if (match == null) return;

        if (!ReferenceEquals(PresetGameList.SelectedItem, match))
            PresetGameList.SelectedItem = match;
        else if (_presetGame == null || _presets.Count == 0)
        {
            _suppressEditorEvents = true;
            try
            {
                _selectedPreset = null;
                _presetGame = match;
                _presets.Clear();
                match.Presets ??= new List<QuickPreset>();
                foreach (var p in match.Presets)
                    _presets.Add(p);
                LblPresetGame.Text = $"Presets — {match.Name}";
                if (_presets.Count > 0 && PresetList.SelectedItem == null)
                    PresetList.SelectedIndex = 0;
            }
            finally
            {
                _suppressEditorEvents = false;
            }
        }
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
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
        }
        catch { }

        if (MainTabs.SelectedItem != TabPlay)
            MainTabs.SelectedItem = TabPlay;

        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            bool light = string.Equals(App.Services.Config.Current.Ui?.Theme, "light", StringComparison.OrdinalIgnoreCase);
            int useDark = light ? 0 : 1;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20, ref useDark, sizeof(int));
        }
        catch { /* older Windows */ }

        EventManager.RegisterClassHandler(
            typeof(System.Windows.Controls.Button),
            System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            new RoutedEventHandler(OnAnyButtonClick),
            handledEventsToo: true);
    }

    private static DateTime _lastClickSoundUtc = DateTime.MinValue;

    /// <summary>Only "primary" action buttons get the click chime — Ghost/secondary buttons stay silent.</summary>
    private static readonly HashSet<string> ClickSoundButtons = new(StringComparer.Ordinal)
    {
        "BtnApply", "BtnSave", "BtnEmergency", "BtnScan", "BtnLaunchGame", "BtnLaunchGamePreset"
    };

    private static void OnAnyButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (string.IsNullOrEmpty(btn.Name) || !ClickSoundButtons.Contains(btn.Name))
            return;
        var now = DateTime.UtcNow;
        if ((now - _lastClickSoundUtc).TotalMilliseconds < 160) return;
        _lastClickSoundUtc = now;
        UiSound.Click();
    }

    private void LaunchGame_Click(object sender, RoutedEventArgs e) => LaunchProfile(_selected ?? _presetGame);

    private void LaunchProfile(GameProfile? profile)
    {
        if (profile == null)
        {
            ThemedDialog.Show(this, "Select a game profile first.", "Launch");
            return;
        }

        string? path = profile.ExePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ThemedDialog.Show(this,
                "No exe path on this profile.\nUse Browse… or Scan to fill ExePath, then try again.",
                "Launch");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                UseShellExecute = true
            });
            ShowToast($"Launching {profile.Name}");
            UiSound.Launch();
        }
        catch (Exception ex)
        {
            UiSound.Error();
            ThemedDialog.Show(this, "Could not start game:\n" + ex.Message, "Launch");
        }
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

    private void NakidevCredit_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
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

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!TryResolveUnsavedChanges())
            return;

        var win = new SettingsWindow { Owner = this };
        win.ShowDialog();
        // Live preview in Settings may have changed runtime sound state — restore from saved config if cancelled
        UiSound.ApplyFromConfig(App.Services.Config.Current.Ui);
        ApplyLocalization();
        UpdateActiveHeader(App.Services.Monitor.CurrentProfile);
        if (win.Imported)
            ReloadFromConfig();
        if (System.Windows.Application.Current is App app)
            app.RebuildTrayMenu();
    }

    public void ApplyLocalization()
    {
        UpdateTitleDirtyState();
        BtnApply.Content = Loc.T("btn.apply");
        if (BtnEmergency != null)
        {
            BtnEmergency.Content = Loc.T("btn.emergency.short");
            BtnEmergency.ToolTip = Loc.T("btn.emergency.tip");
        }
        if (BtnReset != null) BtnReset.Content = Loc.T("btn.reset.global");
        if (LblResetGlobalHint != null) LblResetGlobalHint.Text = Loc.T("btn.reset.global.hint");
        BtnScan.Content = Loc.T("btn.scan");
        BtnAddManual.Content = Loc.T("btn.addManual");
        BtnRestore.Content = Loc.T("btn.restore");
        BtnDup.Content = Loc.T("btn.dup");
        BtnDelete.Content = Loc.T("btn.delete");
        TabPlay.Header = Loc.T("tab.play");
        TabProfiles.Header = Loc.T("tab.profiles");
        TabPresets.Header = Loc.T("tab.presets");
        TabGlobal.Header = Loc.T("tab.global");
        TabLog.Header = Loc.T("tab.log");
        if (LblPlayTitle != null) LblPlayTitle.Text = Loc.T("play.title");
        if (LblPlayFps != null) LblPlayFps.Text = Loc.T("profile.fpsLimit");
        if (LblPlayColor != null) LblPlayColor.Text = Loc.T("play.color");
        if (LblPlayColorHint != null) LblPlayColorHint.Text = Loc.T("play.color.hint");
        if (LblPlayPresets != null) LblPlayPresets.Text = Loc.T("play.presets");
        if (ExpPlayHotkeys != null) ExpPlayHotkeys.Header = Loc.T("play.hotkeys");
        if (ExpPlaySession != null) ExpPlaySession.Header = Loc.T("play.session");
        if (BtnPlayLaunch != null) BtnPlayLaunch.Content = Loc.T("btn.launchGame");
        if (BtnPlayOverlay != null) BtnPlayOverlay.Content = Loc.T("btn.overlay");
        if (ChkPlaySoftAltTab != null) ChkPlaySoftAltTab.Content = Loc.T("profile.softAltTab");
        TxtSettingsCaption.Text = Loc.T("btn.settings");
        BtnSettingsCaption.ToolTip = Loc.T("btn.settings");
        TxtSearch.ToolTip = Loc.T("search.placeholder");
        LblDisplay.Text = Loc.T("display");

        if (LblUnsaved != null) LblUnsaved.Text = Loc.T("dirty.unsaved");
        if (BtnOpenPresets != null) BtnOpenPresets.Content = Loc.T("btn.openPresets");
        if (ExpSession != null) ExpSession.Header = Loc.T("session.advanced");
        if (LblHotkeyCheat != null) LblHotkeyCheat.Text = Loc.T("hotkey.cheatsheet");
        if (LblSessionSub != null) LblSessionSub.Text = Loc.T("session.sub");
        if (ChkQuietToast != null) ChkQuietToast.Content = Loc.T("session.quiet");
        if (ChkNoNightLight != null) ChkNoNightLight.Content = Loc.T("session.night");
        if (ChkNoAutoHdr != null) ChkNoAutoHdr.Content = Loc.T("session.hdr");
        if (ChkMonBright != null) ChkMonBright.Content = Loc.T("session.bright");
        if (ChkSwitchAudio != null) ChkSwitchAudio.Content = Loc.T("session.audio");
        if (LblScaling != null) LblScaling.Text = Loc.T("session.scaling");

        if (BtnPresetAdd != null) BtnPresetAdd.Content = Loc.T("btn.add");
        if (BtnPresetDelete != null) BtnPresetDelete.Content = Loc.T("btn.delete");
        if (BtnPresetExport != null) BtnPresetExport.Content = Loc.T("presets.export");
        if (BtnPresetImport != null) BtnPresetImport.Content = Loc.T("presets.import");
        if (BtnPackGallery != null) BtnPackGallery.Content = Loc.T("presets.gallery");
        if (BtnExportProfile != null) BtnExportProfile.Content = Loc.T("profile.export");
        if (BtnImportProfile != null) BtnImportProfile.Content = Loc.T("profile.import");

        if (LblRefreshRate != null) LblRefreshRate.Text = Loc.T("display.refresh");
        if (LblStartupPreset != null) LblStartupPreset.Text = Loc.T("profile.startup");
        if (LblAliases != null) LblAliases.Text = Loc.T("profile.aliases");
        if (ExpAdvanced != null) ExpAdvanced.Header = Loc.T("profile.advanced");
        if (ChkApplyOnFocus != null) ChkApplyOnFocus.Content = Loc.T("profile.focus");
        if (ChkSoftAltTab != null) ChkSoftAltTab.Content = Loc.T("profile.softAltTab");
        if (LblFpsLimit != null) LblFpsLimit.Text = Loc.T("profile.fpsLimit");
        UpdateFpsLimitAvailability();
        if (LblMonitorLayout != null) LblMonitorLayout.Text = Loc.T("session.layout");
        if (LblSessionTemplates != null) LblSessionTemplates.Text = Loc.T("session.templates");
        if (BtnTplCompetitive != null) BtnTplCompetitive.Content = Loc.T("session.tpl.competitive");
        if (BtnTplImmersive != null) BtnTplImmersive.Content = Loc.T("session.tpl.immersive");
        if (BtnTplMinimal != null) BtnTplMinimal.Content = Loc.T("session.tpl.minimal");
        if (LblHkNext != null) LblHkNext.Text = Loc.T("hotkey.next");
        if (LblHkPrev != null) LblHkPrev.Text = Loc.T("hotkey.prev");
        if (LblHkAb != null) LblHkAb.Text = Loc.T("hotkey.compareAb");
        if (LblHkZoom != null) LblHkZoom.Text = Loc.T("hotkey.zoom");
        if (LblHkOverlay != null) LblHkOverlay.Text = Loc.T("hotkey.overlay");
        if (LblHkEmergency != null) LblHkEmergency.Text = Loc.T("hotkey.emergency");
        if (LblAppHotkeys != null) LblAppHotkeys.Text = Loc.T("hotkey.app");
        if (LblZoomFactor != null) LblZoomFactor.Text = Loc.T("zoom.factor");
        if (LblZoomHint != null) LblZoomHint.Text = Loc.T("zoom.hint");
        if (LblAppHotkeysHint != null) LblAppHotkeysHint.Text = Loc.T("hotkey.hint");
        if (StatusText != null && !_dirty) StatusText.Text = Loc.T("status.monitoring");
        if (BtnLogRefresh != null) BtnLogRefresh.Content = Loc.T("log.refresh");
        if (BtnLogFolder != null) BtnLogFolder.Content = Loc.T("log.openFolder");
        if (TabLog != null) TabLog.Header = Loc.T("tab.log");

        if (BtnOverlay != null) BtnOverlay.Content = Loc.T("btn.overlay");

        ApplyInfoTips();
        UpdateSessionLabels();
        UpdateActiveHeader(App.Services.Monitor.CurrentProfile);
    }

    private void ApplyInfoTips()
    {
        if (BtnOverlay != null) BtnOverlay.ToolTip = Loc.T("btn.overlay.tip");
        if (InfoRestoreMode != null) SetTip(InfoRestoreMode, "restore.mode.tip");
        if (LblRestoreMode != null) LblRestoreMode.Text = Loc.T("restore.mode.lbl");
        SetTip(InfoSoftAltTab, "profile.softAltTab.tip");
        SetTip(InfoFpsLimit, "profile.fpsLimit.tip");
        SetTip(InfoNight, "session.night.tip");
        SetTip(InfoHdr, "session.hdr.tip");
        SetTip(InfoMonBright, "session.bright.tip");
        SetTip(InfoCompanions, "companions.tip");
        SetTip(InfoPresetBackend, "color.backend.tip");
        SetTip(InfoPresetLock, "color.lock.tip");
        if (BtnPresetExport != null) BtnPresetExport.ToolTip = Loc.T("presets.export.tip");
        if (BtnPresetImport != null) BtnPresetImport.ToolTip = Loc.T("presets.import.tip");
    }

    private static void SetTip(InfoHint? hint, string key)
    {
        if (hint != null)
            hint.Tip = Loc.T(key);
    }

    /// <summary>Reflects _dirty in the title bar (" •" suffix) — call after any _dirty change.</summary>
    private void UpdateTitleDirtyState()
    {
        string baseTitle = Loc.T("app.name");
        string shown = _dirty ? baseTitle + " •" : baseTitle;
        Title = shown;
        if (TitleAppName != null) TitleAppName.Text = shown;
    }

    private void OpenPresets_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = TabPresets;
        FocusPresetsForActiveOrSelectedGame(_selected);
    }

    /// <summary>Shows the startup preset (and its color, if applied) for the selected profile.</summary>
    private void UpdateColorSummary(GameProfile? p)
    {
        if (LblColorSummary == null) return;
        if (p == null)
        {
            LblColorSummary.Text = Loc.T("color.summary.default");
            return;
        }

        var preset = !string.IsNullOrWhiteSpace(p.StartupPresetId)
            ? p.Presets?.FirstOrDefault(pr => pr.Id == p.StartupPresetId)
            : null;
        if (preset == null)
        {
            LblColorSummary.Text = Loc.T("color.summary.none");
            return;
        }

        string summary = $"Startup preset: {preset.Name}";
        if (preset.ApplyColor && preset.Color != null)
            summary += $"  ·  B{(int)Math.Round(preset.Color.Brightness * 100)}%  C{preset.Color.Contrast:F2}  G{preset.Color.Gamma:F2}";
        LblColorSummary.Text = summary;
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
        RefreshPlayTab();
    }

    /// <summary>Game whose preset hotkeys stay registered when no match is running.</summary>
    public GameProfile? HotkeyPresetFallback => _presetGame ?? _selected;

    /// <summary>Selected profile for overlay when no game is running.</summary>
    public GameProfile? SelectedProfileForOverlay => _selected;

    public void NotifyOverlayApplied() => ShowToast(Loc.T("overlay.applied"));

    private void RefreshHotkeyBindings()
    {
        App.Services.Hotkeys.RegisterFromConfig(
            App.Services.Config.Current,
            App.Services.Monitor.CurrentProfile,
            HotkeyPresetFallback);
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
        string? keepProfileId = _selected?.Id;
        string? keepPresetGameId = _presetGame?.Id;
        string? keepPresetId = _selectedPreset?.Id;
        int keepTab = MainTabs.SelectedIndex;

        _suppressEditorEvents = true;
        // Drop editor pointers before clearing collections — avoids Sync to orphaned profiles.
        _selectedPreset = null;
        _presetGame = null;
        _selected = null;

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
        SelectComboByTag(CmbDefaultPower, cfg.Defaults.PowerPlan ?? "balanced");

        cfg.Defaults.Color = ColorSettings.Neutral;
        UpdateDriverUiAvailability();

        cfg.GlobalHotkeys ??= new GlobalHotkeys();
        if (HkToggleOverlay != null) HkToggleOverlay.Text = cfg.GlobalHotkeys.ToggleOverlay ?? "";
        if (HkEmergency != null) HkEmergency.Text = cfg.GlobalHotkeys.EmergencyRestore ?? "";
        if (HkNextPreset != null) HkNextPreset.Text = cfg.GlobalHotkeys.NextPreset ?? "";
        if (HkPrevPreset != null) HkPrevPreset.Text = cfg.GlobalHotkeys.PreviousPreset ?? "";
        if (HkCompareAb != null) HkCompareAb.Text = cfg.GlobalHotkeys.CompareAb ?? "";
        if (HkToggleZoom != null) HkToggleZoom.Text = cfg.GlobalHotkeys.ToggleZoom ?? "";
        SelectZoomFactor(cfg.Ui?.ZoomFactor ?? 4);

        FillAudioDevices();

        if (_profiles.Count > 0)
        {
            var keep = !string.IsNullOrEmpty(keepProfileId)
                ? _profiles.FirstOrDefault(p => p.Id == keepProfileId)
                : null;
            ProfileList.SelectedItem = keep ?? _profiles[0];

            var keepGame = !string.IsNullOrEmpty(keepPresetGameId)
                ? _profiles.FirstOrDefault(p => p.Id == keepPresetGameId)
                : null;
            PresetGameList.SelectedItem = keepGame ?? ProfileList.SelectedItem as GameProfile;

            // SelectionChanged is suppressed — load presets manually.
            _presetGame = PresetGameList.SelectedItem as GameProfile;
            _presets.Clear();
            _selectedPreset = null;
            if (_presetGame != null)
            {
                _presetGame.Presets ??= new List<QuickPreset>();
                foreach (var p in _presetGame.Presets)
                    _presets.Add(p);
                LblPresetGame.Text = $"Presets — {_presetGame.Name}";
            }
        }
        else
            ClearEditor();

        if (!string.IsNullOrEmpty(keepPresetId) && _presets.Count > 0)
        {
            var pr = _presets.FirstOrDefault(p => p.Id == keepPresetId);
            if (pr != null)
            {
                PresetList.SelectedItem = pr;
                _selectedPreset = pr;
                LoadPresetEditor(pr);
            }
            else if (_presets.Count > 0)
            {
                PresetList.SelectedIndex = 0;
            }
        }
        else if (_presets.Count > 0)
        {
            PresetList.SelectedIndex = 0;
        }

        _ignoreTabChange = true;
        if (keepTab >= 0 && keepTab < MainTabs.Items.Count)
            MainTabs.SelectedIndex = keepTab;
        _ignoreTabChange = false;
        _lastTabIndex = MainTabs.SelectedIndex;

        _suppressEditorEvents = false;
        _dirty = false;
        if (LblUnsaved != null) LblUnsaved.Visibility = Visibility.Collapsed;
        UpdateTitleDirtyState();
        RefreshLog_Click(this, new RoutedEventArgs());
    }

    private void MaybeFirstScan()
    {
        var cfg = App.Services.Config.Current;
        if (cfg.FirstScanDone) return;
        cfg.FirstScanDone = true;
        App.Services.Config.Save(cfg, raiseChanged: false);

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

    private void Resolution_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEditorEvents) return;
        RefreshRefreshRateCombo();
        EditorChanged(sender, e);
    }

    private void Resolution_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressEditorEvents) return;
        RefreshRefreshRateCombo();
        if (_selected == null) return;
        MarkDirty();
        PushEditorToSelected();
    }

    private string? ReadResolutionText()
    {
        if (CmbResolution == null) return null;
        var text = CmbResolution.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(text) && !string.Equals(text, "(don't change)", StringComparison.OrdinalIgnoreCase))
            return text;
        var sel = CmbResolution.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(sel) || string.Equals(sel, "(don't change)", StringComparison.OrdinalIgnoreCase))
            return null;
        return sel;
    }

    private void RefreshRefreshRateCombo(int preferHz = -1)
    {
        if (CmbRefreshRate == null) return;
        int keep = preferHz >= 0
            ? preferHz
            : (CmbRefreshRate.SelectedItem is ComboBoxItem cur && int.TryParse(cur.Tag?.ToString(), out var hz) ? hz : 0);

        var res = ReadResolutionText();
        string? device = (CmbDisplay.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(device)) device = null;

        CmbRefreshRate.Items.Clear();
        CmbRefreshRate.Items.Add(new ComboBoxItem { Content = Loc.T("display.refresh.none"), Tag = "0" });
        if (!string.IsNullOrWhiteSpace(res))
        {
            foreach (var rate in DisplayEngine.GetAvailableRefreshRates(res, device))
                CmbRefreshRate.Items.Add(new ComboBoxItem { Content = $"{rate} Hz", Tag = rate.ToString() });
        }

        SelectComboByTag(CmbRefreshRate, keep.ToString());
    }

    private void FillStartupPresetCombo(GameProfile? p, string? preferId = null)
    {
        if (CmbStartupPreset == null) return;
        string? keep = preferId ?? (CmbStartupPreset.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (preferId == null && p != null) keep = p.StartupPresetId;

        CmbStartupPreset.Items.Clear();
        CmbStartupPreset.Items.Add(new ComboBoxItem { Content = Loc.T("profile.startup.none"), Tag = "" });
        if (p?.Presets != null)
        {
            foreach (var pr in p.Presets)
                CmbStartupPreset.Items.Add(new ComboBoxItem { Content = pr.Name, Tag = pr.Id });
        }
        SelectComboByTag(CmbStartupPreset, keep ?? "");
    }

    private async void ScanGames_Click(object sender, RoutedEventArgs e)
        => await RunGameScanAsync(confirmIfAny: false);

    private async Task RunGameScanAsync(bool confirmIfAny)
    {
        ScanBusy.ShowAnimated();
        ScanBusy.SetStatus("Looking through disks…");
        UiSound.BeginWork();

        var minDelay = Task.Delay(1600);
        List<GameScanner.FoundGame> found;
        try
        {
            found = await Task.Run(() =>
                GameScanner.ScanAll(msg => Dispatcher.Invoke(() => ScanBusy.SetStatus(msg))));
        }
        catch (Exception ex)
        {
            UiSound.EndWork(UiWorkResult.Cancel);
            UiSound.Error();
            ScanBusy.HideAnimated();
            ThemedDialog.Show(this, "Scan failed:\n" + ex.Message, "Scan");
            return;
        }

        await minDelay;
        ScanBusy.HideAnimated();

        if (found.Count == 0)
        {
            UiSound.EndWork(UiWorkResult.Empty);
            ThemedDialog.Show(this,
                "No known games found on this PC.\nUse Add manually or Running… to pick any .exe.",
                "Scan");
            return;
        }

        UiSound.EndWork(UiWorkResult.Done);

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
                    MarkDirty();
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
            MarkDirty();
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
        SyncPlayGameListSelection(_selected);
        RefreshPlayTab();
    }

    /// <summary>Play tab's own game list — keeps ProfileList (Library) and PlayGameList (Play) in sync without looping.</summary>
    private void PlayGameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGameListSync) return;
        var picked = PlayGameList.SelectedItem as GameProfile;
        if (picked == null) return;

        if (_dirty && _selected != null) PushEditorToSelected();
        _selected = picked;
        LoadEditor(_selected);

        _suppressGameListSync = true;
        try
        {
            if (!ReferenceEquals(ProfileList.SelectedItem, picked))
                ProfileList.SelectedItem = picked;
        }
        finally
        {
            _suppressGameListSync = false;
        }
        RefreshPlayTab();
    }

    private void SyncPlayGameListSelection(GameProfile? profile)
    {
        if (_suppressGameListSync || PlayGameList == null) return;
        _suppressGameListSync = true;
        try
        {
            if (!ReferenceEquals(PlayGameList.SelectedItem, profile))
                PlayGameList.SelectedItem = profile;
        }
        finally
        {
            _suppressGameListSync = false;
        }
    }

    private void LoadEditor(GameProfile? p)
    {
        _suppressEditorEvents = true;
        if (p == null) { ClearEditor(); _suppressEditorEvents = false; return; }

        ChkEnabled.IsChecked = p.Enabled;
        TxtName.Text = p.Name;
        TxtProcess.Text = p.ProcessName;
        if (TxtAliases != null)
            TxtAliases.Text = p.ProcessAliases is { Count: > 0 }
                ? string.Join(", ", p.ProcessAliases)
                : "";
        ChkApplyRes.IsChecked = p.ApplyResolution;
        ChkApplyPower.IsChecked = p.ApplyPowerPlan;
        p.ApplyColor = false;

        if (string.IsNullOrWhiteSpace(p.Resolution))
        {
            CmbResolution.SelectedIndex = 0;
            CmbResolution.Text = "(don't change)";
        }
        else
        {
            if (!CmbResolution.Items.Contains(p.Resolution)) CmbResolution.Items.Add(p.Resolution);
            CmbResolution.SelectedItem = p.Resolution;
            CmbResolution.Text = p.Resolution;
        }

        RefreshRefreshRateCombo(p.RefreshRate);
        FillStartupPresetCombo(p, p.StartupPresetId);
        if (SldApplyDelay != null) SldApplyDelay.Value = Math.Clamp(p.ApplyDelaySeconds, 0, 30);
        if (ChkApplyOnFocus != null) ChkApplyOnFocus.IsChecked = p.ApplyOnFocus;
        if (ChkSoftAltTab != null) ChkSoftAltTab.IsChecked = p.SoftRestoreOnAltTab;
        if (CmbFpsLimit != null) SelectOrSetFpsLimit(CmbFpsLimit, p.FpsLimit);

        SelectComboByTag(CmbPower, p.PowerPlan ?? "");
        SelectComboByTag(CmbDisplay, p.DisplayDevice ?? "");
        SelectComboByTag(CmbRestoreMode, p.RestoreMode.ToString());
        var sx = p.Session ?? new SessionExtras();
        SldDeferred.Value = sx.DeferredApplySeconds;
        ChkQuietToast.IsChecked = sx.QuietNotifications;
        ChkNoNightLight.IsChecked = sx.DisableNightLight;
        ChkNoAutoHdr.IsChecked = sx.DisableAutoHdr;
        var layout = sx.MonitorLayout;
        if (string.IsNullOrWhiteSpace(layout))
            layout = sx.IsolatePrimaryMonitor ? "isolatePrimary" : "keepAll";
        if (CmbMonitorLayout != null) SelectComboByTag(CmbMonitorLayout, layout);
        ChkMonBright.IsChecked = sx.ApplyMonitorBrightness;
        SldMonBright.Value = sx.MonitorBrightness;
        ChkSwitchAudio.IsChecked = sx.SwitchAudioDevice;
        SelectAudioDevice(sx.AudioDeviceId);
        SelectComboByTag(CmbScaling, string.IsNullOrWhiteSpace(sx.ScalingMode) ? "default" : sx.ScalingMode!);
        UpdateSessionLabels();
        CompanionList.ItemsSource = null;
        CompanionList.ItemsSource = p.Companions;
        UpdateColorSummary(p);
        _suppressEditorEvents = false;
        // Keep _dirty — profile switch must not pretend changes were saved to disk
    }

    private void ClearEditor()
    {
        TxtName.Text = "";
        TxtProcess.Text = "";
        if (TxtAliases != null) TxtAliases.Text = "";
        CompanionList.ItemsSource = null;
        UpdateColorSummary(null);
    }

    // -- Play tab --------------------------------------------------------

    /// <summary>Play tab shows the running game if any, else whatever is selected on Profiles.</summary>
    private GameProfile? ResolvePlayTargetProfile()
    {
        string? id = App.Services.Monitor.CurrentProfile?.Id ?? _selected?.Id;
        return id == null ? null : _profiles.FirstOrDefault(p => p.Id == id);
    }

    public void RefreshPlayTab()
    {
        if (TxtPlayName == null) return; // not yet initialized
        var profile = ResolvePlayTargetProfile();
        _playProfile = profile;
        SyncPlayGameListSelection(profile);

        _suppressPlayEvents = true;
        try
        {
            if (profile == null)
            {
                TxtPlayName.Text = Loc.T("play.noGame");
                TxtPlayProcess.Text = "";
                TxtPlayStatus.Text = "";
                PlayPresetChips.Children.Clear();
                if (ChkPlaySoftAltTab != null) ChkPlaySoftAltTab.IsChecked = false;
                SelectOrSetFpsLimit(CmbPlayFps, 0);
                if (TxtPlayHotkeys != null) TxtPlayHotkeys.Text = "";
                SetPlaySlidersEnabled(false);
                _playPreset = null;
                return;
            }

            SetPlaySlidersEnabled(true);
            TxtPlayName.Text = profile.Name;
            TxtPlayProcess.Text = profile.ProcessName;
            bool active = App.Services.Monitor.CurrentProfile?.Id == profile.Id;
            TxtPlayStatus.Text = active ? Loc.T("play.status.active") : Loc.T("play.status.idle");

            if (ChkPlaySoftAltTab != null) ChkPlaySoftAltTab.IsChecked = profile.SoftRestoreOnAltTab;
            SelectOrSetFpsLimit(CmbPlayFps, profile.FpsLimit);

            string? activePresetId = active ? App.Services.Monitor.ActivePresetId : null;
            QuickPreset? preset = activePresetId != null
                ? profile.Presets?.FirstOrDefault(p => p.Id == activePresetId)
                : (_playPreset != null ? profile.Presets?.FirstOrDefault(p => p.Id == _playPreset.Id) : null);
            if (preset == null && !string.IsNullOrWhiteSpace(profile.StartupPresetId))
                preset = profile.Presets?.FirstOrDefault(p => p.Id == profile.StartupPresetId);
            preset ??= profile.Presets?.FirstOrDefault();
            _playPreset = preset;

            RebuildPlayPresetChips(profile, preset);
            LoadPlaySlidersFrom(preset, profile);
            RefreshPlayHotkeyCheatSheet(profile);
        }
        finally
        {
            _suppressPlayEvents = false;
        }
    }

    private void SetPlaySlidersEnabled(bool enabled)
    {
        if (PlayEditor != null) PlayEditor.IsEnabled = enabled;
    }

    private void RebuildPlayPresetChips(GameProfile profile, QuickPreset? selected)
    {
        if (PlayPresetChips == null) return;
        PlayPresetChips.Children.Clear();

        var liveChip = MakePlayChip(Loc.T("play.liveChip"), selected == null);
        liveChip.Click += (_, _) => PlayPresetChip_Click(null);
        PlayPresetChips.Children.Add(liveChip);

        foreach (var p in profile.Presets ?? new List<QuickPreset>())
        {
            var chip = MakePlayChip(p.Name, selected != null && selected.Id == p.Id);
            chip.Click += (_, _) => PlayPresetChip_Click(p);
            PlayPresetChips.Children.Add(chip);
        }
    }

    private System.Windows.Controls.Button MakePlayChip(string text, bool selected) => new()
    {
        Content = text,
        Style = (Style)FindResource(selected ? "ChipButtonSelected" : "ChipButton"),
        Margin = new Thickness(0, 0, 8, 8)
    };

    private void PlayPresetChip_Click(QuickPreset? preset)
    {
        if (_playProfile == null) return;
        _playPreset = preset;
        RebuildPlayPresetChips(_playProfile, preset);
        LoadPlaySlidersFrom(preset, _playProfile);

        if (preset != null)
        {
            if (App.Services.Monitor.CurrentProfile?.Id == _playProfile.Id)
                App.Services.Monitor.ApplyPreset(preset);
            else
            {
                var c = (preset.Color ?? ColorSettings.Neutral).Clone();
                c.Clamp();
                App.Services.Display.ApplyColor(c);
            }
        }
    }

    private void LoadPlaySlidersFrom(QuickPreset? preset, GameProfile profile)
    {
        ColorSettings source;
        if (preset != null)
        {
            preset.EnsureDualColorSlots();
            source = preset.Color.Clone();
        }
        else
        {
            source = App.Services.Display.LiveColor.Clone();
        }
        _playBackend = source.Backend;

        bool wasSuppressed = _suppressPlayEvents;
        _suppressPlayEvents = true;
        try
        {
            ColorUiHelper.ApplyColorSliders(source, SldPlayB, SldPlayC, SldPlayG, SldPlayV, SldPlayS, SldPlayH);
            RefreshPlayLabels();
        }
        finally
        {
            _suppressPlayEvents = wasSuppressed;
        }
    }

    private void RefreshPlayLabels()
    {
        if (LblPlayB == null) return;
        ColorUiHelper.UpdateLabels(LblPlayB, LblPlayC, LblPlayG, LblPlayV, SldPlayB, SldPlayC, SldPlayG, SldPlayV, _playBackend, LblPlayH, SldPlayH);
        if (ValPlayB != null) ValPlayB.Text = LblPlayB.Text.Replace("Bright ", "");
        if (ValPlayC != null) ValPlayC.Text = LblPlayC.Text.Replace("Contr ", "");
        if (ValPlayG != null) ValPlayG.Text = LblPlayG.Text.Replace("Gamma ", "");
        if (ValPlayV != null && LblPlayV != null) ValPlayV.Text = LblPlayV.Text.Replace("Vibr ", "");
        if (ValPlayH != null && LblPlayH != null) ValPlayH.Text = LblPlayH.Text.Replace("Hue ", "").Replace("°", "");
        if (LblPlayS != null) LblPlayS.Text = $"Shadow {(int)SldPlayS.Value}";
        if (ValPlayS != null) ValPlayS.Text = $"{(int)SldPlayS.Value}";
    }

    private void PlaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressPlayEvents || !IsInitialized || _playProfile == null) return;
        RefreshPlayLabels();

        var c = ColorUiHelper.ReadColorFromSliders(_playBackend, SldPlayB, SldPlayC, SldPlayG, SldPlayV, SldPlayS,
            lockColor: _playPreset?.Color?.LockColor ?? true, hue: SldPlayH);
        App.Services.Display.ApplyColor(c);

        if (_playPreset != null)
        {
            _playPreset.ApplyColor = true;
            _playPreset.Color = c;
            _playPreset.SaveActiveToDualSlots();
            MarkDirty();

            if (App.Services.Monitor.CurrentProfile?.Id == _playProfile.Id
                && App.Services.Monitor.ActivePresetId == _playPreset.Id)
                App.Services.Monitor.SetColorLockPaused(false);
        }
    }

    private void PlayLaunch_Click(object sender, RoutedEventArgs e) => LaunchProfile(_playProfile);

    private void PlayOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
            app.ToggleGameOverlay();
    }

    private void PlaySoftAltTab_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressPlayEvents || _playProfile == null) return;
        _playProfile.SoftRestoreOnAltTab = ChkPlaySoftAltTab.IsChecked == true;
        MarkDirty();
        if (_selected != null && _selected.Id == _playProfile.Id && ChkSoftAltTab != null)
            ChkSoftAltTab.IsChecked = _playProfile.SoftRestoreOnAltTab;
    }

    private void PlayFps_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPlayEvents || _playProfile == null) return;
        _playProfile.FpsLimit = ReadFpsLimit(CmbPlayFps);
        MarkDirty();
        if (_selected != null && _selected.Id == _playProfile.Id)
            SelectOrSetFpsLimit(CmbFpsLimit, _playProfile.FpsLimit);
    }

    private void PlayFps_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressPlayEvents || _playProfile == null) return;
        int fps = ReadFpsLimit(CmbPlayFps);
        SelectOrSetFpsLimit(CmbPlayFps, fps);
        _playProfile.FpsLimit = fps;
        MarkDirty();
        if (_selected != null && _selected.Id == _playProfile.Id)
            SelectOrSetFpsLimit(CmbFpsLimit, fps);
    }

    private void RefreshPlayHotkeyCheatSheet(GameProfile profile)
    {
        if (TxtPlayHotkeys == null) return;
        var gh = App.Services.Config.Current.GlobalHotkeys;
        var lines = new List<string>();

        void AddGlobal(string? gesture, string labelKey)
        {
            if (!string.IsNullOrWhiteSpace(gesture))
                lines.Add($"{Loc.T(labelKey)}: {gesture}");
        }
        AddGlobal(gh?.ToggleOverlay, "tray.overlay");
        AddGlobal(gh?.EmergencyRestore, "tray.reset");
        AddGlobal(gh?.NextPreset, "hotkey.next");
        AddGlobal(gh?.PreviousPreset, "hotkey.prev");
        AddGlobal(gh?.CompareAb, "hotkey.compareAb");
        AddGlobal(gh?.ToggleZoom, "hotkey.zoom");

        if (profile.Presets != null)
        {
            foreach (var p in profile.Presets.Where(p => !string.IsNullOrWhiteSpace(p.Hotkey)))
                lines.Add($"{p.Name}: {p.Hotkey}");
        }

        TxtPlayHotkeys.Text = lines.Count > 0 ? string.Join("   ·   ", lines) : Loc.T("play.hotkeys.none");
    }

    private void EditorChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEditorEvents || _selected == null) return;
        MarkDirty();
        PushEditorToSelected();
        UpdateColorSummary(_selected);
        ProfileList.Items.Refresh();
        RefreshPlayQuickFields();
    }

    /// <summary>Syncs the Play tab's Soft-restore/FPS fields without rebuilding preset chips
    /// (avoids clobbering a manually-selected "Live" chip on unrelated Profiles-tab edits).</summary>
    private void RefreshPlayQuickFields()
    {
        if (_playProfile == null || TxtPlayName == null || _selected == null || _selected.Id != _playProfile.Id) return;
        if (ChkPlaySoftAltTab != null) ChkPlaySoftAltTab.IsChecked = _playProfile.SoftRestoreOnAltTab;
        SelectOrSetFpsLimit(CmbPlayFps, _playProfile.FpsLimit);
    }

    private void ColorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEditorEvents) return;
        UpdateSessionLabels();
        if (_selected == null) return;
        MarkDirty();
        PushEditorToSelected();
    }

    private void GlobalEditorChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEditorEvents) return;
        MarkDirty();
    }

    private void MarkDirty()
    {
        _dirty = true;
        if (LblUnsaved != null) LblUnsaved.Visibility = Visibility.Visible;
        UpdateTitleDirtyState();
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void FlushAutosave()
    {
        _autoSaveTimer.Stop();
        if (!_dirty) return;
        if (_selected != null) PushEditorToSelected();
        if (_selectedPreset != null) PushPresetEditor();
        SyncPresetsBackToGame();
        PersistConfigToDisk(showBusy: false);
    }

    private static void SelectOrSetFpsLimit(System.Windows.Controls.ComboBox? combo, int fps)
    {
        if (combo == null) return;
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem ci && string.Equals(ci.Tag?.ToString(), fps.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = ci;
                combo.Text = ci.Content?.ToString() ?? fps.ToString();
                return;
            }
        }
        combo.SelectedItem = null;
        combo.Text = fps > 0 ? fps.ToString() : "(off)";
    }

    private static int ReadFpsLimit(System.Windows.Controls.ComboBox? combo)
    {
        if (combo == null) return 0;
        string? text = (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (text == null || !int.TryParse(text, out var fromTag))
        {
            var typed = combo.Text?.Trim();
            if (string.IsNullOrWhiteSpace(typed) || string.Equals(typed, "(off)", StringComparison.OrdinalIgnoreCase))
                return 0;
            fromTag = int.TryParse(typed, out var n) ? n : 0;
        }
        return Math.Clamp(fromTag, 0, 1023);
    }

    private void FpsLimit_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressEditorEvents || _selected == null) return;
        int fps = ReadFpsLimit(CmbFpsLimit);
        SelectOrSetFpsLimit(CmbFpsLimit, fps);
        MarkDirty();
        PushEditorToSelected();
    }

    private void UpdateFpsLimitAvailability()
    {
        if (LblFpsLimitHint == null) return;
        bool available = FpsLimitService.IsNvidiaAvailable;
        LblFpsLimitHint.Text = available
            ? Loc.T("profile.fpsLimit.hint")
            : Loc.T("profile.fpsLimit.unavailable");
        if (CmbFpsLimit != null) CmbFpsLimit.ToolTip = LblFpsLimitHint.Text;
    }

    private void UpdateSessionLabels()
    {
        if (LblDeferred != null) LblDeferred.Text = Loc.Tf("session.deferred", (int)SldDeferred.Value);
        if (LblMonBright != null) LblMonBright.Text = Loc.Tf("session.bright.lbl", (int)SldMonBright.Value);
        if (LblApplyDelay != null && SldApplyDelay != null)
            LblApplyDelay.Text = Loc.Tf("profile.delay", (int)SldApplyDelay.Value);
    }

    private void PresetBackendToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEditorEvents) return;
        var next = ReadBackendToggle(TogPresetBackend);

        if (_selectedPreset == null)
        {
            // Keep chrome in sync even with no preset selected.
            UpdateBackendActiveLabel(TogPresetBackend, next == ColorBackend.LowLevel);
            UpdatePresetLabels();
            UpdatePresetShadowEnabled();
            return;
        }

        if (next == _presetEditorBackend)
        {
            UpdateBackendActiveLabel(TogPresetBackend, next == ColorBackend.LowLevel);
            UpdatePresetLabels();
            UpdatePresetShadowEnabled();
            return;
        }

        _selectedPreset.EnsureDualColorSlots();
        var prev = ColorUiHelper.ReadColorFromSliders(_presetEditorBackend, SldPresetB, SldPresetC, SldPresetG, SldPresetV, SldPresetShadow, ChkPresetLock.IsChecked == true, SldPresetH);
        _selectedPreset.Color = prev;
        _selectedPreset.SaveActiveToDualSlots();
        _presetEditorBackend = next;
        var loaded = _selectedPreset.ActivateBackend(next);

        _suppressEditorEvents = true;
        try
        {
            ColorUiHelper.ApplyColorSliders(loaded, SldPresetB, SldPresetC, SldPresetG, SldPresetV, SldPresetShadow, SldPresetH);
            ChkPresetLock.IsChecked = loaded.LockColor;
            ColorUiHelper.ConfigureGammaRangeForBackend(next, SldPresetG);
        }
        finally
        {
            _suppressEditorEvents = false;
        }

        UpdatePresetLabels();
        UpdatePresetShadowEnabled();
        UpdateBackendActiveLabel(TogPresetBackend, next == ColorBackend.LowLevel);
        MarkDirty();
        PushPresetEditor();
        RefreshPresetListSafe();
    }

    private void UpdatePresetShadowEnabled()
    {
        bool low = ReadBackendToggle(TogPresetBackend) == ColorBackend.LowLevel;
        if (SldPresetShadow != null) SldPresetShadow.IsEnabled = low;
        if (LblPresetShadow != null) LblPresetShadow.Opacity = low ? 1.0 : 0.45;
    }

    private void UpdateDriverUiAvailability()
    {
        string label = GpuVendorDetect.DriverLabel;
        if (TogPresetBackend == null) return;
        TogPresetBackend.Content = label;
        TogPresetBackend.IsEnabled = true;
    }

    private void PushEditorToSelected()
    {
        if (_selected == null) return;
        _selected.Enabled = ChkEnabled.IsChecked == true;
        _selected.Name = TxtName.Text.Trim();
        _selected.ProcessName = TxtProcess.Text.Trim();
        _selected.ProcessAliases = ParseAliases(TxtAliases?.Text);
        _selected.ApplyResolution = ChkApplyRes.IsChecked == true;
        _selected.ApplyPowerPlan = ChkApplyPower.IsChecked == true;
        _selected.ApplyColor = false;

        var res = ReadResolutionText();
        _selected.Resolution = res;

        if (CmbRefreshRate?.SelectedItem is ComboBoxItem hzItem
            && int.TryParse(hzItem.Tag?.ToString(), out var hz))
            _selected.RefreshRate = hz;
        else
            _selected.RefreshRate = 0;

        if (CmbStartupPreset?.SelectedItem is ComboBoxItem sp)
            _selected.StartupPresetId = string.IsNullOrWhiteSpace(sp.Tag?.ToString()) ? null : sp.Tag!.ToString();

        if (SldApplyDelay != null)
            _selected.ApplyDelaySeconds = (int)SldApplyDelay.Value;
        if (ChkApplyOnFocus != null)
            _selected.ApplyOnFocus = ChkApplyOnFocus.IsChecked == true;
        if (ChkSoftAltTab != null)
            _selected.SoftRestoreOnAltTab = ChkSoftAltTab.IsChecked == true;
        if (CmbFpsLimit != null)
            _selected.FpsLimit = ReadFpsLimit(CmbFpsLimit);

        if (CmbPower.SelectedItem is ComboBoxItem pi)
            _selected.PowerPlan = string.IsNullOrWhiteSpace(pi.Tag?.ToString()) ? null : pi.Tag!.ToString();

        if (CmbDisplay.SelectedItem is ComboBoxItem di)
            _selected.DisplayDevice = string.IsNullOrWhiteSpace(di.Tag?.ToString()) ? null : di.Tag!.ToString();

        if (CmbRestoreMode.SelectedItem is ComboBoxItem rm
            && Enum.TryParse<RestoreMode>(rm.Tag?.ToString(), out var restoreMode))
            _selected.RestoreMode = restoreMode;

        _selected.Session ??= new SessionExtras();
        _selected.Session.DeferredApplySeconds = (int)SldDeferred.Value;
        _selected.Session.QuietNotifications = ChkQuietToast.IsChecked == true;
        _selected.Session.DisableNightLight = ChkNoNightLight.IsChecked == true;
        _selected.Session.DisableAutoHdr = ChkNoAutoHdr.IsChecked == true;
        if (CmbMonitorLayout?.SelectedItem is ComboBoxItem layoutItem)
        {
            var layout = layoutItem.Tag?.ToString() ?? "keepAll";
            _selected.Session.MonitorLayout = layout;
            if (string.Equals(layout, "isolatePrimary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(layout, "primaryOnly", StringComparison.OrdinalIgnoreCase))
                _selected.Session.IsolatePrimaryMonitor = true;
            else if (string.Equals(layout, "keepAll", StringComparison.OrdinalIgnoreCase))
                _selected.Session.IsolatePrimaryMonitor = false;
        }
        _selected.Session.ApplyMonitorBrightness = ChkMonBright.IsChecked == true;
        _selected.Session.MonitorBrightness = (int)SldMonBright.Value;
        _selected.Session.SwitchAudioDevice = ChkSwitchAudio.IsChecked == true;
        _selected.Session.AudioDeviceId = (CmbAudio.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                                          ?? CmbAudio.SelectedItem?.ToString();
        if (CmbScaling.SelectedItem is ComboBoxItem sc)
            _selected.Session.ScalingMode = sc.Tag?.ToString() ?? "default";
    }

    private static List<string> ParseAliases(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => GameCatalog.Normalize(s.Trim()))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        FlushAutosave();
        SaveBusy.ShowSaved(Loc.T("toast.saved"));
        SetStatus("Saved");
        ShowToast(Loc.T("toast.saved"));
    }

    private void Overlay_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
            app.ToggleGameOverlay();
    }

    private void EmergencyRestore_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Zoom.Off();
        App.Services.Monitor.EmergencyRestore();
        ShowToast(Loc.T("toast.emergency"));
        SetStatus(Loc.T("toast.emergency"));
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_selected != null) PushEditorToSelected();
        if (_selectedPreset != null) PushPresetEditor();
        FlushAutosave();

        // Apply targets the active tab — not whatever game happens to be selected in the list
        if (ReferenceEquals(MainTabs.SelectedItem, TabGlobal))
        {
            if (App.Services.Monitor.HasActivePreset)
            {
                SaveBusy.ShowSaved(Loc.T("toast.saved"));
                ShowToast(Loc.T("toast.global.kept.preset"));
                SetStatus("Global saved (preset active)");
                return;
            }

            App.Services.Monitor.ClearActivePreset();
            App.Services.Display.RestoreDefaults(App.Services.Config.Current.Defaults);
            SaveBusy.ShowSaved(Loc.T("toast.applied"));
            ShowToast("Applied global defaults");
            SetStatus("Applied global defaults");
            return;
        }

        if (ReferenceEquals(MainTabs.SelectedItem, TabPresets))
        {
            if (_selectedPreset != null)
            {
                App.Services.Monitor.ApplyPreset(_selectedPreset);
                SaveBusy.ShowSaved(Loc.T("toast.applied"));
                ShowToast($"Applied: {_selectedPreset.Name}");
                SetStatus($"Preset: {_selectedPreset.Name}");
                return;
            }
            ThemedDialog.Show(this, "Select a preset first.", "Apply");
            return;
        }

        // Profiles (or other)
        if (_selected != null)
        {
            App.Services.Monitor.ClearActivePreset();
            App.Services.Display.ApplyProfile(_selected, App.Services.Config.Current.Defaults);
            SaveBusy.ShowSaved(Loc.T("toast.applied"));
            ShowToast($"Applied: {_selected.Name}");
            SetStatus($"Applied: {_selected.Name}");
            return;
        }

        App.Services.Monitor.ClearActivePreset();
        App.Services.Display.RestoreDefaults(App.Services.Config.Current.Defaults);
        SaveBusy.ShowSaved(Loc.T("toast.applied"));
        ShowToast("Applied defaults");
    }

    private void PersistConfigToDisk(bool showBusy)
    {
        var cfg = App.Services.Config.Current;
        cfg.Profiles = _profiles.Select(CloneProfile).ToList();
        foreach (var p in cfg.Profiles)
            p.ApplyColor = false;
        cfg.Presets = null;
        cfg.Defaults.Resolution = CmbDefaultRes.SelectedItem?.ToString();
        cfg.Defaults.PowerPlan = (CmbDefaultPower.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                                 ?? "balanced";
        cfg.Defaults.Color = ColorSettings.Neutral;

        if (cfg.FactoryDefaults == null || string.IsNullOrWhiteSpace(cfg.FactoryDefaults.Resolution))
            cfg.FactoryDefaults = ConfigService.CaptureFactoryDefaults();
        cfg.FactoryDefaults.Color = ColorSettings.Neutral;

        cfg.GlobalHotkeys ??= new GlobalHotkeys();
        if (HkToggleOverlay != null)
            cfg.GlobalHotkeys.ToggleOverlay = NormHotkey(HkToggleOverlay.Text);
        if (HkEmergency != null)
            cfg.GlobalHotkeys.EmergencyRestore = NormHotkey(HkEmergency.Text);
        if (HkNextPreset != null)
            cfg.GlobalHotkeys.NextPreset = NormHotkey(HkNextPreset.Text);
        if (HkPrevPreset != null)
            cfg.GlobalHotkeys.PreviousPreset = NormHotkey(HkPrevPreset.Text);
        if (HkCompareAb != null)
            cfg.GlobalHotkeys.CompareAb = NormHotkey(HkCompareAb.Text);
        if (HkToggleZoom != null)
            cfg.GlobalHotkeys.ToggleZoom = NormHotkey(HkToggleZoom.Text);
        cfg.Ui ??= new UiPreferences();
        cfg.Ui.ZoomFactor = ReadZoomFactor();
        App.Services.Zoom.ApplyFromConfig(cfg.Ui);

        cfg.ConfigVersion = ConfigService.CurrentVersion;

        App.Services.Config.Save(cfg, raiseChanged: false);
        App.Services.Hotkeys.RegisterFromConfig(
            cfg,
            App.Services.Monitor.CurrentProfile,
            HotkeyPresetFallback);
        App.Services.Monitor.NotifyConfigSaved();
        _dirty = false;
        if (LblUnsaved != null) LblUnsaved.Visibility = Visibility.Collapsed;
        UpdateTitleDirtyState();

        if (showBusy)
            UiSound.Save();

        if (showBusy)
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exe))
                AutostartService.SetEnabled(cfg.StartWithWindows, exe!);

            AppLog.Info("Configuration saved from UI.");
            SaveBusy.ShowSaved();
            SetStatus("Saved");
            ShowToast(Loc.T("toast.saved"));
        }
        else
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exe))
                AutostartService.SetEnabled(cfg.StartWithWindows, exe!);
        }
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
            MarkDirty();
            ProfileList.SelectedItem = _profiles.Last();
            ProfileList.Items.Refresh();
            PresetGameList.Items.Refresh();
            UiSound.Done();
        }
        else
        {
            UiSound.Empty();
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
        App.Services.Config.Save(cfg, raiseChanged: false);

        _suppressEditorEvents = true;
        CmbDefaultRes.SelectedItem = factory.Resolution;
        SelectComboByTag(CmbDefaultPower, factory.PowerPlan ?? "balanced");
        _suppressEditorEvents = false;

        App.Services.Monitor.EmergencyRestore();
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
        MarkDirty();
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
        MarkDirty();
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
        MarkDirty();
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
            MarkDirty();
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
            MarkDirty();
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
            MarkDirty();
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
            MarkDirty();
        }
    }

    private void RemoveCompanion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        if (CompanionList.SelectedItem is not CompanionApp c) return;
        _selected.Companions.Remove(c);
        CompanionList.ItemsSource = null;
        CompanionList.ItemsSource = _selected.Companions;
        MarkDirty();
    }

    // -- Presets (per-game) ---------------------------------------------------

    private GameProfile? _presetGame;

    private void PresetGameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEditorEvents) return;

        if (_selectedPreset != null) PushPresetEditor();
        SyncPresetsBackToGame();

        _suppressEditorEvents = true;
        try
        {
            // Null selection BEFORE Clear — otherwise SelectionChanged Push/Sync wipes presets.
            _selectedPreset = null;
            _presetGame = PresetGameList.SelectedItem as GameProfile;
            _presets.Clear();

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
        finally
        {
            _suppressEditorEvents = false;
        }

        RefreshHotkeyBindings();
        UpdatePresetEmptyCta();
    }

    private void SyncPresetsBackToGame()
    {
        if (_presetGame == null) return;
        // Never overwrite a non-empty profile with an empty UI list during list rebuild races.
        if (_presets.Count == 0
            && _presetGame.Presets != null
            && _presetGame.Presets.Count > 0
            && _selectedPreset == null)
            return;

        _presetGame.Presets = _presets.ToList();
        if (_selected != null && _selected.Id == _presetGame.Id)
            FillStartupPresetCombo(_selected, _selected.StartupPresetId);
    }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEditorEvents) return;

        var next = PresetList.SelectedItem as QuickPreset;
        // Items.Refresh() can re-fire SelectionChanged for the same item and wipe typing.
        if (next != null && _selectedPreset != null && next.Id == _selectedPreset.Id)
            return;

        if (_selectedPreset != null) PushPresetEditor();
        _selectedPreset = next;
        LoadPresetEditor(_selectedPreset);
    }

    private void LoadPresetEditor(QuickPreset? p)
    {
        _suppressEditorEvents = true;
        try
        {
            if (p == null)
            {
                TxtPresetName.Text = "";
                TxtPresetHotkey.Text = "";
                return;
            }

            TxtPresetName.Text = p.Name;
            TxtPresetHotkey.Text = p.Hotkey ?? "";
            ChkPresetRes.IsChecked = p.ApplyResolution;
            p.EnsureDualColorSlots();
            _presetEditorBackend = p.Color.Backend;
            SetBackendToggle(p.Color.Backend, TogPresetBackend);
            ChkPresetLock.IsChecked = p.Color.LockColor;
            if (!string.IsNullOrWhiteSpace(p.Resolution) && CmbPresetRes.Items.Contains(p.Resolution))
                CmbPresetRes.SelectedItem = p.Resolution;
            else if (CmbPresetRes.Items.Count > 0)
                CmbPresetRes.SelectedIndex = 0;
            ColorUiHelper.ApplyColorSliders(p.Color, SldPresetB, SldPresetC, SldPresetG, SldPresetV, SldPresetShadow, SldPresetH);
            UpdatePresetLabels();
            UpdatePresetShadowEnabled();
            UpdateBackendActiveLabel(TogPresetBackend, p.Color.Backend == ColorBackend.LowLevel);
        }
        finally
        {
            _suppressEditorEvents = false;
        }
    }

    /// <summary>Name typing must not Refresh the list (that reloads the TextBox and eats characters).</summary>
    private void PresetName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEditorEvents || _selectedPreset == null) return;
        _selectedPreset.Name = TxtPresetName.Text;
        MarkDirty();
    }

    private void PresetName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_selectedPreset == null) return;
        _selectedPreset.Name = TxtPresetName.Text.Trim();
        if (TxtPresetName.Text != _selectedPreset.Name)
        {
            _suppressEditorEvents = true;
            TxtPresetName.Text = _selectedPreset.Name;
            _suppressEditorEvents = false;
        }
        RefreshPresetListSafe();
        MarkDirty();
    }

    private void PresetEditorChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEditorEvents || _selectedPreset == null) return;
        MarkDirty();
        PushPresetEditor();
        RefreshPresetListSafe();
    }

    private void PresetColor_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEditorEvents) return;
        UpdatePresetLabels();
        if (_selectedPreset == null) return;
        _selectedPreset.ApplyColor = true;
        MarkDirty();
        PushPresetEditor();
        // Don't refresh list on every slider tick — name/summary flicker isn't needed.
    }

    private void RefreshPresetListSafe()
    {
        var keepId = _selectedPreset?.Id;
        _suppressEditorEvents = true;
        PresetList.Items.Refresh();
        if (keepId != null)
        {
            var match = _presets.FirstOrDefault(p => p.Id == keepId);
            if (match != null && !ReferenceEquals(PresetList.SelectedItem, match))
                PresetList.SelectedItem = match;
        }
        _suppressEditorEvents = false;
        UpdatePresetEmptyCta();
    }

    private void UpdatePresetEmptyCta()
    {
        if (PresetEmptyCta == null) return;
        bool empty = _presets.Count == 0 && PresetGameList.SelectedItem != null;
        PresetEmptyCta.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (LblPresetEmpty != null) LblPresetEmpty.Text = Loc.T("presets.empty");
        if (BtnPresetEmptyGallery != null) BtnPresetEmptyGallery.Content = Loc.T("presets.empty.gallery");
    }

    private void UpdatePresetLabels()
    {
        if (LblPresetB == null) return;
        var tmp = new ColorSettings
        {
            Brightness = SldPresetB.Value,
            Contrast = SldPresetC.Value,
            Gamma = SldPresetG.Value,
            Backend = ReadBackendToggle(TogPresetBackend)
        };
        string label = GpuVendorDetect.DriverLabel;
        if (tmp.Backend == ColorBackend.LowLevel)
        {
            var (b, c, g) = tmp.ToRivaTunerUnits();
            LblPresetB.Text = $"Brightness: {b}  (RT)";
            LblPresetC.Text = $"Contrast: {c}  (RT)";
            LblPresetG.Text = $"Gamma: {g:F2}  (RT)";
        }
        else
        {
            LblPresetB.Text = $"Brightness: {(int)Math.Round(tmp.Brightness * 100)}%  ({label} CP)";
            LblPresetC.Text = $"Contrast: {(int)Math.Round(Math.Clamp(tmp.Contrast / 2.0, 0, 1) * 100)}%  ({label} CP)";
            LblPresetG.Text = $"Gamma: {Math.Clamp(tmp.Gamma, 0.4, 2.8):F2}  ({label} CP)";
        }
        if (LblPresetV != null)
        {
            LblPresetV.Text = tmp.Backend == ColorBackend.LowLevel
                ? $"Vibrance: {(int)SldPresetV.Value}"
                : $"Digital vibrance: {(int)SldPresetV.Value}%  ({label} CP)";
        }
        if (LblPresetShadow != null) LblPresetShadow.Text = $"Shadow Boost: {(int)Math.Round(SldPresetShadow.Value)}";
        if (LblPresetH != null && SldPresetH != null) LblPresetH.Text = $"Hue: {(int)Math.Round(SldPresetH.Value)}°";
    }

    private void PushPresetEditor()
    {
        if (_selectedPreset == null) return;
        // Keep live name from TextBox (trim only on LostFocus) so mid-type spaces aren't eaten.
        if (!ReferenceEquals(Keyboard.FocusedElement, TxtPresetName))
            _selectedPreset.Name = TxtPresetName.Text.Trim();
        else
            _selectedPreset.Name = TxtPresetName.Text;
        _selectedPreset.Hotkey = string.IsNullOrWhiteSpace(TxtPresetHotkey.Text) ? null : TxtPresetHotkey.Text.Trim();
        _selectedPreset.ApplyResolution = ChkPresetRes.IsChecked == true;
        // Keep ApplyColor as-is (res-only presets stay false until color sliders change).
        _selectedPreset.Resolution = CmbPresetRes.SelectedItem?.ToString();

        var c = ColorUiHelper.ReadColorFromSliders(
            ReadBackendToggle(TogPresetBackend),
            SldPresetB, SldPresetC, SldPresetG, SldPresetV, SldPresetShadow,
            ChkPresetLock.IsChecked == true, SldPresetH);
        // Guard against saving unloaded slider defaults (0/0/0/0) over a good preset.
        if (c.Brightness <= 0.02 && c.Contrast <= 0.02 && c.Vibrance == 0
            && _selectedPreset.Color != null
            && !(_selectedPreset.Color.Brightness <= 0.02 && _selectedPreset.Color.Contrast <= 0.02 && _selectedPreset.Color.Vibrance == 0))
        {
            c = _selectedPreset.Color.Clone();
            c.Backend = ReadBackendToggle(TogPresetBackend);
        }

        _selectedPreset.Color = c;
        _presetEditorBackend = _selectedPreset.Color.Backend;
        _selectedPreset.SaveActiveToDualSlots();
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
        p.EnsureDualColorSlots();
        _presets.Add(p);
        SyncPresetsBackToGame();
        PresetList.SelectedItem = p;
        MarkDirty();
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPreset == null) return;
        _presets.Remove(_selectedPreset);
        _selectedPreset = null;
        SyncPresetsBackToGame();
        MarkDirty();
    }

    private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        // Clear binding
        if (key is Key.Escape or Key.Back or Key.Delete)
        {
            box.Text = "";
            MarkDirty();
            if (box == TxtPresetHotkey && _selectedPreset != null)
            {
                PushPresetEditor();
                FlushAutosave();
            }
            return;
        }

        var gesture = HotkeyService.GestureFromKeys(Keyboard.Modifiers, key);
        ApplyCapturedGesture(box, gesture);
    }

    /// <summary>
    /// Captures mouse XButton1/XButton2 as hotkeys on the same textboxes as keyboard capture
    /// (see HotkeyService's WH_MOUSE_LL fallback for how these are recognized at runtime).
    /// </summary>
    private void HotkeyBox_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        if (e.ChangedButton is not (System.Windows.Input.MouseButton.XButton1 or System.Windows.Input.MouseButton.XButton2))
            return;

        var gesture = HotkeyService.GestureFromMouse(Keyboard.Modifiers, e.ChangedButton);
        if (gesture == null) return;

        e.Handled = true;
        box.Focus();
        ApplyCapturedGesture(box, gesture);
    }

    /// <summary>Shared conflict-check + assignment logic for keyboard and mouse hotkey capture.</summary>
    private void ApplyCapturedGesture(System.Windows.Controls.TextBox box, string gesture)
    {
        string? ignorePresetId = box == TxtPresetHotkey ? _selectedPreset?.Id : null;
        var conflict = HotkeyService.FindConflictInConfig(
            App.Services.Config.Current,
            gesture,
            ignorePresetId,
            box == TxtPresetHotkey ? (_presetGame ?? _selected) : null);
        if (!string.IsNullOrWhiteSpace(conflict))
            ShowToast(Loc.Tf("hotkey.conflict", conflict));

        if (box == TxtPresetHotkey)
        {
            box.Text = gesture;
            if (_selectedPreset != null)
            {
                MarkDirty();
                PushPresetEditor();
                FlushAutosave();
                RefreshHotkeyBindings();
            }
            else
            {
                MarkDirty();
            }
            return;
        }

        box.Text = gesture;
        MarkDirty();
        RefreshHotkeyBindings();
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: System.Windows.Controls.TextBox box })
            return;
        box.Text = "";
        MarkDirty();
        if (box == TxtPresetHotkey && _selectedPreset != null)
        {
            PushPresetEditor();
            FlushAutosave();
        }
    }

    private static string? NormHotkey(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void RefreshLog_Click(object sender, RoutedEventArgs e)
    {
        TxtLog.Text = string.Join(Environment.NewLine, AppLog.Tail(400));
        TxtLog.ScrollToEnd();
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = AppLog.LogFilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            else if (!string.IsNullOrWhiteSpace(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Open log");
            ShowToast(ex.Message);
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (App.Current is App app && !app.IsExitRequested)
        {
            FlushAutosave();

            if (App.Services.Config.Current.Ui?.MinimizeToTrayOnClose == false)
            {
                app.ExitApp();
                return;
            }
            e.Cancel = true;
            HideWithFade();
        }
    }

    /// <summary>
    /// Used by tray Exit. Autosave is always flushed (no discard prompt).
    /// </summary>
    public bool PromptUnsavedBeforeExit()
    {
        FlushAutosave();
        return true;
    }

    private bool TryResolveUnsavedChanges()
    {
        FlushAutosave();
        return true;
    }

    private void SetBackendToggle(ColorBackend backend, System.Windows.Controls.Primitives.ToggleButton tog)
    {
        string label = GpuVendorDetect.DriverLabel;
        tog.Content = label;
        // Checked = Low Level (right), Unchecked = GPU driver (left)
        tog.IsChecked = backend == ColorBackend.LowLevel;
        UpdateBackendActiveLabel(tog, backend == ColorBackend.LowLevel);
    }

    private void UpdateBackendActiveLabel(System.Windows.Controls.Primitives.ToggleButton tog, bool lowLevel)
    {
        string label = GpuVendorDetect.DriverLabel;
        string active = lowLevel
            ? "Active: Low Level (RivaTuner)"
            : $"Active: {label} (Control Panel)";

        if (ReferenceEquals(tog, TogPresetBackend) && LblPresetBackendActive != null)
            LblPresetBackendActive.Text = active;
    }

    private void FillAudioDevices()
    {
        if (CmbAudio == null) return;
        var prev = (CmbAudio.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        CmbAudio.Items.Clear();
        CmbAudio.Items.Add(new ComboBoxItem { Content = "(default / don't change)", Tag = "" });
        try
        {
            foreach (var d in AudioEndpoint.ListDevices())
                CmbAudio.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Enumerate audio devices");
        }
        SelectAudioDevice(prev);
    }

    private void SelectAudioDevice(string? id)
    {
        if (CmbAudio == null) return;
        if (string.IsNullOrWhiteSpace(id))
        {
            CmbAudio.SelectedIndex = 0;
            return;
        }
        foreach (var item in CmbAudio.Items)
        {
            if (item is ComboBoxItem ci && string.Equals(ci.Tag?.ToString(), id, StringComparison.OrdinalIgnoreCase))
            {
                CmbAudio.SelectedItem = ci;
                return;
            }
        }
        CmbAudio.SelectedIndex = 0;
    }

    private void ExportPresets_Click(object sender, RoutedEventArgs e)
    {
        if (_presetGame == null)
        {
            ThemedDialog.Show(this, "Select a game first.", "Presets");
            return;
        }
        SyncPresetsBackToGame();
        if (PresetPackService.Export(_presetGame))
            ShowToast("Preset pack exported");
    }

    private void ImportPresets_Click(object sender, RoutedEventArgs e)
    {
        if (_presetGame == null)
        {
            ThemedDialog.Show(this, "Select a game first.", "Presets");
            return;
        }
        int n = PresetPackService.Import(_presetGame);
        if (n <= 0) return;
        _suppressEditorEvents = true;
        try
        {
            _selectedPreset = null;
            _presets.Clear();
            foreach (var p in _presetGame.Presets)
                _presets.Add(p);
            PresetList.Items.Refresh();
            if (_presets.Count > 0)
                PresetList.SelectedIndex = 0;
            if (_selected != null && _selected.Id == _presetGame.Id)
                FillStartupPresetCombo(_selected);
        }
        finally
        {
            _suppressEditorEvents = false;
        }
        MarkDirty();
        FlushAutosave();
        ShowToast($"Imported {n} preset(s)");
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            ThemedDialog.Show(this, Loc.T("profile.pick"), Loc.T("profile.export"));
            return;
        }
        PushEditorToSelected();
        if (ProfilePackService.Export(_selected))
            ShowToast(Loc.T("toast.profile.exported"));
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var imported = ProfilePackService.Import();
        if (imported == null) return;
        GameVisuals.Apply(imported);
        _profiles.Add(imported);
        MarkDirty();
        FlushAutosave();
        ProfileList.SelectedItem = imported;
        ProfileList.Items.Refresh();
        PresetGameList.Items.Refresh();
        ShowToast(Loc.T("toast.profile.imported"));
    }

    private void PackGallery_Click(object sender, RoutedEventArgs e)
    {
        var win = new PackGalleryWindow { Owner = this };
        win.AcceptTargetGame((_presetGame ?? _selected)?.Name);
        win.ShowDialog();
    }

    /// <summary>Apply a built-in preset pack onto the currently selected Presets-tab game.</summary>
    public void ApplyPackPresets(IEnumerable<QuickPreset> presets, string packName)
    {
        if (_presetGame == null)
        {
            UiSound.Error();
            ThemedDialog.Show(this, Loc.T("presets.pick.game"), Loc.T("presets.gallery"));
            return;
        }

        SyncPresetsBackToGame();
        _presetGame.Presets ??= new List<QuickPreset>();
        foreach (var src in presets)
        {
            var clone = QuickPreset.CloneOf(src);
            clone.Id = _presetGame.Id + "_" + Guid.NewGuid().ToString("N")[..8];
            _presetGame.Presets.Add(clone);
        }

        _suppressEditorEvents = true;
        try
        {
            _presets.Clear();
            foreach (var p in _presetGame.Presets)
                _presets.Add(p);
            PresetList.Items.Refresh();
            if (_presets.Count > 0)
                PresetList.SelectedIndex = _presets.Count - 1;
            if (_selected != null && _selected.Id == _presetGame.Id)
                FillStartupPresetCombo(_selected);
        }
        finally
        {
            _suppressEditorEvents = false;
        }

        MarkDirty();
        FlushAutosave();
        ShowToast(Loc.Tf("toast.pack.applied", packName));
    }

    private void MonitorLayout_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEditorEvents || _selected == null) return;
        // IsolatePrimaryMonitor is derived from the CmbMonitorLayout tag inside PushEditorToSelected.
        MarkDirty();
        PushEditorToSelected();
    }

    private void SessionTemplateCompetitive_Click(object sender, RoutedEventArgs e)
        => ApplySessionTemplate(quiet: true, night: true, hdr: true, layout: "isolatePrimary", scaling: "stretch");

    private void SessionTemplateImmersive_Click(object sender, RoutedEventArgs e)
        => ApplySessionTemplate(quiet: true, night: true, hdr: false, layout: "keepAll", scaling: "default", bright: true, brightPct: 70);

    private void SessionTemplateMinimal_Click(object sender, RoutedEventArgs e)
        => ApplySessionTemplate(quiet: false, night: false, hdr: false, layout: "keepAll", scaling: "default");

    private void ApplySessionTemplate(bool quiet, bool night, bool hdr, string layout, string scaling,
        bool bright = false, int brightPct = 80)
    {
        if (_selected == null) return;
        _suppressEditorEvents = true;
        ChkQuietToast.IsChecked = quiet;
        ChkNoNightLight.IsChecked = night;
        ChkNoAutoHdr.IsChecked = hdr;
        SelectComboByTag(CmbMonitorLayout, layout);
        SelectComboByTag(CmbScaling, scaling);
        ChkMonBright.IsChecked = bright;
        if (bright) SldMonBright.Value = brightPct;
        SldDeferred.Value = 0;
        _suppressEditorEvents = false;
        UpdateSessionLabels();
        MarkDirty();
        PushEditorToSelected();
        ShowToast(Loc.T("toast.session.template"));
    }

    private static ColorBackend ReadBackendToggle(System.Windows.Controls.Primitives.ToggleButton tog) =>
        tog.IsChecked == true ? ColorBackend.LowLevel : ColorBackend.Driver;

    private static GameProfile CloneProfile(GameProfile p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Enabled = p.Enabled,
        ProcessName = p.ProcessName,
        ProcessAliases = (p.ProcessAliases ?? new List<string>()).ToList(),
        ExePath = p.ExePath,
        Resolution = p.Resolution,
        RefreshRate = p.RefreshRate,
        StartupPresetId = p.StartupPresetId,
        ApplyDelaySeconds = p.ApplyDelaySeconds,
        ApplyOnFocus = p.ApplyOnFocus,
        DisplayDevice = p.DisplayDevice,
        PowerPlan = p.PowerPlan,
        ApplyColor = p.ApplyColor,
        ApplyResolution = p.ApplyResolution,
        ApplyPowerPlan = p.ApplyPowerPlan,
        FpsLimit = p.FpsLimit,
        SoftRestoreOnAltTab = p.SoftRestoreOnAltTab,
        RestoreMode = p.RestoreMode,
        Color = p.Color.Clone(),
        ColorLowLevel = p.ColorLowLevel?.Clone(),
        ColorDriver = p.ColorDriver?.Clone(),
        Session = (p.Session ?? new SessionExtras()).Clone(),
        Companions = p.Companions.Select(c => new CompanionApp
        {
            Path = c.Path,
            Arguments = c.Arguments,
            LaunchMode = c.LaunchMode,
            TaskName = c.TaskName,
            OnStop = c.OnStop,
            StopHotkey = c.StopHotkey,
            DismissDialogs = c.DismissDialogs,
            MinimizeToTray = c.MinimizeToTray
        }).ToList(),
        Presets = (p.Presets ?? new List<QuickPreset>()).Select(QuickPreset.CloneOf).ToList()
    };

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

    private void SelectZoomFactor(int factor)
    {
        if (CmbZoomFactor == null) return;
        SelectComboByTag(CmbZoomFactor, factor.ToString());
    }

    private int ReadZoomFactor()
    {
        if (CmbZoomFactor?.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out int f)
            && f >= 2 && f <= 12)
            return f;
        return 4;
    }
}


