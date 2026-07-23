using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private readonly SessionSnapshotService _snapshots;
    private readonly object _gate = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _colorLockTimer;
    private DispatcherTimer? _deferredTimer;
    private CancellationTokenSource? _activationCts;

    private readonly HashSet<string> _activeProcesses = new(StringComparer.OrdinalIgnoreCase);
    private GameProfile? _currentProfile;
    private string? _currentCompanionsKey;
    private string? _activePresetId;
    private bool _started;
    private bool _colorLockActive;
    private bool _colorLockPaused;
    private ColorSettings? _colorLockSource;

    public event Action<string>? StatusChanged;
    public event Action<GameProfile?>? ActiveProfileChanged;

    public GameProfile? CurrentProfile
    {
        get { lock (_gate) return _currentProfile; }
    }

    public bool HasActivePreset
    {
        get { lock (_gate) return _activePresetId != null; }
    }

    public string? ActivePresetId
    {
        get { lock (_gate) return _activePresetId; }
    }

    public string? LastPresetHotkeyName { get; private set; }
    public string? LastApplyToastDetail { get; private set; }
    public bool SnapshotActive => _snapshots.HasSnapshot;

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
        _snapshots = new SessionSnapshotService(engine, config.ConfigDirectory);
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

        // Crash leftover: restore previous session before watching games.
        if (_snapshots.HasSnapshot)
        {
            AppLog.Info("Found leftover active-session.json — restoring.");
            _snapshots.RestoreAll(_session, _config.Current.Defaults);
            StatusChanged?.Invoke("Restored previous session snapshot");
        }

        RefreshWatchList();

        GameProfile? pick = null;
        foreach (var profile in _config.Current.Profiles.Where(p => p.Enabled))
        {
            if (!AnyProcessRunning(profile)) continue;
            foreach (var n in EnumerateProcessNames(profile))
            {
                if (ProcessWatcher.IsProcessRunning(n))
                    lock (_gate) _activeProcesses.Add(Normalize(n));
            }
            pick = profile;
        }

        _watcher.Start();
        StatusChanged?.Invoke("Monitoring");

        if (pick != null)
            RunOnUi(() => ActivateProfile(pick, relaunchCompanions: true));
    }

    public void EmergencyRestore()
    {
        RunOnUi(() =>
        {
            CancelPendingActivation();
            StopDeferred();
            StopColorLock();
            ClearActivePreset();
            _engine.ClearAbCompare();

            GameProfile? cur;
            lock (_gate)
            {
                cur = _currentProfile;
                if (cur != null)
                    StopCompanionsFor(cur);
                _currentProfile = null;
                _currentCompanionsKey = null;
            }

            bool restoredSnap = false;
            if (_snapshots.HasSnapshot)
                restoredSnap = _snapshots.RestoreAll(_session, _config.Current.FactoryDefaults ?? _config.Current.Defaults);
            else
            {
                try { _session.Restore(); } catch { }
                var factory = _config.Current.FactoryDefaults ?? _config.Current.Defaults;
                _engine.RestoreFactory(factory);
            }

            StatusChanged?.Invoke(restoredSnap
                ? "Emergency Restore: snapshot restored"
                : "Emergency Restore: factory settings");
            ActiveProfileChanged?.Invoke(null);
            AppLog.Info("Emergency Restore completed.");
        });
    }

    public void ApplyPreset(QuickPreset preset)
    {
        RunOnUi(() =>
        {
            lock (_gate) _activePresetId = preset.Id;

            try { _engine.ClearAbCompare(); } catch { }

            if (preset.ApplyResolution && !string.IsNullOrWhiteSpace(preset.Resolution))
                _engine.SetResolution(preset.Resolution!);

            // Color only when this preset opts in (res-only presets must not wipe live gamma).
            if (preset.ApplyColor)
            {
                var color = (preset.Color ?? ColorSettings.Neutral).Clone();
                color.Clamp();
                try
                {
                    _engine.ApplyColor(color);
                    if (color.LockColor)
                        StartColorLock(color);
                    else
                        StopColorLock();
                }
                catch (Exception ex)
                {
                    AppLog.Error("Preset color apply failed: " + ex.Message);
                }

                AppLog.Info($"Preset applied: {preset.Name} (color, backend={color.Backend}, lock={color.LockColor})");
            }
            else
            {
                AppLog.Info($"Preset applied: {preset.Name} (resolution only — color left alone)");
            }

            StatusChanged?.Invoke($"Preset: {preset.Name}");
        });
    }

    public void ClearActivePreset()
    {
        lock (_gate) _activePresetId = null;
    }

    public bool CyclePreset(int direction)
    {
        GameProfile? cur;
        string? presetId;
        lock (_gate)
        {
            cur = _currentProfile;
            presetId = _activePresetId;
        }

        cur ??= (System.Windows.Application.Current?.MainWindow as MainWindow)?.HotkeyPresetFallback;
        if (cur == null) return false;
        cur = _config.Current.Profiles.FirstOrDefault(p => p.Id == cur.Id) ?? cur;
        var list = cur.Presets;
        if (list == null || list.Count == 0) return false;

        int idx = list.FindIndex(p => p.Id == presetId);
        if (idx < 0) idx = direction > 0 ? -1 : 0;
        int next = (idx + direction + list.Count * 8) % list.Count;
        var preset = list[next];
        ApplyPreset(preset);
        LastPresetHotkeyName = preset.Name;
        StatusChanged?.Invoke($"Preset: {preset.Name}");
        return true;
    }

    public void HandleHotkey(string action)
    {
        RunOnUi(() =>
        {
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
                case "toggleOverlay":
                    if (System.Windows.Application.Current is App app)
                        app.ToggleGameOverlay();
                    return;
                case "emergencyRestore":
                    EmergencyRestore();
                    return;
                case "nextPreset":
                    if (CyclePreset(+1))
                    { /* LastPresetHotkeyName set by CyclePreset */ }
                    return;
                case "previousPreset":
                    if (CyclePreset(-1))
                    { /* LastPresetHotkeyName set by CyclePreset */ }
                    return;
                case "compareAb":
                    if (_engine.ToggleAbCompare())
                        SetColorLockPaused(_engine.IsAbShowingFactory);
                    return;
            }

            StatusChanged?.Invoke($"Hotkey: {action}");
        });
    }

    public void SetColorLockPaused(bool paused)
    {
        _colorLockPaused = paused;
        if (!paused && _colorLockActive && _colorLockSource != null && !_colorLockTimer.IsEnabled)
            _colorLockTimer.Start();
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
            // Profile deleted or disabled while game is active → safe restore.
            if (fresh == null || !fresh.Enabled)
            {
                AppLog.Info($"Active profile {(fresh == null ? "deleted" : "disabled")} — Emergency Restore.");
                EmergencyRestore();
                return;
            }

            lock (_gate) _currentProfile = fresh;

            if (presetId != null)
            {
                var preset = fresh.Presets?.FirstOrDefault(p => p.Id == presetId);
                if (preset == null)
                {
                    lock (_gate) _activePresetId = null;
                    try { StopColorLock(); } catch { }
                    return;
                }

                try
                {
                    if (!preset.ApplyColor)
                        return;
                    var color = (preset.Color ?? ColorSettings.Neutral).Clone();
                    color.Clamp();
                    _engine.ApplyColor(color);
                    if (color.LockColor)
                        StartColorLock(color);
                    else
                        StopColorLock();
                }
                catch (Exception ex)
                {
                    AppLog.Error("ConfigChanged preset re-apply: " + ex.Message);
                }
                return;
            }

            // No active preset — leave display alone (color is presets-only).
        });
        AppLog.Info("Profile monitor noted config change.");
        StatusChanged?.Invoke("Config reloaded");
    }

    private void RefreshWatchList()
    {
        var names = new List<string>();
        foreach (var p in _config.Current.Profiles.Where(x => x.Enabled))
        {
            foreach (var n in EnumerateProcessNames(p))
                names.Add(n);
        }
        _watcher.SetWatchList(names);
    }

    /// <summary>
    /// Call after in-app autosave (raiseChanged: false) so watch-list and active
    /// profile pointer stay in sync without re-hitting resolution/power mid-game.
    /// </summary>
    public void NotifyConfigSaved()
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
            if (fresh == null || !fresh.Enabled)
            {
                AppLog.Info("Active profile removed/disabled after save — Emergency Restore.");
                EmergencyRestore();
                return;
            }

            lock (_gate) _currentProfile = fresh;

            // Soft path: refresh color from the active preset only — no ChangeDisplaySettings / powercfg.
            if (presetId == null) return;
            var preset = fresh.Presets?.FirstOrDefault(p => p.Id == presetId);
            if (preset == null || !preset.ApplyColor) return;

            try
            {
                var color = (preset.Color ?? ColorSettings.Neutral).Clone();
                color.Clamp();
                _engine.ApplyColor(color);
                if (color.LockColor)
                    StartColorLock(color);
                else
                    StopColorLock();
            }
            catch (Exception ex)
            {
                AppLog.Error("NotifyConfigSaved preset color: " + ex.Message);
            }
        });
    }

    private void OnProcessStarted(string processName)
    {
        var profile = FindProfile(processName);
        if (profile == null) return;
        lock (_gate) _activeProcesses.Add(Normalize(processName));
        _ = ActivateProfileAsync(profile);
    }

    private async Task ActivateProfileAsync(GameProfile profile)
    {
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            try { _activationCts?.Cancel(); } catch { }
            try { _activationCts?.Dispose(); } catch { }
            _activationCts = cts;
        }

        try
        {
            // Prefer live config copy
            var fresh = _config.Current.Profiles.FirstOrDefault(p => p.Id == profile.Id) ?? profile;

            int delay = Math.Clamp(fresh.ApplyDelaySeconds, 0, 120);
            if (delay > 0)
            {
                AppLog.Info($"Apply delay {delay}s for {fresh.Name}");
                await Task.Delay(TimeSpan.FromSeconds(delay), cts.Token).ConfigureAwait(false);
                if (!ProfileStillActive(fresh))
                {
                    AppLog.Info($"Apply delay cancelled — process gone ({fresh.Name})");
                    return;
                }
            }

            if (fresh.ApplyOnFocus)
            {
                AppLog.Info($"Waiting for focus (up to 30s) for {fresh.Name}");
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (!ProfileStillActive(fresh))
                    {
                        AppLog.Info($"Focus wait cancelled — process gone ({fresh.Name})");
                        return;
                    }
                    if (ForegroundProcessHelper.IsForegroundOwnedBy(EnumerateProcessNames(fresh)))
                        break;
                    await Task.Delay(250, cts.Token).ConfigureAwait(false);
                }
            }

            if (!ProfileStillActive(fresh))
                return;

            await _dispatcher.InvokeAsync(() =>
            {
                ActivateProfile(fresh, relaunchCompanions: true);
            });
        }
        catch (OperationCanceledException)
        {
            AppLog.Info($"Activation cancelled for {profile.Name}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Async profile activation: " + ex.Message);
        }
    }

    private void OnProcessStopped(string processName)
    {
        var profile = FindProfileIncludingDisabled(processName);
        if (profile == null) return;

        bool wasCurrent;
        bool stillRunning;
        GameProfile? next = null;
        RestoreMode restoreMode = RestoreMode.PreviousSnapshot;
        lock (_gate)
        {
            _activeProcesses.Remove(Normalize(processName));
            wasCurrent = _currentProfile != null && MatchesProcess(_currentProfile, processName);
            stillRunning = wasCurrent && AnyActiveNameFor(_currentProfile!);

            if (wasCurrent && !stillRunning)
            {
                var cur = _currentProfile;
                if (cur != null)
                {
                    restoreMode = cur.RestoreMode;
                    StopCompanionsFor(cur);
                }
                _currentProfile = null;
                _currentCompanionsKey = null;

                next = _config.Current.Profiles.FirstOrDefault(p =>
                    p.Enabled && AnyActiveNameFor(p));
            }
        }

        if (wasCurrent && !stillRunning)
            CancelPendingActivation();

        if (!wasCurrent || stillRunning)
        {
            if (!wasCurrent)
            {
                foreach (var c in profile.Companions)
                    _companions.Stop(c);
            }
            return;
        }

        RunOnUi(() =>
        {
            if (next != null)
            {
                ActivateProfile(next, relaunchCompanions: true);
                return;
            }

            StopDeferred();
            StopColorLock();
            ClearActivePreset();
            _engine.ClearAbCompare();
            ApplyRestoreMode(restoreMode);
            StatusChanged?.Invoke("Idle");
            ActiveProfileChanged?.Invoke(null);
        });
    }

    private void ApplyRestoreMode(RestoreMode mode)
    {
        switch (mode)
        {
            case RestoreMode.DoNothing:
                // Leave tuned color/display; also leave session extras as applied.
                _snapshots.Clear();
                AppLog.Info("RestoreMode=DoNothing — left tuned state.");
                break;
            case RestoreMode.GlobalDefaults:
                try { _session.Restore(); } catch { }
                _engine.RestoreDefaults(_config.Current.Defaults);
                _snapshots.Clear();
                AppLog.Info("RestoreMode=GlobalDefaults.");
                break;
            default:
                if (_snapshots.HasSnapshot)
                {
                    _snapshots.RestoreAll(_session, _config.Current.Defaults);
                    AppLog.Info("RestoreMode=PreviousSnapshot.");
                }
                else
                {
                    try { _session.Restore(); } catch { }
                    _engine.RestoreDefaults(_config.Current.Defaults);
                    AppLog.Info("No snapshot — fell back to Global Defaults.");
                }
                break;
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
        {
            StopCompanionsFor(previous);
            StopDeferred();
            StopColorLock();
            // Leaving previous game: restore its extras before new capture.
            try { _session.Restore(); } catch { }
        }

        if (same)
        {
            // Soft re-apply: config may have changed, or a second start event arrived.
            // Do NOT capture a new snapshot (would overwrite pre-game PC state).
            AppLog.Info($"Re-applying active profile: {profile.Name}");
            try { ApplyProfileCore(profile); }
            catch (Exception ex) { AppLog.Error("Same-profile re-apply: " + ex.Message); }

            try
            {
                var delay = profile.Session?.DeferredApplySeconds ?? 0;
                if (delay > 0)
                    ScheduleDeferred(profile, delay);
            }
            catch { }

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
        LastApplyToastDetail = null;
        try
        {
            // Capture PC state before applying this game (for PreviousSnapshot / crash).
            var snap = _snapshots.Capture(profile, _session);
            LastApplyToastDetail = $"Snapshot saved · {snap.Resolution}";
            ApplyProfileCore(profile);

            // Persist topology/scaling flags after session apply.
            var live = _snapshots.Current;
            if (live != null)
            {
                _session.MergeLiveInto(live);
                _snapshots.Persist(live);
            }
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

        // Startup preset only for new game activation (not soft reapply).
        if (!string.IsNullOrWhiteSpace(profile.StartupPresetId))
        {
            var preset = profile.Presets?.FirstOrDefault(p => p.Id == profile.StartupPresetId)
                         ?? FindPresetById(profile.StartupPresetId!);
            if (preset != null)
            {
                ApplyPreset(preset);
                LastPresetHotkeyName = preset.Name;
            }
        }

        StatusChanged?.Invoke($"Active: {profile.Name}");
        ActiveProfileChanged?.Invoke(profile);
    }

    private void ApplyProfileCore(GameProfile profile)
    {
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
            if (!string.IsNullOrWhiteSpace(_session.LastWarning))
            {
                if (string.IsNullOrWhiteSpace(LastApplyToastDetail))
                    LastApplyToastDetail = _session.LastWarning;
                else
                    LastApplyToastDetail += " · " + _session.LastWarning;
            }
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
                    var color = (keep.Color ?? ColorSettings.Neutral).Clone();
                    color.Clamp();
                    _engine.ApplyColor(color);
                    if (color.LockColor)
                        StartColorLock(color);
                    else
                        StopColorLock();
                    AppLog.Info($"Kept active preset after profile apply: {keep.Name}");
                }
                catch (Exception ex)
                {
                    AppLog.Error("Preset re-apply after profile: " + ex.Message);
                }
            }
            return;
        }

        // Color is presets-only — do not lock/re-apply profile color.
        try { StopColorLock(); }
        catch (Exception ex) { AppLog.Error("Color lock stop: " + ex.Message); }
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

    private void StartColorLock(ColorSettings color)
    {
        _colorLockActive = true;
        _colorLockPaused = false;
        _colorLockSource = color.Clone();
        _colorLockTimer.Interval = color.Backend == ColorBackend.LowLevel
            ? TimeSpan.FromMilliseconds(500)
            : TimeSpan.FromSeconds(2);
        if (!_colorLockTimer.IsEnabled)
            _colorLockTimer.Start();
    }

    private void StopColorLock()
    {
        _colorLockActive = false;
        _colorLockPaused = false;
        _colorLockSource = null;
        if (_colorLockTimer.IsEnabled)
            _colorLockTimer.Stop();
    }

    private void OnColorLockTick()
    {
        if (_colorLockPaused) return;
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
            p.Enabled && MatchesProcess(p, norm));
    }

    private GameProfile? FindProfileIncludingDisabled(string processName)
    {
        var norm = Normalize(processName);
        return _config.Current.Profiles.FirstOrDefault(p => MatchesProcess(p, norm));
    }

    private bool ProfileStillActive(GameProfile profile)
    {
        lock (_gate)
        {
            if (AnyActiveNameFor(profile))
                return true;
        }
        return AnyProcessRunning(profile);
    }

    private bool AnyActiveNameFor(GameProfile profile)
    {
        foreach (var n in _activeProcesses)
        {
            if (MatchesProcess(profile, n))
                return true;
        }
        return false;
    }

    private static bool AnyProcessRunning(GameProfile profile)
    {
        foreach (var n in EnumerateProcessNames(profile))
        {
            if (ProcessWatcher.IsProcessRunning(n))
                return true;
        }
        return false;
    }

    private static bool MatchesProcess(GameProfile profile, string processName)
    {
        var norm = Normalize(processName);
        if (!string.IsNullOrWhiteSpace(profile.ProcessName) && Normalize(profile.ProcessName) == norm)
            return true;
        if (profile.ProcessAliases == null) return false;
        foreach (var a in profile.ProcessAliases)
        {
            if (!string.IsNullOrWhiteSpace(a) && Normalize(a) == norm)
                return true;
        }
        return false;
    }

    private static IEnumerable<string> EnumerateProcessNames(GameProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.ProcessName))
            yield return profile.ProcessName;
        if (profile.ProcessAliases == null) yield break;
        foreach (var a in profile.ProcessAliases)
        {
            if (!string.IsNullOrWhiteSpace(a))
                yield return a;
        }
    }

    private static string Normalize(string name)
    {
        name = name.Trim();
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name += ".exe";
        return name.ToLowerInvariant();
    }

    private void CancelPendingActivation()
    {
        try { _activationCts?.Cancel(); } catch { }
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
        CancelPendingActivation();
        try { _activationCts?.Dispose(); } catch { }
        _activationCts = null;
        StopDeferred();
        StopColorLock();
        try { _session.Dispose(); } catch { }
        _watcher.ProcessStarted -= OnProcessStarted;
        _watcher.ProcessStopped -= OnProcessStopped;
        _config.ConfigChanged -= OnConfigChanged;
        _watcher.Dispose();
    }
}

/// <summary>Tiny Win32 helpers for focus-gated profile activation.</summary>
internal static class ForegroundProcessHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public static bool IsForegroundOwnedBy(IEnumerable<string> processNames)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in processNames)
        {
            var n = Normalize(raw);
            if (!string.IsNullOrWhiteSpace(n))
                targets.Add(n);
        }
        if (targets.Count == 0) return false;

        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;

        try
        {
            using var p = Process.GetProcessById((int)pid);
            var name = Normalize(p.ProcessName);
            return targets.Contains(name);
        }
        catch
        {
            return false;
        }
    }

    private static string Normalize(string name)
    {
        name = name.Trim();
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name += ".exe";
        return name.ToLowerInvariant();
    }
}
