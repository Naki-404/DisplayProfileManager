using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Visor.Services;

namespace Visor;

/// <summary>
/// Compact "FPS · 1 % low · frametime" readout in the top-left corner of the game's monitor, shown only
/// while <see cref="MonitorService"/> is capturing and <see cref="Enabled"/> (Settings → Monitor) is on.
/// Same click-through / no-activate / topmost recipe as <see cref="OsdWindow"/>, but with no motion at all
/// (numbers change once a second; nothing fades or slides) so it is reduced-motion safe by construction.
/// Hides itself as soon as the capture ends. Everything shown comes from the last PresentMon summary.
/// </summary>
public partial class FpsOsdWindow : Window
{
    private const uint SwpNosize = 0x0001, SwpNozorder = 0x0004, SwpNoactivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private static FpsOsdWindow? _instance;
    private static MonitorService? _service;
    private static bool _enabled = true;
    private static bool _capturing;

    private FpsOsdWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            OverlayWin32.SetClickThrough(this);
            OverlayWin32.StayTopmost(this);
        };
    }

    /// <summary>Settings → Monitor → "FPS overlay". Turning it off hides a visible overlay at once.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            OnUi(SyncVisibility);
        }
    }

    /// <summary>True while the overlay is on screen (for tests / diagnostics).</summary>
    public static bool IsShowing => _instance?.IsVisible == true;

    /// <summary>
    /// Follows the service's state and summaries. Idempotent; the first caller (the Monitor page at shell
    /// start-up) wins and later calls with the same service are no-ops. Never starts a capture.
    /// </summary>
    public static void Attach(MonitorService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (ReferenceEquals(_service, service)) return;
        if (_service != null)
        {
            _service.StateChanged -= OnStateChanged;
            _service.SummaryUpdated -= OnSummaryUpdated;
        }
        _service = service;
        service.StateChanged += OnStateChanged;
        service.SummaryUpdated += OnSummaryUpdated;
        _capturing = service.State is MonitorState.Capturing or MonitorState.Stopping;
        OnUi(SyncVisibility);
    }

    /// <summary>Detaches from the service and closes the window (app exit).</summary>
    public static void Shutdown()
    {
        if (_service != null)
        {
            _service.StateChanged -= OnStateChanged;
            _service.SummaryUpdated -= OnSummaryUpdated;
            _service = null;
        }
        _capturing = false;
        OnUi(() =>
        {
            try { _instance?.Close(); } catch { /* already closed */ }
            _instance = null;
        });
    }

    private static void OnStateChanged(MonitorState state, MonitorErrorCode _)
    {
        _capturing = state is MonitorState.Capturing or MonitorState.Stopping;
        OnUi(SyncVisibility);
    }

    private static void OnSummaryUpdated(FrameStatsSummary summary) => OnUi(() =>
    {
        if (!_capturing || !_enabled) return;
        _instance ??= new FpsOsdWindow();
        _instance.ShowSummary(summary);
    });

    private static void OnUi(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;
        var d = app.Dispatcher;
        if (d.HasShutdownStarted) return;
        if (d.CheckAccess()) Safe(action);
        else d.BeginInvoke(() => Safe(action), DispatcherPriority.Background);
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { AppLog.Warn(ex, "FPS OSD"); }
    }

    private static void SyncVisibility()
    {
        bool show = _capturing && _enabled;
        if (!show)
        {
            if (_instance?.IsVisible == true) _instance.Hide();
            return;
        }
        _instance ??= new FpsOsdWindow();
        var last = _service?.LastSummary ?? FrameStatsSummary.Empty;
        _instance.ShowSummary(last);
    }

    private void ShowSummary(FrameStatsSummary s)
    {
        var ci = CultureInfo.CurrentCulture;
        if (s.FrameCount == 0)
        {
            TxtFps.Text = "—";
            TxtDetail.Text = Loc.T("monitor.osd.waiting");
        }
        else
        {
            TxtFps.Text = Math.Round(s.AvgFps).ToString(ci);
            TxtDetail.Text = string.Format(ci, "1% {0} · {1:F1} ms", Math.Round(s.OnePercentLowFps), s.AvgFrameTimeMs);
        }
        if (!IsVisible) Show();
        UpdateLayout();
        PlaceTopLeft(12);
        OverlayWin32.StayTopmost(this);
    }

    /// <summary>Top-left of the game's monitor (foreground window's monitor), physical pixels — no DIP maths.</summary>
    private void PlaceTopLeft(int marginPx)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!OverlayWin32.TryGetMonitorRects(OverlayWin32.TargetMonitor(), out var bounds, out _)) return;
        SetWindowPos(hwnd, IntPtr.Zero, bounds.Left + marginPx, bounds.Top + marginPx, 0, 0, SwpNosize | SwpNozorder | SwpNoactivate);
    }
}
