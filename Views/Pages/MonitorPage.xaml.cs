using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Visor.Controls;
using Visor.Services;
using Visor.Services.Optimize;

namespace Visor;

/// <summary>
/// Monitor section: FPS / frametime capture of one game through Visor's built-in ETW capture. The page only
/// reflects <see cref="MonitorService"/>: a capture starts on an explicit click (the backend starts the
/// elevated <c>Visor.exe --frame-capture</c> helper with <c>runas</c>, which is the one UAC prompt), never on
/// activation or on game start; Stop needs no prompt. Service events arrive on worker threads; they are
/// marshalled to the dispatcher only while the page is live, and frames are throttled to a 500 ms chart redraw.
/// Every number shown is measured from the game's present events — the limiter label is marked "likely".
/// </summary>
public partial class MonitorPage : ShellPageBase
{
    private const int ChartRedrawMs = 500;

    /// <summary>The pre-UAC explanation is shown once per app session (not persisted).</summary>
    private static bool _startUacExplained;

    private readonly DispatcherTimer _chartTimer;
    private readonly Action<MonitorState, MonitorErrorCode> _onState;
    private readonly Action<FrameStatsSummary> _onSummary;
    private readonly Action<FrameRecord> _onFrame;
    private readonly NotifyCollectionChangedEventHandler _onProfilesChanged;

    /// <summary>Mirror of IsLive that worker threads may read (IsLive touches dependency properties).</summary>
    private volatile bool _live;
    private bool _subscribed;
    private int _framesDirty;
    private DateTime? _captureStartedUtc;
    private MonitorState _lastSeenState = MonitorState.Idle;
    private bool _stopFailedDismissed;
    private ExperimentMetricSink? _experimentSink;
    private (TimeSpan Duration, long Frames, string Target)? _lastSession;

    public MonitorPage(IShellHost host) : base(host)
    {
        InitializeComponent();
        var svc = FrameMonitor;

        var ui = App.Services.Config.Current.Ui;
        FpsOsdWindow.Enabled = ui?.MonitorOsdEnabled != false;
        FpsOsdWindow.Attach(svc);

        _chartTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(ChartRedrawMs) };
        _chartTimer.Tick += (_, _) => ChartTick();

