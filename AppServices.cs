using DisplayProfileManager.Services;

namespace DisplayProfileManager;

public sealed class AppServices : IDisposable
{
    public ConfigService Config { get; }
    public DisplayEngine Display { get; }
    public ProcessWatcher Watcher { get; }
    public CompanionService Companions { get; }
    public ProfileMonitor Monitor { get; }
    public HotkeyService Hotkeys { get; }
    public ScreenZoomService Zoom { get; }

    public AppServices()
    {
        Config = new ConfigService();
        AppLog.Configure(Config.LogPath);
        Config.LoadOrCreate();

        Display = new DisplayEngine();
        Zoom = new ScreenZoomService();
        Zoom.ApplyFromConfig(Config.Current.Ui);

        CrashRestored = SessionGuard.WasDirtyShutdown();
        if (CrashRestored)
        {
            // Wipe leftover game gamma for safety, but do NOT capture identity as "factory".
            Display.RestoreCrashSafe();
            Display.CaptureFactoryGammaRamp(captureRamp: false);
        }
        else
        {
            Display.CaptureFactoryGammaRamp(captureRamp: true);
        }

        SessionGuard.MarkRunning();

        Watcher = new ProcessWatcher();
        Companions = new CompanionService();
        Monitor = new ProfileMonitor(Config, Display, Watcher, Companions);
        Hotkeys = new HotkeyService();
    }

    /// <summary>True if this launch applied crash-safe gamma/driver restore.</summary>
    public bool CrashRestored { get; }

    public void Dispose()
    {
        try { Zoom.Dispose(); } catch { }
        try { Monitor.Dispose(); } catch { }
        try { Hotkeys.Dispose(); } catch { }
        try { Display.DisposeDriverColor(); } catch { }
        try { Config.Dispose(); } catch { }
        try { SessionGuard.MarkCleanExit(); } catch { }
    }
}
