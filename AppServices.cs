using Visor.Services;

namespace Visor;

public sealed class AppServices : IDisposable
{
    public ConfigService Config { get; }
    public DisplayEngine Display { get; }
    public ProcessWatcher Watcher { get; }
    public CompanionService Companions { get; }
    public ProfileMonitor Monitor { get; }
    public HotkeyService Hotkeys { get; }
    public ScreenZoomService Zoom { get; }
    public ActivityHistoryStore History { get; }
    /// <summary>1 Hz hardware sensors; samples only while a page holds a <see cref="SensorService.Subscribe"/> token.</summary>
    public SensorService Sensors { get; }
    /// <summary>FPS / frametime capture via PresentMon (named FrameMonitor: <see cref="Monitor"/> is the profile monitor).</summary>
    public MonitorService FrameMonitor { get; }

    /// <summary>Crash-event summary is re-read on demand at most this often (System log scan: 100 ms – a few s).</summary>
    public static readonly TimeSpan CrashSummaryMinAge = TimeSpan.FromMinutes(10);

    private readonly object _healthGate = new();
    private CrashEventSummary? _crashSummary;
    private DateTime _crashSummaryUtc = DateTime.MinValue;
    private Task? _crashRead;
    private HealthReport? _health;
    private bool _probesStarted;
    private bool _disposed;

    public AppServices()
    {
        // WMI GPU detect (hundreds of ms) overlaps with config load + window construction;
        // nothing on the startup path waits for it (DriverLabel is non-blocking).
        GpuVendorDetect.BeginDetect();

        Config = new ConfigService();
        AppLog.Configure(Config.LogPath);
        Config.LoadOrCreate();
        History = new ActivityHistoryStore(Config.ConfigDirectory);

        Display = new DisplayEngine(Config.ConfigDirectory);
        Zoom = new ScreenZoomService();
        Zoom.ApplyFromConfig(Config.Current.Ui);

        // Hardware services sit right after Display: sensors share NvApiHost with the driver color
        // path and must be disposed before it shuts NvAPI down (see Dispose).
        Sensors = new SensorService();
        Sensors.SnapshotReady += OnSensorSnapshot;
        FrameMonitor = new MonitorService();

        Watcher = new ProcessWatcher();
        Companions = new CompanionService();
        Monitor = new ProfileMonitor(Config, Display, Watcher, Companions, History);
        Hotkeys = new HotkeyService();

        // Crash restore only from real evidence: a leftover session snapshot is restored first
        // (pre-game modes / ramps / extras). A dirty exit without one re-applies the baseline
        // saved at the last clean start, if any — never a blind identity ramp.
        // Ordering kept: leftover restore → gamma baseline capture (sync, fast GDI) → then the
        // slow driver baseline (NvAPI / ADL init) + baseline file save on the thread pool.
        bool dirty = SessionGuard.WasDirtyShutdown();
        bool hadLeftover = Monitor.HasLeftoverSession;
        bool leftoverOk = true;
        var leftover = hadLeftover ? Monitor.RestoreLeftover(out leftoverOk) : LeftoverRestoreResult.Nothing;
        bool sessionRestored = leftover == LeftoverRestoreResult.SessionRestored;
        // A lone pending-fps-restore.json is the tail of a clean exit / logoff whose queued
        // FPS-cap undo ran out of time — it is applied quietly, never reported as a crash.
        if (sessionRestored)
            History.Add("leftoverRestored", success: leftoverOk, detail: leftoverOk ? null : "partial");
        else if (leftover == LeftoverRestoreResult.PendingFpsOnly && !leftoverOk)
            History.Add("leftoverRestored", success: false, detail: "pending FPS cap");
        else if (hadLeftover && leftover == LeftoverRestoreResult.Nothing)
            History.Add("leftoverRestored", success: false, detail: "failed");
        bool baselineRestored = Display.InitializeBaseline(useSavedBaseline: dirty && !sessionRestored, deferDriverBaseline: true);
        CrashRestored = sessionRestored || baselineRestored;
        // Now the desktop is in its real state — finish migrations that read it (factory power plan).
        Config.CompleteDeferredMigration();

        SessionGuard.MarkRunning();

        BaselineCapture = Task.Run(() =>
        {
            try { Display.CompleteBaselineCapture(); }
            catch (Exception ex) { AppLog.Error(ex, "Deferred baseline capture"); }
        });
    }

    /// <summary>True if this launch restored state left behind by a crashed / killed run.</summary>
    public bool CrashRestored { get; }

    /// <summary>Background completion of the display baseline (driver color + file); for diagnostics.</summary>
    public Task BaselineCapture { get; }

    // ---------------------------------------------------------------- hardware probes + health

