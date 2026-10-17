using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Visor.Controls;
using Visor.Models;
using Visor.Services;

namespace Visor;

/// <summary>
/// Shell: chrome / Win32 / DPI, nav rail, top bar, page host, keyboard routing, command palette, toasts,
/// autosave + config reload. Page content lives in <c>Views\Pages</c>; pages talk back through <see cref="IShellHost"/>.
/// </summary>
public partial class MainWindow : Window, IShellHost
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20 = 19;
    private const string HelpUrl = "https://github.com/Naki-404/DisplayProfileManager";

    private readonly ObservableCollection<GameProfile> _profiles = new();
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer _statusTimer;

    private readonly HomePage _home;
    private readonly MyPcPage _myPc;
    private readonly GamesPage _games;
    private readonly OptimizePage _optimize;
    private readonly NetworkPage _network;
    private readonly MonitorPage _monitor;
    private readonly SettingsPage _settings;
    private readonly ShellPageBase[] _pages;

    private bool _dirty;
    private int _lastTabIndex;
    private Action? _toastAction;
    private bool _restoreMaximized;
    private bool _railCompact;
    private bool _suppressNav;
    private bool _suppressThemeCombo;
    private bool _paletteOpen;

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

    // -- Re-entrant suppression scope (shared by every page editor) ------------------------------------

    private int _editorSuppress;

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

    public bool SuppressingEditor => _editorSuppress > 0;

    public IDisposable SuppressEditor()
    {
        _editorSuppress++;
        return new Scope(() => _editorSuppress--);
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
        _editorSuppress = 1; // InitializeComponent (window + pages) fires Checked/SelectionChanged events
        // NavHome has IsChecked="True" in XAML: its Checked fires while InitializeComponent is still
        // parsing, before MainTabs (declared later) exists. Nav sync starts once the shell is built.
        _suppressNav = true;
        InitializeComponent();

        _home = new HomePage(this);
        _myPc = new MyPcPage(this);
        _games = new GamesPage(this);
        _optimize = new OptimizePage(this);
        _network = new NetworkPage(this);
        _monitor = new MonitorPage(this);
        _settings = new SettingsPage(this);
        _pages = new ShellPageBase[] { _home, _myPc, _games, _optimize, _network, _monitor, _settings };
        TabHome.Content = _home;
        TabMyPc.Content = _myPc;
        TabGames.Content = _games;
        TabOptimize.Content = _optimize;
        TabNetwork.Content = _network;
        TabMonitor.Content = _monitor;
        TabSettings.Content = _settings;
        _suppressNav = false;

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            if (!_dirty) return;
            // Always push editors first — otherwise dual-slot / slider state can save as 0/0/0/0.
            FlushAutosave();
        };
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusText.Text = "";
        };

        TitleLogo.Source = AssetLoader.AppIcon(32);
        RailLogo.Source = AssetLoader.AppIcon(32);
        LblMachine.Text = Environment.MachineName;

        ApplyInitialBounds();
        var ui = App.Services.Config.Current.Ui ?? new UiPreferences();
        InfoHint.TooltipMode = ui.TooltipMode;
        TogTooltipMode.IsChecked = ui.TooltipMode;
        SetRailCompact(ui.SidebarCollapsed, animate: false);
        _editorSuppress = 0;

        OptimizePage.PruneDismissedRecommendations();
        HistoryText.SafeActionTitleResolver = SafeActionUi.TryTitleFor;
        ReloadFromConfig();
        ApplyLocalization();
        UpdateActiveHeader(App.Services.Monitor.CurrentProfile);

        Loaded += (_, _) => _games.MaybeFirstScan();
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
            _home.Refresh();
            _optimize.Refresh();
        });
        App.Services.Monitor.StatusChanged += _onMonitorStatusChanged;
        _onActiveProfileChanged = _ => Dispatcher.BeginInvoke(() => _home.Refresh());
        App.Services.Monitor.ActiveProfileChanged += _onActiveProfileChanged;
        _onHistoryChanged = () => Dispatcher.BeginInvoke(() =>
        {
            _home.Refresh();
            _settings.OnHistoryChanged();
        });
        App.Services.History.Changed += _onHistoryChanged;
        _onConfigChanged = () => Dispatcher.BeginInvoke(() => _home.Refresh());
        App.Services.Config.ConfigChanged += _onConfigChanged;
        _ = GpuVendorDetect.WhenDetected.ContinueWith(_ =>
            Dispatcher.BeginInvoke(() => _home.Refresh()),
            _gpuDetectCts.Token, TaskContinuationOptions.None, TaskScheduler.Default);
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
            // Members may be null when the constructor failed part-way (startup error path closes the window).
            _autoSaveTimer?.Stop();
            _statusTimer?.Stop();
            _games?.StopTimers();
            _monitor?.Shutdown();
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
        CurrentPage?.RefreshIfStale();
    }

    private ShellPageBase? CurrentPage => MainTabs.SelectedContent as ShellPageBase;

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

    /// <summary>Sidebar + Games columns prefer ≥1100 DIP when the work area allows it.</summary>
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
        _games.StopDesktopPreview();
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
    }

    private static DateTime _lastClickSoundUtc = DateTime.MinValue;

    /// <summary>Only "primary" action buttons get the click chime — Ghost/secondary buttons stay silent.</summary>
    private static readonly HashSet<string> ClickSoundButtons = new(StringComparer.Ordinal)
    {
        "BtnScan", "BtnLaunchGame", "BtnApplyProfileNow", "BtnApplyPreset", "BtnApplyDefaults", "BtnHomeScan"
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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void NakidevCredit_Click(object sender, MouseButtonEventArgs e) => OpenUrl("https://t.me/nakidev");

    private void HelpLink_Click(object sender, RoutedEventArgs e) => OpenUrl(HelpUrl);

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Open URL");
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (App.Current is App app && !app.IsExitRequested)
        {
            if (_network == null) return; // constructor failed part-way — nothing to save, let it close
            _network.CancelProbes();
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
        _network.CancelProbes();
        _games.StopDesktopPreview();
        FlushAutosave();
        SaveWindowBounds();
        return true;
    }

    // -- Keyboard shortcuts ----------------------------------------------------------------------

    private bool IsHotkeyCaptureFocused() =>
        Keyboard.FocusedElement is System.Windows.Controls.TextBox tb
        && (_games.IsHotkeyBox(tb) || _settings.IsHotkeyBox(tb));

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (IsHotkeyCaptureFocused()) return; // let the capture box see every key (incl. Ctrl+1)

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShellSection? target = e.Key switch
            {
                Key.D1 or Key.NumPad1 => ShellSection.Home,
                Key.D2 or Key.NumPad2 => ShellSection.MyPc,
                Key.D3 or Key.NumPad3 => ShellSection.Games,
                Key.D4 or Key.NumPad4 => ShellSection.Optimize,
                Key.D5 or Key.NumPad5 => ShellSection.Network,
                Key.D6 or Key.NumPad6 => ShellSection.Monitor,
                Key.D7 or Key.NumPad7 => ShellSection.Settings,
                _ => null
            };
            if (target is { } section)
            {
                Navigate(section);
                FocusNavFor(section);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F)
            {
                Navigate(ShellSection.Games);
                _games.FocusSearch();
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

        if (e.Key == Key.Escape && _games.TryClearSearchOnEscape())
            e.Handled = true;
    }

    /// <summary>Del removes the selected item of the focused list (with confirmation).</summary>
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Keyboard.Modifiers != ModifierKeys.None) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        if (_games.HandleDeleteKey())
            e.Handled = true;
    }

    // -- Navigation ----------------------------------------------------------------------------------

    private static readonly ShellSection[] SectionOrder =
    {
        ShellSection.Home, ShellSection.MyPc, ShellSection.Games, ShellSection.Optimize, ShellSection.Network,
        ShellSection.Monitor, ShellSection.Settings
    };

    private TabItem TabFor(ShellSection s) => s switch
    {
        ShellSection.MyPc => TabMyPc,
        ShellSection.Games => TabGames,
        ShellSection.Optimize => TabOptimize,
        ShellSection.Network => TabNetwork,
        ShellSection.Monitor => TabMonitor,
        ShellSection.Settings => TabSettings,
        _ => TabHome
    };

    private NavRailItem NavFor(ShellSection s) => s switch
    {
        ShellSection.MyPc => NavMyPc,
        ShellSection.Games => NavGames,
        ShellSection.Optimize => NavOptimize,
        ShellSection.Network => NavNetwork,
        ShellSection.Monitor => NavMonitor,
        ShellSection.Settings => NavSettings,
        _ => NavHome
    };

    private static ShellSection SectionForTag(string? tag) => tag switch
    {
        "mypc" => ShellSection.MyPc,
        "games" or "play" or "library" => ShellSection.Games,
        "optimize" => ShellSection.Optimize,
        "network" => ShellSection.Network,
        "monitor" or "fps" => ShellSection.Monitor,
        "settings" => ShellSection.Settings,
        _ => ShellSection.Home
    };

    public ShellSection CurrentSection => CurrentPage?.Section ?? ShellSection.Home;

    public bool IsSectionLive(ShellSection section) => IsVisible && CurrentSection == section;

    public void Navigate(ShellSection section)
    {
        var tab = TabFor(section);
        if (!ReferenceEquals(MainTabs.SelectedItem, tab))
            MainTabs.SelectedItem = tab;
    }

    public void Navigate(ShellSection section, string? subSection)
    {
        Navigate(section);
        if (!string.IsNullOrEmpty(subSection) && TabFor(section).Content is ShellPageBase page)
            page.ShowSubSection(subSection);
    }

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

        foreach (var removed in e.RemovedItems.OfType<TabItem>())
            (removed.Content as ShellPageBase)?.OnDeactivated();

        _lastTabIndex = newIndex;
        CurrentPage?.OnActivated();
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
            FocusNavFor(CurrentSection);
        }, DispatcherPriority.Input);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressNav) return;
        if (sender is not NavRailItem rb || rb.IsChecked != true) return;
        var section = SectionForTag(rb.Tag as string);
        var tab = TabFor(section);
        if (!ReferenceEquals(MainTabs.SelectedItem, tab))
            MainTabs.SelectedItem = tab;
        else
            UpdatePageTitle();
    }

    private void NavGames_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        EggBusy?.NotifyProfilesClick();

    private void SyncNavFromTabs()
    {
        if (_suppressNav) return;
        _suppressNav = true;
        NavFor(CurrentSection).IsChecked = true;
        _suppressNav = false;
        UpdatePageTitle();
    }

    private void FocusNavFor(ShellSection section) => NavFor(section).Focus();

    private void UpdatePageTitle()
    {
        if (LblPageTitle == null) return;
        LblPageTitle.Text = Loc.T(CurrentPage?.TitleKey ?? "nav.home");
    }

    // -- Nav rail: collapse + themes ----------------------------------------------------------------------

    private void RailCollapse_Click(object sender, RoutedEventArgs e)
    {
        SetRailCompact(!_railCompact, animate: true);
        var cfg = App.Services.Config.Current;
        cfg.Ui ??= new UiPreferences();
        cfg.Ui.SidebarCollapsed = _railCompact;
        App.Services.Config.Save(cfg, raiseChanged: false);
    }

    private void SetRailCompact(bool compact, bool animate)
    {
        _railCompact = compact;
        foreach (ShellSection s in SectionOrder)
            NavFor(s).IsCompact = compact;
        LblNavMain.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LblBrand.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        BetaPill.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        // 56-DIP rail: logo (22) + collapse button (28) + the 12/8 side margins don't fit (70 > 55) and the logo
        // was squeezed under the button. Compact shows only the centered expand button, on the nav items' axis.
        BrandRow.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        DockPanel.SetDock(BtnRailCollapse, compact ? Dock.Top : Dock.Right);
        BtnRailCollapse.HorizontalAlignment = compact ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Stretch;
        BrandDock.Margin = compact ? new Thickness(0, 2, 0, 14) : new Thickness(12, 2, 8, 14);
        RailFooter.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RailCollapseIcon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, compact ? "IconExpand" : "IconCollapse");
        string tip = Loc.T(compact ? "nav.expand" : "nav.collapse");
        BtnRailCollapse.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(BtnRailCollapse, tip);

        double target = compact ? ShellFit.NavRailCompactWidth : ShellFit.NavRailWidth;
        NavRailPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
        if (!animate || UiMotion.Reduced)
        {
            NavRailPanel.Width = target;
            return;
        }
        var anim = new System.Windows.Media.Animation.DoubleAnimation(NavRailPanel.ActualWidth, target,
            TimeSpan.FromMilliseconds(UiMotion.NormalMs))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
        };
        anim.Completed += (_, _) => NavRailPanel.Width = target;
        NavRailPanel.BeginAnimation(FrameworkElement.WidthProperty, anim);
    }

    private void SyncThemeCombo()
    {
        _suppressThemeCombo = true;
        try
        {
            ComboUi.SelectByTag(CmbRailTheme, App.Services.Config.Current.Ui?.Theme ?? "dark");
        }
        finally
        {
            _suppressThemeCombo = false;
        }
    }

    private void RailTheme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressThemeCombo || CmbRailTheme.SelectedItem is not ComboBoxItem item) return;
        string theme = item.Tag?.ToString() ?? "dark";
        var cfg = App.Services.Config.Current;
        cfg.Ui ??= new UiPreferences();
        if (string.Equals(cfg.Ui.Theme, theme, StringComparison.OrdinalIgnoreCase)) return;
        cfg.Ui.Theme = theme;
        if (theme == "custom")
            cfg.Ui.CustomPalette ??= ThemeService.SeedCustom(cfg.Ui.CustomAccent, cfg.Ui.CustomBackground);
        ThemeService.Apply(cfg.Ui);
        App.Services.Config.Save(cfg, raiseChanged: false);
        ApplyChromeTheme();
        if (System.Windows.Application.Current is App app)
            app.RebuildTrayMenu();
    }

    // -- Tooltip mode -------------------------------------------------------------------------------------

    public bool TooltipMode
    {
        get => InfoHint.TooltipMode;
        set
        {
            if (InfoHint.TooltipMode == value && TogTooltipMode.IsChecked == value) return;
            InfoHint.TooltipMode = value;
            if (TogTooltipMode.IsChecked != value)
                TogTooltipMode.IsChecked = value; // re-enters through TooltipMode_Changed with the same value
            var cfg = App.Services.Config.Current;
            cfg.Ui ??= new UiPreferences();
            if (cfg.Ui.TooltipMode != value)
            {
                cfg.Ui.TooltipMode = value;
                App.Services.Config.Save(cfg, raiseChanged: false);
            }
            _home.Refresh();
        }
    }

    private void TooltipMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        TooltipMode = TogTooltipMode.IsChecked == true;
    }

    // -- Command palette ------------------------------------------------------------------------------------

    private void OpenCommandPalette()
    {
        if (_paletteOpen) return;
        _paletteOpen = true;
        try
        {
            var win = new CommandPaletteWindow(BuildCommandPaletteItems(), id => id switch
            {
                "cmd.scan" => !_games.ScanRunning,
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
            new() { Id = "nav.mypc", Title = Loc.T("nav.mypc"), Group = nav, SearchText = "ctrl+2" },
            new() { Id = "nav.games", Title = Loc.T("nav.games"), Group = nav, SearchText = "ctrl+3" },
            new() { Id = "nav.optimize", Title = Loc.T("nav.optimize"), Group = nav, SearchText = "ctrl+4" },
            new() { Id = "nav.network", Title = Loc.T("nav.network"), Group = nav, SearchText = "ctrl+5" },
            new() { Id = "nav.monitor", Title = Loc.T("nav.monitor"), Group = nav, SearchText = "ctrl+6 fps frametime capture etw" },
            new() { Id = "nav.settings", Title = Loc.T("nav.settings"), Group = nav, SearchText = "ctrl+7" },
            new() { Id = "cmd.preferences", Title = Loc.T("shell.preferences"), Subtitle = Loc.T("shell.preferences.tip"), Group = nav },
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
            case "nav.home": Navigate(ShellSection.Home); break;
            case "nav.mypc": Navigate(ShellSection.MyPc); break;
            case "nav.games": Navigate(ShellSection.Games); break;
            case "nav.optimize": Navigate(ShellSection.Optimize); break;
            case "nav.network": Navigate(ShellSection.Network); break;
            case "nav.monitor": Navigate(ShellSection.Monitor); break;
            case "nav.settings": Navigate(ShellSection.Settings); break;
            case "cmd.preferences": OpenPreferences(); break;
            case "cmd.pause":
            case "cmd.resume":
                TogglePauseMonitoring();
                break;
            case "cmd.emergency": EmergencyRestore(); break;
            case "cmd.scan": ScanGames(); break;
            case "cmd.add": AddGame(); break;
            case "cmd.overlay": _games.Overlay_Click(this, new RoutedEventArgs()); break;
            case "cmd.history": OpenHistory(); break;
            default:
                if (item.Id.StartsWith("game.", StringComparison.Ordinal) && item.Id.Length > 5)
                {
                    var id = item.Id["game.".Length..];
                    var match = _profiles.FirstOrDefault(p => p.Id == id);
                    if (match != null)
                        OpenGame(match);
                }
                break;
        }
        SyncNavFromTabs();
    }

    // -- IShellHost: navigation helpers + session actions ---------------------------------------------------

    public void OpenGame(GameProfile profile)
    {
        Navigate(ShellSection.Games);
        _games.SelectProfile(profile);
    }

    public void AddGame() => _games.AddProfile_Click(this, new RoutedEventArgs());

    public void ScanGames()
    {
        Navigate(ShellSection.Games);
        _games.ScanGames_Click(this, new RoutedEventArgs());
    }

    public void OpenHistory()
    {
        Navigate(ShellSection.Settings);
        _settings.ShowHistory();
    }

    public void OpenPreferences() => Settings_Click(this, new RoutedEventArgs());

    public void TogglePauseMonitoring()
    {
        App.Services.Monitor.IsPaused = !App.Services.Monitor.IsPaused;
        _home.Refresh();
        _optimize.Refresh();
        if (System.Windows.Application.Current is App app)
            app.RebuildTrayMenu();
    }

    public void EmergencyRestore()
    {
        App.Services.Zoom.Off();
        App.Services.Monitor.EmergencyRestore();
        _games.OnEmergencyRestored();
        ShowToast(Loc.T("toast.emergency"));
    }

    public ScanOverlay ScanBusy => ScanBusyOverlay;

    public bool ConfirmDestructive(string message, string title, string actionText)
    {
        if (App.Services.Config.Current.Ui?.ConfirmDelete == false)
            return true;
        return ThemedDialog.Confirm(this, message, title, actionText, danger: true);
    }

    public ObservableCollection<GameProfile> Profiles => _profiles;
    public bool IsDirty => _dirty;
    public GameProfile? SelectedProfile => _games.SelectedProfile;
    public IEnumerable<(System.Windows.Controls.TextBox Box, string LabelKey)> GlobalHotkeyBoxes => _settings.GlobalHotkeyBoxes;
    public IEnumerable<(GameProfile Game, IEnumerable<QuickPreset> Presets)> PresetHotkeyScope() => _games.PresetHotkeyScope();
    public void NotifyGlobalHotkeysChanged() => _games.RefreshGamesTab();
    Window IShellHost.Window => this;

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
            _home.Refresh();
        SyncThemeCombo();
        ApplyChromeTheme();
        UpdateActiveHeader(App.Services.Monitor.CurrentProfile);
        if (win.Imported)
            ReloadFromConfig();
        if (System.Windows.Application.Current is App app)
            app.RebuildTrayMenu();
    }

    /// <summary>Single entry point: relabels the shell and every page for the current locale.</summary>
    public void ApplyLocalization()
    {
        _appliedLocale = Loc.Locale;
        UpdateTitleDirtyState();
        ApplyShellLocalization();
        foreach (var page in _pages)
            page.ApplyLocalization();
        UpdateActiveHeader(App.Services.Monitor.CurrentProfile);
    }

    /// <summary>Nav rail, top bar, caption buttons, footer.</summary>
    private void ApplyShellLocalization()
    {
        LblNavMain.Text = Loc.T("nav.section.main");
        LblBeta.Text = Loc.T("shell.beta");
        foreach (var s in SectionOrder)
            NavFor(s).Label = Loc.T(TabFor(s).Content is ShellPageBase p ? p.TitleKey : "nav.home");
        BtnRailCollapse.ToolTip = Loc.T(_railCompact ? "nav.expand" : "nav.collapse");
        System.Windows.Automation.AutomationProperties.SetName(BtnRailCollapse, (string)BtnRailCollapse.ToolTip);
        ItmThemeDark.Content = Loc.T("setup.theme.dark");
        ItmThemeLight.Content = Loc.T("setup.theme.light");
        ItmThemeCustom.Content = Loc.T("setup.theme.custom");
        System.Windows.Automation.AutomationProperties.SetName(CmbRailTheme, Loc.T("shell.themes"));
        CmbRailTheme.ToolTip = Loc.T("shell.themes");
        SyncThemeCombo();
        LblHelpTitle.Text = Loc.T("shell.help.title");
        LblHelpBody.Text = Loc.T("shell.help.body");
        BtnHelpLink.Content = Loc.T("shell.help.link");
        BtnHelpLink.ToolTip = HelpUrl;

        TogTooltipMode.Content = Loc.T("shell.tooltipMode");
        TogTooltipMode.ToolTip = Loc.T("shell.tooltipMode.tip");
        BtnTopSettings.ToolTip = Loc.T("shell.preferences");
        System.Windows.Automation.AutomationProperties.SetName(BtnTopSettings, Loc.T("shell.preferences"));
        SyncNavFromTabs();

        BtnMinimize.ToolTip = Loc.T("caption.minimize");
        BtnCloseCaption.ToolTip = Loc.T("caption.close");
        System.Windows.Automation.AutomationProperties.SetName(BtnMinimize, Loc.T("caption.minimize"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseCaption, Loc.T("caption.close"));
        UpdateMaximizeCaption();
        TxtShortcuts.Text = Loc.T("shortcuts.footer");
        TxtCredit.Text = "Nakidev · @nakidev";
        TxtCredit.ToolTip = Loc.T("about.contacts");
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
            ActiveDot.Status = StatusKind.Unknown;
        }
        else
        {
            var bits = new List<string> { profile.Name };
            if (profile.ApplyResolution && !string.IsNullOrWhiteSpace(profile.Resolution))
                bits.Add(ColorUiHelper.FormatResolution(profile.Resolution));
            ActiveHeaderText.Text = Loc.T("active.prefix") + " " + string.Join(" · ", bits);
            ActiveDot.Status = StatusKind.Ok;
        }
        ActiveDot.Label = ActiveHeaderText.Text;
        // A game just started → jump the Games editor to it (once per change, user can still pick another).
        _games.OnActiveProfileChanged(profile);
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

    public void ShowUndoToast(string message, Action undo) => ShowToast(message, Loc.T("btn.undo"), undo);

    // -- Public API used by App / overlay / gallery ----------------------------------------------------

    /// <summary>Game whose preset hotkeys stay registered when no match is running.</summary>
    public GameProfile? HotkeyPresetFallback => _games.HotkeyPresetFallback;

    /// <summary>Selected game for the overlay when no game is running (the live config instance, not the editor copy).</summary>
    public GameProfile? SelectedProfileForOverlay => _games.SelectedProfileForOverlay;

    /// <summary>Overlay is about to edit config objects directly — write our pending edits first.</summary>
    public void FlushPendingEdits() => FlushAutosave();

    /// <summary>Overlay saved color into the config — pick it up (the overlay shows its own confirmation).</summary>
    public void NotifyOverlayApplied()
    {
        if (!_dirty)
            ReloadFromConfig();
    }

    /// <summary>A game started: make sure the Games page (and its Presets section) show it.</summary>
    public void FocusPresetsForActiveOrSelectedGame(GameProfile? prefer = null) =>
        _games.FocusPresetsForActiveOrSelectedGame(prefer);

    /// <summary>Pack gallery → presets of the selected game.</summary>
    public void ApplyPackPresets(IEnumerable<QuickPreset> presets, string packName) =>
        _games.ApplyPackPresets(presets, packName);

    public void RefreshHotkeyBindings()
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
        var (keepProfileId, keepPresetId) = _games.CaptureSelection();
        var cfg = App.Services.Config.Current;

        using (SuppressEditor())
        {
            // Drop editor pointers before clearing collections — avoids syncing into orphaned profiles.
            _games.DetachEditors();
            _profiles.Clear();
            foreach (var p in cfg.Profiles)
                _profiles.Add(p.Clone());
            GameVisuals.ApplyAll(_profiles);

            var resolutions = DisplayEngine.GetAvailableResolutions().ToList();
            _games.ReloadResolutionLists(resolutions);
            _settings.LoadFromConfig(cfg, resolutions);

            cfg.Defaults.Color = ColorSettings.Neutral;
            _games.UpdateDriverUiAvailability();
            _games.FillAudioDevices();
        }

        _games.RestoreSelection(keepProfileId, keepPresetId);

        _dirty = false;
        UpdateTitleDirtyState();
        _settings.RefreshLogIfVisible();
    }

    // -- Autosave ---------------------------------------------------------------------------------------

    public void MarkDirty()
    {
        _dirty = true;
        UpdateTitleDirtyState();
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    public void FlushAutosave()
    {
        _autoSaveTimer.Stop();
        if (!_dirty) return;
        _games.PushPendingEdits();
        PersistConfigToDisk();
    }

    private void PersistConfigToDisk()
    {
        var cfg = App.Services.Config.Current;
        cfg.Profiles = _profiles.Select(p => p.Clone()).ToList();
        foreach (var p in cfg.Profiles)
            p.ApplyColor = false;
        cfg.Presets = null;
        _settings.WriteTo(cfg);
        cfg.Defaults.Color = ColorSettings.Neutral;

        if (cfg.FactoryDefaults == null || string.IsNullOrWhiteSpace(cfg.FactoryDefaults.Resolution))
            cfg.FactoryDefaults = ConfigService.CaptureFactoryDefaults();
        cfg.FactoryDefaults.Color = ColorSettings.Neutral;

        App.Services.Zoom.ApplyFromConfig(cfg.Ui);
        cfg.ConfigVersion = ConfigService.CurrentVersion;

        App.Services.Config.Save(cfg, raiseChanged: false);
        RefreshHotkeyBindings();
        App.Services.Monitor.NotifyConfigSaved();
        _dirty = false;
        UpdateTitleDirtyState();
        SetStatus(Loc.T("status.saved"));
    }
}
