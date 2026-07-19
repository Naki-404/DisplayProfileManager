using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

/// <summary>
/// Applies PC display/power settings when a watched game is present.
/// Does not inject into or send input to the game process.
/// Single-active profile: last started game wins; previous companions stopped cleanly.
/// </summary>
public sealed class ProfileMonitor : IDisposable
{
    private readonly ConfigService _config;
    private readonly DisplayEngine _engine;
    private readonly ProcessWatcher _watcher;
    private readonly CompanionService _companions;
    private readonly object _gate = new();

    private readonly HashSet<string> _activeProcesses = new(StringComparer.OrdinalIgnoreCase);
    private GameProfile? _currentProfile;
    private string? _currentCompanionsKey;

    public event Action<string>? StatusChanged;
    public event Action<GameProfile?>? ActiveProfileChanged;

    public GameProfile? CurrentProfile
    {
        get { lock (_gate) return _currentProfile; }
    }

    public bool IsPaused
    {
        get => _watcher.IsPaused;
        set
        {
            _watcher.IsPaused = value;
            StatusChanged?.Invoke(value ? "Paused" : "Monitoring");
            AppLog.Info(value ? "Monitoring paused." : "Monitoring resumed.");
        }
    }

    public ProfileMonitor(ConfigService config, DisplayEngine engine, ProcessWatcher watcher, CompanionService companions)
    {
        _config = config;
        _engine = engine;
        _watcher = watcher;
        _companions = companions;

        _watcher.ProcessStarted += OnProcessStarted;
        _watcher.ProcessStopped += OnProcessStopped;
        _config.ConfigChanged += OnConfigChanged;
    }

