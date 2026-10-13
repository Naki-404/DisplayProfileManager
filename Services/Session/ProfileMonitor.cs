using System.Runtime.InteropServices;
using Visor.Models;
using System.Windows.Threading;

namespace Visor.Services;

/// <summary>
/// Applies PC display/power/session settings when a watched game is present.
/// Does not inject into or send input to the game process.
/// </summary>
/// <remarks>
/// Threading: every state change runs on the UI dispatcher. <c>_gate</c> only guards the
/// small shared fields read from watcher threads — no slow work ever runs under it.
/// Companion start/stop (process waits) and NVIDIA DRS writes (FPS cap) run on ordered
/// background queues so the UI and tray never freeze.
/// </remarks>
/// <summary>What <see cref="ProfileMonitor.RestoreLeftover"/> found on disk from the previous run.</summary>
public enum LeftoverRestoreResult
{
    Nothing,
    /// <summary>Only pending-fps-restore.json — a queued FPS-cap undo that did not finish; not a crash.</summary>
    PendingFpsOnly,
    /// <summary>active-session.json — the previous run ended mid-game (crash / kill / power loss).</summary>
    SessionRestored
}

public sealed class ProfileMonitor : IDisposable
{
    private readonly ConfigService _config;
    private readonly DisplayEngine _engine;
    private readonly ProcessWatcher _watcher;
    private readonly CompanionService _companions;
    private readonly ActivityHistoryStore? _history;
    private readonly SessionExtrasService _session = new();
    private readonly SessionSnapshotService _snapshots;
    private readonly object _gate = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _colorLockTimer;
    private readonly SerialQueue _companionQueue = new("Companions");
    private readonly SerialQueue _driverQueue = new("Driver settings");
    private DispatcherTimer? _deferredTimer;
    /// <summary>Profile the (single, reused) deferred timer is armed for.</summary>
    private string? _deferredProfileId;
    private CancellationTokenSource? _activationCts;
    private DispatcherTimer? _focusPollTimer;
    private bool _focusPollHasFocus = true;
    /// <summary>Exe keys of the profile the focus poll watches (built once per activation, not per tick).</summary>
    private HashSet<string> _focusPollKeys = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Set by <see cref="ShutdownRestore"/>: every wait on the exit path is bounded by it.</summary>
    private DateTime? _shutdownDeadlineUtc;
    private static readonly TimeSpan ExitBudget = TimeSpan.FromSeconds(9);
    /// <summary>Windows gives ~5 s at logoff: restore synchronously, never wait on background queues.</summary>
    private static readonly TimeSpan LogoffBudget = TimeSpan.FromSeconds(3);

    private readonly HashSet<string> _activeProcesses = new(StringComparer.OrdinalIgnoreCase);
    private GameProfile? _currentProfile;
    private string? _currentCompanionsKey;
    private string? _activePresetId;
    /// <summary>Profile activated from the tray without its game running (ends on Emergency / EndForcedProfile).</summary>
    private string? _forcedProfileId;
    /// <summary>Profile whose (delayed / focus-gated) activation is pending.</summary>
    private GameProfile? _pendingActivationProfile;
    /// <summary>Bumped whenever companions of a session are stopped; queued launches of an older generation are dropped.</summary>
    private int _companionGeneration;
    /// <summary>Mode the active session set (re-applied if Windows reverts it).</summary>
    private (string Resolution, int RefreshRate, string? Device)? _sessionMode;
    private DispatcherTimer? _modeGuardTimer;
    private readonly List<DateTime> _modeGuardHistory = new();
    private bool _started;
    private bool _shutdownDone;
    private bool _colorLockActive;
    private bool _colorLockPaused;
    private ColorSettings? _colorLockSource;
    /// <summary>True while ActivateProfile applies a startup preset — avoid a second history row.</summary>
    private bool _suppressPresetHistory;

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

    /// <summary>True while the current profile was forced from the tray (its game is not running).</summary>
    public bool IsForcedActivation
    {
        get { lock (_gate) return _forcedProfileId != null && _currentProfile?.Id == _forcedProfileId; }
    }

    public string? LastPresetHotkeyName { get; private set; }
    public string? LastApplyToastDetail { get; private set; }
    public bool SnapshotActive => _snapshots.HasSnapshot;

    public bool IsPaused
    {
        get => _watcher.IsPaused;
        set
        {
            if (_watcher.IsPaused == value) return;
            _watcher.IsPaused = value;
            StatusChanged?.Invoke(value ? "Paused" : "Monitoring"); // App maps these tokens via Loc
            _history?.Add(value ? "paused" : "resumed");
            AppLog.Info(value ? "Monitoring paused." : "Monitoring resumed.");
        }
    }

