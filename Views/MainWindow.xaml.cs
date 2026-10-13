using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
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

/// <summary>Shell: ctor, lifecycle, window chrome / Win32 / DPI, keyboard routing, navigation, command palette, toasts, autosave.</summary>
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

    private Action? _toastAction;
    private bool _restoreMaximized;

    // Service subscriptions are kept as fields so OnClosed can unsubscribe them (the window is a singleton,
    // but the services outlive a crashed/re-created window and must not call into a dead one).
    private readonly Action _onLocChanged;
    private readonly Action<string> _onMonitorStatusChanged;
    private readonly Action<GameProfile?> _onActiveProfileChanged;
    private readonly Action _onHistoryChanged;
    private readonly Action _onConfigChanged;
    private readonly CancellationTokenSource _gpuDetectCts = new();
    /// <summary>Locale the labels were last built for — lets the Loc.Changed and explicit ApplyLocalization triggers dedupe.</summary>
    private string? _appliedLocale;
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

    static MainWindow()
    {
        // Class handler (not per-instance): the click chime for primary buttons app-wide.
        EventManager.RegisterClassHandler(
            typeof(System.Windows.Controls.Button),
            System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            new RoutedEventHandler(OnAnyButtonClick),
            handledEventsToo: true);
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
        PresetGameList.ItemsSource = _profiles;
        CmbPresetGame.ItemsSource = _profiles;
        PresetList.ItemsSource = _presets;
        TitleLogo.Source = AssetLoader.AppIcon(32);

        ApplyInitialBounds();
        LoadResolutions();
        FillDisplays();
        FillAudioDevices();
        _editorSuppress = 0;

        PruneDismissedRecommendations();
        ReloadFromConfig();
        ApplyLocalization();
        UpdateActiveHeader(App.Services.Monitor.CurrentProfile);
        RefreshGamesTab();

        Loaded += (_, _) => MaybeFirstScan();
        // Deferred so the explicit ApplyLocalization() that App/Settings call right after Loc.SetLocale wins
        // and this one becomes a no-op (one relabel per locale change instead of two).
        _onLocChanged = () => Dispatcher.BeginInvoke(() =>
        {
            if (!string.Equals(_appliedLocale, Loc.Locale, StringComparison.Ordinal))
                ApplyLocalization();
        }, DispatcherPriority.Background);
        Loc.Changed += _onLocChanged;

        MainTabs.SelectionChanged += MainTabs_SelectionChanged;
        _lastTabIndex = MainTabs.SelectedIndex;
        _onMonitorStatusChanged = _ => Dispatcher.BeginInvoke(() =>
        {
            RefreshPauseButton();
            RefreshHomeDashboard();
        });
        App.Services.Monitor.StatusChanged += _onMonitorStatusChanged;
        _onActiveProfileChanged = _ => Dispatcher.BeginInvoke(RefreshHomeDashboard);
        App.Services.Monitor.ActiveProfileChanged += _onActiveProfileChanged;
        _onHistoryChanged = () => Dispatcher.BeginInvoke(() =>
        {
            if (HomePageIsLive)
                RefreshHomeActivity();
            else
                _homeStale = true;
            if (RbMoreHistory?.IsChecked == true)
                RefreshHistoryPanel();
        });
        App.Services.History.Changed += _onHistoryChanged;
        _onConfigChanged = () => Dispatcher.BeginInvoke(RefreshHomeDashboard);
        App.Services.Config.ConfigChanged += _onConfigChanged;
        _ = GpuVendorDetect.WhenDetected.ContinueWith(_ =>
            Dispatcher.BeginInvoke(RefreshHomeDashboard),
            _gpuDetectCts.Token, TaskContinuationOptions.None, TaskScheduler.Default);
        RefreshPauseButton();
        SyncNavFromTabs();
        System.Windows.Automation.AutomationProperties.SetName(MainTabs, "");

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
            // Remembered bounds may come from a monitor that is gone / smaller / at another DPI.
            ApplyShellFitForCurrentMonitor();
        };
        // Debounce — DpiChanged fires mid-drag on mixed-DPI moves; clamping immediately jumps size.
        DpiChanged += (_, _) => ScheduleShellFitRefresh();
        LocationChanged += (_, _) => ScheduleShellFitRefresh();
        // Restore from maximized often keeps Left/Top (no LocationChanged); still re-fit RestoreBounds.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
                ScheduleShellFitRefresh();
            UpdateMaximizeCaption();
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>Real exit only (close-to-tray cancels Closing): detach everything that could call into this window.</summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try
        {
            _autoSaveTimer.Stop();
            _tileRefreshTimer.Stop();
            _presetListRefreshTimer.Stop();
            _statusTimer.Stop();
            _shellFitTimer?.Stop();
            _gpuDetectCts.Cancel();
            Loc.Changed -= _onLocChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            App.Services.Monitor.StatusChanged -= _onMonitorStatusChanged;
            App.Services.Monitor.ActiveProfileChanged -= _onActiveProfileChanged;
            App.Services.History.Changed -= _onHistoryChanged;
            App.Services.Config.ConfigChanged -= _onConfigChanged;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "MainWindow unsubscribe");
        }
    }

    /// <summary>Pages refresh only while visible; whatever went stale while hidden is rebuilt here.</summary>
    private void RefreshStalePages()
    {
        if (!IsVisible) return;
        if (_homeStale && ReferenceEquals(MainTabs.SelectedItem, TabHome))
            RefreshHomeDashboard();
        if (_systemStale && ReferenceEquals(MainTabs.SelectedItem, TabSystem))
            RefreshSystemPage(includeProcesses: false);
        if (_networkStale && ReferenceEquals(MainTabs.SelectedItem, TabNetwork))
            RefreshNetworkPage();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(ScheduleShellFitRefresh);

    private DispatcherTimer? _shellFitTimer;

    private void ScheduleShellFitRefresh()
    {
        // Debounce while the user drags between monitors — clamp after the move settles.
        _shellFitTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(280)
        };
        _shellFitTimer.Tick -= ShellFitTimer_Tick;
        _shellFitTimer.Tick += ShellFitTimer_Tick;
        _shellFitTimer.Stop();
        _shellFitTimer.Start();
    }

    private void ShellFitTimer_Tick(object? sender, EventArgs e)
    {
        _shellFitTimer?.Stop();
        if (WindowState == WindowState.Normal)
            ApplyShellFitForCurrentMonitor();
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
    /// Default size via <see cref="ShellFit"/> for 1366×768 @125% and 1080p @150%.
    /// A remembered size is only bounded from below here; the upper bound and position are enforced in
    /// physical pixels by <see cref="OverlayWin32.EnsureInsideWorkArea"/> once the HWND exists.
    /// </summary>
    private void ApplyInitialBounds()
    {
        var wa = WorkAreaDipForPoint(null);
        ApplyShellMinWidth(wa.Width);
        MinHeight = ShellFit.MinHeightFor(wa.Height);
        var def = ShellFit.DefaultSize(wa.Width, wa.Height);
        Width = def.Width;
        Height = def.Height;

        var st = UiStateStore.Current;
        if (st.WindowWidth is double sw && st.WindowHeight is double sh
            && !double.IsNaN(sw) && !double.IsNaN(sh) && sw > 100 && sh > 100)
        {
            Width = Math.Max(MinWidth, sw);
            Height = Math.Max(MinHeight, sh);
        }
        if (st.WindowLeft is double l && st.WindowTop is double t
            && !double.IsNaN(l) && !double.IsNaN(t) && !double.IsInfinity(l) && !double.IsInfinity(t))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
            // Recompute min against the monitor that owns the restored position.
            wa = WorkAreaDipForPoint(new System.Windows.Point(l + Width / 2, t + Height / 2));
            ApplyShellMinWidth(wa.Width);
            MinHeight = ShellFit.MinHeightFor(wa.Height);

            // Single monitor: the pure DIP clamp is exact, so apply it before the HWND exists. With several
            // monitors DIPs differ per monitor DPI; the physical-pixel pass in SourceInitialized decides then.
            if (System.Windows.Forms.Screen.AllScreens.Length == 1)
            {
                var r = UiStateStore.ClampToWorkAreas(new Rect(Left, Top, Width, Height), new[] { wa });
                Left = r.Left;
                Top = r.Top;
                Width = Math.Max(MinWidth, r.Width);
                Height = Math.Max(MinHeight, r.Height);
            }
        }
        _restoreMaximized = st.WindowMaximized;
    }

    /// <summary>Sidebar + Presets columns prefer ≥1100 DIP when the work area allows it.</summary>
    private void ApplyShellMinWidth(double workAreaWidthDip)
    {
        MinWidth = ShellFit.MinWidthFor(workAreaWidthDip);
        if (Width > 0 && Width < MinWidth)
            Width = MinWidth;
    }

    /// <summary>
    /// Refresh MinWidth/MinHeight for the monitor under the window, shrink size if needed, and
    /// re-run the physical work-area clamp so a move/DPI change cannot leave the shell hanging off-screen.
    /// </summary>
    private void ApplyShellFitForCurrentMonitor()
    {
        if (WindowState != WindowState.Normal) return;

        var wa = WorkAreaDipForPoint(null);
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero && ActualWidth > 0)
        {
            try
            {
                var center = PointToScreen(new System.Windows.Point(ActualWidth / 2, ActualHeight / 2));
                wa = WorkAreaDipForPoint(new System.Windows.Point(center.X, center.Y), physicalPixels: true);
            }
            catch (InvalidOperationException) { /* not mapped yet */ }
        }

        ApplyShellMinWidth(wa.Width);
        MinHeight = ShellFit.MinHeightFor(wa.Height);

        double maxW = Math.Max(MinWidth, wa.Width - 16);
        double maxH = Math.Max(MinHeight, wa.Height - 16);
        if (Width > maxW) Width = maxW;
        if (Height > maxH) Height = maxH;

        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
            OverlayWin32.EnsureInsideWorkArea(this);
    }

    private Rect WorkAreaDipForPoint(System.Windows.Point? dipOrPhysical, bool physicalPixels = false)
    {
        System.Windows.Forms.Screen screen;
        if (dipOrPhysical is { } pt)
        {
            var px = physicalPixels
                ? new System.Drawing.Point((int)Math.Round(pt.X), (int)Math.Round(pt.Y))
                : DipToScreenPixels(pt);
            screen = System.Windows.Forms.Screen.FromPoint(px);
        }
        else
        {
            var handle = new WindowInteropHelper(this).Handle;
            screen = handle != IntPtr.Zero
                ? System.Windows.Forms.Screen.FromHandle(handle)
                : System.Windows.Forms.Screen.PrimaryScreen!;
        }

        double scaleX = 1, scaleY = 1;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } ct)
        {
            scaleX = ct.TransformToDevice.M11;
            scaleY = ct.TransformToDevice.M22;
        }
        else
        {
            // Pre-HWND: approximate with system DPI for the chosen screen's bounds.
            using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            scaleX = g.DpiX / 96.0;
            scaleY = g.DpiY / 96.0;
        }

        var wa = screen.WorkingArea;
        return new Rect(wa.Left / scaleX, wa.Top / scaleY, wa.Width / scaleX, wa.Height / scaleY);
    }

    private System.Drawing.Point DipToScreenPixels(System.Windows.Point dip)
    {
        double scaleX = 1, scaleY = 1;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } ct)
        {
            scaleX = ct.TransformToDevice.M11;
            scaleY = ct.TransformToDevice.M22;
        }
        else
        {
            // Match WorkAreaDipForPoint pre-HWND path (system DPI, not identity).
            using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            scaleX = g.DpiX / 96.0;
            scaleY = g.DpiY / 96.0;
        }
        return new System.Drawing.Point((int)Math.Round(dip.X * scaleX), (int)Math.Round(dip.Y * scaleY));
    }

    private void SaveWindowBounds()
    {
        try
        {
            var st = UiStateStore.Current;
            // ActualWidth/Height: the HWND may have been shrunk to the work area without touching Width/Height.
            var r = WindowState == WindowState.Normal
                ? new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height)
                : RestoreBounds;
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

    /// <summary>Bumped on every show/hide so a hide fade that is still running cannot hide a re-shown window.</summary>
    private int _visibilityGen;

    public void ShowWithFade()
    {
        _visibilityGen++;
        // Tray start / overlay hide the taskbar button — bring it back whenever the window is shown.
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized || _restoreMaximized)
            WindowState = _restoreMaximized ? WindowState.Maximized : WindowState.Normal;
        _restoreMaximized = false;
        // Per-monitor DPI / display layout may have changed while hidden in the tray.
        ApplyShellFitForCurrentMonitor();
        Activate();
        RefreshStalePages();
        UiMotion.PopIn(ChromeRoot);
        UiMotion.FadeSlideIn(ContentRoot, fromY: 8, ms: 200);
    }

    public void HideWithFade()
    {
        StopDesktopPreview();
        SaveWindowBounds();
        int gen = ++_visibilityGen;
        UiMotion.PopOut(ChromeRoot, () =>
        {
            if (gen != _visibilityGen) return; // re-shown while fading out
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

        if (MainTabs.SelectedItem != TabHome)
            MainTabs.SelectedItem = TabHome;
        RefreshStalePages();
        UpdateMaximizeCaption();

        ApplyChromeTheme();
        // Nested MaxHeight lists inside page ScrollViewers — don't let them trap the wheel.
        NestedScroll.ForwardWheelAtEdge(CompanionList);
        NestedScroll.ForwardWheelAtEdge(ProcessList);
    }

    private static DateTime _lastClickSoundUtc = DateTime.MinValue;

    /// <summary>Only "primary" action buttons get the click chime — Ghost/secondary buttons stay silent.</summary>
    private static readonly HashSet<string> ClickSoundButtons = new(StringComparer.Ordinal)
    {
        "BtnEmergency", "BtnScan", "BtnLaunchGame",
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

    /// <summary>The caption button toggles between Maximize and Restore for tooltips and screen readers.</summary>
    private void UpdateMaximizeCaption()
    {
        if (BtnMaximize == null) return;
        string text = Loc.T(WindowState == WindowState.Maximized ? "caption.restore" : "caption.maximize");
        BtnMaximize.ToolTip = text;
        System.Windows.Automation.AutomationProperties.SetName(BtnMaximize, text);
    }

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
            CancelNetworkProbes();
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
        CancelNetworkProbes();
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
                Key.D1 or Key.NumPad1 => TabHome,
                Key.D2 or Key.NumPad2 => TabGames,
                Key.D3 or Key.NumPad3 => TabPresets,
                Key.D4 or Key.NumPad4 => TabSystem,
                Key.D5 or Key.NumPad5 => TabNetwork,
                Key.D6 or Key.NumPad6 => TabMore,
                _ => null
            };
            if (target != null)
            {
                MainTabs.SelectedItem = target;
                SyncNavFromTabs();
                FocusNavForTab(target);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F)
            {
                MainTabs.SelectedItem = TabGames;
                Dispatcher.BeginInvoke(() =>
                {
                    TxtSearch.Focus();
                    TxtSearch.SelectAll();
                }, DispatcherPriority.Input);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.K)
            {
                OpenCommandPalette();
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
            SyncNavFromTabs();
            return;
        }

        if (_dirty)
            FlushAutosave();

        if (!ReferenceEquals(MainTabs.SelectedItem, TabGames) || RbGamesColor?.IsChecked != true)
            StopDesktopPreview();

        _lastTabIndex = newIndex;
        if (ReferenceEquals(MainTabs.SelectedItem, TabPresets))
        {
            FocusPresetsForActiveOrSelectedGame(_selected);
            // Color-tab sliders may have edited the same preset object — show fresh values.
            LoadPresetEditor(_selectedPreset);
        }
        if (ReferenceEquals(MainTabs.SelectedItem, TabHome))
            RefreshHomeDashboard();
        if (ReferenceEquals(MainTabs.SelectedItem, TabSystem))
            RefreshSystemPage(includeProcesses: false);
        if (ReferenceEquals(MainTabs.SelectedItem, TabNetwork))
            RefreshNetworkPage();
        else
            CancelNetworkProbes();
        if (ReferenceEquals(MainTabs.SelectedItem, TabGames))
        {
            if (RbGamesColor?.IsChecked == true)
                RefreshGamesTab();
        }
        if (ReferenceEquals(MainTabs.SelectedItem, TabMore))
        {
            if (RbMoreLog?.IsChecked == true)
                RefreshLog();
            if (RbMoreHistory?.IsChecked == true)
                RefreshHistoryPanel();
        }
        SyncNavFromTabs();
        // Soft nudge — avoid blanking opacity to 0 (hard PageFadeIn flash on every switch).
        if (MainTabs.SelectedContent is FrameworkElement fe)
            UiMotion.SoftContentIn(fe);
        // Programmatic navigation (Home buttons, palette, Ctrl+N) must not strand keyboard focus on a
        // control inside the now-collapsed page. Queued at Input priority so a caller that focuses a field
        // right after switching (Add game → name box, Ctrl+F → search) still wins.
        var target = MainTabs.SelectedItem as TabItem;
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(MainTabs.SelectedItem, target)) return;
            if (MainTabs.SelectedContent is FrameworkElement page && page.IsKeyboardFocusWithin) return;
            if (NavRail.IsKeyboardFocusWithin) return;
            FocusNavForTab(target);
        }, DispatcherPriority.Input);
    }

    private bool _suppressNav;

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressNav) return;
        if (sender is not System.Windows.Controls.RadioButton rb || rb.IsChecked != true) return;
        string tag = rb.Tag as string ?? "home";

        var tab = TabForNav(tag);
        if (tab != null && !ReferenceEquals(MainTabs.SelectedItem, tab))
            MainTabs.SelectedItem = tab;
        else
            UpdatePageTitle(tag);
    }

    private void NavGames_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        EggBusy?.NotifyProfilesClick();

    private void PauseMonitoring_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Monitor.IsPaused = !App.Services.Monitor.IsPaused;
        RefreshPauseButton();
        RefreshHomeDashboard();
        if (System.Windows.Application.Current is App app)
            app.RebuildTrayMenu();
    }

    private void RefreshPauseButton()
    {
        if (BtnPause == null) return;
        bool paused = App.Services.Monitor.IsPaused;
        BtnPause.Content = Loc.T(paused ? "nav.resume" : "nav.pause");
        BtnPause.ToolTip = Loc.T(paused ? "nav.resume.tip" : "nav.pause.tip");
        System.Windows.Automation.AutomationProperties.SetName(BtnPause, BtnPause.Content?.ToString() ?? "");
        if (BtnHomePause != null)
        {
            BtnHomePause.Content = Loc.T(paused ? "nav.resume" : "nav.pause");
            BtnHomePause.ToolTip = Loc.T(paused ? "nav.resume.tip" : "nav.pause.tip");
        }
    }

    private bool _paletteOpen;

    private void OpenCommandPalette()
    {
        if (_paletteOpen) return;
        _paletteOpen = true;
        try
        {
            var win = new CommandPaletteWindow(BuildCommandPaletteItems(), id => id switch
            {
                "cmd.scan" => !_scanRunning,
                "cmd.emergency" => !_systemBusy,
                _ => true
            }) { Owner = this };
            if (win.ShowDialog() == true && win.Selected != null)
                ExecuteCommandPalette(win.Selected);
        }
        finally
        {
            _paletteOpen = false;
        }
    }

    private List<CommandPaletteItem> BuildCommandPaletteItems()
    {
        string nav = Loc.T("palette.group.navigate");
        string actions = Loc.T("palette.group.actions");
        string games = Loc.T("palette.group.games");
        bool paused = App.Services.Monitor.IsPaused;

        var items = new List<CommandPaletteItem>
        {
            new() { Id = "nav.home", Title = Loc.T("nav.home"), Group = nav, SearchText = "ctrl+1" },
            new() { Id = "nav.games", Title = Loc.T("nav.games"), Group = nav, SearchText = "ctrl+2" },
            new() { Id = "nav.presets", Title = Loc.T("nav.presets"), Group = nav, SearchText = "ctrl+3" },
            new() { Id = "nav.system", Title = Loc.T("nav.system"), Group = nav, SearchText = "ctrl+4" },
            new() { Id = "nav.network", Title = Loc.T("nav.network"), Group = nav, SearchText = "ctrl+5" },
            new() { Id = "nav.tools", Title = Loc.T("nav.tools"), Group = nav, SearchText = "ctrl+6" },
            new() { Id = "nav.settings", Title = Loc.T("nav.settings"), Group = nav },
            new()
            {
                Id = paused ? "cmd.resume" : "cmd.pause",
                Title = Loc.T(paused ? "nav.resume" : "nav.pause"),
                Group = actions
            },
            new() { Id = "cmd.emergency", Title = Loc.T("btn.emergency"), Group = actions, SearchText = "reset display" },
            new() { Id = "cmd.scan", Title = Loc.T("btn.scan"), Group = actions },
            new() { Id = "cmd.add", Title = Loc.T("btn.add"), Subtitle = Loc.T("nav.games"), Group = actions },
            new() { Id = "cmd.overlay", Title = Loc.T("btn.overlay"), Group = actions },
            new() { Id = "cmd.history", Title = Loc.T("tab.history"), Group = actions },
        };

        foreach (var p in _profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            items.Add(new CommandPaletteItem
            {
                Id = "game." + p.Id,
                Title = p.Name,
                Subtitle = p.ProcessName,
                Group = games,
                SearchText = p.ExePath
            });
        }
        return items;
    }

    private void ExecuteCommandPalette(CommandPaletteItem item)
    {
        switch (item.Id)
        {
            case "nav.home":
                MainTabs.SelectedItem = TabHome;
                break;
            case "nav.games":
                MainTabs.SelectedItem = TabGames;
                break;
            case "nav.presets":
                MainTabs.SelectedItem = TabPresets;
                break;
            case "nav.system":
                MainTabs.SelectedItem = TabSystem;
                break;
            case "nav.network":
                MainTabs.SelectedItem = TabNetwork;
                break;
            case "nav.tools":
                MainTabs.SelectedItem = TabMore;
                break;
            case "nav.settings":
                Settings_Click(this, new RoutedEventArgs());
                break;
            case "cmd.pause":
            case "cmd.resume":
                PauseMonitoring_Click(this, new RoutedEventArgs());
                break;
            case "cmd.emergency":
                EmergencyRestore_Click(this, new RoutedEventArgs());
                break;
            case "cmd.scan":
                MainTabs.SelectedItem = TabGames;
                ScanGames_Click(this, new RoutedEventArgs());
                break;
            case "cmd.add":
                HomeAddGame_Click(this, new RoutedEventArgs());
                break;
            case "cmd.overlay":
                Overlay_Click(this, new RoutedEventArgs());
                break;
            case "cmd.history":
                HomeOpenHistory_Click(this, new RoutedEventArgs());
                break;
            default:
                if (item.Id.StartsWith("game.", StringComparison.Ordinal)
                    && item.Id.Length > 5)
                {
                    var id = item.Id["game.".Length..];
                    var match = _profiles.FirstOrDefault(p => p.Id == id);
                    if (match != null)
                    {
                        MainTabs.SelectedItem = TabGames;
                        SelectProfile(match);
                    }
                }
                break;
        }
        SyncNavFromTabs();
    }

    private void SyncNavFromTabs()
    {
        string tag = NavTagForTab(MainTabs.SelectedItem as TabItem);
        if (_suppressNav) return;
        _suppressNav = true;
        SelectNavRadio(tag);
        _suppressNav = false;
        UpdatePageTitle(tag);
    }

    private string NavTagForTab(TabItem? tab)
    {
        if (ReferenceEquals(tab, TabGames)) return "games";
        if (ReferenceEquals(tab, TabPresets)) return "presets";
        if (ReferenceEquals(tab, TabSystem)) return "system";
        if (ReferenceEquals(tab, TabNetwork)) return "network";
        if (ReferenceEquals(tab, TabMore)) return "tools";
        return "home";
    }

    private TabItem? TabForNav(string tag) => tag switch
    {
        "home" => TabHome,
        "games" or "play" or "library" => TabGames,
        "presets" => TabPresets,
        "system" => TabSystem,
        "network" => TabNetwork,
        "tools" => TabMore,
        _ => null
    };

    private void SelectNavRadio(string tag)
    {
        var rb = NavRadioForTag(tag);
        if (rb != null)
            rb.IsChecked = true;
    }

    private System.Windows.Controls.RadioButton? NavRadioForTag(string tag) => tag switch
    {
        "home" => NavHome,
        "games" or "play" or "library" => NavGames,
        "presets" => NavPresets,
        "system" => NavSystem,
        "network" => NavNetwork,
        "tools" => NavTools,
        _ => NavHome
    };

    private void FocusNavForTab(TabItem? tab) =>
        NavRadioForTag(NavTagForTab(tab))?.Focus();

    private void UpdatePageTitle(string tag)
    {
        if (LblPageTitle == null) return;
        LblPageTitle.Visibility = Visibility.Visible;
        LblPageTitle.Text = Loc.T(tag switch
        {
            "games" => "nav.games",
            "presets" => "nav.presets",
            "system" => "nav.system",
            "network" => "nav.network",
            "tools" => "nav.tools",
            _ => "nav.home"
        });
    }

    // -- Settings / localization -------------------------------------------------------------------

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        FlushAutosave();
        var win = new SettingsWindow { Owner = this };
        win.ShowDialog();
        // Live preview in Settings may have changed runtime sound state — restore from saved config if cancelled
        UiSound.ApplyFromConfig(App.Services.Config.Current.Ui);
        // Loc.Changed already relabeled while the dialog was open; only settings that change derived text remain.
        if (!string.Equals(_appliedLocale, Loc.Locale, StringComparison.Ordinal))
            ApplyLocalization();
        else
            RefreshHomeDashboard();
        ApplyChromeTheme();
        UpdateActiveHeader(App.Services.Monitor.CurrentProfile);
        if (win.Imported)
            ReloadFromConfig();
        if (System.Windows.Application.Current is App app)
            app.RebuildTrayMenu();
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
            ActiveDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SuccessBrush");
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
        RefreshGamesTab();
    }

    /// <summary>Ambient status in the title bar (fades after a few seconds). Not used for action feedback.</summary>
    public void SetStatus(string status)
    {
        // ProfileMonitor emits stable English tokens for pause/monitor; map for the active locale.
        StatusText.Text = status switch
        {
            "Paused" => Loc.T("status.paused"),
            "Monitoring" => Loc.T("status.monitoring"),
            _ => status
        };
        UiMotion.PulseOpacity(StatusText);
        UiMotion.AnnounceLiveRegion(StatusText);
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
        RefreshGamesTab();
        if (RbMoreLog.IsChecked == true)
            RefreshLog();
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
