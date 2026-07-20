using DisplayProfileManager.Models;
using System.Windows;
using System.Windows.Threading;

namespace DisplayProfileManager.Services;

/// <summary>
/// Applies PC display/power/session settings when a watched game is present.
/// Does not inject into or send input to the game process.
/// </summary>
public sealed class ProfileMonitor : IDisposable
{
    private readonly ConfigService _config;
    private readonly DisplayEngine _engine;
    private readonly ProcessWatcher _watcher;
    private readonly CompanionService _companions;
    private readonly SessionExtrasService _session = new();
    private readonly object _gate = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _colorLockTimer;
    private DispatcherTimer? _deferredTimer;

    private readonly HashSet<string> _activeProcesses = new(StringComparer.OrdinalIgnoreCase);
    private GameProfile? _currentProfile;
    private string? _currentCompanionsKey;
    private string? _activePresetId;
    private bool _started;
    private bool _colorLockActive;

    public event Action<string>? StatusChanged;
    public event Action<GameProfile?>? ActiveProfileChanged;

    public GameProfile? CurrentProfile
    {
        get { lock (_gate) return _currentProfile; }
    }

    /// <summary>True while a quick-preset color/resolution is driving the display (not profile base / Global).</summary>
    public bool HasActivePreset
    {
        get { lock (_gate) return _activePresetId != null; }
    }

    public string? ActivePresetId
    {
        get { lock (_gate) return _activePresetId; }
    }

    /// <summary>Name of the last preset applied via hotkey (for UI toast).</summary>
    public string? LastPresetHotkeyName { get; private set; }

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
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _watcher.ProcessStarted += OnProcessStarted;
        _watcher.ProcessStopped += OnProcessStopped;
        _config.ConfigChanged += OnConfigChanged;

        _colorLockTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _colorLockTimer.Tick += (_, _) => OnColorLockTick();
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        RefreshWatchList();