        _onState = OnServiceStateChanged;
        _onSummary = s => { if (_live) Dispatcher.BeginInvoke(() => { if (IsLive) ShowSummary(s); }, DispatcherPriority.Background); };
        _onFrame = _ => Interlocked.Exchange(ref _framesDirty, 1);
        _onProfilesChanged = (_, _) => { if (IsLive) RebuildTargets(); };
        Subscribe();
    }

    public override ShellSection Section => ShellSection.Monitor;
    public override string TitleKey => "nav.monitor";

    // -- Lifecycle ---------------------------------------------------------------------------------------

    public override void ApplyLocalization()
    {
        BtnMonStart.Content = Loc.T("monitor.start");
        BtnMonStop.Content = Loc.T("monitor.stop");
        BtnMonStop.ToolTip = Loc.T("monitor.stop.tip");
        LblTarget.Text = Loc.T("monitor.target");
        TxtTargetHint.Text = Loc.T("monitor.target.hint");
        BtnPickRunning.Content = Loc.T("monitor.pickRunning");
        LblFpsAvg.Text = Loc.T("monitor.fps");
        LblLow1.Text = Loc.T("monitor.low1");
        LblLow01.Text = Loc.T("monitor.low01");
        LblP99.Text = Loc.T("monitor.p99");
        LblStutters.Text = Loc.T("monitor.stutters");
        LblBound.Text = Loc.T("monitor.bound");
        LblC2p.Text = Loc.T("monitor.c2p");
        LblChart.Text = Loc.T("monitor.chart");
        TxtChartLegend.Text = Loc.T("monitor.chart.legend");
        Chart.EmptyText = Loc.T("monitor.chart.empty");
        LblSummary.Text = Loc.T("monitor.summary");
        BtnDismiss.Content = Loc.T("monitor.dismiss");
        TxtHowItWorks.Text = Loc.T("monitor.how");
        DotState.Label = Loc.T("nav.monitor");
        RefreshState();
        ShowSummary(FrameMonitor.LastSummary);
        RefreshSessionSummary();
        RefreshExperimentNote();
    }

    public override void OnActivated()
    {
        _live = true;
        Subscribe();
        RebuildTargets();
        RefreshState();
        ShowSummary(FrameMonitor.LastSummary);
        RefreshSessionSummary();
        Interlocked.Exchange(ref _framesDirty, 1);
        ChartTick();
        _chartTimer.Start();
    }

    public override void OnDeactivated()
    {
        _live = false;
        _chartTimer.Stop();
    }

    public override void RefreshIfStale()
    {
        if (!IsLive) return;
        _live = true;
        RefreshState();
        ShowSummary(FrameMonitor.LastSummary);
        Interlocked.Exchange(ref _framesDirty, 1);
        ChartTick();
        if (!_chartTimer.IsEnabled) _chartTimer.Start();
    }

    /// <summary>Window is closing for real: detach from the service (the capture itself keeps running by design).</summary>
    public void Shutdown()
    {
        _live = false;
        _chartTimer.Stop();
        Unsubscribe();
        AttachExperiment(null);
        FpsOsdWindow.Shutdown();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        var svc = FrameMonitor;
        svc.StateChanged += _onState;
        svc.SummaryUpdated += _onSummary;
        svc.FrameReceived += _onFrame;
        Host.Profiles.CollectionChanged += _onProfilesChanged;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        var svc = FrameMonitor;
        svc.StateChanged -= _onState;
        svc.SummaryUpdated -= _onSummary;
        svc.FrameReceived -= _onFrame;
        Host.Profiles.CollectionChanged -= _onProfilesChanged;
    }

    // -- Service events ----------------------------------------------------------------------------------

    /// <summary>Worker thread. Session bookkeeping is thread-safe; UI refresh is posted only while live.</summary>
    private void OnServiceStateChanged(MonitorState state, MonitorErrorCode error)
    {
        var prev = _lastSeenState;
        _lastSeenState = state;
        bool wasRunning = prev is MonitorState.Starting or MonitorState.Capturing or MonitorState.Stopping;
        bool isRunning = state is MonitorState.Starting or MonitorState.Capturing or MonitorState.Stopping;
        if (state == MonitorState.Capturing && prev == MonitorState.Starting)
            _captureStartedUtc = DateTime.UtcNow;
        if (wasRunning && !isRunning && _captureStartedUtc is DateTime started)
        {
            long frames = FrameMonitor.Stats.TotalAdded;
            if (frames > 0)
                _lastSession = (DateTime.UtcNow - started, frames, FrameMonitor.Target);
            _captureStartedUtc = null;
        }
        if (state != MonitorState.Capturing || error != MonitorErrorCode.StopFailed)
            _stopFailedDismissed = false;

        if (!_live) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLive) return;
            RefreshState();
            RefreshSessionSummary();
            if (!isRunning)
            {
                Interlocked.Exchange(ref _framesDirty, 1);
                ChartTick();
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>≤ 2 redraws / s: pulls the window from the service only when new frames arrived.</summary>
    private void ChartTick()
    {
        if (Interlocked.Exchange(ref _framesDirty, 0) == 0)
        {
            UpdateLiveTargetText();
            return;
        }
        var svc = FrameMonitor;
        var frames = svc.Stats.Frames();
        if (frames.Length == 0)
            Chart.Clear();
        else
            Chart.SetSeries(frames, svc.LastSummary.P99FrameTimeMs);
        UpdateLiveTargetText();
    }

    // -- State / error card ------------------------------------------------------------------------------

    private void RefreshState()
    {
        if (DotState == null) return;
        var svc = FrameMonitor;
        var state = svc.State;
        var error = svc.LastError;

        DotState.Status = state switch
        {
            MonitorState.Capturing => StatusKind.Ok,
            MonitorState.Starting or MonitorState.Stopping => StatusKind.Warn,
            MonitorState.Error => StatusKind.Bad,
            _ => StatusKind.Unknown
        };
        TxtState.Text = Loc.T("monitor.state." + state.ToString().ToLowerInvariant());
        bool running = state is MonitorState.Starting or MonitorState.Capturing or MonitorState.Stopping;
        // Under the dotnet host / a test runner there is no Visor.exe to elevate: Start stays disabled and says why.
        bool supported = svc.IsCaptureSupported;
        BtnMonStart.IsEnabled = supported && state is (MonitorState.Idle or MonitorState.Error);
        BtnMonStart.ToolTip = supported ? Loc.T("monitor.start.tip") : Loc.T("monitor.err.elevationUnavailable");
        BtnMonStop.IsEnabled = state is MonitorState.Starting or MonitorState.Capturing;
        CmbTarget.IsEnabled = !running;
        BtnPickRunning.IsEnabled = CmbTarget.IsEnabled;
        UpdateLiveTargetText();

        bool showError = state == MonitorState.Error && error != MonitorErrorCode.None;
        bool showStopFailed = state == MonitorState.Capturing && error == MonitorErrorCode.StopFailed && !_stopFailedDismissed;
        if (!showError && !showStopFailed)
        {
            ErrorCard.Visibility = Visibility.Collapsed;
            return;
        }
        ShowErrorCard(error, svc.LastErrorDetail, svc.Target, warning: showStopFailed);
    }

    private void UpdateLiveTargetText()
    {
        var svc = FrameMonitor;
        var state = svc.State;
        string target = svc.Target;
        if (string.IsNullOrEmpty(target) || state is MonitorState.Idle or MonitorState.Error)
        {
            TxtLiveTarget.Text = "";
            return;
        }
        if (state == MonitorState.Capturing && _captureStartedUtc is DateTime started)
        {
            var el = DateTime.UtcNow - started;
            TxtLiveTarget.Text = target + " · " + el.ToString(el.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
        }
        else
            TxtLiveTarget.Text = target;
    }

    /// <summary>One card per <see cref="MonitorErrorCode"/>: what happened, what to do, which buttons help.</summary>
    private void ShowErrorCard(MonitorErrorCode code, string detail, string target, bool warning)
    {
        ErrorCard.Visibility = Visibility.Visible;
        ErrorCard.SetResourceReference(Border.BorderBrushProperty, warning ? "WarningBrush" : "DangerBrush");
        DotError.Status = warning ? StatusKind.Warn : StatusKind.Bad;
        DotError.Label = Loc.T("monitor.err.title");

        // Loc keys are case-insensitive: "monitor.err." + UacDeclined resolves "monitor.err.uacDeclined".
        string key = "monitor.err." + code;
        TxtErrorTitle.Text = code == MonitorErrorCode.ProcessNotFound && !string.IsNullOrEmpty(target)
            ? target + " — " + Loc.T(key)
            : Loc.T(key);
        TxtErrorBody.Text = "";
        TxtErrorBody.Visibility = Visibility.Collapsed;

        // ElevationUnavailable is an environment notice (dev host), not a failure of this capture: no raw detail, no retry.
        bool environment = code == MonitorErrorCode.ElevationUnavailable;
        if (environment)
        {
            ErrorCard.SetResourceReference(Border.BorderBrushProperty, "WarningBrush");
            DotError.Status = StatusKind.Warn;
        }

        // Raw detail from the backend (exit code, Win32 error) — never localized, never hidden.
        bool hasDetail = !environment && !string.IsNullOrWhiteSpace(detail);
        TxtErrorDetail.Text = hasDetail ? detail : "";
        TxtErrorDetail.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;

        bool retry = code is MonitorErrorCode.UacDeclined or MonitorErrorCode.ProcessNotFound or MonitorErrorCode.EtwFailed
            or MonitorErrorCode.StartFailed or MonitorErrorCode.NoOutput or MonitorErrorCode.OutputUnreadable;
        BtnRetry.Visibility = retry || warning ? Visibility.Visible : Visibility.Collapsed;
        BtnRetry.Content = warning ? Loc.T("monitor.stop") : Loc.T("monitor.retry");
        BtnDismiss.Content = Loc.T("monitor.dismiss");
    }

    // -- Live numbers ------------------------------------------------------------------------------------

    private void ShowSummary(FrameStatsSummary s)
    {
        if (TxtFpsAvg == null) return;
        var ci = CultureInfo.CurrentCulture;
        if (s == null || s.FrameCount == 0)
        {
            foreach (var t in new[] { TxtFpsAvg, TxtLow1, TxtLow01, TxtP99, TxtStutters, TxtBound, TxtC2p })
                t.Text = "—";
            CardBound.ToolTip = null;
            CardC2p.ToolTip = null;
            CardC2p.Visibility = Visibility.Collapsed;
            TxtStutters.ToolTip = null;
            TxtFramesLine.Text = Loc.T("monitor.empty");
            return;
        }

        TxtFpsAvg.Text = Math.Round(s.AvgFps).ToString(ci);
        TxtLow1.Text = Math.Round(s.OnePercentLowFps).ToString(ci);
        TxtLow01.Text = Math.Round(s.PointOnePercentLowFps).ToString(ci);
        TxtP99.Text = string.Format(ci, "{0:F1} ms", s.P99FrameTimeMs);

        if (s.CoveredSeconds >= 1)
        {
            TxtStutters.Text = Math.Round(s.StutterCount * 60.0 / s.CoveredSeconds).ToString(ci);
            TxtStutters.ToolTip = Loc.Tf("monitor.stutters.tip", s.StutterCount, Math.Round(s.CoveredSeconds));
        }
        else
        {
            TxtStutters.Text = "—";
            TxtStutters.ToolTip = null;
        }

        TxtBound.Text = Loc.T("monitor.bound." + (s.Bound switch
        {
            BoundKind.LikelyGpu => "gpu",
            BoundKind.LikelyCpu => "cpu",
            BoundKind.LikelyCappedOrBalanced => "capped",
            _ => "unknown"
        }));
        CardBound.ToolTip = string.IsNullOrWhiteSpace(s.BoundEvidence) ? Loc.T("monitor.bound.evidence.none") : s.BoundEvidence;

        // Click-to-photon is not measured by the built-in capture; the tile only appears if a value ever exists.
        if (s.AvgClickToPhotonMs is double c2p)
        {
            TxtC2p.Text = string.Format(ci, "{0:F1} ms", c2p);
            CardC2p.ToolTip = Loc.Tf("monitor.c2p.tip", s.ClickToPhotonSamples);
            CardC2p.Visibility = Visibility.Visible;
        }
        else
        {
            TxtC2p.Text = "—";
            CardC2p.ToolTip = null;
            CardC2p.Visibility = Visibility.Collapsed;
        }

        TxtFramesLine.Text = Loc.Tf("monitor.frames.line", s.FrameCount, Math.Round(s.CoveredSeconds));
    }

    private void RefreshSessionSummary()
    {
        if (SummaryCard == null) return;
        if (_lastSession is not { } ses || FrameMonitor.IsCapturing)
        {
            SummaryCard.Visibility = Visibility.Collapsed;
            return;
        }
        var ci = CultureInfo.CurrentCulture;
        SummaryCard.Visibility = Visibility.Visible;
        string dur = ses.Duration.ToString(ses.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
        TxtSummary.Text = Loc.Tf("monitor.summary.line", ses.Target, dur, ses.Frames.ToString("N0", ci));
        var last = FrameMonitor.LastSummary;
        TxtSummaryDetail.Text = last.FrameCount > 0
            ? Loc.Tf("monitor.summary.detail", Math.Round(last.AvgFps), Math.Round(last.OnePercentLowFps), last.P99FrameTimeMs.ToString("F1", ci))
            : "";
    }

    // -- Target ------------------------------------------------------------------------------------------

    /// <summary>Running game first, then every enabled profile; the typed text survives a rebuild.</summary>
    private void RebuildTargets()
    {
        if (CmbTarget == null) return;
        string typed = CmbTarget.Text?.Trim() ?? "";
        string? selectedExe = (CmbTarget.SelectedItem as ComboBoxItem)?.Tag as string;
        CmbTarget.Items.Clear();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var running = App.Services.Monitor.CurrentProfile;
        string? preferExe = null;
        if (running != null && !string.IsNullOrWhiteSpace(running.ProcessName))
        {
            var exe = ProcessNames.ToExeName(running.ProcessName);
            seen.Add(ProcessNames.Key(exe));
            CmbTarget.Items.Add(new ComboBoxItem { Content = Loc.Tf("monitor.target.running", running.Name, exe), Tag = exe });
            preferExe = exe;
        }
        foreach (var p in Host.Profiles.Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.ProcessName))
                     .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var exe = ProcessNames.ToExeName(p.ProcessName);
            if (!seen.Add(ProcessNames.Key(exe))) continue;
            CmbTarget.Items.Add(new ComboBoxItem { Content = p.Name + " · " + exe, Tag = exe });
        }

        // Keep what the user had: an explicit selection, then the service's last target, then typed text.
        string? want = selectedExe ?? preferExe;
        if (want == null && !string.IsNullOrEmpty(FrameMonitor.Target)) want = FrameMonitor.Target;
        if (want != null && SelectTargetItem(want)) return;
        if (!string.IsNullOrEmpty(typed))
        {
            if (!SelectTargetItem(typed)) CmbTarget.Text = typed;
            return;
        }
        if (CmbTarget.Items.Count > 0) CmbTarget.SelectedIndex = 0;
    }

    private bool SelectTargetItem(string exe)
    {
        foreach (var item in CmbTarget.Items.OfType<ComboBoxItem>())
        {
            if (ProcessNames.Same(item.Tag as string, exe))
            {
                CmbTarget.SelectedItem = item;
                return true;
            }
        }
        return false;
    }

    /// <summary>"game.exe" from the selection or the typed text; null when nothing usable was entered.</summary>
    private string? ResolveTarget()
    {
        string? exe = (CmbTarget.SelectedItem as ComboBoxItem)?.Tag as string;
        string typed = CmbTarget.Text?.Trim() ?? "";
        // An item is selected but the text was edited afterwards: the text wins.
        if (exe != null && CmbTarget.SelectedItem is ComboBoxItem ci && !string.Equals(ci.Content as string, typed, StringComparison.Ordinal))
            exe = null;
        exe ??= typed;
        if (string.IsNullOrWhiteSpace(exe)) return null;
        exe = System.IO.Path.GetFileName(exe.Trim());
        return ProcessNames.IsPlainExeName(exe) ? ProcessNames.ToExeName(exe) : null;
    }

    private void PickRunning_Click(object sender, RoutedEventArgs e)
    {
        var pick = new ProcessPickerWindow(ProcessWatcher.GetRunningExeNames().ToList()) { Owner = Host.Window };
        if (pick.ShowDialog() != true || string.IsNullOrWhiteSpace(pick.SelectedProcess)) return;
        var exe = ProcessNames.ToExeName(pick.SelectedProcess);
        if (!SelectTargetItem(exe)) CmbTarget.Text = exe;
    }

    // -- Start / Stop ------------------------------------------------------------------------------------

    private void Start_Click(object sender, RoutedEventArgs e) => StartCapture();

    private void StartCapture()
    {
        var svc = FrameMonitor;
        if (svc.IsCapturing) return;
        var target = ResolveTarget();
        if (target == null)
        {
            UiSound.Warn();
            Host.ShowToast(Loc.T("monitor.target.invalid"));
            CmbTarget.Focus();
            return;
        }

        // Pre-flight without side effects: when the capture cannot run here or the game is not running,
        // StartCapture fails before any elevation, so the UAC explanation would be misleading — skip it then.
        bool willPrompt = false;
        try
        {
            willPrompt = svc.IsCaptureSupported
                         && (!svc.RequireRunningTarget || ProcessSnapshot.IsRunning(ProcessNames.Key(target)));
        }
        catch (Exception ex) { AppLog.Warn(ex, "Monitor pre-flight"); }

        if (willPrompt && !_startUacExplained)
        {
            if (!ThemedDialog.Show(Host.Window, Loc.T("monitor.uac.start"), Loc.T("monitor.uac.title"), confirm: true))
                return;
            _startUacExplained = true;
        }

        _lastSession = null;
        var result = svc.StartCapture(target); // the UAC prompt appears inside this call
        RefreshState();
        RefreshSessionSummary();
        if (result.Started)
            Host.ShowToast(Loc.Tf("monitor.toast.started", target));
        else if (result.Error is MonitorErrorCode.UacDeclined or MonitorErrorCode.ElevationUnavailable)
            UiSound.Warn();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopCapture();

    /// <summary>Sends the stop request over the capture pipe — no second UAC prompt.</summary>
    private void StopCapture()
    {
        var svc = FrameMonitor;
        if (svc.State is not (MonitorState.Starting or MonitorState.Capturing)) return;
        svc.StopCapture();
        RefreshState();
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        var svc = FrameMonitor;
        if (svc.State == MonitorState.Capturing && svc.LastError == MonitorErrorCode.StopFailed)
        {
            StopCapture();
            return;
        }
        if (svc.State == MonitorState.Error) svc.ClearError();
        StartCapture();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        var svc = FrameMonitor;
        if (svc.State == MonitorState.Error) svc.ClearError();
        else _stopFailedDismissed = true;
        RefreshState();
    }

    // -- Optimize integration ----------------------------------------------------------------------------

    /// <summary>
    /// Feeds per-second monitor samples into an Optimize before/after experiment (null detaches). Only
    /// measured values are exported; the page shows which experiment is being fed.
    /// </summary>
    public void AttachExperiment(OptimizationExperiment? experiment)
    {
        var svc = FrameMonitor;
        if (_experimentSink != null)
        {
            svc.DetachSink(_experimentSink);
            _experimentSink = null;
        }
        if (experiment != null)
        {
            _experimentSink = new ExperimentMetricSink(experiment);
            svc.AttachSink(_experimentSink);
        }
        RefreshExperimentNote();
    }

    private void RefreshExperimentNote()
    {
        if (TxtExperiment == null) return;
        if (_experimentSink == null)
        {
            TxtExperiment.Visibility = Visibility.Collapsed;
            return;
        }
        TxtExperiment.Visibility = Visibility.Visible;
        TxtExperiment.Text = Loc.Tf("monitor.experiment", _experimentSink.Experiment.ActionId);
    }
}
