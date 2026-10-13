using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Visor.Models;
using Visor.Services;

namespace Visor;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20 = 19;

    private readonly ObservableCollection<GameProfile> _profiles = new();
    private readonly ObservableCollection<QuickPreset> _presets = new();
    /// <summary>Live, filterable view used by the Library list only (Play / Presets lists stay unfiltered).</summary>
    private readonly ListCollectionView _libraryView;
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer _tileRefreshTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _presetListRefreshTimer;

    private GameProfile? _selected;
    private GameProfile? _presetGame;
    private QuickPreset? _selectedPreset;
    private ColorBackend _presetEditorBackend = ColorBackend.LowLevel;
    /// <summary>
    /// True only while the Presets-tab editor holds changes not yet pushed into _selectedPreset.
    /// Guards against a stale editor overwriting edits made elsewhere (Play sliders) on autosave.
    /// </summary>
    private bool _presetEditorDirty;
    private bool _dirty;
    private int _lastTabIndex;
    private string? _lastActiveProfileId;

    private GameProfile? _playProfile;
    private QuickPreset? _playPreset;
    private ColorBackend _playBackend = ColorBackend.LowLevel;
    private bool _previewOnDesktop;
    /// <summary>User picked the "Live" chip (one-off color, not saved to a preset).</summary>
    private bool _playLive;
    private bool _suppressGameListSync;

    private Action? _toastAction;
    private bool _restoredPosition;
    private bool _restoreMaximized;
    private readonly Dictionary<System.Windows.Controls.TextBox, string> _hotkeyOriginal = new();

    // -- Re-entrant suppression scopes (replaces the old boolean that nested calls used to reset) ----------

    private int _editorSuppress;
    private int _playSuppress;
    private bool SuppressingEditor => _editorSuppress > 0;
    private bool SuppressingPlay => _playSuppress > 0;

    private sealed class Scope : IDisposable
    {
        private Action? _onDispose;
        public Scope(Action onDispose) => _onDispose = onDispose;
        public void Dispose()
        {
            _onDispose?.Invoke();
            _onDispose = null;
        }
    }

    /// <summary>Programmatic editor updates inside this scope don't fire change handlers. Nesting is safe.</summary>
    private IDisposable SuppressEditor()
    {
        _editorSuppress++;
        return new Scope(() => _editorSuppress--);
    }

    private IDisposable SuppressPlay()
    {
        _playSuppress++;
        return new Scope(() => _playSuppress--);
    }

    public MainWindow()
    {
        _editorSuppress = 1; // InitializeComponent fires Checked/SelectionChanged events
        InitializeComponent();
        _libraryView = new ListCollectionView(_profiles) { Filter = LibraryFilter };

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            if (!_dirty) return;
            // Always push editors first — otherwise dual-slot / slider state can save as 0/0/0/0.
            FlushAutosave();
        };
        _tileRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _tileRefreshTimer.Tick += (_, _) =>
        {
            _tileRefreshTimer.Stop();
            RefreshGameTiles();
        };
        _presetListRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _presetListRefreshTimer.Tick += (_, _) =>
        {
            _presetListRefreshTimer.Stop();
            RefreshPresetListSafe();
        };
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusText.Text = "";
        };

        ProfileList.ItemsSource = _libraryView;
        PlayGameList.ItemsSource = _profiles;
        PresetGameList.ItemsSource = _profiles;
        PresetList.ItemsSource = _presets;
        TitleLogo.Source = AssetLoader.AppIcon(32);

        ApplyInitialBounds();
        LoadResolutions();
        FillDisplays();
        FillAudioDevices();
        _editorSuppress = 0;

        ReloadFromConfig();
        ApplyLocalization();
        UpdateActiveHeader(App.Services.Monitor.CurrentProfile);
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
            if (_restoredPosition && !OverlayWin32.IsReasonablyOnScreen(this))
                CenterOnPrimaryWorkArea();
        };
    }

    // -- Window chrome / sizing -----------------------------------------------------------------

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
        // Only adjust the maximized rectangle (WindowStyle=None would cover the taskbar).
        // Leave handled=false so WPF still applies MinWidth/MinHeight in its own WM_GETMINMAXINFO handler.
        if (msg == WmGetMinMaxInfo)
            ClampMaximizedToWorkArea(hwnd, lParam);
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

    /// <summary>
    /// Default size fits 1366×768 @125% and 1080p @150% (DIP work areas ~1093×576 and 1280×688);
    /// a remembered size/position is clamped to the current work area.
    /// </summary>
    private void ApplyInitialBounds()
    {
        var wa = SystemParameters.WorkArea;
        MinWidth = Math.Max(640, Math.Min(860, wa.Width - 16));
        MinHeight = Math.Max(420, Math.Min(520, wa.Height - 16));
        Width = Math.Max(MinWidth, Math.Min(1080, wa.Width - 48));
        Height = Math.Max(MinHeight, Math.Min(720, wa.Height - 48));

        var st = UiStateStore.Current;
        if (st.WindowWidth is double sw && st.WindowHeight is double sh && sw > 100 && sh > 100)
        {
            Width = Math.Clamp(sw, MinWidth, Math.Max(MinWidth, wa.Width));
            Height = Math.Clamp(sh, MinHeight, Math.Max(MinHeight, wa.Height));
        }
        if (st.WindowLeft is double l && st.WindowTop is double t && !double.IsNaN(l) && !double.IsNaN(t))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
            _restoredPosition = true;
        }
        _restoreMaximized = st.WindowMaximized;
    }

    private void CenterOnPrimaryWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width);
        Height = Math.Min(Height, wa.Height);
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
    }

    private void SaveWindowBounds()
    {
        try
        {
            var st = UiStateStore.Current;
            var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (r.IsEmpty || double.IsNaN(r.Left) || r.Width < 100) return;
            st.WindowLeft = r.Left;
            st.WindowTop = r.Top;
            st.WindowWidth = r.Width;
            st.WindowHeight = r.Height;
            st.WindowMaximized = WindowState == WindowState.Maximized;
            UiStateStore.Save();
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Save window bounds");
        }
    }

    /// <summary>Dark / light DWM caption to match the theme (affects Alt-Tab thumbnail and resize frame).</summary>
    private void ApplyChromeTheme()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int useDark = ThemeService.IsDarkTheme(App.Services.Config.Current.Ui) ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20, ref useDark, sizeof(int));
        }
        catch { /* older Windows */ }
    }

    public void ShowWithFade()
    {
        // Tray start / overlay hide the taskbar button — bring it back whenever the window is shown.
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized || _restoreMaximized)
            WindowState = _restoreMaximized ? WindowState.Maximized : WindowState.Normal;
        _restoreMaximized = false;
        Activate();
        UiMotion.PopIn(ChromeRoot);
        UiMotion.FadeSlideIn(ContentRoot, fromY: 8, ms: 200);
    }

    public void HideWithFade()
    {
        StopDesktopPreview();
        SaveWindowBounds();
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

        ApplyChromeTheme();

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
        "BtnEmergency", "BtnScan", "BtnLaunchGame", "BtnPlayLaunch",
        "BtnApplyProfileNow", "BtnApplyPreset", "BtnApplyDefaults"
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

    private void NakidevCredit_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://t.me/nakidev",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (App.Current is App app && !app.IsExitRequested)
        {
            FlushAutosave();
            SaveWindowBounds();

            if (App.Services.Config.Current.Ui?.MinimizeToTrayOnClose == false)
            {
                app.ExitApp();
                return;
            }
            e.Cancel = true;

            // First close: explain once that Visor stays in the tray (closing used to look like quitting).
            var st = UiStateStore.Current;
            if (!st.TrayNoticeShown)
            {
                st.TrayNoticeShown = true;
                UiStateStore.Save();
                ThemedDialog.Show(this, Loc.T("tray.notice"), Loc.T("tray.notice.title"));
            }
            HideWithFade();
        }
    }

    /// <summary>Used by tray Exit. Autosave is always flushed (no discard prompt).</summary>
    public bool PromptUnsavedBeforeExit()
    {
        StopDesktopPreview();
        FlushAutosave();
        SaveWindowBounds();
        return true;
    }

    // -- Keyboard shortcuts ----------------------------------------------------------------------

    private bool IsHotkeyCaptureFocused() =>
        Keyboard.FocusedElement is System.Windows.Controls.TextBox tb && IsHotkeyBox(tb);

    private bool IsHotkeyBox(System.Windows.Controls.TextBox tb) =>
        tb == TxtPresetHotkey || GlobalHotkeyBoxes().Any(b => b.Box == tb);

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (IsHotkeyCaptureFocused()) return; // let the capture box see every key (incl. Ctrl+1)

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            TabItem? target = e.Key switch
            {
                Key.D1 or Key.NumPad1 => TabPlay,
                Key.D2 or Key.NumPad2 => TabProfiles,
                Key.D3 or Key.NumPad3 => TabPresets,
                Key.D4 or Key.NumPad4 => TabMore,
                _ => null
            };
            if (target != null)
            {
                MainTabs.SelectedItem = target;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F)
            {
                MainTabs.SelectedItem = TabProfiles;
                Dispatcher.BeginInvoke(() =>
                {
                    TxtSearch.Focus();
                    TxtSearch.SelectAll();
                }, DispatcherPriority.Input);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Escape && ReferenceEquals(Keyboard.FocusedElement, TxtSearch) && TxtSearch.Text.Length > 0)
        {
            TxtSearch.Text = "";
            e.Handled = true;
        }
    }

    /// <summary>Del removes the selected item of the focused list (with confirmation).</summary>
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Keyboard.Modifiers != ModifierKeys.None) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;

        if (ProfileList.IsKeyboardFocusWithin && _selected != null)
        {
            DeleteProfile_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (PresetList.IsKeyboardFocusWithin && _selectedPreset != null)
        {
            DeletePreset_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (CompanionList.IsKeyboardFocusWithin && CompanionList.SelectedItem != null)
        {
            RemoveCompanion_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    // -- Tabs --------------------------------------------------------------------------------------

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs) return;

        int newIndex = MainTabs.SelectedIndex;
        if (newIndex == _lastTabIndex)
        {
            if (MainTabs.SelectedContent is FrameworkElement feSame)
                UiMotion.SoftContentIn(feSame);
            return;
        }

        if (_dirty)
            FlushAutosave();

        if (!ReferenceEquals(MainTabs.SelectedItem, TabPlay))
            StopDesktopPreview();

        _lastTabIndex = newIndex;
        if (ReferenceEquals(MainTabs.SelectedItem, TabPresets))
        {
            FocusPresetsForActiveOrSelectedGame(_selected);
            // Play sliders may have edited the same preset object — show fresh values.
            LoadPresetEditor(_selectedPreset);
        }
        if (ReferenceEquals(MainTabs.SelectedItem, TabPlay))
            RefreshPlayTab();
        if (ReferenceEquals(MainTabs.SelectedItem, TabMore) && RbMoreLog.IsChecked == true)
            RefreshLog();
        if (MainTabs.SelectedContent is FrameworkElement fe)
            UiMotion.SoftContentIn(fe);
    }

    private void MainTabs_ProfilesHeaderDown(object sender, MouseButtonEventArgs e)
    {
        // Count only clicks on the Library tab header (TabPanel), never content area
        if (e.OriginalSource is not DependencyObject d) return;
        if (FindAncestor<TabPanel>(d) == null) return;
        if (ReferenceEquals(FindAncestor<TabItem>(d), TabProfiles))
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

    private void MoreSection_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelDefaults == null || PanelLog == null) return;
        bool log = RbMoreLog.IsChecked == true;
        PanelDefaults.Visibility = log ? Visibility.Collapsed : Visibility.Visible;
        PanelLog.Visibility = log ? Visibility.Visible : Visibility.Collapsed;
        if (log) RefreshLog();
    }

    // -- Settings / localization -------------------------------------------------------------------

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        FlushAutosave();
        var win = new SettingsWindow { Owner = this };
        win.ShowDialog();
        // Live preview in Settings may have changed runtime sound state — restore from saved config if cancelled
        UiSound.ApplyFromConfig(App.Services.Config.Current.Ui);
        ApplyLocalization();
        ApplyChromeTheme();
        UpdateActiveHeader(App.Services.Monitor.CurrentProfile);
        if (win.Imported)
            ReloadFromConfig();
        if (System.Windows.Application.Current is App app)
            app.RebuildTrayMenu();
    }

    public void ApplyLocalization()
    {
        UpdateTitleDirtyState();
        TabPlay.Header = Loc.T("tab.play");
        TabProfiles.Header = Loc.T("tab.profiles");
        TabPresets.Header = Loc.T("tab.presets");
        TabMore.Header = Loc.T("tab.more");
        TabPlay.ToolTip = "Ctrl+1";
        TabProfiles.ToolTip = "Ctrl+2";
        TabPresets.ToolTip = "Ctrl+3";
        TabMore.ToolTip = "Ctrl+4";

        BtnEmergency.Content = Loc.T("btn.emergency");
        BtnEmergency.ToolTip = Loc.T("btn.emergency.tip");
        TxtSettingsCaption.Text = Loc.T("btn.settings");
        BtnSettingsCaption.ToolTip = Loc.T("btn.settings");
        BtnMinimize.ToolTip = Loc.T("caption.minimize");
        BtnMaximize.ToolTip = Loc.T("caption.maximize");
        BtnCloseCaption.ToolTip = Loc.T("caption.close");
        TxtShortcuts.Text = Loc.T("shortcuts.footer");
        TxtCredit.ToolTip = Loc.T("about.contacts");

        // Play
        LblPlayTitle.Text = Loc.T("play.title");
        LblPlayColor.Text = Loc.T("play.color");
        LblPlayPresets.Text = Loc.T("play.presets");
        ChkPlayPreview.Content = Loc.T("play.preview");
        ChkPlayPreview.ToolTip = Loc.T("play.preview.tip");
        ExpPlayHotkeys.Header = Loc.T("play.hotkeys");
        BtnPlayLaunch.Content = Loc.T("btn.launchGame");
        BtnPlayLaunch.ToolTip = Loc.T("btn.launchGame.tip");
        BtnPlayOverlay.Content = Loc.T("btn.overlay");
        BtnPlayOverlay.ToolTip = Loc.T("btn.overlay.tip");
        BtnPlayGameSettings.Content = Loc.T("play.gameSettings");
        LblPlayEmpty.Text = Loc.T("play.empty");
        BtnPlayEmptyScan.Content = Loc.T("btn.scanGames");
        ColorUiHelper.ApplyLabelNames(LblPlayB, LblPlayC, LblPlayG, LblPlayV, LblPlayH, LblPlayS);
        LblPlayH.ToolTip = Loc.T("color.hue.tip");
        LblPlayS.ToolTip = Loc.T("color.shadow.tip");
        LblPlayV.ToolTip = Loc.T("color.vibrance.tip");

        // Library
        TxtSearch.Tag = Loc.T("search.placeholder");
        TxtSearch.ToolTip = Loc.T("search.tip");
        BtnScan.Content = Loc.T("btn.scan");
        BtnScan.ToolTip = Loc.T("btn.scan.tip");
        BtnAddManual.Content = Loc.T("btn.addManual");
        BtnAddManual.ToolTip = Loc.T("btn.addManual.tip");
        BtnRestore.Content = Loc.T("btn.restore");
        BtnRestore.ToolTip = Loc.T("btn.restore.tip");
        BtnDup.Content = Loc.T("btn.dup");
        BtnDup.ToolTip = Loc.T("btn.dup.tip");
        BtnDelete.Content = Loc.T("btn.delete");
        BtnDelete.ToolTip = Loc.T("btn.delete.tip");
        BtnExportProfile.Content = Loc.T("profile.export");
        BtnExportProfile.ToolTip = Loc.T("profile.export.tip");
        BtnImportProfile.Content = Loc.T("profile.import");
        BtnImportProfile.ToolTip = Loc.T("profile.import.tip");
        LblProfileTitle.Text = Loc.T("profile.title");
        ChkEnabled.Content = Loc.T("profile.enabled");
        ChkEnabled.ToolTip = Loc.T("profile.enabled.tip");
        LblName.Text = Loc.T("profile.name");
        LblProcess.Text = Loc.T("profile.process");
        BtnBrowseExe.Content = Loc.T("btn.browse");
        BtnBrowseExe.ToolTip = Loc.T("btn.browse.tip");
        BtnPickRunning.Content = Loc.T("btn.running");
        BtnPickRunning.ToolTip = Loc.T("btn.running.tip");
        BtnLaunchGame.Content = Loc.T("btn.launchGame");
        BtnLaunchGame.ToolTip = Loc.T("btn.launchGame.tip");
        BtnApplyProfileNow.Content = Loc.T("btn.applyNow");
        BtnApplyProfileNow.ToolTip = Loc.T("profile.applyNow.tip");
        BtnOverlay.Content = Loc.T("btn.overlay");
        BtnOverlay.ToolTip = Loc.T("btn.overlay.tip");
        LblDisplaySection.Text = Loc.T("display");
        ChkApplyRes.Content = Loc.T("display.changeRes");
        LblResolution.Text = Loc.T("display.resolution");
        LblRefreshRate.Text = Loc.T("display.refresh");
        LblDisplay.Text = Loc.T("display.monitor");
        ChkApplyPower.Content = Loc.T("display.changePower");
        ItmPowerHigh.Content = Loc.T("power.high");
        ItmPowerBalanced.Content = Loc.T("power.balanced");
        LblPresetsSection.Text = Loc.T("tab.presets");
        BtnOpenPresets.Content = Loc.T("btn.openPresets");
        LblStartupPreset.Text = Loc.T("profile.startup");
        ExpAdvanced.Header = Loc.T("profile.advanced");
        LblAliases.Text = Loc.T("profile.aliases");
        TxtAliases.ToolTip = Loc.T("profile.aliases.tip");
        ChkApplyOnFocus.Content = Loc.T("profile.focus");
        ChkSoftAltTab.Content = Loc.T("profile.softAltTab");
        LblFpsLimit.Text = Loc.T("profile.fpsLimit");
        ItmFpsOff.Content = Loc.T("fps.off");
        LblRestoreMode.Text = Loc.T("restore.mode.lbl");
        ItmRestoreSnapshot.Content = Loc.T("restore.mode.snapshot");
        ItmRestoreDefaults.Content = Loc.T("restore.mode.defaults");
        ItmRestoreNothing.Content = Loc.T("restore.mode.nothing");
        ExpSession.Header = Loc.T("session.title");
        LblSessionSub.Text = Loc.T("session.sub");
        LblSessionTemplates.Text = Loc.T("session.templates");
        BtnTplCompetitive.Content = Loc.T("session.tpl.competitive");
        BtnTplImmersive.Content = Loc.T("session.tpl.immersive");
        BtnTplMinimal.Content = Loc.T("session.tpl.minimal");
        BtnTplCompetitive.ToolTip = Loc.T("session.tpl.competitive.tip");
        BtnTplImmersive.ToolTip = Loc.T("session.tpl.immersive.tip");
        BtnTplMinimal.ToolTip = Loc.T("session.tpl.minimal.tip");
        LblMonitorLayout.Text = Loc.T("session.layout");
        ItmLayoutKeep.Content = Loc.T("session.layout.keepAll");
        ItmLayoutIsolate.Content = Loc.T("session.layout.isolate");
        ItmLayoutPrimary.Content = Loc.T("session.layout.primaryOnly");
        ChkQuietToast.Content = Loc.T("session.quiet");
        ChkNoAutoHdr.Content = Loc.T("session.hdr");
        ChkMonBright.Content = Loc.T("session.bright");
        ChkSwitchAudio.Content = Loc.T("session.audio");
        LblScaling.Text = Loc.T("session.scaling");
        ItmScaleDefault.Content = Loc.T("session.scaling.default");
        ItmScaleStretch.Content = Loc.T("session.scaling.stretch");
        ItmScaleCenter.Content = Loc.T("session.scaling.center");
        LblCompanions.Text = Loc.T("companions.title");
        LblCompanionsEmpty.Text = Loc.T("companions.empty");
        BtnCompanionAdd.Content = Loc.T("btn.add");
        BtnCompanionEdit.Content = Loc.T("btn.edit");
        BtnCompanionRemove.Content = Loc.T("btn.remove");
        LblEditorEmpty.Text = Loc.T("library.pick");

        // Presets
        LblPresetGamePick.Text = Loc.T("presets.game");
        BtnPresetAdd.Content = Loc.T("btn.add");
        BtnPresetAdd.ToolTip = Loc.T("presets.add.tip");
        BtnPresetDelete.Content = Loc.T("btn.delete");
        BtnPresetExport.Content = Loc.T("presets.export");
        BtnPresetImport.Content = Loc.T("presets.import");
        BtnPackGallery.Content = Loc.T("presets.gallery");
        BtnPackGallery.ToolTip = Loc.T("presets.gallery.hint");
        LblPresetEditorTitle.Text = Loc.T("preset.title");
        BtnApplyPreset.Content = Loc.T("btn.applyNow");
        BtnApplyPreset.ToolTip = Loc.T("preset.applyNow.tip");
        LblPresetName.Text = Loc.T("preset.name");
        LblPresetHotkey.Text = Loc.T("preset.hotkey");
        LblPresetHotkeyHint.Text = Loc.T("preset.hotkey.hint");
        BtnPresetHotkeyClear.Content = Loc.T("btn.clear");
        ChkPresetRes.Content = Loc.T("preset.setRes");
        LblPresetColor.Text = Loc.T("preset.color");
        LblPresetColorHint.Text = Loc.T("preset.color.hint");
        LblPresetBackend.Text = Loc.T("color.backend");
        ChkPresetLock.Content = Loc.T("color.lock");
        ColorUiHelper.ApplyLabelNames(LblPresetB, LblPresetC, LblPresetG, LblPresetV, LblPresetH, LblPresetShadow);
        LblPresetH.ToolTip = Loc.T("color.hue.tip");
        LblPresetShadow.ToolTip = Loc.T("color.shadow.tip");
        LblPresetV.ToolTip = Loc.T("color.vibrance.tip");
        LblPresetB.ToolTip = Loc.T("color.brightness.tip");
        ColorUiHelper.LabelBackendToggle(TogPresetBackend);

        // More
        RbMoreDefaults.Content = Loc.T("tab.global");
        RbMoreLog.Content = Loc.T("tab.log");
        LblDefaultsTitle.Text = Loc.T("defaults.title");
        LblDefaultsHint.Text = Loc.T("defaults.hint");
        BtnApplyDefaults.Content = Loc.T("btn.applyNow");
        BtnApplyDefaults.ToolTip = Loc.T("defaults.applyNow.tip");
        LblDefaultRes.Text = Loc.T("display.resolution");
        LblDefaultPower.Text = Loc.T("defaults.power");
        ItmDefPowerKeep.Content = Loc.T("power.keep");
        ItmDefPowerBalanced.Content = Loc.T("power.balanced");
        ItmDefPowerHigh.Content = Loc.T("power.high");
        LblAppHotkeys.Text = Loc.T("hotkey.app");
        LblAppHotkeysHint.Text = Loc.T("hotkey.hint");
        LblHkOverlay.Text = Loc.T("hotkey.overlay");
        LblHkEmergency.Text = Loc.T("hotkey.emergency");
        LblHkNext.Text = Loc.T("hotkey.next");
        LblHkPrev.Text = Loc.T("hotkey.prev");
        LblHkAb.Text = Loc.T("hotkey.compareAb");
        LblHkZoom.Text = Loc.T("hotkey.zoom");
        LblHkZoom.ToolTip = Loc.T("zoom.hint");
        foreach (var b in new[] { BtnClearHk0, BtnClearHk1, BtnClearHk2, BtnClearHk3, BtnClearHk4, BtnClearHk5 })
            b.Content = Loc.T("btn.clear");
        LblZoomFactor.Text = Loc.T("zoom.factor");
        LblZoomHint.Text = Loc.T("zoom.hint");
        LblHotkeyCheat.Text = Loc.T("hotkey.cheatsheet");
        LblResetGlobalHint.Text = Loc.T("btn.reset.global.hint");
        BtnReset.Content = Loc.T("btn.reset.global");
        BtnLogRefresh.Content = Loc.T("log.refresh");
        BtnLogFolder.Content = Loc.T("log.openFolder");
        ChkLogWrap.Content = Loc.T("log.wrap");

        foreach (var (box, _) in GlobalHotkeyBoxes())
            box.Tag = Loc.T("hotkey.placeholder");
        TxtPresetHotkey.Tag = Loc.T("hotkey.placeholder");

        ApplyInfoTips();
        UpdateSessionLabels();
        UpdateFpsLimitAvailability();
        UpdateDriverUiAvailability();
        FillDisplays(keepSelection: true);
        FillAudioDevices();
        RefreshRefreshRateCombo();
        if (_selected != null)
        {
            using (SuppressEditor())
            {
                FillStartupPresetCombo(_selected, _selected.StartupPresetId);
                SelectOrSetFpsLimit(CmbFpsLimit, _selected.FpsLimit);
            }
        }
        UpdateColorSummary(_selected);
        UpdatePresetEmptyCta();
        UpdateLibraryEmpty();
        RefreshPresetListSafe();
        CompanionList.Items.Refresh();
        UpdatePresetLabels();
        UpdateBackendActiveLabel(ColorUiHelper.ReadBackendToggle(TogPresetBackend) == ColorBackend.LowLevel);
        UpdateActiveHeader(App.Services.Monitor.CurrentProfile);
    }

    private void ApplyInfoTips()
    {
        SetTip(InfoRestoreMode, "restore.mode.tip");
        SetTip(InfoSoftAltTab, "profile.softAltTab.tip");
        SetTip(InfoFpsLimit, "profile.fpsLimit.tip");
        SetTip(InfoDeferred, "session.deferred.tip");
        SetTip(InfoQuiet, "session.quiet.tip");
        SetTip(InfoHdr, "session.hdr.tip");
        SetTip(InfoMonBright, "session.bright.tip");
        SetTip(InfoAudio, "session.audio.tip");
        SetTip(InfoScaling, "session.scaling.tip");
        SetTip(InfoCompanions, "companions.tip");
        SetTip(InfoPresetBackend, "color.backend.tip");
        SetTip(InfoPresetLock, "color.lock.tip");
        ChkApplyRes.ToolTip = Loc.T("display.res.tip");
        ChkApplyPower.ToolTip = Loc.T("display.power.tip");
        BtnPresetExport.ToolTip = Loc.T("presets.export.tip");
        BtnPresetImport.ToolTip = Loc.T("presets.import.tip");
    }

    private static void SetTip(InfoHint? hint, string key)
    {
        if (hint != null)
            hint.Tip = Loc.T(key);
    }

    /// <summary>Reflects _dirty in the window title (" •" suffix).</summary>
    private void UpdateTitleDirtyState()
    {
        string baseTitle = Loc.T("app.name");
        Title = _dirty ? baseTitle + " •" : baseTitle;
        TitleAppName.Text = baseTitle;
    }

    // -- Header / feedback ---------------------------------------------------------------------------

    public void UpdateActiveHeader(GameProfile? profile)
    {
        bool show = App.Services.Config.Current.Ui?.ShowActiveInHeader != false;
        ActivePill.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (profile == null)
        {
            ActiveHeaderText.Text = Loc.T("active.none");
            ActiveDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "MutedBrush");
        }
        else
        {
            var bits = new List<string> { profile.Name };
            if (profile.ApplyResolution && !string.IsNullOrWhiteSpace(profile.Resolution))
                bits.Add(ColorUiHelper.FormatResolution(profile.Resolution));
            ActiveHeaderText.Text = Loc.T("active.prefix") + " " + string.Join(" · ", bits);
            ActiveDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
        }

        // A game just started → jump the Play tab to it (once per change, user can still pick another).
        string? activeId = profile?.Id;
        if (activeId != _lastActiveProfileId)
        {
            _lastActiveProfileId = activeId;
            if (activeId != null)
            {
                var match = _profiles.FirstOrDefault(p => p.Id == activeId);
                if (match != null && !ReferenceEquals(match, _selected))
                    SelectProfile(match);
            }
        }
        RefreshPlayTab();
    }

    /// <summary>Ambient status in the title bar (fades after a few seconds). Not used for action feedback.</summary>
    public void SetStatus(string status)
    {
        StatusText.Text = status;
        UiMotion.PulseOpacity(StatusText);
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    /// <summary>
    /// One action → one message. In-window snackbar when the window is in front; otherwise an
    /// in-game OSD banner on the game's monitor (hotkeys, tray actions, game start).
    /// </summary>
    public void ShowToast(string message) => ShowToast(message, null, null);

    public void ShowToast(string message, string? actionText, Action? action)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        bool windowInFront = IsVisible && WindowState != WindowState.Minimized
                             && (IsActive || App.Services.Monitor.CurrentProfile == null);
        if (!windowInFront)
        {
            var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            OsdWindow.Flash(lines[0], lines.Length > 1 ? string.Join(" · ", lines.Skip(1)) : null);
            return;
        }

        _toastAction = action;
        bool hasAction = action != null && !string.IsNullOrWhiteSpace(actionText);
        ToastAction.Content = actionText ?? "";
        ToastAction.Visibility = hasAction ? Visibility.Visible : Visibility.Collapsed;
        ToastHost.IsHitTestVisible = hasAction;
        ToastHost.Padding = hasAction ? new Thickness(18, 9, 10, 9) : new Thickness(18, 9, 18, 9);
        UiMotion.ShowToast(ToastHost, ToastText, message, hasAction ? 5000 : null);
    }

    private void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        var action = _toastAction;
        _toastAction = null;
        UiMotion.HideToast(ToastHost);
        ToastHost.IsHitTestVisible = false;
        try { action?.Invoke(); }
        catch (Exception ex) { AppLog.Warn(ex, "Toast action"); }
    }

    /// <summary>Shows a snackbar with Undo for a destructive action.</summary>
    private void ShowUndoToast(string message, Action undo) => ShowToast(message, Loc.T("btn.undo"), undo);

    // -- Public API used by App / overlay / gallery ----------------------------------------------------

    /// <summary>Game whose preset hotkeys stay registered when no match is running.</summary>
    public GameProfile? HotkeyPresetFallback => _presetGame ?? _selected;

    /// <summary>Selected game for the overlay when no game is running (the live config instance, not the editor copy).</summary>
    public GameProfile? SelectedProfileForOverlay =>
        _selected == null ? null : App.Services.Config.Current.Profiles.FirstOrDefault(p => p.Id == _selected.Id);

    /// <summary>Overlay is about to edit config objects directly — write our pending edits first.</summary>
    public void FlushPendingEdits() => FlushAutosave();

    /// <summary>Overlay saved color into the config — pick it up (the overlay shows its own confirmation).</summary>
    public void NotifyOverlayApplied()
    {
        if (!_dirty)
            ReloadFromConfig();
    }

    private void RefreshHotkeyBindings()
    {
        App.Services.Hotkeys.RegisterFromConfig(
            App.Services.Config.Current,
            App.Services.Monitor.CurrentProfile,
            HotkeyPresetFallback);
    }

    public void ReloadFromConfig()
    {
        // Never silently drop pending edits: write them first, then reload what's on disk.
        if (_dirty)
            FlushAutosave();
        string? keepProfileId = _selected?.Id;
        string? keepPresetGameId = _presetGame?.Id;
        string? keepPresetId = _selectedPreset?.Id;
        var cfg = App.Services.Config.Current;

        using (SuppressEditor())
        {
            // Drop editor pointers before clearing collections — avoids syncing into orphaned profiles.
            _selectedPreset = null;
            _presetGame = null;
            _selected = null;
            _playProfile = null;
            _playPreset = null;

            _profiles.Clear();
            foreach (var p in cfg.Profiles)
                _profiles.Add(p.Clone());
            GameVisuals.ApplyAll(_profiles);
            _presets.Clear();

            CmbDefaultRes.Items.Clear();
            CmbPresetRes.Items.Clear();
            foreach (var r in DisplayEngine.GetAvailableResolutions())
            {
                CmbDefaultRes.Items.Add(r);
                CmbPresetRes.Items.Add(r);
            }
            var defRes = cfg.Defaults.Resolution ?? DisplayEngine.GetCurrentResolution();
            if (!string.IsNullOrWhiteSpace(defRes) && !CmbDefaultRes.Items.Contains(defRes))
                CmbDefaultRes.Items.Add(defRes);
            CmbDefaultRes.SelectedItem = defRes;
            SelectPowerPlan(CmbDefaultPower, cfg.Defaults.PowerPlan, "");

            cfg.Defaults.Color = ColorSettings.Neutral;
            UpdateDriverUiAvailability();

            cfg.GlobalHotkeys ??= new GlobalHotkeys();
            HkToggleOverlay.Text = cfg.GlobalHotkeys.ToggleOverlay ?? "";
            HkEmergency.Text = cfg.GlobalHotkeys.EmergencyRestore ?? "";
            HkNextPreset.Text = cfg.GlobalHotkeys.NextPreset ?? "";
            HkPrevPreset.Text = cfg.GlobalHotkeys.PreviousPreset ?? "";
            HkCompareAb.Text = cfg.GlobalHotkeys.CompareAb ?? "";
            HkToggleZoom.Text = cfg.GlobalHotkeys.ToggleZoom ?? "";
            SelectComboByTag(CmbZoomFactor, (cfg.Ui?.ZoomFactor ?? 4).ToString());

            FillAudioDevices();
        }

        var keep = _profiles.FirstOrDefault(p => p.Id == keepProfileId) ?? _profiles.FirstOrDefault();
        SelectProfile(keep);

        var keepGame = _profiles.FirstOrDefault(p => p.Id == keepPresetGameId) ?? keep;
        SelectPresetGame(keepGame, keepPresetId);

        _dirty = false;
        UpdateTitleDirtyState();
        UpdateLibraryEmpty();
        RefreshPlayTab();
        if (RbMoreLog.IsChecked == true)
            RefreshLog();
    }

    private void MaybeFirstScan()
    {
        // Key off FirstScanDone only — the default config already contains a few core profiles.
        var cfg = App.Services.Config.Current;
        if (cfg.FirstScanDone) return;
        cfg.FirstScanDone = true;
        App.Services.Config.Save(cfg, raiseChanged: false);
        _ = RunGameScanAsync(confirmIfAny: true);
    }

    // -- Library ------------------------------------------------------------------------------------------

    private bool LibraryFilter(object item)
    {
        var q = TxtSearch?.Text?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return item is GameProfile p &&
               ((p.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (p.ProcessName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_libraryView == null) return;
        _libraryView.Refresh();
        // Never leave the editor on a game the list doesn't show — pick the first visible one.
        if (!SuppressingEditor && ProfileList.SelectedItem == null && !_libraryView.IsEmpty)
            SelectProfile(_libraryView.Cast<GameProfile>().First());
        UpdateLibraryEmpty();
    }

    private void UpdateLibraryEmpty()
    {
        if (LibraryEmpty == null) return;
        bool none = _profiles.Count == 0;
        bool noMatch = !none && _libraryView.IsEmpty;
        LibraryEmpty.Visibility = none || noMatch ? Visibility.Visible : Visibility.Collapsed;
        LblLibraryEmpty.Text = none ? Loc.T("library.empty") : Loc.Tf("library.noMatch", TxtSearch.Text.Trim());
        BtnLibraryEmptyAction.Content = none ? Loc.T("btn.scanGames") : Loc.T("btn.clearSearch");
        PlayEmpty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LibraryEmptyAction_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0)
            ScanGames_Click(sender, e);
        else
            TxtSearch.Text = "";
    }

    /// <summary>
    /// Selects a game everywhere (Library list, Play list, editor). If the search filter hides it, the
    /// filter is cleared first so the editor never edits an invisible game.
    /// </summary>
    private void SelectProfile(GameProfile? p)
    {
        if (_selected != null && !ReferenceEquals(_selected, p) && _dirty)
            PushEditorToSelected();

        using (SuppressEditor())
        {
            if (p != null && !_libraryView.Contains(p))
            {
                TxtSearch.Text = "";
                _libraryView.Refresh();
            }
            ProfileList.SelectedItem = p;
        }
        _selected = p;
        LoadEditor(p);
        SyncPlayGameListSelection(p);
        if (p != null)
            ProfileList.ScrollIntoView(p);
        UpdateLibraryEmpty();
        RefreshPlayTab();
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SuppressingEditor) return;
        var next = ProfileList.SelectedItem as GameProfile;
        if (ReferenceEquals(next, _selected)) return;
        if (_dirty && _selected != null) PushEditorToSelected();
        _selected = next;
        LoadEditor(_selected);
        SyncPlayGameListSelection(_selected);
        RefreshPlayTab();
    }

    /// <summary>Play tab's own game list — keeps Library and Play selection in sync without looping.</summary>
    private void PlayGameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGameListSync || SuppressingEditor) return;
        if (PlayGameList.SelectedItem is not GameProfile picked) return;
        if (ReferenceEquals(picked, _selected)) return;
        SelectProfile(picked);
    }

    private void SyncPlayGameListSelection(GameProfile? profile)
    {
        if (_suppressGameListSync || PlayGameList == null) return;
        _suppressGameListSync = true;
        try
        {
            if (!ReferenceEquals(PlayGameList.SelectedItem, profile))
                PlayGameList.SelectedItem = profile;
            if (profile != null)
                PlayGameList.ScrollIntoView(profile);
        }
        finally
        {
            _suppressGameListSync = false;
        }
    }

    private void RefreshGameTiles()
    {
        using (SuppressEditor())
        {
            ProfileList.Items.Refresh();
            PlayGameList.Items.Refresh();
            PresetGameList.Items.Refresh();
            if (_selected != null)
            {
                ProfileList.SelectedItem = _selected;
                PlayGameList.SelectedItem = _selected;
            }
            if (_presetGame != null)
                PresetGameList.SelectedItem = _presetGame;
        }
    }

    private void LoadEditor(GameProfile? p)
    {
        using (SuppressEditor())
        {
            bool has = p != null;
            EditorPanel.IsEnabled = has;
            EditorPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            LblEditorEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            BtnDup.IsEnabled = has;
            BtnDelete.IsEnabled = has;
            BtnExportProfile.IsEnabled = has;

            if (p == null)
            {
                ClearEditor();
                return;
            }

            ChkEnabled.IsChecked = p.Enabled;
            TxtName.Text = p.Name;
            TxtProcess.Text = p.ProcessName;
            TxtAliases.Text = p.ProcessAliases is { Count: > 0 } ? string.Join(", ", p.ProcessAliases) : "";
            ChkApplyRes.IsChecked = p.ApplyResolution;
            ChkApplyPower.IsChecked = p.ApplyPowerPlan;
            p.ApplyColor = false;

            SelectComboByTag(CmbDisplay, p.DisplayDevice ?? "");
            if (string.IsNullOrWhiteSpace(p.Resolution))
            {
                CmbResolution.SelectedIndex = -1;
                CmbResolution.Text = "";
            }
            else
            {
                if (!CmbResolution.Items.Contains(p.Resolution)) CmbResolution.Items.Add(p.Resolution);
                CmbResolution.SelectedItem = p.Resolution;
                CmbResolution.Text = p.Resolution;
            }

            RefreshRefreshRateCombo(p.RefreshRate);
            FillStartupPresetCombo(p, p.StartupPresetId);
            SldApplyDelay.Value = Math.Clamp(p.ApplyDelaySeconds, 0, 120);
            ChkApplyOnFocus.IsChecked = p.ApplyOnFocus;
            ChkSoftAltTab.IsChecked = p.SoftRestoreOnAltTab;
            SelectOrSetFpsLimit(CmbFpsLimit, p.FpsLimit);

            SelectPowerPlan(CmbPower, p.PowerPlan, "highPerformance");
            SelectComboByTag(CmbRestoreMode, p.RestoreMode.ToString());
            var sx = p.Session ?? new SessionExtras();
            SldDeferred.Value = sx.DeferredApplySeconds;
            ChkQuietToast.IsChecked = sx.QuietNotifications;
            ChkNoNightLight.IsChecked = sx.DisableNightLight;
            ChkNoAutoHdr.IsChecked = sx.DisableAutoHdr;
            var layout = sx.MonitorLayout;
            if (string.IsNullOrWhiteSpace(layout))
                layout = sx.IsolatePrimaryMonitor ? "isolatePrimary" : "keepAll";
            SelectComboByTag(CmbMonitorLayout, layout);
            ChkMonBright.IsChecked = sx.ApplyMonitorBrightness;
            SldMonBright.Value = sx.MonitorBrightness;
            ChkSwitchAudio.IsChecked = sx.SwitchAudioDevice;
            SelectAudioDevice(sx.AudioDeviceId);
            SelectComboByTag(CmbScaling, string.IsNullOrWhiteSpace(sx.ScalingMode) ? "default" : sx.ScalingMode!);
            RefreshCompanionList(p);
            UpdateSessionLabels();
            UpdateColorSummary(p);
            UpdateDependentEnables();
        }
        // Keep _dirty — profile switch must not pretend changes were saved to disk
    }

    /// <summary>Resets every editor field so no stale value from the previous game can leak into a save.</summary>
    private void ClearEditor()
    {
        using (SuppressEditor())
        {
            ChkEnabled.IsChecked = false;
            TxtName.Text = "";
            TxtProcess.Text = "";
            TxtAliases.Text = "";
            ChkApplyRes.IsChecked = false;
            CmbResolution.SelectedIndex = -1;
            CmbResolution.Text = "";
            RefreshRefreshRateCombo(0);
            SelectComboByTag(CmbDisplay, "");
            ChkApplyPower.IsChecked = false;
            SelectPowerPlan(CmbPower, null, "highPerformance");
            FillStartupPresetCombo(null, "");
            SldApplyDelay.Value = 0;
            ChkApplyOnFocus.IsChecked = false;
            ChkSoftAltTab.IsChecked = false;
            SelectOrSetFpsLimit(CmbFpsLimit, 0);
            SelectComboByTag(CmbRestoreMode, nameof(RestoreMode.PreviousSnapshot));
            SldDeferred.Value = 0;
            ChkQuietToast.IsChecked = false;
            ChkNoNightLight.IsChecked = false;
            ChkNoAutoHdr.IsChecked = false;
            SelectComboByTag(CmbMonitorLayout, "keepAll");
            ChkMonBright.IsChecked = false;
            SldMonBright.Value = 80;
            ChkSwitchAudio.IsChecked = false;
            SelectAudioDevice(null);
            SelectComboByTag(CmbScaling, "default");
            RefreshCompanionList(null);
            UpdateColorSummary(null);
            UpdateSessionLabels();
            UpdateDependentEnables();
        }
    }

    /// <summary>Controls that only matter when their checkbox is on are disabled otherwise.</summary>
    private void UpdateDependentEnables()
    {
        ResolutionPanel.IsEnabled = ChkApplyRes.IsChecked == true;
        CmbPower.IsEnabled = ChkApplyPower.IsChecked == true;
        MonBrightPanel.IsEnabled = ChkMonBright.IsChecked == true;
        CmbAudio.IsEnabled = ChkSwitchAudio.IsChecked == true;
        CmbPresetRes.IsEnabled = ChkPresetRes.IsChecked == true;
    }

    private void EditorChanged(object sender, RoutedEventArgs e)
    {
        if (SuppressingEditor || _selected == null) return;

        // Turning on "Change resolution" with an empty box: start from the current desktop mode.
        if (ReferenceEquals(sender, ChkApplyRes) && ChkApplyRes.IsChecked == true && string.IsNullOrWhiteSpace(ReadResolutionText()))
        {
            using (SuppressEditor())
            {
                var cur = DisplayEngine.GetCurrentResolution();
                if (!string.IsNullOrWhiteSpace(cur))
                {
                    if (!CmbResolution.Items.Contains(cur)) CmbResolution.Items.Add(cur);
                    CmbResolution.SelectedItem = cur;
                    CmbResolution.Text = cur;
                    RefreshRefreshRateCombo();
                }
            }
        }

        UpdateDependentEnables();
        MarkDirty();
        PushEditorToSelected();
        UpdateColorSummary(_selected);
        RefreshPlayQuickFields();
        if (ReferenceEquals(sender, TxtName) || ReferenceEquals(sender, TxtProcess) || ReferenceEquals(sender, ChkEnabled))
        {
            _tileRefreshTimer.Stop();
            _tileRefreshTimer.Start();
        }
    }

    private void EditorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SuppressingEditor) return;
        UpdateSessionLabels();
        if (_selected == null) return;
        MarkDirty();
        PushEditorToSelected();
    }

    private void Display_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SuppressingEditor) return;
        RefreshRefreshRateCombo();
        EditorChanged(sender, e);
    }

    private void FillDisplays(bool keepSelection = false)
    {
        string? keep = keepSelection ? (CmbDisplay.SelectedItem as ComboBoxItem)?.Tag?.ToString() : null;
        using (SuppressEditor())
        {
            CmbDisplay.Items.Clear();
            CmbDisplay.Items.Add(new ComboBoxItem { Content = Loc.T("display.monitor.default"), Tag = "" });
            foreach (var d in DisplayEngine.GetDisplays())
            {
                var label = d.Primary ? $"{d.Friendly} ★" : d.Friendly;
                CmbDisplay.Items.Add(new ComboBoxItem { Content = label, Tag = d.DeviceName, ToolTip = d.DeviceName });
            }
            SelectComboByTag(CmbDisplay, keep ?? "");
        }
    }

    private void LoadResolutions()
    {
        CmbResolution.Items.Clear();
        foreach (var r in DisplayEngine.GetAvailableResolutions())
            CmbResolution.Items.Add(r);
    }

    private void Resolution_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SuppressingEditor) return;
        RefreshRefreshRateCombo();
        EditorChanged(sender, e);
    }

    private void Resolution_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SuppressingEditor || _selected == null || CmbResolution.IsKeyboardFocusWithin) return;
        var typed = ReadResolutionText();
        if (typed != null && !DisplayEngine.TryParseRes(typed, out _, out _))
        {
            ShowToast(Loc.Tf("display.res.invalid", typed));
            using (SuppressEditor())
                CmbResolution.Text = _selected.Resolution ?? "";
            return;
        }
        RefreshRefreshRateCombo();
        MarkDirty();
        PushEditorToSelected();
    }

    private string? ReadResolutionText()
    {
        var text = CmbResolution.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
            return text.Replace('×', 'x').Replace(" ", "");
        var sel = CmbResolution.SelectedItem?.ToString();
        return string.IsNullOrWhiteSpace(sel) ? null : sel;
    }

    private void RefreshRefreshRateCombo(int preferHz = -1)
    {
        using (SuppressEditor())
        {
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
    }

    private void FillStartupPresetCombo(GameProfile? p, string? preferId = null)
    {
        string? keep = preferId ?? p?.StartupPresetId;
        CmbStartupPreset.Items.Clear();
        CmbStartupPreset.Items.Add(new ComboBoxItem { Content = Loc.T("profile.startup.none"), Tag = "" });
        if (p?.Presets != null)
        {
            foreach (var pr in p.Presets)
                CmbStartupPreset.Items.Add(new ComboBoxItem { Content = pr.Name, Tag = pr.Id });
        }
        SelectComboByTag(CmbStartupPreset, keep ?? "");
    }

    /// <summary>Library "Presets" section: what this game has and what applies on start.</summary>
    private void UpdateColorSummary(GameProfile? p)
    {
        if (p == null)
        {
            LblColorSummary.Text = "";
            return;
        }
        var presets = p.Presets ?? new List<QuickPreset>();
        if (presets.Count == 0)
        {
            LblColorSummary.Text = Loc.T("library.presets.none");
            return;
        }
        var lines = new List<string> { Loc.Tf("library.presets.count", presets.Count, string.Join(", ", presets.Select(x => x.Name))) };
        var startup = !string.IsNullOrWhiteSpace(p.StartupPresetId)
            ? presets.FirstOrDefault(pr => pr.Id == p.StartupPresetId)
            : null;
        lines.Add(startup != null
            ? Loc.Tf("library.presets.startup", startup.Name, ColorUiHelper.Describe(startup))
            : Loc.T("color.summary.none"));
        LblColorSummary.Text = string.Join("\n", lines);
    }

    private void PushEditorToSelected()
    {
        if (_selected == null) return;
        _selected.Enabled = ChkEnabled.IsChecked == true;
        _selected.Name = TxtName.Text.Trim();
        _selected.ProcessName = TxtProcess.Text.Trim();
        _selected.ProcessAliases = ParseAliases(TxtAliases.Text);
        _selected.ApplyResolution = ChkApplyRes.IsChecked == true;
        _selected.ApplyPowerPlan = ChkApplyPower.IsChecked == true;
        _selected.ApplyColor = false;

        var res = ReadResolutionText();
        _selected.Resolution = res != null && DisplayEngine.TryParseRes(res, out _, out _) ? res : _selected.Resolution;
        if (string.IsNullOrWhiteSpace(res)) _selected.Resolution = null;

        _selected.RefreshRate = CmbRefreshRate.SelectedItem is ComboBoxItem hzItem && int.TryParse(hzItem.Tag?.ToString(), out var hz)
            ? hz
            : 0;

        if (CmbStartupPreset.SelectedItem is ComboBoxItem sp)
            _selected.StartupPresetId = string.IsNullOrWhiteSpace(sp.Tag?.ToString()) ? null : sp.Tag!.ToString();

        _selected.ApplyDelaySeconds = (int)SldApplyDelay.Value;
        _selected.ApplyOnFocus = ChkApplyOnFocus.IsChecked == true;
        _selected.SoftRestoreOnAltTab = ChkSoftAltTab.IsChecked == true;
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
        if (CmbMonitorLayout.SelectedItem is ComboBoxItem layoutItem)
        {
            var layout = layoutItem.Tag?.ToString() ?? "keepAll";
            _selected.Session.MonitorLayout = layout;
            // IsolatePrimaryMonitor mirrors the layout combo for older consumers.
            _selected.Session.IsolatePrimaryMonitor =
                string.Equals(layout, "isolatePrimary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(layout, "primaryOnly", StringComparison.OrdinalIgnoreCase);
        }
        _selected.Session.ApplyMonitorBrightness = ChkMonBright.IsChecked == true;
        _selected.Session.MonitorBrightness = (int)SldMonBright.Value;
        _selected.Session.SwitchAudioDevice = ChkSwitchAudio.IsChecked == true;
        var audioTag = (CmbAudio.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        _selected.Session.AudioDeviceId = string.IsNullOrWhiteSpace(audioTag) ? null : audioTag;
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

    private static string? FpsOffText => Loc.T("fps.off");

    private static void SelectOrSetFpsLimit(System.Windows.Controls.ComboBox combo, int fps)
    {
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
        combo.Text = fps > 0 ? fps.ToString() : FpsOffText;
    }

    private static int ReadFpsLimit(System.Windows.Controls.ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem ci && int.TryParse(ci.Tag?.ToString(), out var fromTag)
            && string.Equals(combo.Text?.Trim(), ci.Content?.ToString(), StringComparison.OrdinalIgnoreCase))
            return Math.Clamp(fromTag, 0, 1023);

        var typed = combo.Text?.Trim() ?? "";
        if (typed.Length == 0 || string.Equals(typed, FpsOffText, StringComparison.OrdinalIgnoreCase))
            return 0;
        var digits = new string(typed.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? Math.Clamp(n, 0, 1023) : 0;
    }

    private void FpsLimit_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SuppressingEditor || _selected == null || CmbFpsLimit.IsKeyboardFocusWithin) return;
        int fps = ReadFpsLimit(CmbFpsLimit);
        using (SuppressEditor())
            SelectOrSetFpsLimit(CmbFpsLimit, fps);
        MarkDirty();
        PushEditorToSelected();
    }

    private void UpdateFpsLimitAvailability()
    {
        bool available = FpsLimitService.IsNvidiaAvailable;
        LblFpsLimitHint.Text = available
            ? Loc.T("profile.fpsLimit.hint")
            : Loc.T("profile.fpsLimit.unavailable");
        CmbFpsLimit.ToolTip = LblFpsLimitHint.Text;
    }

    private void UpdateSessionLabels()
    {
        LblDeferred.Text = Loc.Tf("session.deferred", (int)SldDeferred.Value);
        LblMonBright.Text = Loc.Tf("session.bright.lbl", (int)SldMonBright.Value);
        LblApplyDelay.Text = Loc.Tf("profile.delay", (int)SldApplyDelay.Value);
    }

    /// <summary>Keeps the Play tab's quick fields in sync without rebuilding chips (preserves a "Live" chip).</summary>
    private void RefreshPlayQuickFields()
    {
        if (_playProfile == null || _selected == null || _selected.Id != _playProfile.Id) return;
        TxtPlayName.Text = _playProfile.Name;
        TxtPlayProcess.Text = _playProfile.ProcessName;
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var p = new GameProfile
        {
            Name = Loc.T("profile.new"),
            ProcessName = "",
            ApplyResolution = false,
            ApplyColor = false,
            ApplyPowerPlan = true,
            Color = ColorSettings.Neutral
        };
        _profiles.Add(p);
        GameVisuals.Apply(p);
        SelectProfile(p);
        MarkDirty();
        MainTabs.SelectedItem = TabProfiles;
        Dispatcher.BeginInvoke(() =>
        {
            TxtName.Focus();
            TxtName.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>Copy = new identity: new Id, new preset Ids, no hotkeys, disabled (no two profiles fight over one exe).</summary>
    private void DupProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        PushEditorToSelected();
        var copy = _selected.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = Loc.Tf("profile.copyName", _selected.Name);
        copy.Enabled = false;
        var idMap = new Dictionary<string, string>();
        foreach (var pr in copy.Presets ?? new List<QuickPreset>())
        {
            var newId = copy.Id + "_" + Guid.NewGuid().ToString("N")[..8];
            idMap[pr.Id] = newId;
            pr.Id = newId;
            pr.Hotkey = null;
        }
        copy.StartupPresetId = copy.StartupPresetId != null && idMap.TryGetValue(copy.StartupPresetId, out var mapped)
            ? mapped
            : null;

        int index = _profiles.IndexOf(_selected);
        _profiles.Insert(index + 1, copy);
        GameVisuals.Apply(copy);
        SelectProfile(copy);
        MarkDirty();
        ShowToast(Loc.Tf("toast.duplicated", copy.Name));
    }

    private bool ConfirmDestructive(string message, string title, string actionText)
    {
        if (App.Services.Config.Current.Ui?.ConfirmDelete == false)
            return true;
        return ThemedDialog.Confirm(this, message, title, actionText, danger: true);
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var victim = _selected;
        string name = string.IsNullOrWhiteSpace(victim.Name) ? victim.ProcessName : victim.Name;
        if (!ConfirmDestructive(Loc.Tf("confirm.deleteProfile", name), Loc.T("confirm.deleteProfile.title"), Loc.T("btn.delete")))
            return;

        PushEditorToSelected();
        int index = _profiles.IndexOf(victim);
        bool wasPresetGame = ReferenceEquals(_presetGame, victim);
        if (wasPresetGame)
        {
            // Detach first so SyncPresetsBackToGame can't write into the removed profile.
            PushPresetEditorIfDirty();
            SyncPresetsBackToGame();
            _presetGame = null;
        }

        using (SuppressEditor())
            _profiles.Remove(victim);
        var next = _profiles.Count == 0 ? null : _profiles[Math.Clamp(index, 0, _profiles.Count - 1)];
        _selected = null;
        SelectProfile(next);
        if (wasPresetGame) SelectPresetGame(next);
        MarkDirty();
        FlushAutosave();

        ShowUndoToast(Loc.Tf("toast.deleted", name), () =>
        {
            using (SuppressEditor())
                _profiles.Insert(Math.Clamp(index, 0, _profiles.Count), victim);
            SelectProfile(victim);
            MarkDirty();
            FlushAutosave();
            ShowToast(Loc.Tf("toast.restored", name));
        });
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.T("filter.exe"),
            Title = Loc.T("profile.browse.title")
        };
        if (dlg.ShowDialog(this) != true) return;

        using (SuppressEditor())
        {
            TxtProcess.Text = Path.GetFileName(dlg.FileName);
            if (string.IsNullOrWhiteSpace(TxtName.Text) || TxtName.Text == Loc.T("profile.new"))
                TxtName.Text = GameCatalog.GetFriendlyName(TxtProcess.Text)
                               ?? Path.GetFileNameWithoutExtension(dlg.FileName);
        }
        _selected.ExePath = dlg.FileName;
        PushEditorToSelected();
        GameVisuals.Apply(_selected);
        RefreshGameTiles();
        MarkDirty();
        RefreshPlayTab();
    }

    private void PickRunning_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var procs = ProcessWatcher.GetRunningExeNames().ToList();
        var pick = new ProcessPickerWindow(procs) { Owner = this };
        if (pick.ShowDialog() != true || string.IsNullOrWhiteSpace(pick.SelectedProcess)) return;

        using (SuppressEditor())
        {
            TxtProcess.Text = pick.SelectedProcess;
            if (string.IsNullOrWhiteSpace(TxtName.Text) || TxtName.Text == Loc.T("profile.new"))
                TxtName.Text = GameCatalog.GetFriendlyName(pick.SelectedProcess)
                               ?? Path.GetFileNameWithoutExtension(pick.SelectedProcess);
        }
        MarkDirty();
        PushEditorToSelected();
        GameVisuals.Apply(_selected);
        RefreshGameTiles();
    }

    // -- Companions ------------------------------------------------------------------------------------------

    private void RefreshCompanionList(GameProfile? p, CompanionApp? select = null)
    {
        CompanionList.ItemsSource = null;
        CompanionList.ItemsSource = p?.Companions;
        if (select != null) CompanionList.SelectedItem = select;
        bool empty = p?.Companions == null || p.Companions.Count == 0;
        LblCompanionsEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        UpdateCompanionButtons();
    }

    private void UpdateCompanionButtons()
    {
        bool sel = CompanionList.SelectedItem is CompanionApp;
        BtnCompanionEdit.IsEnabled = sel;
        BtnCompanionRemove.IsEnabled = sel;
    }

    private void CompanionList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCompanionButtons();

    private void CompanionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CompanionList.SelectedItem is CompanionApp)
            EditCompanion_Click(sender, e);
    }

    private void AddCompanion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var c = new CompanionApp();
        var dlg = new CompanionEditWindow(c) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _selected.Companions ??= new List<CompanionApp>();
        _selected.Companions.Add(c);
        RefreshCompanionList(_selected, c);
        MarkDirty();
    }

    private void EditCompanion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || CompanionList.SelectedItem is not CompanionApp c) return;
        var dlg = new CompanionEditWindow(c) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        RefreshCompanionList(_selected, c);
        MarkDirty();
    }

    private void RemoveCompanion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || CompanionList.SelectedItem is not CompanionApp c) return;
        var owner = _selected;
        string label = CompanionEditWindow.Describe(c);
        if (!ConfirmDestructive(Loc.Tf("confirm.removeCompanion", label), Loc.T("confirm.removeCompanion.title"), Loc.T("btn.remove")))
            return;
        int index = owner.Companions.IndexOf(c);
        owner.Companions.Remove(c);
        RefreshCompanionList(owner);
        MarkDirty();
        FlushAutosave();
        ShowUndoToast(Loc.T("toast.companion.removed"), () =>
        {
            owner.Companions.Insert(Math.Clamp(index, 0, owner.Companions.Count), c);
            if (ReferenceEquals(_selected, owner))
                RefreshCompanionList(owner, c);
            MarkDirty();
            FlushAutosave();
        });
    }

    // -- Library: scan / import / export / restore -------------------------------------------------------

    private async void ScanGames_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunGameScanAsync(confirmIfAny: false);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Scan games");
            ScanBusy.HideAnimated();
            ThemedDialog.Show(this, Loc.T("scan.failed") + "\n" + ex.Message, Loc.T("scan.title"));
        }
    }

    private async Task RunGameScanAsync(bool confirmIfAny)
    {
        ScanBusy.ShowAnimated();
        UiSound.BeginWork();

        var minDelay = Task.Delay(1200);
        List<GameScanner.FoundGame> found;
        try
        {
            found = await Task.Run(() =>
                GameScanner.ScanAll(msg => Dispatcher.BeginInvoke(() => ScanBusy.SetStatus(msg))));
        }
        catch (Exception ex)
        {
            UiSound.EndWork(UiWorkResult.Cancel);
            UiSound.Error();
            ScanBusy.HideAnimated();
            AppLog.Error(ex, "Game scan");
            ThemedDialog.Show(this, Loc.T("scan.failed") + "\n" + ex.Message, Loc.T("scan.title"));
            return;
        }

        await minDelay;
        ScanBusy.HideAnimated();

        if (found.Count == 0)
        {
            UiSound.EndWork(UiWorkResult.Empty);
            ShowToast(Loc.T("scan.none"));
            return;
        }

        UiSound.EndWork(UiWorkResult.Done);

        var fresh = found.Where(g => !_profiles.Any(p =>
            string.Equals(p.ProcessName, g.Process, StringComparison.OrdinalIgnoreCase))).ToList();
        if (confirmIfAny && fresh.Count > 0)
        {
            var names = string.Join(", ", fresh.Select(f => f.Name).Distinct());
            if (!ThemedDialog.Confirm(this, Loc.Tf("scan.found", names), Loc.T("scan.title"), Loc.T("scan.addAll")))
                return;
        }

        var added = AddFoundGames(found);
        ShowToast(added == 0 ? Loc.T("scan.allListed") : Loc.Tf("scan.added", added));
    }

    private int AddFoundGames(List<GameScanner.FoundGame> found)
    {
        int added = 0;
        GameProfile? last = null;
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
            last = profile;
            added++;
        }
        if (added > 0 || _dirty)
        {
            MarkDirty();
            RefreshGameTiles();
        }
        if (last != null)
            SelectProfile(last);
        UpdateLibraryEmpty();
        return added;
    }

    private void RestoreCoreGames_Click(object sender, RoutedEventArgs e)
    {
        int added = 0;
        GameProfile? last = null;
        foreach (var core in GameCatalog.CreateCoreProfiles())
        {
            if (_profiles.Any(p => string.Equals(p.ProcessName, core.ProcessName, StringComparison.OrdinalIgnoreCase)))
                continue;
            _profiles.Add(core);
            GameVisuals.Apply(core);
            last = core;
            added++;
        }
        if (added > 0)
        {
            MarkDirty();
            SelectProfile(last);
            UiSound.Done();
            ShowToast(Loc.Tf("toast.restoredGames", added));
        }
        else
        {
            UiSound.Empty();
            ShowToast(Loc.T("toast.restoredGames.none"));
        }
        UpdateLibraryEmpty();
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            ShowToast(Loc.T("profile.pick"));
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
        SelectProfile(imported);
        MarkDirty();
        FlushAutosave();
        ShowToast(Loc.Tf("toast.profile.imported", imported.Name));
    }

    private void LaunchGame_Click(object sender, RoutedEventArgs e) => LaunchProfile(_selected);

    private void LaunchProfile(GameProfile? profile)
    {
        if (profile == null)
        {
            ShowToast(Loc.T("profile.pick"));
            return;
        }

        string? path = profile.ExePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ThemedDialog.Show(this, Loc.T("launch.noPath"), Loc.T("btn.launchGame"));
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
            ShowToast(Loc.Tf("toast.launching", profile.Name));
            UiSound.Launch();
        }
        catch (Exception ex)
        {
            UiSound.Error();
            ThemedDialog.Show(this, Loc.T("launch.failed") + "\n" + ex.Message, Loc.T("btn.launchGame"));
        }
    }

    /// <summary>Library: push this game's display/power settings to the desktop right now (test without launching).</summary>
    private void ApplyProfileNow_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        PushEditorToSelected();
        MarkDirty();
        FlushAutosave();
        var cfg = App.Services.Config.Current;
        var live = cfg.Profiles.FirstOrDefault(p => p.Id == _selected.Id) ?? _selected;
        App.Services.Monitor.ClearActivePreset();
        App.Services.Display.ApplyProfile(live, cfg.Defaults);
        ShowToast(Loc.Tf("toast.applied.profile", _selected.Name));
    }

    private void Overlay_Click(object sender, RoutedEventArgs e)
    {
        FlushAutosave();
        if (System.Windows.Application.Current is App app)
            app.ToggleGameOverlay();
    }

    private void EmergencyRestore_Click(object sender, RoutedEventArgs e)
    {
        _previewOnDesktop = false;
        App.Services.Zoom.Off();
        App.Services.Monitor.EmergencyRestore();
        RefreshPlayTab();
        ShowToast(Loc.T("toast.emergency"));
    }

    private void OpenPresets_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = TabPresets;
        if (_selected != null)
            SelectPresetGame(_selected);
    }

    // -- Play tab ----------------------------------------------------------------------------------------

    private bool PlayIsRunning =>
        _playProfile != null && App.Services.Monitor.CurrentProfile?.Id == _playProfile.Id;

    /// <summary>Sliders touch the real display only for the running game, or when the user opted into a desktop preview.</summary>
    private bool PlayCanApplyLive => PlayIsRunning || _previewOnDesktop;

    public void RefreshPlayTab()
    {
        if (TxtPlayName == null || SuppressingPlay) return;
        var profile = _selected;
        if (!ReferenceEquals(profile, _playProfile))
        {
            StopDesktopPreview();
            _playPreset = null;
            _playLive = false;
        }
        _playProfile = profile;

        using (SuppressPlay())
        {
            bool anyRunning = App.Services.Monitor.CurrentProfile != null;
            BtnPlayLaunch.IsEnabled = profile != null;
            BtnPlayGameSettings.IsEnabled = profile != null;

            if (profile == null)
            {
                TxtPlayName.Text = Loc.T("play.noGame");
                TxtPlayProcess.Text = "";
                PlayStatusPill.Visibility = Visibility.Collapsed;
                PlayPresetChips.Children.Clear();
                TxtPlayHotkeys.Text = "";
                ChkPlayPreview.Visibility = Visibility.Collapsed;
                PlaySliders.IsEnabled = false;
                BtnPlayEditPreset.Visibility = Visibility.Collapsed;
                SetPlayHint(Loc.T("play.hint.pick"), active: false);
                _playPreset = null;
                return;
            }

            bool running = PlayIsRunning;
            TxtPlayName.Text = profile.Name;
            TxtPlayProcess.Text = profile.ProcessName;
            PlayStatusPill.Visibility = Visibility.Visible;
            TxtPlayStatus.Text = running ? Loc.T("play.status.active") : Loc.T("play.status.idle");
            PlayStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, running ? "AccentBrush" : "MutedBrush");

            ChkPlayPreview.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            ChkPlayPreview.IsEnabled = !anyRunning;
            ChkPlayPreview.IsChecked = _previewOnDesktop;

            if (_playLive && !PlayCanApplyLive) _playLive = false;
            string? activePresetId = running && !_playLive ? App.Services.Monitor.ActivePresetId : null;
            QuickPreset? preset = _playLive ? null : activePresetId != null
                ? profile.Presets?.FirstOrDefault(p => p.Id == activePresetId)
                : (_playPreset != null ? profile.Presets?.FirstOrDefault(p => p.Id == _playPreset.Id) : null);
            if (!_playLive)
            {
                if (preset == null && !string.IsNullOrWhiteSpace(profile.StartupPresetId))
                    preset = profile.Presets?.FirstOrDefault(p => p.Id == profile.StartupPresetId);
                preset ??= profile.Presets?.FirstOrDefault();
            }
            _playPreset = preset;

            RebuildPlayPresetChips(profile, preset);
            LoadPlaySlidersFrom(preset);
            PlaySliders.IsEnabled = preset != null || PlayCanApplyLive;
            BtnPlayEditPreset.Visibility = preset != null ? Visibility.Visible : Visibility.Collapsed;
            BtnPlayEditPreset.Content = preset != null ? Loc.Tf("play.editPreset", preset.Name) : "";
            UpdatePlayHint();
            RefreshPlayHotkeyCheatSheet(profile);
        }
    }

    private void UpdatePlayHint()
    {
        if (_playProfile == null) return;
        string presetName = _playPreset?.Name ?? "";
        if (PlayIsRunning)
            SetPlayHint(_playPreset != null
                ? Loc.Tf("play.hint.running", _playProfile.Name, presetName)
                : Loc.Tf("play.hint.runningLive", _playProfile.Name), active: true);
        else if (_previewOnDesktop)
            SetPlayHint(Loc.T("play.hint.preview"), active: true);
        else if (App.Services.Monitor.CurrentProfile != null)
            SetPlayHint(Loc.Tf("play.hint.otherRunning", App.Services.Monitor.CurrentProfile.Name), active: false);
        else if (_playPreset == null)
            SetPlayHint(Loc.T("play.hint.noPresets"), active: false);
        else
            SetPlayHint(Loc.Tf("play.hint.idle", _playProfile.Name, presetName), active: false);
    }

    private void SetPlayHint(string text, bool active)
    {
        LblPlayColorHint.Text = text;
        PlayHintDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, active ? "AccentBrush" : "MutedBrush");
    }

    private void RebuildPlayPresetChips(GameProfile profile, QuickPreset? selected)
    {
        PlayPresetChips.Children.Clear();

        if (PlayCanApplyLive)
        {
            var liveChip = MakePlayChip(Loc.T("play.liveChip"), selected == null, Loc.T("play.liveChip.tip"));
            liveChip.Click += (_, _) => PlayPresetChip_Click(null);
            PlayPresetChips.Children.Add(liveChip);
        }

        foreach (var p in profile.Presets ?? new List<QuickPreset>())
        {
            string tip = ColorUiHelper.Describe(p) + (string.IsNullOrWhiteSpace(p.Hotkey) ? "" : "  ·  " + p.Hotkey);
            var chip = MakePlayChip(p.Name, selected != null && selected.Id == p.Id, tip);
            chip.Click += (_, _) => PlayPresetChip_Click(p);
            PlayPresetChips.Children.Add(chip);
        }

        if ((profile.Presets?.Count ?? 0) == 0)
        {
            var add = MakePlayChip("+ " + Loc.T("play.addPreset"), false, Loc.T("presets.add.tip"));
            add.Click += (_, _) =>
            {
                MainTabs.SelectedItem = TabPresets;
                SelectPresetGame(profile);
                AddPreset_Click(add, new RoutedEventArgs());
            };
            PlayPresetChips.Children.Add(add);
        }
    }

    private System.Windows.Controls.Button MakePlayChip(string text, bool selected, string? tip) => new()
    {
        Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 },
        ToolTip = tip,
        Style = (Style)FindResource(selected ? "ChipButtonSelected" : "ChipButton"),
        Margin = new Thickness(0, 0, 8, 8)
    };

    private void PlayPresetChip_Click(QuickPreset? preset)
    {
        if (_playProfile == null) return;
        _playPreset = preset;
        _playLive = preset == null;
        using (SuppressPlay())
        {
            RebuildPlayPresetChips(_playProfile, preset);
            LoadPlaySlidersFrom(preset);
            PlaySliders.IsEnabled = preset != null || PlayCanApplyLive;
            BtnPlayEditPreset.Visibility = preset != null ? Visibility.Visible : Visibility.Collapsed;
            BtnPlayEditPreset.Content = preset != null ? Loc.Tf("play.editPreset", preset.Name) : "";
        }
        UpdatePlayHint();

        if (preset == null) return;
        if (PlayIsRunning)
            App.Services.Monitor.ApplyPreset(preset);
        else if (_previewOnDesktop)
        {
            var c = (preset.Color ?? ColorSettings.Neutral).Clone();
            c.Clamp();
            App.Services.Display.ApplyColor(c);
        }
        // Not running and no preview: the chip only selects which preset the sliders edit.
    }

    private void LoadPlaySlidersFrom(QuickPreset? preset)
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

        using (SuppressPlay())
        {
            ColorUiHelper.ApplyColorSliders(source, SldPlayB, SldPlayC, SldPlayG, SldPlayV, SldPlayS, SldPlayH);
            bool low = ColorUiHelper.IsLowLevel(_playBackend);
            SldPlayS.IsEnabled = low;
            SldPlayH.IsEnabled = !low;
            RefreshPlayValues();
        }
    }

    private void RefreshPlayValues()
    {
        ColorUiHelper.UpdateValues(ValPlayB, ValPlayC, ValPlayG, ValPlayV, SldPlayB, SldPlayC, SldPlayG, SldPlayV,
            _playBackend, ValPlayH, SldPlayH, ValPlayS, SldPlayS);
    }

    private void PlaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SuppressingPlay || !IsInitialized || _playProfile == null) return;
        RefreshPlayValues();

        var c = ColorUiHelper.ReadColorFromSliders(_playBackend, SldPlayB, SldPlayC, SldPlayG, SldPlayV, SldPlayS,
            lockColor: _playPreset?.Color?.LockColor ?? true, hue: SldPlayH);
        if (PlayCanApplyLive)
            App.Services.Display.ApplyColor(c);

        if (_playPreset != null)
        {
            _playPreset.ApplyColor = true;
            _playPreset.Color = c;
            _playPreset.SaveActiveToDualSlots();
            MarkDirty();

            if (PlayIsRunning && App.Services.Monitor.ActivePresetId == _playPreset.Id)
                App.Services.Monitor.SetColorLockPaused(false);

            if (ReferenceEquals(_playPreset, _selectedPreset))
            {
                PushPresetEditorIfDirty(); // keep pending name edits, then reload colors from the preset
                _playPreset.Color = c;
                _playPreset.SaveActiveToDualSlots();
                LoadPresetEditor(_selectedPreset); // runs inside SuppressEditor()
            }
        }
    }

    private void PlayPreview_Changed(object sender, RoutedEventArgs e)
    {
        if (SuppressingPlay || _playProfile == null) return;
        bool on = ChkPlayPreview.IsChecked == true;
        if (on)
        {
            _previewOnDesktop = true;
            var c = ColorUiHelper.ReadColorFromSliders(_playBackend, SldPlayB, SldPlayC, SldPlayG, SldPlayV, SldPlayS,
                lockColor: false, hue: SldPlayH);
            App.Services.Display.ApplyColor(c);
        }
        else
        {
            StopDesktopPreview();
        }
        RefreshPlayTab();
    }

    /// <summary>Ends a desktop preview and puts the factory ramp back (only when no game owns the display).</summary>
    private void StopDesktopPreview()
    {
        if (!_previewOnDesktop) return;
        _previewOnDesktop = false;
        if (App.Services.Monitor.CurrentProfile == null)
        {
            try { App.Services.Display.ApplyNeutralOrFactoryRamp(); }
            catch (Exception ex) { AppLog.Warn(ex, "Stop desktop preview"); }
        }
        if (ChkPlayPreview != null)
        {
            using (SuppressPlay())
                ChkPlayPreview.IsChecked = false;
        }
    }

    private void PlayLaunch_Click(object sender, RoutedEventArgs e) => LaunchProfile(_playProfile);

    private void PlayGameSettings_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = TabProfiles;
        if (_playProfile != null) SelectProfile(_playProfile);
    }

    private void PlayEditPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_playProfile == null) return;
        var presetId = _playPreset?.Id;
        MainTabs.SelectedItem = TabPresets;
        SelectPresetGame(_playProfile, presetId);
    }

    private void RefreshPlayHotkeyCheatSheet(GameProfile profile)
    {
        var gh = App.Services.Config.Current.GlobalHotkeys;
        var lines = new List<string>();

        void AddGlobal(string? gesture, string labelKey)
        {
            if (!string.IsNullOrWhiteSpace(gesture))
                lines.Add($"{gesture}  —  {Loc.T(labelKey)}");
        }
        AddGlobal(gh?.ToggleOverlay, "hotkey.overlay");
        AddGlobal(gh?.EmergencyRestore, "hotkey.emergency");
        AddGlobal(gh?.NextPreset, "hotkey.next");
        AddGlobal(gh?.PreviousPreset, "hotkey.prev");
        AddGlobal(gh?.CompareAb, "hotkey.compareAb");
        AddGlobal(gh?.ToggleZoom, "hotkey.zoom");

        if (profile.Presets != null)
        {
            foreach (var p in profile.Presets.Where(p => !string.IsNullOrWhiteSpace(p.Hotkey)))
                lines.Add($"{p.Hotkey}  —  {p.Name}");
        }

        TxtPlayHotkeys.Text = lines.Count > 0 ? string.Join("\n", lines) : Loc.T("play.hotkeys.none");
    }

    // -- Defaults / More -------------------------------------------------------------------------------

    private void GlobalEditorChanged(object sender, RoutedEventArgs e)
    {
        if (SuppressingEditor) return;
        MarkDirty();
    }

    private void ApplyDefaultsNow_Click(object sender, RoutedEventArgs e)
    {
        FlushAutosave();
        if (App.Services.Monitor.HasActivePreset)
        {
            ShowToast(Loc.T("toast.global.kept.preset"));
            return;
        }
        App.Services.Monitor.ClearActivePreset();
        App.Services.Display.RestoreDefaults(App.Services.Config.Current.Defaults);
        ShowToast(Loc.T("toast.defaults.applied"));
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var cfg = App.Services.Config.Current;
        var factory = cfg.FactoryDefaults ?? ConfigService.CaptureFactoryDefaults();
        string res = string.IsNullOrWhiteSpace(factory.Resolution) ? "—" : factory.Resolution!;
        string power = PowerPlanLabel(factory.PowerPlan);
        if (!ThemedDialog.Confirm(this, Loc.Tf("confirm.resetDefaults", res, power),
                Loc.T("btn.reset.global"), Loc.T("btn.reset"), danger: true))
            return;

        factory.Color = ColorSettings.Neutral;
        cfg.FactoryDefaults = factory.Clone();
        cfg.Defaults = factory.Clone();
        App.Services.Config.Save(cfg, raiseChanged: false);

        using (SuppressEditor())
        {
            if (!string.IsNullOrWhiteSpace(factory.Resolution) && !CmbDefaultRes.Items.Contains(factory.Resolution))
                CmbDefaultRes.Items.Add(factory.Resolution);
            CmbDefaultRes.SelectedItem = factory.Resolution;
            SelectPowerPlan(CmbDefaultPower, factory.PowerPlan, "");
        }

        App.Services.Monitor.EmergencyRestore();
        ShowToast(Loc.T("toast.defaults.reset"));
    }

    private void RefreshLog_Click(object sender, RoutedEventArgs e) => RefreshLog();

    private void RefreshLog()
    {
        try
        {
            TxtLog.Text = string.Join(Environment.NewLine, AppLog.Tail(400));
            TxtLog.ScrollToEnd();
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Refresh log view");
        }
    }

    private void LogWrap_Changed(object sender, RoutedEventArgs e)
    {
        if (TxtLog == null) return;
        bool wrap = ChkLogWrap.IsChecked == true;
        TxtLog.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        TxtLog.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
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

    // -- Autosave ---------------------------------------------------------------------------------------

    private void MarkDirty()
    {
        _dirty = true;
        UpdateTitleDirtyState();
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void FlushAutosave()
    {
        _autoSaveTimer.Stop();
        if (!_dirty) return;
        if (_selected != null) PushEditorToSelected();
        PushPresetEditorIfDirty();
        SyncPresetsBackToGame();
        PersistConfigToDisk();
    }

    private void PersistConfigToDisk()
    {
        var cfg = App.Services.Config.Current;
        cfg.Profiles = _profiles.Select(p => p.Clone()).ToList();
        foreach (var p in cfg.Profiles)
            p.ApplyColor = false;
        cfg.Presets = null;
        cfg.Defaults.Resolution = CmbDefaultRes.SelectedItem?.ToString() ?? cfg.Defaults.Resolution;
        var defPower = (CmbDefaultPower.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        cfg.Defaults.PowerPlan = string.IsNullOrWhiteSpace(defPower) ? null : defPower;
        cfg.Defaults.Color = ColorSettings.Neutral;

        if (cfg.FactoryDefaults == null || string.IsNullOrWhiteSpace(cfg.FactoryDefaults.Resolution))
            cfg.FactoryDefaults = ConfigService.CaptureFactoryDefaults();
        cfg.FactoryDefaults.Color = ColorSettings.Neutral;

        cfg.GlobalHotkeys ??= new GlobalHotkeys();
        cfg.GlobalHotkeys.ToggleOverlay = NormHotkey(HkToggleOverlay.Text);
        cfg.GlobalHotkeys.EmergencyRestore = NormHotkey(HkEmergency.Text);
        cfg.GlobalHotkeys.NextPreset = NormHotkey(HkNextPreset.Text);
        cfg.GlobalHotkeys.PreviousPreset = NormHotkey(HkPrevPreset.Text);
        cfg.GlobalHotkeys.CompareAb = NormHotkey(HkCompareAb.Text);
        cfg.GlobalHotkeys.ToggleZoom = NormHotkey(HkToggleZoom.Text);
        cfg.Ui ??= new UiPreferences();
        cfg.Ui.ZoomFactor = ReadZoomFactor();
        App.Services.Zoom.ApplyFromConfig(cfg.Ui);

        cfg.ConfigVersion = ConfigService.CurrentVersion;

        App.Services.Config.Save(cfg, raiseChanged: false);
        RefreshHotkeyBindings();
        App.Services.Monitor.NotifyConfigSaved();
        _dirty = false;
        UpdateTitleDirtyState();
        SetStatus(Loc.T("status.saved"));
    }

    // -- Presets (per-game) ------------------------------------------------------------------------------

    /// <summary>
    /// Presets are per-game. When opening the Presets tab (or when a game starts), select that game
    /// so its hotkeyed presets are visible.
    /// </summary>
    public void FocusPresetsForActiveOrSelectedGame(GameProfile? prefer = null)
    {
        prefer ??= _presetGame == null ? (App.Services.Monitor.CurrentProfile ?? _selected ?? _profiles.FirstOrDefault()) : null;
        if (prefer == null) return;

        var match = _profiles.FirstOrDefault(p => p.Id == prefer.Id)
                    ?? _profiles.FirstOrDefault(p =>
                        string.Equals(p.ProcessName, prefer.ProcessName, StringComparison.OrdinalIgnoreCase));
        if (match == null || ReferenceEquals(match, _presetGame)) return;
        SelectPresetGame(match);
    }

    private void PresetGameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SuppressingEditor) return;
        SelectPresetGame(PresetGameList.SelectedItem as GameProfile);
    }

    /// <summary>Switches the Presets tab to a game and explicitly loads the editor for the chosen preset.</summary>
    private void SelectPresetGame(GameProfile? game, string? preferPresetId = null)
    {
        PushPresetEditorIfDirty();
        SyncPresetsBackToGame();

        using (SuppressEditor())
        {
            // Detach before Clear — otherwise a sync could write an empty list into the old game.
            _selectedPreset = null;
            _presetGame = null;
            _presets.Clear();
            PresetGameList.SelectedItem = game;
            _presetGame = game;

            if (game != null)
            {
                game.Presets ??= new List<QuickPreset>();
                foreach (var p in game.Presets)
                    _presets.Add(p);
                LblPresetGame.Text = Loc.Tf("presets.for", game.Name);
                PresetGameList.ScrollIntoView(game);
            }
            else
            {
                LblPresetGame.Text = Loc.T("tab.presets");
            }
        }

        var pick = (preferPresetId != null ? _presets.FirstOrDefault(p => p.Id == preferPresetId) : null)
                   ?? _presets.FirstOrDefault();
        SelectPreset(pick);

        bool hasGame = game != null;
        BtnPresetAdd.IsEnabled = hasGame;
        BtnPresetExport.IsEnabled = hasGame && _presets.Count > 0;
        BtnPresetImport.IsEnabled = hasGame;
        BtnPackGallery.IsEnabled = hasGame;
        RefreshHotkeyBindings();
        UpdatePresetEmptyCta();
    }

    /// <summary>Selects a preset in the list AND loads it into the editor (selection under suppression never does that by itself).</summary>
    private void SelectPreset(QuickPreset? p)
    {
        if (!ReferenceEquals(_selectedPreset, p))
            PushPresetEditorIfDirty();
        using (SuppressEditor())
            PresetList.SelectedItem = p;
        _selectedPreset = p;
        LoadPresetEditor(p);
        if (p != null) PresetList.ScrollIntoView(p);
        BtnPresetDelete.IsEnabled = p != null;
    }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SuppressingEditor) return;
        var next = PresetList.SelectedItem as QuickPreset;
        if (ReferenceEquals(next, _selectedPreset)) return;
        SelectPreset(next);
    }

    private void SyncPresetsBackToGame()
    {
        if (_presetGame == null) return;
        _presetGame.Presets = _presets.ToList();
        // A deleted startup preset must not linger as a dangling id.
        if (_presetGame.StartupPresetId != null && _presetGame.Presets.All(p => p.Id != _presetGame.StartupPresetId))
            _presetGame.StartupPresetId = null;
        if (_selected != null && _selected.Id == _presetGame.Id)
        {
            using (SuppressEditor())
                FillStartupPresetCombo(_selected, _selected.StartupPresetId);
            UpdateColorSummary(_selected);
        }
    }

    private void LoadPresetEditor(QuickPreset? p)
    {
        _presetEditorDirty = false;
        using (SuppressEditor())
        {
            bool has = p != null;
            PresetEditor.IsEnabled = has;
            PresetEditor.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            LblPresetEditorEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            LblPresetEditorEmpty.Text = _presetGame == null ? Loc.T("presets.pick.game") : Loc.T("presets.pick.preset");

            if (p == null)
            {
                // Reset ALL fields so nothing from the previous preset can be pushed into another one.
                TxtPresetName.Text = "";
                TxtPresetHotkey.Text = "";
                ChkPresetRes.IsChecked = false;
                CmbPresetRes.SelectedIndex = -1;
                ChkPresetLock.IsChecked = false;
                var neutral = ColorSettings.Neutral;
                _presetEditorBackend = neutral.Backend;
                ColorUiHelper.SetBackendToggle(neutral.Backend, TogPresetBackend);
                ColorUiHelper.ApplyColorSliders(neutral, SldPresetB, SldPresetC, SldPresetG, SldPresetV, SldPresetShadow, SldPresetH);
                UpdatePresetLabels();
                UpdateDependentEnables();
                return;
            }

            TxtPresetName.Text = p.Name;
            TxtPresetHotkey.Text = p.Hotkey ?? "";
            _hotkeyOriginal[TxtPresetHotkey] = TxtPresetHotkey.Text;
            ChkPresetRes.IsChecked = p.ApplyResolution;
            p.EnsureDualColorSlots();
            _presetEditorBackend = p.Color.Backend;
            ColorUiHelper.SetBackendToggle(p.Color.Backend, TogPresetBackend);
            ChkPresetLock.IsChecked = p.Color.LockColor;
            if (!string.IsNullOrWhiteSpace(p.Resolution))
            {
                if (!CmbPresetRes.Items.Contains(p.Resolution)) CmbPresetRes.Items.Add(p.Resolution);
                CmbPresetRes.SelectedItem = p.Resolution;
            }
            else
                CmbPresetRes.SelectedItem = DisplayEngine.GetCurrentResolution();
            ColorUiHelper.ApplyColorSliders(p.Color, SldPresetB, SldPresetC, SldPresetG, SldPresetV, SldPresetShadow, SldPresetH);
            UpdatePresetLabels();
            UpdatePresetBackendDependent();
            UpdateBackendActiveLabel(ColorUiHelper.IsLowLevel(p.Color.Backend));
            UpdateDependentEnables();
        }
    }

    /// <summary>Name typing must not Refresh the list (that reloads the TextBox and eats characters).</summary>
    private void PresetName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SuppressingEditor || _selectedPreset == null) return;
        _selectedPreset.Name = TxtPresetName.Text;
        _presetEditorDirty = true;
        MarkDirty();
    }

    private void PresetName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_selectedPreset == null || SuppressingEditor) return;
        var trimmed = TxtPresetName.Text.Trim();
        if (trimmed.Length == 0) trimmed = Loc.T("preset.new");
        _selectedPreset.Name = trimmed;
        if (TxtPresetName.Text != trimmed)
        {
            using (SuppressEditor())
                TxtPresetName.Text = trimmed;
        }
        RefreshPresetListSafe();
        SyncPresetsBackToGame();
        MarkDirty();
    }

    private void PresetEditorChanged(object sender, RoutedEventArgs e)
    {
        if (SuppressingEditor || _selectedPreset == null) return;
        UpdateDependentEnables();
        if (ReferenceEquals(sender, ChkPresetRes) && ChkPresetRes.IsChecked == true && CmbPresetRes.SelectedItem == null)
        {
            using (SuppressEditor())
                CmbPresetRes.SelectedItem = DisplayEngine.GetCurrentResolution();
        }
        MarkDirty();
        PushPresetEditor();
        RefreshPresetListSafe();
    }

    private void PresetColor_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SuppressingEditor) return;
        UpdatePresetLabels();
        if (_selectedPreset == null) return;
        _selectedPreset.ApplyColor = true;
        MarkDirty();
        PushPresetEditor();
        // Don't refresh the list on every slider tick — debounce the summary update.
        _presetListRefreshTimer.Stop();
        _presetListRefreshTimer.Start();
    }

    private void RefreshPresetListSafe()
    {
        using (SuppressEditor())
        {
            PresetList.Items.Refresh();
            if (_selectedPreset != null && !ReferenceEquals(PresetList.SelectedItem, _selectedPreset))
                PresetList.SelectedItem = _selectedPreset;
        }
        UpdatePresetEmptyCta();
    }

    private void UpdatePresetEmptyCta()
    {
        bool empty = _presets.Count == 0 && _presetGame != null;
        PresetEmptyCta.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        LblPresetEmpty.Text = Loc.T("presets.empty");
        BtnPresetEmptyGallery.Content = Loc.T("presets.empty.gallery");
        BtnPresetExport.IsEnabled = _presetGame != null && _presets.Count > 0;
    }

    private void UpdatePresetLabels()
    {
        var backend = ColorUiHelper.ReadBackendToggle(TogPresetBackend);
        ColorUiHelper.UpdateValues(ValPresetB, ValPresetC, ValPresetG, ValPresetV, SldPresetB, SldPresetC, SldPresetG, SldPresetV,
            backend, ValPresetH, SldPresetH, ValPresetShadow, SldPresetShadow);
    }

    /// <summary>Shadows only exist on the Low Level ramp, hue only on the driver path.</summary>
    private void UpdatePresetBackendDependent()
    {
        bool low = ColorUiHelper.ReadBackendToggle(TogPresetBackend) == ColorBackend.LowLevel;
        SldPresetShadow.IsEnabled = low;
        LblPresetShadow.Opacity = low ? 1.0 : 0.45;
        ValPresetShadow.Opacity = low ? 1.0 : 0.45;
        SldPresetH.IsEnabled = !low;
        LblPresetH.Opacity = low ? 0.45 : 1.0;
        ValPresetH.Opacity = low ? 0.45 : 1.0;
    }

    private void PresetBackendToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (SuppressingEditor) return;
        var next = ColorUiHelper.ReadBackendToggle(TogPresetBackend);

        if (_selectedPreset == null || next == _presetEditorBackend)
        {
            UpdateBackendActiveLabel(next == ColorBackend.LowLevel);
            UpdatePresetLabels();
            UpdatePresetBackendDependent();
            return;
        }

        _selectedPreset.EnsureDualColorSlots();
        var prev = ColorUiHelper.ReadColorFromSliders(_presetEditorBackend, SldPresetB, SldPresetC, SldPresetG, SldPresetV, SldPresetShadow, ChkPresetLock.IsChecked == true, SldPresetH);
        _selectedPreset.Color = prev;
        _selectedPreset.SaveActiveToDualSlots();
        _presetEditorBackend = next;
        var loaded = _selectedPreset.ActivateBackend(next);

        using (SuppressEditor())
        {
            ColorUiHelper.ApplyColorSliders(loaded, SldPresetB, SldPresetC, SldPresetG, SldPresetV, SldPresetShadow, SldPresetH);
            ChkPresetLock.IsChecked = loaded.LockColor;
            ColorUiHelper.ConfigureGammaRangeForBackend(next, SldPresetG);
        }

        UpdatePresetLabels();
        UpdatePresetBackendDependent();
        UpdateBackendActiveLabel(next == ColorBackend.LowLevel);
        MarkDirty();
        PushPresetEditor();
        RefreshPresetListSafe();
    }

    private void UpdateDriverUiAvailability()
    {
        ColorUiHelper.LabelBackendToggle(TogPresetBackend);
        TogPresetBackend.IsEnabled = true;
    }

    private void UpdateBackendActiveLabel(bool lowLevel)
    {
        LblPresetBackendActive.Text = lowLevel
            ? Loc.T("color.backend.low.active")
            : Loc.Tf("color.backend.driver.active", GpuVendorDetect.DriverLabel);
    }

    private void PushPresetEditorIfDirty()
    {
        if (_selectedPreset != null && _presetEditorDirty)
            PushPresetEditor();
    }

    private void PushPresetEditor()
    {
        _presetEditorDirty = false;
        if (_selectedPreset == null) return;
        // Keep live name from TextBox (trim only on LostFocus) so mid-type spaces aren't eaten.
        _selectedPreset.Name = ReferenceEquals(Keyboard.FocusedElement, TxtPresetName)
            ? TxtPresetName.Text
            : TxtPresetName.Text.Trim();
        _selectedPreset.Hotkey = string.IsNullOrWhiteSpace(TxtPresetHotkey.Text) ? null : TxtPresetHotkey.Text.Trim();
        _selectedPreset.ApplyResolution = ChkPresetRes.IsChecked == true;
        // Keep ApplyColor as-is (res-only presets stay false until color sliders change).
        _selectedPreset.Resolution = CmbPresetRes.SelectedItem?.ToString() ?? _selectedPreset.Resolution;

        var c = ColorUiHelper.ReadColorFromSliders(
            ColorUiHelper.ReadBackendToggle(TogPresetBackend),
            SldPresetB, SldPresetC, SldPresetG, SldPresetV, SldPresetShadow,
            ChkPresetLock.IsChecked == true, SldPresetH);
        // Guard against saving unloaded slider defaults (0/0/0/0) over a good preset.
        if (c.Brightness <= 0.02 && c.Contrast <= 0.02 && c.Vibrance == 0
            && _selectedPreset.Color != null
            && !(_selectedPreset.Color.Brightness <= 0.02 && _selectedPreset.Color.Contrast <= 0.02 && _selectedPreset.Color.Vibrance == 0))
        {
            c = _selectedPreset.Color.Clone();
            c.Backend = ColorUiHelper.ReadBackendToggle(TogPresetBackend);
        }

        _selectedPreset.Color = c;
        _presetEditorBackend = _selectedPreset.Color.Backend;
        _selectedPreset.SaveActiveToDualSlots();
    }

    private string UniquePresetName(string baseName)
    {
        if (_presets.All(p => !string.Equals(p.Name, baseName, StringComparison.OrdinalIgnoreCase)))
            return baseName;
        for (int i = 2; i < 100; i++)
        {
            var candidate = $"{baseName} {i}";
            if (_presets.All(p => !string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return baseName;
    }

    private void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_presetGame == null)
        {
            ShowToast(Loc.T("presets.pick.game"));
            return;
        }
        var p = new QuickPreset
        {
            Id = _presetGame.Id + "_" + Guid.NewGuid().ToString("N")[..8],
            Name = UniquePresetName(Loc.T("preset.new")),
            ApplyColor = true,
            Color = ColorSettings.Neutral,
            Hotkey = null
        };
        p.EnsureDualColorSlots();
        _presets.Add(p);
        SyncPresetsBackToGame();
        SelectPreset(p);
        MarkDirty();
        UpdatePresetEmptyCta();
        Dispatcher.BeginInvoke(() =>
        {
            TxtPresetName.Focus();
            TxtPresetName.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPreset == null || _presetGame == null) return;
        var victim = _selectedPreset;
        var game = _presetGame;
        if (!ConfirmDestructive(Loc.Tf("confirm.deletePreset", victim.Name, game.Name), Loc.T("confirm.deletePreset.title"), Loc.T("btn.delete")))
            return;

        PushPresetEditorIfDirty();
        int index = _presets.IndexOf(victim);
        bool wasStartup = game.StartupPresetId == victim.Id;
        _selectedPreset = null; // don't push the deleted preset back
        using (SuppressEditor())
            _presets.Remove(victim);
        SyncPresetsBackToGame(); // persists even when the list is now empty
        SelectPreset(_presets.Count == 0 ? null : _presets[Math.Clamp(index, 0, _presets.Count - 1)]);
        MarkDirty();
        FlushAutosave();
        RefreshHotkeyBindings();
        UpdatePresetEmptyCta();

        ShowUndoToast(Loc.Tf("toast.deleted", victim.Name), () =>
        {
            if (!ReferenceEquals(_presetGame, game))
                SelectPresetGame(game);
            using (SuppressEditor())
                _presets.Insert(Math.Clamp(index, 0, _presets.Count), victim);
            if (wasStartup) game.StartupPresetId = victim.Id;
            SyncPresetsBackToGame();
            SelectPreset(victim);
            MarkDirty();
            FlushAutosave();
            UpdatePresetEmptyCta();
            ShowToast(Loc.Tf("toast.restored", victim.Name));
        });
    }

    private void ApplyPresetNow_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPreset == null) return;
        PushPresetEditorIfDirty();
        MarkDirty();
        FlushAutosave();
        App.Services.Monitor.ApplyPreset(_selectedPreset);
        ShowToast(Loc.Tf("toast.preset.applied", _selectedPreset.Name));
        RefreshPlayTab();
    }

    private void ExportPresets_Click(object sender, RoutedEventArgs e)
    {
        if (_presetGame == null)
        {
            ShowToast(Loc.T("presets.pick.game"));
            return;
        }
        PushPresetEditorIfDirty();
        SyncPresetsBackToGame();
        if (PresetPackService.Export(_presetGame))
            ShowToast(Loc.T("toast.presets.exported"));
    }

    private void ImportPresets_Click(object sender, RoutedEventArgs e)
    {
        if (_presetGame == null)
        {
            ShowToast(Loc.T("presets.pick.game"));
            return;
        }
        PushPresetEditorIfDirty();
        SyncPresetsBackToGame();
        var game = _presetGame;
        int before = game.Presets?.Count ?? 0;
        int n = PresetPackService.Import(game);
        if (n <= 0) return;

        var firstNew = game.Presets?.Skip(before).FirstOrDefault();
        SelectPresetGame(game, firstNew?.Id);
        MarkDirty();
        FlushAutosave();
        ShowToast(Loc.Tf("toast.presets.imported", n));
    }

    private void PackGallery_Click(object sender, RoutedEventArgs e)
    {
        var win = new PackGalleryWindow { Owner = this };
        win.AcceptTargetGame((_presetGame ?? _selected)?.Name);
        win.ShowDialog();
    }

    /// <summary>
    /// Apply a built-in preset pack onto the Presets-tab game. Re-applying the same pack replaces presets
    /// with the same name (keeping their Id and the user's hotkey) instead of duplicating them.
    /// </summary>
    public void ApplyPackPresets(IEnumerable<QuickPreset> presets, string packName)
    {
        var game = _presetGame ?? _selected;
        if (game == null)
        {
            UiSound.Error();
            ThemedDialog.Show(this, Loc.T("presets.pick.game"), Loc.T("presets.gallery"));
            return;
        }
        if (!ReferenceEquals(game, _presetGame))
            SelectPresetGame(game);

        PushPresetEditorIfDirty();
        SyncPresetsBackToGame();

        int added = 0, replaced = 0;
        QuickPreset? last = null;
        foreach (var src in presets)
        {
            var clone = QuickPreset.CloneOf(src);
            var existing = _presets.FirstOrDefault(p => string.Equals(p.Name?.Trim(), src.Name?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                clone.Id = existing.Id;
                clone.Hotkey = existing.Hotkey ?? (HotkeyFreeInGame(clone.Hotkey, existing.Id) ? clone.Hotkey : null);
                using (SuppressEditor())
                    _presets[_presets.IndexOf(existing)] = clone;
                replaced++;
            }
            else
            {
                clone.Id = game.Id + "_" + Guid.NewGuid().ToString("N")[..8];
                if (!HotkeyFreeInGame(clone.Hotkey, null)) clone.Hotkey = null;
                using (SuppressEditor())
                    _presets.Add(clone);
                added++;
            }
            last = clone;
        }

        _selectedPreset = null;
        SyncPresetsBackToGame();
        SelectPreset(last ?? _presets.FirstOrDefault());
        MarkDirty();
        FlushAutosave();
        RefreshPresetListSafe();
        ShowToast(Loc.Tf("toast.pack.applied", packName, added, replaced));
    }

    // -- Hotkey capture -----------------------------------------------------------------------------------

    private IEnumerable<(System.Windows.Controls.TextBox Box, string LabelKey)> GlobalHotkeyBoxes()
    {
        yield return (HkToggleOverlay, "hotkey.overlay");
        yield return (HkEmergency, "hotkey.emergency");
        yield return (HkNextPreset, "hotkey.next");
        yield return (HkPrevPreset, "hotkey.prev");
        yield return (HkCompareAb, "hotkey.compareAb");
        yield return (HkToggleZoom, "hotkey.zoom");
    }

    private static bool SameGesture(string? a, uint mods, uint vk) =>
        !string.IsNullOrWhiteSpace(a)
        && HotkeyService.TryParseGesture(a!, out var am, out var av)
        && am == mods && av == vk;

    private bool HotkeyFreeInGame(string? gesture, string? ignorePresetId)
    {
        if (string.IsNullOrWhiteSpace(gesture) || !HotkeyService.TryParseGesture(gesture!, out var m, out var vk))
            return true;
        if (GlobalHotkeyBoxes().Any(g => SameGesture(g.Box.Text, m, vk))) return false;
        return !_presets.Any(p => p.Id != ignorePresetId && SameGesture(p.Hotkey, m, vk));
    }

    /// <summary>Localized description of what already uses this gesture, or null when it's free.</summary>
    private string? FindHotkeyConflict(System.Windows.Controls.TextBox box, string gesture)
    {
        if (!HotkeyService.TryParseGesture(gesture, out var mods, out var vk)) return null;

        foreach (var (other, labelKey) in GlobalHotkeyBoxes())
        {
            if (ReferenceEquals(other, box)) continue;
            if (SameGesture(other.Text, mods, vk))
                return Loc.Tf("hotkey.conflict.global", Loc.T(labelKey));
        }

        // Preset hotkeys are per game; global hotkeys must not collide with any game's presets.
        IEnumerable<(GameProfile Game, IEnumerable<QuickPreset> Presets)> scope = ReferenceEquals(box, TxtPresetHotkey)
            ? (_presetGame != null ? new[] { (_presetGame, (IEnumerable<QuickPreset>)_presets) } : Array.Empty<(GameProfile, IEnumerable<QuickPreset>)>())
            : _profiles.Select(p => (p, ReferenceEquals(p, _presetGame) ? (IEnumerable<QuickPreset>)_presets : p.Presets ?? new List<QuickPreset>()));

        foreach (var (game, list) in scope)
        {
            foreach (var preset in list)
            {
                if (ReferenceEquals(box, TxtPresetHotkey) && ReferenceEquals(preset, _selectedPreset)) continue;
                if (SameGesture(preset.Hotkey, mods, vk))
                    return Loc.Tf("hotkey.conflict.preset", preset.Name, game.Name);
            }
        }
        return null;
    }

    private void HotkeyBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        _hotkeyOriginal[box] = box.Text;
        box.Tag = Loc.T("hotkey.listening");
        box.SelectAll();
    }

    private void HotkeyBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box)
            box.Tag = Loc.T("hotkey.placeholder");
    }

    private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        // Tab / Shift+Tab keep normal keyboard navigation.
        if (key == Key.Tab && (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
            return;

        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        // Esc cancels capture and keeps the previous binding.
        if (key == Key.Escape && mods == ModifierKeys.None)
        {
            box.Text = _hotkeyOriginal.TryGetValue(box, out var orig) ? orig : box.Text;
            Keyboard.ClearFocus();
            return;
        }

        // Backspace / Delete clear the binding.
        if ((key is Key.Back or Key.Delete) && mods == ModifierKeys.None)
        {
            CommitHotkey(box, "");
            return;
        }

        var gesture = HotkeyService.GestureFromKeys(mods, key);
        ApplyCapturedGesture(box, gesture);
    }

    /// <summary>
    /// Captures mouse XButton1/XButton2 as hotkeys on the same textboxes as keyboard capture
    /// (see HotkeyService's WH_MOUSE_LL fallback for how these are recognized at runtime).
    /// </summary>
    private void HotkeyBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2))
            return;

        var gesture = HotkeyService.GestureFromMouse(Keyboard.Modifiers, e.ChangedButton);
        if (gesture == null) return;

        e.Handled = true;
        box.Focus();
        ApplyCapturedGesture(box, gesture);
    }

    /// <summary>Conflicts are blocked: the old binding stays and the message names who owns the key.</summary>
    private void ApplyCapturedGesture(System.Windows.Controls.TextBox box, string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return;
        var conflict = FindHotkeyConflict(box, gesture);
        if (conflict != null)
        {
            UiSound.Warn();
            box.Text = _hotkeyOriginal.TryGetValue(box, out var orig) ? orig : box.Text;
            box.SelectAll();
            ShowToast(Loc.Tf("hotkey.conflict.blocked", gesture, conflict));
            return;
        }
        CommitHotkey(box, gesture);
        box.SelectAll();
    }

    private void CommitHotkey(System.Windows.Controls.TextBox box, string gesture)
    {
        if (ReferenceEquals(box, TxtPresetHotkey) && _selectedPreset == null) return;
        box.Text = gesture;
        _hotkeyOriginal[box] = gesture;
        if (ReferenceEquals(box, TxtPresetHotkey))
        {
            PushPresetEditor();
            RefreshPresetListSafe();
        }
        MarkDirty();
        FlushAutosave(); // persists and re-registers hotkeys immediately
        RefreshPlayTab();
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: System.Windows.Controls.TextBox box })
            return;
        CommitHotkey(box, "");
    }

    private static string? NormHotkey(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // -- Audio / session ------------------------------------------------------------------------------------

    private void FillAudioDevices()
    {
        var prev = (CmbAudio.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        using (SuppressEditor())
        {
            CmbAudio.Items.Clear();
            CmbAudio.Items.Add(new ComboBoxItem { Content = Loc.T("session.audio.none"), Tag = "" });
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
    }

    private void SelectAudioDevice(string? id)
    {
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
        using (SuppressEditor())
        {
            ChkQuietToast.IsChecked = quiet;
            ChkNoNightLight.IsChecked = night;
            ChkNoAutoHdr.IsChecked = hdr;
            SelectComboByTag(CmbMonitorLayout, layout);
            SelectComboByTag(CmbScaling, scaling);
            ChkMonBright.IsChecked = bright;
            if (bright) SldMonBright.Value = brightPct;
            SldDeferred.Value = 0;
        }
        UpdateSessionLabels();
        UpdateDependentEnables();
        MarkDirty();
        PushEditorToSelected();
        ShowToast(Loc.T("toast.session.template"));
    }

    // -- Helpers ----------------------------------------------------------------------------------------------

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

    /// <summary>
    /// Selects a power plan by tag. null/empty → <paramref name="fallbackTag"/>. Unknown values (custom plan GUIDs
    /// captured from Windows) get a "Current plan (custom)" item so they survive a save instead of being replaced.
    /// </summary>
    private static void SelectPowerPlan(System.Windows.Controls.ComboBox cmb, string? plan, string fallbackTag)
    {
        foreach (var stale in cmb.Items.OfType<ComboBoxItem>().Where(i => i.Uid == "customPlan").ToList())
            cmb.Items.Remove(stale);

        if (string.IsNullOrWhiteSpace(plan))
        {
            SelectComboByTag(cmb, fallbackTag);
            return;
        }
        bool known = cmb.Items.OfType<ComboBoxItem>()
            .Any(i => string.Equals(i.Tag?.ToString(), plan, StringComparison.OrdinalIgnoreCase));
        if (!known)
            cmb.Items.Add(new ComboBoxItem { Content = Loc.T("power.custom"), Tag = plan, Uid = "customPlan", ToolTip = plan });
        SelectComboByTag(cmb, plan);
    }

    private static string PowerPlanLabel(string? plan) => plan?.ToLowerInvariant() switch
    {
        null or "" => Loc.T("power.keep"),
        "highperformance" => Loc.T("power.high"),
        "balanced" => Loc.T("power.balanced"),
        _ => Loc.T("power.custom")
    };

    private int ReadZoomFactor()
    {
        if (CmbZoomFactor.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out int f)
            && f >= 2 && f <= 12)
            return f;
        return App.Services.Config.Current.Ui?.ZoomFactor ?? 4;
    }
}