    public ProfileMonitor(ConfigService config, DisplayEngine engine, ProcessWatcher watcher, CompanionService companions,
        ActivityHistoryStore? history = null)
    {
        _config = config;
        _engine = engine;
        _watcher = watcher;
        _companions = companions;
        _history = history;
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
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>True when active-session.json or pending-fps-restore.json survived the previous run.</summary>
    public bool HasLeftoverSession => _snapshots.HasSnapshot || _snapshots.HasPendingFps;

    /// <summary>
    /// Startup, before baseline capture: restore a session snapshot left by a crashed run.
    /// Synchronous. Returns true when anything was found and restored (errors are logged).
    /// </summary>
    public bool RestoreLeftoverSession() => RestoreLeftoverSession(out _);

    /// <param name="fullySucceeded">False when any restore step failed.</param>
    /// <returns>True when a session snapshot or a pending FPS-cap undo was found (see <see cref="RestoreLeftover"/>).</returns>
    public bool RestoreLeftoverSession(out bool fullySucceeded) =>
        RestoreLeftover(out fullySucceeded) != LeftoverRestoreResult.Nothing;

    /// <summary>
    /// As <see cref="RestoreLeftoverSession(out bool)"/>, but tells a real leftover session
    /// (active-session.json — the previous run died mid-game) apart from a lone
    /// pending-fps-restore.json, which a clean exit / logoff can leave behind when the queued
    /// NVIDIA FPS-cap undo did not finish in its time budget. Only the former is a crash restore.
    /// </summary>
    public LeftoverRestoreResult RestoreLeftover(out bool fullySucceeded)
    {
        fullySucceeded = true;
        var result = LeftoverRestoreResult.Nothing;

        // FPS-cap undo whose background restore never ran (crash right after a session ended, or
        // a logoff that ran out of time).
        var pending = _snapshots.LoadPendingFps();
        if (pending != null)
        {
            result = LeftoverRestoreResult.PendingFpsOnly;
            AppLog.Info("Found a pending FPS-cap restore — applying.");
            fullySucceeded &= RestoreFpsLimit(pending);
        }

        // A corrupt file is quarantined by Current and reported as "not found".
        var snap = _snapshots.Current;
        if (snap == null) return result;

        AppLog.Info($"Found leftover active-session.json ({snap.ProfileName ?? "?"}) — restoring.");
        bool ok = RestoreFpsLimit(snap);
        if (!ok) _snapshots.SavePendingFps(snap); // snapshot is deleted below — keep the FPS undo for a retry
        ok &= _snapshots.RestoreAll(_session, snap);
        AppLog.Info(ok ? "Leftover session restored." : "Leftover session restored with errors (see above).");
        fullySucceeded &= ok;
        return LeftoverRestoreResult.SessionRestored;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        RefreshWatchList();

        // One process-list snapshot for every enabled profile (was one per name per profile).
        var enabled = _config.Current.Profiles.Where(p => p.Enabled).ToList();
        var running = ProcessWatcher.RunningKeys(enabled.SelectMany(EnumerateProcessNames));
        GameProfile? pick = null;
        foreach (var profile in enabled)
        {
            bool any = false;
            foreach (var n in EnumerateProcessNames(profile))
            {
                var key = ProcessNames.Key(n);
                if (!running.Contains(key)) continue;
                any = true;
                lock (_gate) _activeProcesses.Add(key);
            }
            if (any) pick = profile;
        }

        _watcher.Start();
        StatusChanged?.Invoke("Monitoring");

        if (pick != null)
            RunOnUi(() => ActivateProfile(pick, relaunchCompanions: true, forced: false));
    }

    // ------------------------------------------------------------------ session end paths

    /// <summary>
    /// Ends the active session and restores the pre-game snapshot. Without a session only the
    /// baseline gamma / driver color is reset — resolution and power plan are never touched.
    /// </summary>
    /// <param name="historyKind">
    /// Activity token written for this restore (<c>emergency</c> by default).
    /// Pass <c>null</c> to skip history (e.g. reset-defaults / config sync that already has its own meaning).
    /// </param>
    public void EmergencyRestore(string? historyKind = "emergency")
    {
        RunOnUi(() =>
        {
            var ended = CurrentProfile;
            bool hadSnapshot = _snapshots.HasSnapshot;
            bool ok = EndSession(RestoreMode.PreviousSnapshot, stopCompanionsSync: false, explicitColorReset: true);
            string status = historyKind switch
            {
                null => hadSnapshot
                    ? (ok ? "Idle" : "Restore completed with errors")
                    : "Idle",
                "sessionRestored" => hadSnapshot
                    ? (ok ? "Idle" : "Restore completed with errors")
                    : "Idle",
                _ => hadSnapshot
                    ? (ok ? "Emergency Restore: snapshot restored" : "Emergency Restore: restored with errors")
                    : "Emergency Restore: color reset"
            };
            StatusChanged?.Invoke(status);
            ActiveProfileChanged?.Invoke(null);
            if (historyKind != null)
                _history?.Add(historyKind, ended?.Id, ended?.Name, success: ok);
            UiSound.Warn();
            AppLog.Info("Emergency Restore completed.");
        });
    }

    /// <summary>Ends a profile forced from the tray (restores per its RestoreMode). No-op otherwise.</summary>
    public void EndForcedProfile()
    {
        RunOnUi(() =>
        {
            GameProfile? cur;
            lock (_gate)
            {
                cur = _currentProfile;
                if (cur == null || _forcedProfileId != cur.Id) return;
            }
            if (ProfileStillActive(cur))
            {
                // Its game is running now — it is a real session; leave it to the process watcher.
                lock (_gate) _forcedProfileId = null;
                return;
            }
            AppLog.Info($"Forced profile ended: {cur.Name}");
            bool ok = EndSession(cur.RestoreMode, stopCompanionsSync: false);
            NotifySessionRestored(cur, ok);
        });
    }

    /// <summary>
    /// App exit / Windows logoff: synchronous full restore of an active session (always to the
    /// pre-game snapshot), waiting for queued driver work. Safe to call more than once.
    /// </summary>
    /// <param name="stopCompanions">
    /// False at logoff — Windows closes them anyway and time is short: the restore then runs
    /// synchronously without waiting on the companion queue; the driver queue (the queued
    /// FPS-cap undo) is still drained with what is left of the 3 s logoff budget, at most 2 s.
    /// An undo that does not finish in time stays persisted in pending-fps-restore.json and is
    /// retried on the next start.
    /// </param>
    public void ShutdownRestore(bool stopCompanions)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => ShutdownRestore(stopCompanions));
            return;
        }
        if (_shutdownDone) return;
        _shutdownDone = true;
        var started = DateTime.UtcNow;
        _shutdownDeadlineUtc = started + (stopCompanions ? ExitBudget : LogoffBudget);

        try { _watcher.Stop(); }
        catch (Exception ex) { AppLog.Warn(ex, "Watcher stop on shutdown"); }

        EndSession(RestoreMode.PreviousSnapshot, stopCompanionsSync: stopCompanions);
        bool drained = _driverQueue.Drain(RemainingBudget(stopCompanions ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(2)));
        AppLog.Info($"Shutdown restore completed in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms"
                    + (stopCompanions ? "." : " (logoff: companion queue skipped).")
                    + (drained ? "" : " Driver queue still busy — a pending FPS-cap undo is retried on the next start."));
    }

    /// <summary>Wait allowance for one exit step: its own cap, but never past the shutdown deadline.</summary>
    private TimeSpan RemainingBudget(TimeSpan cap)
    {
        if (_shutdownDeadlineUtc is not { } deadline) return cap;
        var left = deadline - DateTime.UtcNow;
        if (left <= TimeSpan.Zero) return TimeSpan.Zero;
        return left < cap ? left : cap;
    }

    /// <summary>
    /// Tears down the active session on the UI thread and restores according to <paramref name="mode"/>.
    /// </summary>
    /// <returns>True when the restore fully succeeded (or there was nothing to restore).</returns>
    private bool EndSession(RestoreMode mode, bool stopCompanionsSync, bool explicitColorReset = false)
    {
        CancelPendingActivation();
        StopDeferred();
        StopColorLock();
        StopFocusPoll();
        ClearActivePreset();
        _engine.ClearAbCompare();

        GameProfile? cur;
        lock (_gate)
        {
            cur = _currentProfile;
            _currentProfile = null;
            _currentCompanionsKey = null;
            _forcedProfileId = null;
        }

        if (cur != null)
        {
            if (stopCompanionsSync)
            {
                Interlocked.Increment(ref _companionGeneration);
                _companionQueue.Drain(RemainingBudget(TimeSpan.FromSeconds(6)));
                foreach (var c in cur.Companions)
                    _companions.Stop(c, RemainingBudget(TimeSpan.FromSeconds(6)));
            }
            else
            {
                StopCompanionsFor(cur);
            }
        }

        bool ok = true;
        var snap = _snapshots.Current;
        if (snap == null)
        {
            // No session: only color can be ours. Emergency always resets it; exit only if Visor changed it.
            ok = _engine.RestoreBaselineColor(onlyIfChanged: !explicitColorReset);
        }
        else
        {
            switch (mode)
            {
                case RestoreMode.DoNothing:
                    // Leave the tuned state (display, extras, FPS cap) as the user asked.
                    _snapshots.Clear();
                    AppLog.Info("RestoreMode=DoNothing — left tuned state.");
                    break;

                case RestoreMode.GlobalDefaults:
                    ok &= _session.Restore(snap);
                    ok &= RestoreFpsLimitQueued(snap, wait: stopCompanionsSync, sessionEnding: true) ?? true;
                    _engine.RestoreDefaults(_config.Current.Defaults);
                    // Display state is owned by the global defaults now — a kept snapshot only retries extras / FPS.
                    foreach (var d in snap.Displays) { d.ModeChanged = false; d.HasGammaRamp = false; }
                    snap.PowerPlanChanged = false;
                    snap.HasDriverColor = false;
                    ok = _snapshots.Finish(snap, ok);
                    AppLog.Info("RestoreMode=GlobalDefaults.");
                    break;

                default:
                    ok &= RestoreFpsLimitQueued(snap, wait: stopCompanionsSync, sessionEnding: true) ?? true;
                    ok &= _snapshots.RestoreAll(_session, snap);
                    AppLog.Info("RestoreMode=PreviousSnapshot.");
                    break;
            }
        }

        _engine.ColorTargetDevice = null;
        _sessionMode = null;
        return ok;
    }

    // ------------------------------------------------------------------ presets / hotkeys

    public void ApplyPreset(QuickPreset preset)
    {
        RunOnUi(() =>
        {
            lock (_gate) _activePresetId = preset.Id;

            _engine.ClearAbCompare();

            if (preset.ApplyResolution && !string.IsNullOrWhiteSpace(preset.Resolution))
                SetSessionResolution(preset.Resolution!, 0, CurrentTargetDevice());

            // Color only when this preset opts in (res-only presets must not wipe live gamma).
            if (preset.ApplyColor)
            {
                ApplyPresetColor(preset, "Preset color apply");
                AppLog.Info($"Preset applied: {preset.Name} (color, backend={preset.Color?.Backend}, lock={preset.Color?.LockColor})");
            }
            else
            {
                AppLog.Info($"Preset applied: {preset.Name} (resolution only — color left alone)");
            }

            StatusChanged?.Invoke($"Preset: {preset.Name}");
            if (!_suppressPresetHistory)
            {
                var cur = CurrentProfile;
                _history?.Add("presetApplied", cur?.Id, cur?.Name, detail: preset.Name);
            }
        });
    }

    public void ClearActivePreset()
    {
        lock (_gate) _activePresetId = null;
    }

    /// <summary>
    /// Manually activate a profile from the tray "Profiles" menu, whether or not its game runs.
    /// The forced session ends on Emergency Restore, <see cref="EndForcedProfile"/>, when its
    /// game starts and exits, or when another game takes over.
    /// </summary>
    public void ForceApplyProfile(GameProfile profile)
    {
        RunOnUi(() =>
        {
            var fresh = _config.Current.Profiles.FirstOrDefault(p => p.Id == profile.Id) ?? profile;
            ActivateProfile(fresh, relaunchCompanions: true, forced: !ProfileStillActive(fresh));
        });
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
        RunOnUi(UiSound.PresetApply);
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
                    UiSound.PresetApply();
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
                    CyclePreset(+1);
                    return;
                case "previousPreset":
                    CyclePreset(-1);
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

    // ------------------------------------------------------------------ config sync

    private void OnConfigChanged()
    {
        SyncWithConfig();
        AppLog.Info("Profile monitor noted config change.");
        StatusChanged?.Invoke("Config reloaded");
    }

    /// <summary>
    /// Call after in-app autosave (raiseChanged: false) so watch-list and active
    /// profile pointer stay in sync without re-hitting resolution/power mid-game.
    /// </summary>
    public void NotifyConfigSaved() => SyncWithConfig();

    /// <summary>
    /// Watch list + active profile pointer follow the config. Only the active preset's color is
    /// refreshed — no ChangeDisplaySettings / power plan change mid-game.
    /// </summary>
    private void SyncWithConfig()
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
                EmergencyRestore(historyKind: "sessionRestored");
                return;
            }

            lock (_gate) _currentProfile = fresh;

            // The Alt+Tab focus poll compares against the profile's exe keys — follow a renamed
            // process / edited aliases / toggled SoftRestoreOnAltTab instead of polling stale keys.
            if (!fresh.SoftRestoreOnAltTab)
                StopFocusPoll();
            else if (_focusPollTimer?.IsEnabled == true)
                _focusPollKeys = KeysOf(fresh);
            else
                UpdateSoftRestoreMonitoring(fresh);

            if (presetId == null) return; // No active preset — color is presets-only.
            var preset = fresh.Presets?.FirstOrDefault(p => p.Id == presetId);
            if (preset == null)
            {
                ClearActivePreset();
                StopColorLock();
                return;
            }
            if (preset.ApplyColor)
                ApplyPresetColor(preset, "Config change preset re-apply");
        });
    }

    private void RefreshWatchList()
    {
        var names = new List<string>();
        foreach (var p in _config.Current.Profiles.Where(x => x.Enabled))
            names.AddRange(EnumerateProcessNames(p));
        _watcher.SetWatchList(names);

        // Keys of profiles that were disabled / removed / renamed are no longer watched — drop them
        // so they cannot keep a session "running" or pick a stale next profile.
        var keys = new HashSet<string>(names.Select(ProcessNames.Key), StringComparer.OrdinalIgnoreCase);
        lock (_gate) _activeProcesses.RemoveWhere(k => !keys.Contains(k));
    }

    // ------------------------------------------------------------------ process events

    private void OnProcessStarted(string processName)
    {
        var key = ProcessNames.Key(processName);
        var profile = FindProfile(key);
        if (profile == null) return;
        lock (_gate) _activeProcesses.Add(key);
        _ = ActivateProfileAsync(profile);
    }

    private async Task ActivateProfileAsync(GameProfile profile)
    {
        if (_shutdownDone) return;
        var cts = new CancellationTokenSource();
        CancellationTokenSource? old;
        lock (_gate)
        {
            old = _activationCts;
            _activationCts = cts;
            _pendingActivationProfile = profile;
        }
        try { old?.Cancel(); } catch (ObjectDisposedException) { }
        old?.Dispose();

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
                var keys = KeysOf(fresh);
                while (DateTime.UtcNow < deadline)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (!ProfileStillActive(fresh))
                    {
                        AppLog.Info($"Focus wait cancelled — process gone ({fresh.Name})");
                        return;
                    }
                    if (ForegroundProcessHelper.IsForegroundOwnedBy(keys))
                        break;
                    await Task.Delay(250, cts.Token).ConfigureAwait(false);
                }
            }

            if (!ProfileStillActive(fresh))
                return;

            await _dispatcher.InvokeAsync(() =>
            {
                // The game may have exited while this was queued behind other UI work.
                if (cts.IsCancellationRequested || _shutdownDone || !ProfileStillActive(fresh)) return;
                ActivateProfile(fresh, relaunchCompanions: true, forced: false);
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
        var key = ProcessNames.Key(processName);
        GameProfile? cur;
        bool stillRunning;
        lock (_gate)
        {
            _activeProcesses.Remove(key);
            // A start that is still waiting (apply delay / focus / dispatcher) for this game: drop it.
            if (_pendingActivationProfile is { } pending && MatchesProcess(pending, key) && !AnyActiveNameFor(pending))
            {
                try { _activationCts?.Cancel(); } catch (ObjectDisposedException) { }
                _pendingActivationProfile = null;
            }
            cur = _currentProfile;
            if (cur == null || !MatchesProcess(cur, key)) return;
            stillRunning = AnyActiveNameFor(cur);
        }
        if (stillRunning) return;
        GameProfile ended = cur;

        RunOnUi(() =>
        {
            // Re-check on the UI thread: another event may have switched / ended the session.
            GameProfile? next;
            lock (_gate)
            {
                if (_currentProfile?.Id != ended.Id || AnyActiveNameFor(ended)) return;
                next = _config.Current.Profiles.FirstOrDefault(p =>
                    p.Enabled && p.Id != ended.Id && AnyActiveNameFor(p));
            }

            CancelPendingActivation();
            if (next != null)
            {
                ActivateProfile(next, relaunchCompanions: true, forced: false);
                return;
            }

            bool ok = EndSession(ended.RestoreMode, stopCompanionsSync: false);
            NotifySessionRestored(ended, ok);
        });
    }

    // ------------------------------------------------------------------ activation

    private void ActivateProfile(GameProfile profile, bool relaunchCompanions, bool forced)
    {
        if (_shutdownDone) return; // never re-apply after the exit / logoff restore
        GameProfile? previous;
        bool same;
        lock (_gate)
        {
            previous = _currentProfile;
            same = previous?.Id == profile.Id;
            _currentProfile = profile;
            // A real game start turns a forced activation into a normal session.
            _forcedProfileId = forced ? profile.Id : null;
        }

        var target = TargetDeviceFor(profile);

        if (same)
        {
            // Soft re-apply: config may have changed, or a second start event arrived.
            // The session snapshot is kept (it holds the pre-game PC state).
            AppLog.Info($"Re-applying active profile: {profile.Name}");
            _engine.ColorTargetDevice = target;
            _ = ApplyProfileCore(profile);
            ScheduleDeferredIfNeeded(profile);

            if (relaunchCompanions && _currentCompanionsKey != profile.Id)
                LaunchCompanionsFor(profile);
            UpdateSoftRestoreMonitoring(profile);
            StatusChanged?.Invoke($"Active: {profile.Name}");
            ActiveProfileChanged?.Invoke(profile);
            RunOnUi(UiSound.Armed);
            return;
        }

        if (previous != null)
            LeaveProfileForSwitch(previous, profile, target);

        AppLog.Info($"Activating profile: {profile.Name}" + (forced ? " (forced from tray)" : ""));
        LastApplyToastDetail = null;
        bool applyOk = true;
        try
        {
            // One snapshot per session — a switch keeps the original pre-game state.
            // Target first, so the driver color is captured from the display we are about to change.
            _engine.ColorTargetDevice = target;
            var snap = _snapshots.BeginIfNone(profile, out bool created);
            var display = _snapshots.EnsureDisplay(snap, target);
            if (created)
                LastApplyToastDetail = $"Snapshot saved · {display.Resolution}";
            applyOk = ApplyProfileCore(profile);
        }
        catch (Exception ex)
        {
            applyOk = false;
            AppLog.Error("Apply profile failed: " + ex);
        }

        ScheduleDeferredIfNeeded(profile);
        if (relaunchCompanions)
            LaunchCompanionsFor(profile);

        // Startup preset only for new game activation (not soft reapply).
        string? startupPresetName = null;
        if (!string.IsNullOrWhiteSpace(profile.StartupPresetId))
        {
            var preset = profile.Presets?.FirstOrDefault(p => p.Id == profile.StartupPresetId)
                         ?? FindPresetById(profile.StartupPresetId!);
            if (preset != null)
            {
                _suppressPresetHistory = true;
                try
                {
                    ApplyPreset(preset);
                    LastPresetHotkeyName = preset.Name;
                    startupPresetName = preset.Name;
                }
                finally
                {
                    _suppressPresetHistory = false;
                }
            }
        }

        UpdateSoftRestoreMonitoring(profile);
        StatusChanged?.Invoke($"Active: {profile.Name}");
        ActiveProfileChanged?.Invoke(profile);
        _history?.Add("profileApplied", profile.Id, profile.Name, detail: startupPresetName, success: applyOk);
        RunOnUi(UiSound.Armed);
    }

    private void NotifySessionRestored(GameProfile? ended, bool success = true)
    {
        StatusChanged?.Invoke("Idle");
        ActiveProfileChanged?.Invoke(null);
        _history?.Add("sessionRestored", ended?.Id, ended?.Name, success: success);
    }

    /// <summary>
    /// Switching games: undo what the previous profile changed (extras, FPS cap, power, gamma,
    /// modes it set) but keep the session snapshot. The display the next profile sets anyway is
    /// not reverted first (avoids a double mode change).
    /// </summary>
    private void LeaveProfileForSwitch(GameProfile previous, GameProfile next, string? nextTarget)
    {
        _sessionMode = null;
        StopCompanionsFor(previous);
        StopDeferred();
        StopColorLock();
        StopFocusPoll();

        var snap = _snapshots.Current;
        if (snap == null) return;
        try
        {
            _session.Restore(snap);
            RestoreFpsLimitQueued(snap, wait: false, sessionEnding: false);
            bool nextSetsMode = next.ApplyResolution && !string.IsNullOrWhiteSpace(next.Resolution);
            // Flags are cleared only for steps that succeeded; failures stay flagged for the final restore.
            _snapshots.RestoreDisplay(snap, nextSetsMode ? DisplayEngine.ResolveDeviceName(nextTarget) : null);
            _snapshots.Persist(snap);
        }
        catch (Exception ex)
        {
            AppLog.Error("Restore before profile switch: " + ex.Message);
        }
    }

    /// <summary>Power plan, resolution, FPS cap and session extras of <paramref name="profile"/>, tracked in the snapshot.</summary>
    /// <returns>False when any apply step threw.</returns>
    private bool ApplyProfileCore(GameProfile profile)
    {
        string? presetId;
        lock (_gate) presetId = _activePresetId;
        var keep = presetId == null
            ? null
            : profile.Presets?.FirstOrDefault(p => p.Id == presetId);
        if (keep == null)
            ClearActivePreset();

        var snap = _snapshots.BeginIfNone(profile, out _);
        var target = TargetDeviceFor(profile);
        bool ok = true;

        try
        {
            if (profile.ApplyPowerPlan && !string.IsNullOrWhiteSpace(profile.PowerPlan))
            {
                // Tracked whenever the API accepted the change — a failed read-back must not
                // leave the plan un-restored at session end.
                if (_engine.TrySetPowerPlan(profile.PowerPlan!) != PowerPlanResult.Failed)
                    snap.PowerPlanChanged = true;
            }
        }
        catch (Exception ex)
        {
            ok = false;
            AppLog.Error("Power plan apply: " + ex.Message);
        }

        try
        {
            if (profile.ApplyResolution && !string.IsNullOrWhiteSpace(profile.Resolution))
                SetSessionResolution(profile.Resolution!, profile.RefreshRate, target);
        }
        catch (Exception ex)
        {
            ok = false;
            AppLog.Error("Display apply: " + ex.Message);
        }

        // FPS cap: only touch NVIDIA DRS when this profile asks for a cap, or to undo our own cap.
        if (profile.FpsLimit > 0)
            QueueFpsLimit(profile, snap);
        else if (snap.FpsLimitChanged)
            RestoreFpsLimitQueued(snap, wait: false, sessionEnding: false);

        try
        {
            _session.Apply(profile.Session ?? new SessionExtras(), profile.Resolution, target, snap);
            if (!string.IsNullOrWhiteSpace(_session.LastWarning))
            {
                LastApplyToastDetail = string.IsNullOrWhiteSpace(LastApplyToastDetail)
                    ? _session.LastWarning
                    : LastApplyToastDetail + " · " + _session.LastWarning;
            }
        }
        catch (Exception ex)
        {
            ok = false;
            AppLog.Error("Session apply: " + ex.Message);
        }

        _snapshots.Persist(snap);

        if (keep != null)
        {
            lock (_gate) _activePresetId = keep.Id;
            if (keep.ApplyColor)
            {
                ApplyPresetColor(keep, "Preset re-apply after profile");
                AppLog.Info($"Kept active preset after profile apply: {keep.Name}");
            }
            return ok;
        }

        // Color is presets-only — do not lock/re-apply profile color.
        StopColorLock();
        return ok;
    }

    /// <summary>Mode change on <paramref name="device"/>, recorded in the session snapshot first.</summary>
    private void SetSessionResolution(string resolution, int refreshRate, string? device)
    {
        bool inSession;
        lock (_gate) inSession = _currentProfile != null;
        var snap = inSession ? _snapshots.Current : null;
        if (snap != null)
        {
            var entry = _snapshots.EnsureDisplay(snap, device);
            entry.ModeChanged = true;
            _snapshots.Persist(snap);
        }
        bool ok = _engine.SetResolution(resolution, refreshRate, device);
        if (inSession && ok)
            _sessionMode = (resolution, refreshRate, DisplayEngine.ResolveDeviceName(device));
    }

    // ------------------------------------------------------------------ session mode guard

    /// <summary>
    /// Game mode changes are dynamic (not in the registry), so Windows falls back to the desktop mode
    /// on ChangeDisplaySettings(NULL), sleep / wake, hot-plug or an HDR toggle. While a session owns
    /// a mode, WM_DISPLAYCHANGE re-applies it (debounced, rate-limited, not while Alt+Tabbed out).
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_shutdownDone) return;
        RunOnUi(() =>
        {
            if (_sessionMode == null) return;
            _modeGuardTimer ??= CreateModeGuardTimer();
            _modeGuardTimer.Stop();
            _modeGuardTimer.Start();
        });
    }

    private DispatcherTimer CreateModeGuardTimer()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(1500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            CheckSessionMode();
        };
        return t;
    }

    private void CheckSessionMode()
    {
        if (_shutdownDone || _sessionMode is not { } want) return;
        GameProfile? cur;
        lock (_gate) cur = _currentProfile;
        if (cur == null) return;
        if (cur.SoftRestoreOnAltTab && !_focusPollHasFocus) return; // desktop mode is intended now

        var (res, hz, _) = DisplayEngine.GetCurrentMode(want.Device);
        bool same = DisplayEngine.TryParseRes(res, out int cw, out int ch)
                    && DisplayEngine.TryParseRes(want.Resolution, out int ww, out int wh)
                    && cw == ww && ch == wh
                    && (want.RefreshRate <= 0 || hz == want.RefreshRate);
        if (same) return;

        var now = DateTime.UtcNow;
        _modeGuardHistory.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
        if (_modeGuardHistory.Count >= 3)
        {
            AppLog.Warn($"Display mode keeps reverting to {res} — not re-applying {want.Resolution} again for now.");
            return;
        }
        _modeGuardHistory.Add(now);
        AppLog.Info($"Display mode reverted to {res} during {cur.Name} — re-applying {want.Resolution}.");
        _engine.SetResolution(want.Resolution, want.RefreshRate, want.Device);
    }

    private void ApplyPresetColor(QuickPreset preset, string context)
    {
        try
        {
            var color = (preset.Color ?? ColorSettings.Neutral).Clone();
            color.Clamp();
            var target = CurrentTargetDevice();
            var snap = _snapshots.Current;
            bool inSession;
            lock (_gate) inSession = _currentProfile != null;
            if (inSession && snap != null)
                _snapshots.EnsureDisplay(snap, target); // pre-game ramp of the display we tint
            _engine.ColorTargetDevice = target;
            _engine.ApplyColor(color);
            if (color.LockColor)
                StartColorLock(color);
            else
                StopColorLock();
        }
        catch (Exception ex)
        {
            AppLog.Error($"{context}: {ex.Message}");
        }
    }

    private void ScheduleDeferredIfNeeded(GameProfile profile)
    {
        var delay = profile.Session?.DeferredApplySeconds ?? 0;
        if (delay > 0)
            ScheduleDeferred(profile, delay);
    }

    /// <summary>One-shot re-apply after the game has settled. A single timer instance is reused.</summary>
    private void ScheduleDeferred(GameProfile profile, int seconds)
    {
        StopDeferred();
        _deferredProfileId = profile.Id;
        _deferredTimer ??= CreateDeferredTimer();
        _deferredTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 120));
        _deferredTimer.Start();
    }

    private DispatcherTimer CreateDeferredTimer()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        t.Tick += (_, _) =>
        {
            var id = _deferredProfileId;
            StopDeferred();
            GameProfile? cur;
            lock (_gate) cur = _currentProfile;
            if (id == null || cur == null || cur.Id != id) return;
            var fresh = _config.Current.Profiles.FirstOrDefault(p => p.Id == id) ?? cur;
            AppLog.Info($"Deferred re-apply for {fresh.Name}");
            ApplyProfileCore(fresh);
            StatusChanged?.Invoke($"Re-applied: {fresh.Name}");
        };
        return t;
    }

    private void StopDeferred()
    {
        _deferredProfileId = null;
        if (_deferredTimer is { IsEnabled: true })
            _deferredTimer.Stop();
    }

    // ------------------------------------------------------------------ color lock

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

    // ------------------------------------------------------------------ FPS cap (NVIDIA DRS, background)

    private static string FpsTarget(GameProfile profile) =>
        string.IsNullOrWhiteSpace(profile.ExePath) ? profile.ProcessName : profile.ExePath!;

    private void QueueFpsLimit(GameProfile profile, ActiveSessionSnapshot snap)
    {
        var target = FpsTarget(profile);
        int fps = profile.FpsLimit;
        if (string.IsNullOrWhiteSpace(target)) return;
        _driverQueue.Enqueue(() =>
        {
            bool ours = snap.FpsLimitChanged && string.Equals(snap.FpsLimitTarget, target, StringComparison.OrdinalIgnoreCase);
            var original = FpsLimitService.TryApply(target, fps);
            if (original == null || ours) return; // first write of the session records the original
            snap.FpsLimitChanged = true;
            snap.FpsLimitTarget = target;
            snap.FpsLimitHadSetting = original.HadSetting;
            snap.FpsLimitWasPredefined = original.WasPredefined;
            snap.FpsLimitOriginalValue = original.Value;
            snap.FpsLimitCreatedProfile = original.CreatedProfile;
            if (_snapshots.IsCurrent(snap))
                _snapshots.PersistIfCurrent(snap);
            else
                _snapshots.SavePendingFps(snap); // session already ended — the queued restore follows
        });
    }

    /// <summary>
    /// Undo Visor's FPS cap on the driver queue (keeps ordering with a pending apply).
    /// When the session is ending the undo data is first written to pending-fps-restore.json,
    /// because the session snapshot is deleted before the queued restore runs.
    /// </summary>
    /// <returns>The restore result when <paramref name="wait"/> is set; null when it was only queued.</returns>
    private bool? RestoreFpsLimitQueued(ActiveSessionSnapshot snap, bool wait, bool sessionEnding)
    {
        if (sessionEnding)
            _snapshots.SavePendingFps(snap);
        bool result = true;
        _driverQueue.Enqueue(() => result = RestoreFpsLimit(snap));
        if (!wait) return null;
        _driverQueue.Drain(RemainingBudget(TimeSpan.FromSeconds(4)));
        return result;
    }

    private bool RestoreFpsLimit(ActiveSessionSnapshot snap)
    {
        if (!snap.FpsLimitChanged || string.IsNullOrWhiteSpace(snap.FpsLimitTarget)) return true;
        bool ok = FpsLimitService.Restore(snap.FpsLimitTarget!, new FpsLimitService.FpsOriginal(
            snap.FpsLimitHadSetting, snap.FpsLimitWasPredefined, snap.FpsLimitOriginalValue, snap.FpsLimitCreatedProfile));
        if (ok)
        {
            snap.FpsLimitChanged = false;
            _snapshots.PersistIfCurrent(snap);
            // The undo file written at session end is cleared whether or not the snapshot is
            // still live (RestoreAll may still be running on the UI thread) — otherwise a clean
            // exit leaves pending-fps-restore.json behind and the next start reports a restore.
            _snapshots.ClearPendingFps(snap);
        }
        else if (!_snapshots.IsCurrent(snap))
        {
            // Keep the undo data; retried on the next start.
            _snapshots.SavePendingFps(snap);
        }
        return ok;
    }

    // ------------------------------------------------------------------ Alt+Tab soft restore

    /// <summary>
    /// Alt+Tab soft-restore: while <see cref="GameProfile.SoftRestoreOnAltTab"/> is set on the
    /// active profile, poll the foreground window and softly restore the desktop when focus
    /// leaves the game, then re-apply the game's display/power/preset (not companions) when it returns.
    /// Independent of ApplyOnFocus, which only gates the *initial* apply.
    /// </summary>
    private void UpdateSoftRestoreMonitoring(GameProfile? profile)
    {
        if (profile == null || !profile.SoftRestoreOnAltTab)
        {
            StopFocusPoll();
            return;
        }

        // Assume the game has focus right after activation (ApplyOnFocus already waited for it
        // when configured); the next tick will correct this if it's wrong.
        _focusPollHasFocus = true;
        _focusPollKeys = KeysOf(profile);

        if (_focusPollTimer == null)
        {
            _focusPollTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(750)
            };
            _focusPollTimer.Tick += (_, _) => OnFocusPollTick();
        }
        if (!_focusPollTimer.IsEnabled)
            _focusPollTimer.Start();
    }

    private void StopFocusPoll()
    {
        _focusPollTimer?.Stop();
    }

    private void OnFocusPollTick()
    {
        GameProfile? profile;
        lock (_gate) profile = _currentProfile;
        if (profile == null || !profile.SoftRestoreOnAltTab)
        {
            StopFocusPoll();
            return;
        }

        bool focused;
        try { focused = ForegroundProcessHelper.IsForegroundOwnedBy(_focusPollKeys); }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Foreground check");
            return;
        }

        if (focused == _focusPollHasFocus) return;
        _focusPollHasFocus = focused;

        if (focused)
            SoftReapplyGame(profile);
        else
            SoftRestoreDesktop(profile);
    }

    /// <summary>Focus left the game: restore pre-game power / modes / color from the session
    /// snapshot without touching companions, extras or deleting the snapshot.</summary>
    private void SoftRestoreDesktop(GameProfile profile)
    {
        try
        {
            StopColorLock();
            var snap = _snapshots.Current;
            bool restored = snap != null && _snapshots.RestoreDisplay(snap, clearFlags: false);
            if (snap == null) _engine.RestoreBaselineColor();
            AppLog.Info($"SoftRestore: desktop restored on focus loss ({profile.Name}), fromSnapshot={restored}");
            StatusChanged?.Invoke($"Soft restore: {profile.Name} (background)");
        }
        catch (Exception ex)
        {
            AppLog.Error("SoftRestore desktop restore failed: " + ex.Message);
        }
    }

    /// <summary>Focus returned to the game: re-apply power plan, resolution (same target display)
    /// and the active preset color — symmetric with <see cref="SoftRestoreDesktop"/>.</summary>
    private void SoftReapplyGame(GameProfile profile)
    {
        try
        {
            var target = TargetDeviceFor(profile);
            if (profile.ApplyPowerPlan && !string.IsNullOrWhiteSpace(profile.PowerPlan))
                _engine.SetPowerPlan(profile.PowerPlan!);
            if (profile.ApplyResolution && !string.IsNullOrWhiteSpace(profile.Resolution))
                _engine.SetResolution(profile.Resolution!, profile.RefreshRate, target);

            string? presetId;
            lock (_gate) presetId = _activePresetId;
            var preset = presetId == null ? null : profile.Presets?.FirstOrDefault(p => p.Id == presetId);
            if (preset != null && preset.ApplyColor)
            {
                if (preset.ApplyResolution && !string.IsNullOrWhiteSpace(preset.Resolution))
                    _engine.SetResolution(preset.Resolution!, 0, target);
                ApplyPresetColor(preset, "SoftRestore preset color");
            }

            AppLog.Info($"SoftRestore: game state re-applied on focus return ({profile.Name})");
            StatusChanged?.Invoke($"Active: {profile.Name}");
        }
        catch (Exception ex)
        {
            AppLog.Error("SoftRestore game reapply failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ companions (background)

    private void LaunchCompanionsFor(GameProfile profile)
    {
        var list = (profile.Companions ?? new List<CompanionApp>()).Select(c => c.Clone()).ToList();
        _currentCompanionsKey = profile.Id;
        if (list.Count == 0) return;
        int generation = Volatile.Read(ref _companionGeneration);
        _companionQueue.Enqueue(() =>
        {
            foreach (var c in list)
            {
                if (Volatile.Read(ref _companionGeneration) != generation) return; // session already over
                _companions.Launch(c);
                // The session ended while this launch was running (e.g. exit drain timed out):
                // the stop for it may already have run, so stop what we just started.
                if (Volatile.Read(ref _companionGeneration) != generation)
                    _companions.Stop(c);
            }
        });
    }

    private void StopCompanionsFor(GameProfile profile)
    {
        Interlocked.Increment(ref _companionGeneration);
        var list = (profile.Companions ?? new List<CompanionApp>()).Select(c => c.Clone()).ToList();
        if (_currentCompanionsKey == profile.Id)
            _currentCompanionsKey = null;
        if (list.Count == 0) return;
        _companionQueue.Enqueue(() =>
        {
            foreach (var c in list)
                _companions.Stop(c);
        });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The display a profile changes: its own device, else the preferred display, else primary.</summary>
    private string? TargetDeviceFor(GameProfile profile)
    {
        var device = DisplayNative.NullIfEmpty(profile.DisplayDevice)
                     ?? DisplayNative.NullIfEmpty(_config.Current.Ui?.PreferredDisplayDevice);
        return DisplayEngine.ResolveDeviceName(device);
    }

    private string? CurrentTargetDevice()
    {
        GameProfile? cur;
        lock (_gate) cur = _currentProfile;
        return cur != null ? TargetDeviceFor(cur) : DisplayEngine.ResolveDeviceName(_engine.ColorTargetDevice);
    }

    private GameProfile? FindProfile(string key) =>
        _config.Current.Profiles.FirstOrDefault(p => p.Enabled && MatchesProcess(p, key));

    private bool ProfileStillActive(GameProfile profile)
    {
        lock (_gate)
        {
            if (AnyActiveNameFor(profile))
                return true;
        }
        return AnyProcessRunning(profile);
    }

    /// <summary>Caller holds <c>_gate</c>.</summary>
    private bool AnyActiveNameFor(GameProfile profile)
    {
        foreach (var n in _activeProcesses)
        {
            if (MatchesProcess(profile, n))
                return true;
        }
        return false;
    }

    /// <summary>One process-list snapshot for all of the profile's names.</summary>
    private static bool AnyProcessRunning(GameProfile profile) =>
        ProcessWatcher.RunningKeys(EnumerateProcessNames(profile)).Count > 0;

    /// <summary>Exe keys (ProcessNames.Key form) of the profile's process name and aliases.</summary>
    private static HashSet<string> KeysOf(GameProfile profile)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in EnumerateProcessNames(profile))
        {
            var key = ProcessNames.Key(n);
            if (key.Length > 0) keys.Add(key);
        }
        return keys;
    }

    private static bool MatchesProcess(GameProfile profile, string processName)
    {
        var key = ProcessNames.Key(processName);
        if (key.Length == 0) return false;
        foreach (var n in EnumerateProcessNames(profile))
        {
            if (ProcessNames.Key(n) == key)
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

    private void CancelPendingActivation()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _activationCts;
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
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
        lock (_gate)
        {
            _activationCts?.Dispose();
            _activationCts = null;
        }
        StopDeferred();
        StopColorLock();
        StopFocusPoll();
        _watcher.ProcessStarted -= OnProcessStarted;
        _watcher.ProcessStopped -= OnProcessStopped;
        _config.ConfigChanged -= OnConfigChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _modeGuardTimer?.Stop();
        _watcher.Dispose();
        // After ShutdownRestore the queues were already drained within the exit budget (or
        // deliberately skipped at logoff) — never add a second wait on top of it.
        if (!_shutdownDone)
        {
            _companionQueue.Drain(TimeSpan.FromSeconds(2));
            _driverQueue.Drain(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Runs actions one after another on the thread pool (order preserved, errors logged).</summary>
    private sealed class SerialQueue
    {
        private readonly string _name;
        private readonly object _lock = new();
        private Task _tail = Task.CompletedTask;

        public SerialQueue(string name) => _name = name;

        public void Enqueue(Action work)
        {
            lock (_lock)
            {
                _tail = _tail.ContinueWith(_ =>
                {
                    try { work(); }
                    catch (Exception ex) { AppLog.Error(ex, _name + " background work"); }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        /// <summary>Waits for queued work (bounded). Used on exit / logoff.</summary>
        /// <returns>True when every queued action finished within <paramref name="timeout"/>.</returns>
        public bool Drain(TimeSpan timeout)
        {
            Task tail;
            lock (_lock) tail = _tail;
            try
            {
                if (tail.Wait(timeout)) return true;
                AppLog.Warn($"{_name}: background work still running after {timeout.TotalSeconds:0.#}s.");
                return false;
            }
            catch (AggregateException ex)
            {
                AppLog.Warn(ex, _name + " drain");
                return true; // finished (with an error that is already logged)
            }
        }
    }
}

/// <summary>
/// Tiny Win32 helpers for focus-gated profile activation. Resolves the foreground PID's exe with
/// one kernel name query (<see cref="ProcessSnapshot.TryGetExeKey"/>: no handle to the game is
/// ever opened, no Process objects). The last PID → exe mapping is cached briefly because the
/// focus poll runs every 750 ms and the foreground process rarely changes between ticks.
/// </summary>
internal static class ForegroundProcessHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private static readonly object CacheGate = new();
    private static uint _cachedPid;
    private static string? _cachedKey;
    private static DateTime _cachedAtUtc;

    /// <param name="keys">Exe keys (ProcessNames.Key form) in an OrdinalIgnoreCase set.</param>
    public static bool IsForegroundOwnedBy(HashSet<string> keys)
    {
        if (keys.Count == 0) return false;

        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;

        var now = DateTime.UtcNow;
        lock (CacheGate)
        {
            if (pid == _cachedPid && _cachedKey != null && now - _cachedAtUtc < CacheTtl)
                return keys.Contains(_cachedKey);
        }

        var key = ProcessSnapshot.TryGetExeKey(pid);
        if (key == null) return false;
        lock (CacheGate)
        {
            _cachedPid = pid;
            _cachedKey = key;
            _cachedAtUtc = now;
        }
        return keys.Contains(key);
    }

    public static bool IsForegroundOwnedBy(IEnumerable<string> processNames)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in processNames)
        {
            var key = ProcessNames.Key(raw);
            if (key.Length > 0)
                targets.Add(key);
        }
        return IsForegroundOwnedBy(targets);
    }
}
