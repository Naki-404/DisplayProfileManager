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

    /// <param name="quick">Windows logoff / shutdown: skip optional waits (queues, NvAPI unload).</param>
    public void Dispose(bool quick)
    {
        if (_disposed) return;
        _disposed = true;
        try { Zoom.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Zoom dispose"); }
        try { Monitor.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Monitor dispose"); }
        // Flushes the last debounced history write before the process goes away.
        try { History.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "History dispose"); }
        try { Hotkeys.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Hotkeys dispose"); }
        try { Display.DisposeDriverColor(quick); } catch (Exception ex) { AppLog.Warn(ex, "Driver color dispose"); }
        try { Config.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Config dispose"); }
        SessionGuard.MarkCleanExit();
    }

    public void Dispose() => Dispose(quick: false);
}