    /// <summary>
    /// Kicks off the one-time hardware inventory (WMI, ~600 ms) and the first crash-event read on the
    /// thread pool. Called after the first frame is on screen; idempotent. Nothing waits on it.
    /// </summary>
    public void BeginHardwareProbes()
    {
        if (_probesStarted || _disposed) return;
        _probesStarted = true;
        HardwareInventoryService.GetAsync().ContinueWith(t =>
        {
            if (t.IsFaulted) AppLog.Warn(t.Exception!, "Hardware inventory");
            else ReevaluateHealth();
        }, TaskScheduler.Default);
        RequestCrashSummaryRefresh(force: true);
    }

    /// <summary>Last crash-event summary (null until the first read completes) and when it was taken.</summary>
    public CrashEventSummary? CrashSummary
    {
        get { lock (_healthGate) return _crashSummary; }
    }

    public DateTime CrashSummaryUtc
    {
        get { lock (_healthGate) return _crashSummaryUtc; }
    }

    /// <summary>
    /// Re-reads the System log off-thread unless the cached summary is younger than
    /// <see cref="CrashSummaryMinAge"/> (or a read is already in flight). Returns the in-flight task, or null.
    /// </summary>
    public Task? RequestCrashSummaryRefresh(bool force = false)
    {
        lock (_healthGate)
        {
            if (_disposed) return null;
            if (_crashRead is { IsCompleted: false }) return _crashRead;
            if (!force && _crashSummary != null && DateTime.UtcNow - _crashSummaryUtc < CrashSummaryMinAge) return null;
            _crashRead = Task.Run(() =>
            {
                CrashEventSummary summary;
                try { summary = CrashEventReader.Read(); }
                catch (Exception ex)
                {
                    AppLog.Warn(ex, "Crash events");
                    return;
                }
                lock (_healthGate)
                {
                    _crashSummary = summary;
                    _crashSummaryUtc = DateTime.UtcNow;
                }
                CrashSummaryChanged?.Invoke();
                ReevaluateHealth();
            });
            return _crashRead;
        }
    }

    /// <summary>Raised on a worker thread after a crash-summary read completes.</summary>
    public event Action? CrashSummaryChanged;

    /// <summary>
    /// Latest <see cref="HealthReport"/>: evaluated on every sensor snapshot (only while a page subscribes)
    /// and whenever the inventory or the crash summary arrives. Null before the first evaluation.
    /// </summary>
    public HealthReport? Health
    {
        get { lock (_healthGate) return _health; }
    }

    /// <summary>Raised on the sampler / worker thread — UI code marshals to its dispatcher.</summary>
    public event Action<HealthReport>? HealthChanged;

    private void OnSensorSnapshot(object? sender, SensorSnapshot snapshot) => Evaluate(snapshot);

    private void ReevaluateHealth() => Evaluate(Sensors.Latest);

    private void Evaluate(SensorSnapshot? snapshot)
    {
        if (_disposed) return;
        HealthReport report;
        try
        {
            report = HealthEvaluator.Evaluate(HardwareInventoryService.Cached, snapshot, CrashSummary, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Health evaluate");
            return;
        }
        lock (_healthGate) _health = report;
        try { HealthChanged?.Invoke(report); }
        catch (Exception ex) { AppLog.Warn(ex, "Health subscriber"); }
    }

    // ---------------------------------------------------------------- shutdown

    /// <param name="quick">Windows logoff / shutdown: skip optional waits (queues, NvAPI unload).</param>
    public void Dispose(bool quick)
    {
        if (_disposed) return;
        _disposed = true;
        // FrameMonitor first: it only stops listening — the elevated PresentMon capture ends on its own
        // (--terminate_on_proc_exit / --timed); no UAC prompt is ever shown from the exit path.
        try { FrameMonitor.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Frame monitor dispose"); }
        try { Zoom.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Zoom dispose"); }
        try { Monitor.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Monitor dispose"); }
        // Flushes the last debounced history write before the process goes away.
        try { History.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "History dispose"); }
        try { Hotkeys.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Hotkeys dispose"); }
        // Sensors read NvAPI: stop the sampler (join ≤ 2 s, normally instant — it waits on the cancel
        // handle) BEFORE DisposeDriverColor unloads NvAPI.
        try
        {
            Sensors.SnapshotReady -= OnSensorSnapshot;
            Sensors.Dispose();
        }
        catch (Exception ex) { AppLog.Warn(ex, "Sensors dispose"); }
        try { Display.DisposeDriverColor(quick); } catch (Exception ex) { AppLog.Warn(ex, "Driver color dispose"); }
        try { Config.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Config dispose"); }
        SessionGuard.MarkCleanExit();
    }

    public void Dispose() => Dispose(quick: false);
}
