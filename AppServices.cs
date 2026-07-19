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
        // Capture OS gamma before any profile applies (factory restore).
        Display.CaptureFactoryGammaRamp();

        Watcher = new ProcessWatcher();
        Companions = new CompanionService();
        Monitor = new ProfileMonitor(Config, Display, Watcher, Companions);
        Hotkeys = new HotkeyService();
    }

    public void Dispose()
    {
        try { Monitor.Dispose(); } catch { }
        try { Hotkeys.Dispose(); } catch { }
        try { Config.Dispose(); } catch { }
    }
}
