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

    public AppServices()
    {
        Config = new ConfigService();
        AppLog.Configure(Config.LogPath);
        Config.LoadOrCreate();

        Display = new DisplayEngine();

        // Dirty lock left behind → previous exit crashed/killed; restore identity first.
        CrashRestored = SessionGuard.WasDirtyShutdown();
        if (CrashRestored)
        {
            Display.RestoreCrashSafe();
            Display.CaptureFactoryGammaRamp();
        }
        else
        {
            Display.CaptureFactoryGammaRamp();
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
        try { Monitor.Dispose(); } catch { }
        try { Hotkeys.Dispose(); } catch { }
        try { Display.DisposeDriverColor(); } catch { }
        try { Config.Dispose(); } catch { }
        try { SessionGuard.MarkCleanExit(); } catch { }
    }
}
