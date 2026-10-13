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

    private bool _disposed;

    public AppServices()
    {
        Config = new ConfigService();
        AppLog.Configure(Config.LogPath);
        Config.LoadOrCreate();

        Display = new DisplayEngine(Config.ConfigDirectory);
        Zoom = new ScreenZoomService();
        Zoom.ApplyFromConfig(Config.Current.Ui);

        Watcher = new ProcessWatcher();
        Companions = new CompanionService();
        Monitor = new ProfileMonitor(Config, Display, Watcher, Companions);
        Hotkeys = new HotkeyService();

        // Crash restore only from real evidence: a leftover session snapshot is restored first
        // (pre-game modes / ramps / extras). A dirty exit without one re-applies the baseline
        // saved at the last clean start, if any — never a blind identity ramp.
        bool dirty = SessionGuard.WasDirtyShutdown();
        bool sessionRestored = Monitor.HasLeftoverSession && Monitor.RestoreLeftoverSession();
        bool baselineRestored = Display.InitializeBaseline(useSavedBaseline: dirty && !sessionRestored);
        CrashRestored = sessionRestored || baselineRestored;
        // Now the desktop is in its real state — finish migrations that read it (factory power plan).
        Config.CompleteDeferredMigration();

        SessionGuard.MarkRunning();
    }

    /// <summary>True if this launch restored state left behind by a crashed / killed run.</summary>
    public bool CrashRestored { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Zoom.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Zoom dispose"); }
        try { Monitor.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Monitor dispose"); }
        try { Hotkeys.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Hotkeys dispose"); }
        try { Display.DisposeDriverColor(); } catch (Exception ex) { AppLog.Warn(ex, "Driver color dispose"); }
        try { Config.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Config dispose"); }
        SessionGuard.MarkCleanExit();
    }
}