        GameProfile? pick = null;
        foreach (var profile in _config.Current.Profiles.Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.ProcessName)))
        {
            if (!ProcessWatcher.IsProcessRunning(profile.ProcessName)) continue;
            lock (_gate) _activeProcesses.Add(Normalize(profile.ProcessName));
            pick = profile;
        }

        // Seed watcher _seen before any Activate so WMI/poll cannot double-fire the same game.
        _watcher.Start();
        StatusChanged?.Invoke("Monitoring");

        if (pick != null)
            RunOnUi(() => ActivateProfile(pick, relaunchCompanions: true));
    }

    public void Stop() => _watcher.Stop();

    public void ResetDisplayNow()
    {
        RunOnUi(() =>
        {
            StopDeferred();
            StopColorLock();
            ClearActivePreset();
            _session.Restore();
            var factory = _config.Current.FactoryDefaults ?? _config.Current.Defaults;
            _engine.RestoreFactory(factory);
            StatusChanged?.Invoke("Factory settings restored");
        });
    }

    public void ApplyPreset(QuickPreset preset)
    {
        RunOnUi(() =>
        {
            lock (_gate) _activePresetId = preset.Id;

            if (preset.ApplyResolution && !string.IsNullOrWhiteSpace(preset.Resolution))
                _engine.SetResolution(preset.Resolution!);

            if (preset.ApplyColor)
            {
                _engine.ApplyColor(preset.Color.Clone());
                if (preset.Color.LockColor)
                    StartColorLock(preset.Color);
                else
                    StopColorLock();
            }

            AppLog.Info($"Preset applied: {preset.Name}");
            StatusChanged?.Invoke($"Preset: {preset.Name}");
        });
    }

    /// <summary>Drop active-preset override so profile / Global can drive color again.</summary>
    public void ClearActivePreset()
    {
        lock (_gate) _activePresetId = null;
    }

    public void HandleHotkey(string action)
    {
        RunOnUi(() =>
        {
            var cfg = _config.Current;
            double step = cfg.HotkeyStep <= 0 ? 0.05 : cfg.HotkeyStep;

            if (action.StartsWith("preset:", StringComparison.OrdinalIgnoreCase))
            {
                var id = action["preset:".Length..];
                var preset = FindPresetById(id);
                if (preset != null)
                {
                    ApplyPreset(preset);
                    StatusChanged?.Invoke($"Preset: {preset.Name}");
                    LastPresetHotkeyName = preset.Name;
                }
                else
                {
                    LastPresetHotkeyName = null;
                    AppLog.Info($"Preset hotkey ignored — id '{id}' not found in config.");
                }
                return;
            }

            LastPresetHotkeyName = null;

            switch (action)
            {
                case "brightnessUp": _engine.AdjustBrightness(step); break;
                case "brightnessDown": _engine.AdjustBrightness(-step); break;
                case "contrastUp": _engine.AdjustContrast(step); break;
                case "contrastDown": _engine.AdjustContrast(-step); break;
                case "gammaUp": _engine.AdjustGamma(step); break;
                case "gammaDown": _engine.AdjustGamma(-step); break;
                case "resetColor":
                    ClearActivePreset();
                    _engine.ResetColorToFactoryOrNeutral(cfg.FactoryDefaults?.Color);
                    break;
                case "compareAb":
                    if (!_engine.ToggleAbCompare())
                        StatusChanged?.Invoke("A/B: apply Preview color first");
                    else
                        StatusChanged?.Invoke(_engine.IsAbShowingFactory ? "A/B: Factory" : "A/B: Preview");
                    return;
            }

            StatusChanged?.Invoke($"Hotkey: {action}");
        });
    }

    private QuickPreset? FindPresetById(string id)
    {
        GameProfile? cur;
        lock (_gate) cur = _currentProfile;

        if (cur != null)
        {
            var fresh = _config.Current.Profiles.FirstOrDefault(p => p.Id == cur.Id) ?? cur;
            var hit = fresh.Presets?.FirstOrDefault(p => p.Id == id);
            if (hit != null) return hit;
        }

        foreach (var p in _config.Current.Profiles)
        {
            var hit = p.Presets?.FirstOrDefault(x => x.Id == id);
            if (hit != null) return hit;
        }

        return null;
    }

    private void OnConfigChanged()
    {
        RefreshWatchList();
        RunOnUi(() =>
        {
            GameProfile? cur;
            string? presetId;
            lock (_gate)
            {
                cur = _currentProfile;
                presetId = _activePresetId;
            }
            if (cur == null) return;

            var fresh = _config.Current.Profiles.FirstOrDefault(p => p.Id == cur.Id);
            if (fresh == null) return;
            lock (_gate) _currentProfile = fresh;

            // Active preset wins: Global / profile base must not overwrite preset color.
            if (presetId != null)
            {
                var preset = fresh.Presets?.FirstOrDefault(p => p.Id == presetId);
                if (preset == null)
                {
                    lock (_gate) _activePresetId = null;
                }
                else if (preset.ApplyColor)
                {
                    _engine.ApplyColor(preset.Color.Clone());
                    if (preset.Color.LockColor)
                        StartColorLock(preset.Color);
                    else
                        StopColorLock();
                    return;
                }
                else
                {
                    return;
                }
            }

            if (fresh.ApplyColor)
            {
                _engine.ApplyColor(fresh.Color.Clone());
                StartColorLockIfNeeded(fresh);
            }
        });
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
        RunOnUi(() => ActivateProfile(profile, relaunchCompanions: true));
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

        if (!wasCurrent)
        {
            foreach (var c in profile.Companions)
                _companions.Stop(c);
        }

        RunOnUi(() =>
        {
            if (next != null)
            {
                ActivateProfile(next, relaunchCompanions: true);
                return;
            }

            if (wasCurrent)
            {
                StopDeferred();
                StopColorLock();
                ClearActivePreset();
                _session.Restore();
                AppLog.Info($"Last game stopped ({processName}) → restore defaults");
                _engine.RestoreDefaults(_config.Current.Defaults);
                StatusChanged?.Invoke("Idle (defaults)");
                ActiveProfileChanged?.Invoke(null);
            }
        });
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
        {
            StopCompanionsFor(previous);
            StopDeferred();
            StopColorLock();
            _session.Restore();
        }

        if (same)
        {
            // Same profile already active — avoid re-isolate / re-capture side effects.
            if (relaunchCompanions && _currentCompanionsKey != profile.Id)
            {
                foreach (var c in profile.Companions)
                    _companions.Launch(c);
                _currentCompanionsKey = profile.Id;
            }
            StatusChanged?.Invoke($"Active: {profile.Name}");
            ActiveProfileChanged?.Invoke(profile);
            return;
        }

        AppLog.Info($"Activating profile: {profile.Name}");
        try
        {
            ApplyProfileCore(profile);
        }
        catch (Exception ex)
        {
            AppLog.Error("Apply profile failed: " + ex);
        }

        try
        {
            var delay = profile.Session?.DeferredApplySeconds ?? 0;
            if (delay > 0)
                ScheduleDeferred(profile, delay);

            if (relaunchCompanions)
            {
                foreach (var c in profile.Companions)
                    _companions.Launch(c);
                _currentCompanionsKey = profile.Id;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Post-apply profile steps failed: " + ex.Message);
        }

        StatusChanged?.Invoke($"Active: {profile.Name}");
        ActiveProfileChanged?.Invoke(profile);
    }

    private void ApplyProfileCore(GameProfile profile)
    {
        // Keep a preset that was Applied / hotkeyed before the game started,
        // as long as it still belongs to this profile.
        string? presetId;
        lock (_gate) presetId = _activePresetId;
        var keep = presetId == null
            ? null
            : profile.Presets?.FirstOrDefault(p => p.Id == presetId);
        if (keep == null)
            ClearActivePreset();

        try
        {
            _engine.ApplyProfile(profile, _config.Current.Defaults);
        }
        catch (Exception ex)
        {
            AppLog.Error("Display apply: " + ex.Message);
        }

        try
        {
            _session.Apply(profile.Session ?? new SessionExtras(), profile.Resolution, profile.DisplayDevice);
        }
        catch (Exception ex)
        {
            AppLog.Error("Session apply: " + ex.Message);
        }

        if (keep != null)
        {
            lock (_gate) _activePresetId = keep.Id;
            if (keep.ApplyColor)
            {
                try
                {
                    _engine.ApplyColor(keep.Color.Clone());
                    if (keep.Color.LockColor)
                        StartColorLock(keep.Color);
                    else
                        StopColorLock();
                    AppLog.Info($"Kept active preset after profile apply: {keep.Name}");
                }
                catch (Exception ex)
                {
                    AppLog.Error("Preset re-apply after profile: " + ex.Message);
                }
                return;
            }
        }

        try
        {
            StartColorLockIfNeeded(profile);
        }
        catch (Exception ex)
        {
            AppLog.Error("Color lock start: " + ex.Message);
        }
    }

    private void ScheduleDeferred(GameProfile profile, int seconds)
    {
        StopDeferred();
        var id = profile.Id;
        _deferredTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 120))
        };
        _deferredTimer.Tick += (_, _) =>
        {
            StopDeferred();
            GameProfile? cur;
            lock (_gate) cur = _currentProfile;
            if (cur == null || cur.Id != id) return;
            var fresh = _config.Current.Profiles.FirstOrDefault(p => p.Id == id) ?? cur;
            AppLog.Info($"Deferred re-apply for {fresh.Name}");
            ApplyProfileCore(fresh);
            StatusChanged?.Invoke($"Re-applied: {fresh.Name}");
        };
        _deferredTimer.Start();
    }

    private void StopDeferred()
    {
        if (_deferredTimer == null) return;
        _deferredTimer.Stop();
        _deferredTimer = null;
    }

    private void StartColorLockIfNeeded(GameProfile profile)
    {
        if (profile.ApplyColor && profile.Color.LockColor)
            StartColorLock(profile.Color);
        else
            StopColorLock();
    }

    private void StartColorLock(ColorSettings color)
    {
        _colorLockActive = true;
        _colorLockTimer.Interval = color.Backend == ColorBackend.LowLevel
            ? TimeSpan.FromMilliseconds(500)
            : TimeSpan.FromSeconds(2);
        if (!_colorLockTimer.IsEnabled)
            _colorLockTimer.Start();
    }

    private void StopColorLock()
    {
        _colorLockActive = false;
        if (_colorLockTimer.IsEnabled)
            _colorLockTimer.Stop();
    }

    private void OnColorLockTick()
    {
        GameProfile? profile;
        lock (_gate) profile = _currentProfile;
        if (!_colorLockActive || profile == null)
        {
            StopColorLock();
            return;
        }
        try { _engine.ReapplyLiveColor(); }
        catch (Exception ex) { AppLog.Error("Color lock reapply failed: " + ex.Message); }
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

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        StopDeferred();
        StopColorLock();
        try { _session.Dispose(); } catch { }
        _watcher.ProcessStarted -= OnProcessStarted;
        _watcher.ProcessStopped -= OnProcessStopped;
        _config.ConfigChanged -= OnConfigChanged;
        _watcher.Dispose();
    }
}