    public void Start()
    {
        RefreshWatchList();

        GameProfile? pick = null;
        foreach (var profile in _config.Current.Profiles.Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.ProcessName)))
        {
            if (!ProcessWatcher.IsProcessRunning(profile.ProcessName)) continue;
            lock (_gate) _activeProcesses.Add(Normalize(profile.ProcessName));
            pick = profile; // last running wins on startup
        }

        if (pick != null)
            ActivateProfile(pick, relaunchCompanions: true);

        _watcher.Start();
        StatusChanged?.Invoke("Monitoring");
    }

    public void Stop() => _watcher.Stop();

    public void ResetDisplayNow()
    {
        var factory = _config.Current.FactoryDefaults ?? _config.Current.Defaults;
        _engine.RestoreFactory(factory);
        StatusChanged?.Invoke("Factory settings restored");
    }

    public void ApplyPreset(QuickPreset preset)
    {
        if (preset.ApplyResolution && !string.IsNullOrWhiteSpace(preset.Resolution))
            _engine.SetResolution(preset.Resolution!);

        if (preset.ApplyColor)
            _engine.ApplyColor(preset.Color.Clone());

        AppLog.Info($"Preset applied: {preset.Name}");
        StatusChanged?.Invoke($"Preset: {preset.Name}");
    }

    public void HandleHotkey(string action)
    {
        var cfg = _config.Current;
        double step = cfg.HotkeyStep <= 0 ? 0.05 : cfg.HotkeyStep;

        if (action.StartsWith("preset:", StringComparison.OrdinalIgnoreCase))
        {
            var id = action["preset:".Length..];
            GameProfile? profile;
            lock (_gate) profile = _currentProfile;
            var preset = profile?.Presets?.FirstOrDefault(p => p.Id == id);
            if (preset != null) ApplyPreset(preset);
            else
                AppLog.Info($"Preset {id} ignored — no active game profile.");
            return;
        }

        switch (action)
        {
            case "brightnessUp":
                _engine.AdjustBrightness(step);
                break;
            case "brightnessDown":
                _engine.AdjustBrightness(-step);
                break;
            case "contrastUp":
                _engine.AdjustContrast(step);
                break;
            case "contrastDown":
                _engine.AdjustContrast(-step);
                break;
            case "gammaUp":
                _engine.AdjustGamma(step);
                break;
            case "gammaDown":
                _engine.AdjustGamma(-step);
                break;
            case "resetColor":
                _engine.ResetColorToFactoryOrNeutral(cfg.FactoryDefaults?.Color);
                break;
        }

        StatusChanged?.Invoke($"Hotkey: {action}");
    }

    private void OnConfigChanged()
    {
        RefreshWatchList();
        AppLog.Info("Profile monitor noted config change.");
        StatusChanged?.Invoke("Config reloaded");
    }

    private void RefreshWatchList()
    {
        var names = _config.Current.Profiles
            .Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.ProcessName))
            .Select(p => p.ProcessName);
        _watcher.SetWatchList(names);
    }

    private void OnProcessStarted(string processName)
    {
        var profile = FindProfile(processName);
        if (profile == null) return;
        lock (_gate) _activeProcesses.Add(Normalize(processName));
        ActivateProfile(profile, relaunchCompanions: true);
    }

    private void OnProcessStopped(string processName)
    {
        var profile = FindProfile(processName);
        if (profile == null) return;

        bool wasCurrent;
        GameProfile? next = null;
        lock (_gate)
        {
            _activeProcesses.Remove(Normalize(processName));
            wasCurrent = _currentProfile != null
                         && Normalize(_currentProfile.ProcessName) == Normalize(processName);

            if (wasCurrent)
            {
                var cur = _currentProfile;
                if (cur != null)
                    StopCompanionsFor(cur);
                _currentProfile = null;
                _currentCompanionsKey = null;

                next = _config.Current.Profiles.FirstOrDefault(p =>
                    p.Enabled && _activeProcesses.Contains(Normalize(p.ProcessName)));
            }
        }

        // Stop companions for the game that exited even if it wasn't current
        if (!wasCurrent)
        {
            foreach (var c in profile.Companions)
                _companions.Stop(c);
        }

        if (next != null)
        {
            ActivateProfile(next, relaunchCompanions: true);
            return;
        }

        if (wasCurrent)
        {
            AppLog.Info($"Last game stopped ({processName}) → restore defaults");
            _engine.RestoreDefaults(_config.Current.Defaults);
            StatusChanged?.Invoke("Idle (defaults)");
            ActiveProfileChanged?.Invoke(null);
        }
    }

    private void ActivateProfile(GameProfile profile, bool relaunchCompanions)
    {
        GameProfile? previous;
        bool same;
        lock (_gate)
        {
            previous = _currentProfile;
            same = previous?.Id == profile.Id;
            _currentProfile = profile;
        }

        if (!same && previous != null)
            StopCompanionsFor(previous);

        AppLog.Info($"Activating profile: {profile.Name}");
        _engine.ApplyProfile(profile, _config.Current.Defaults);

        if (relaunchCompanions && (!same || _currentCompanionsKey != profile.Id))
        {
            foreach (var c in profile.Companions)
                _companions.Launch(c);
            _currentCompanionsKey = profile.Id;
        }

        StatusChanged?.Invoke($"Active: {profile.Name}");
        ActiveProfileChanged?.Invoke(profile);
    }

    private void StopCompanionsFor(GameProfile profile)
    {
        foreach (var c in profile.Companions)
            _companions.Stop(c);
        if (_currentCompanionsKey == profile.Id)
            _currentCompanionsKey = null;
    }

    private GameProfile? FindProfile(string processName)
    {
        var norm = Normalize(processName);
        return _config.Current.Profiles.FirstOrDefault(p =>
            p.Enabled &&
            !string.IsNullOrWhiteSpace(p.ProcessName) &&
            Normalize(p.ProcessName) == norm);
    }

    private static string Normalize(string name)
    {
        name = name.Trim();
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name += ".exe";
        return name.ToLowerInvariant();
    }

    public void Dispose()
    {
        _watcher.ProcessStarted -= OnProcessStarted;
        _watcher.ProcessStopped -= OnProcessStopped;
        _config.ConfigChanged -= OnConfigChanged;
        _watcher.Dispose();
    }
}
