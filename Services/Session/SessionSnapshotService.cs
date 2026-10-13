using System.IO;
using System.Text.Json;
using Visor.Models;

namespace Visor.Services;

/// <summary>
/// Captures PC display/power/color state before a game session changes it, persists it to
/// active-session.json, and restores it on exit / emergency / crash restart.
/// One snapshot per session: switching profiles keeps the original pre-game state.
/// </summary>
public sealed class SessionSnapshotService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly DisplayEngine _engine;
    private readonly string _path;
    private readonly object _io = new();
    private ActiveSessionSnapshot? _memory;
    private const int MaxRestoreAttempts = 3;

    public SessionSnapshotService(DisplayEngine engine, string configDirectory)
    {
        _engine = engine;
        _path = Path.Combine(configDirectory, "active-session.json");
    }

    public string SnapshotPath => _path;

    public bool HasSnapshot
    {
        get { lock (_io) return _memory != null || File.Exists(_path); }
    }

    public ActiveSessionSnapshot? Current
    {
        get { lock (_io) return _memory ?? LoadFromDiskLocked(); }
    }

    /// <summary>
    /// Starts the session snapshot (power plan + driver color) unless one already exists —
    /// a profile switch or re-activation keeps the original pre-game state.
    /// </summary>
    public ActiveSessionSnapshot BeginIfNone(GameProfile profile, out bool created)
    {
        var existing = Current;
        if (existing != null)
        {
            created = false;
            return existing;
        }

        var snap = new ActiveSessionSnapshot
        {
            Version = ActiveSessionSnapshot.CurrentVersion,
            CapturedUtc = DateTime.UtcNow,
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            PowerPlanGuid = DisplayEngine.GetActivePowerPlanGuid()
        };

        try
        {
            var drv = _engine.CaptureDriverColorCurrent();
            if (drv != null)
            {
                snap.HasDriverColor = true;
                snap.DriverVendor = drv.Vendor;
                snap.DriverDisplayName = drv.DisplayName;
                snap.DriverVibranceLevel = drv.VibranceLevel;
                snap.DriverNormalizedVibrance = drv.NormalizedVibrance;
                snap.DriverHueAngle = drv.HueAngle;
                snap.DriverBrightness = drv.Brightness;
                snap.DriverContrast = drv.Contrast;
                snap.DriverSaturation = drv.Saturation;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Snapshot driver color: " + ex.Message);
        }

        Persist(snap);
        created = true;
        AppLog.Info($"Session snapshot started for {profile.Name} (power={snap.PowerPlanGuid ?? "?"}).");
        return snap;
    }

    /// <summary>Records <paramref name="device"/>'s current mode + gamma ramp the first time the session touches it.</summary>
    public SnapshotDisplay EnsureDisplay(ActiveSessionSnapshot snap, string? device)
    {
        var name = DisplayEngine.ResolveDeviceName(device);
        SnapshotDisplay entry;
        lock (_io)
        {
            var hit = snap.Displays.FirstOrDefault(d => string.Equals(d.Device, name, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;

            var (res, hz, _) = DisplayEngine.GetCurrentMode(name);
            entry = new SnapshotDisplay { Device = name, Resolution = res, RefreshRate = hz };
            if (_engine.TryCaptureGammaRamp(name, out var r, out var g, out var b))
            {
                entry.HasGammaRamp = true;
                entry.GammaRedB64 = DisplayNative.EncodeChannel(r);
                entry.GammaGreenB64 = DisplayNative.EncodeChannel(g);
                entry.GammaBlueB64 = DisplayNative.EncodeChannel(b);
            }
            snap.Displays.Add(entry);
            AppLog.Info($"Session snapshot: {name ?? "primary"} {res}@{hz}.");
        }
        Persist(snap);
        return entry;
    }

    /// <summary>
    /// Restores power plan, display modes, driver color and gamma recorded in the snapshot.
    /// Session extras are restored separately. The snapshot is kept.
    /// </summary>
    /// <param name="skipModeDevice">Display whose mode is about to be set again (profile switch) — not reverted.</param>
    /// <returns>False if any step failed (details are logged).</returns>
    /// <param name="clearFlags">
    /// True for a real restore (session end / profile switch): power / mode flags are cleared for the
    /// steps that succeeded. False for the Alt+Tab soft restore — the game state comes back later.
    /// </param>
    public bool RestoreDisplay(ActiveSessionSnapshot snap, string? skipModeDevice = null, bool clearFlags = true)
    {
        bool ok = true;

        if (snap.PowerPlanChanged && !string.IsNullOrWhiteSpace(snap.PowerPlanGuid))
        {
            bool done = _engine.SetPowerPlan(snap.PowerPlanGuid!);
            if (done && clearFlags) snap.PowerPlanChanged = false;
            ok &= done;
        }

        foreach (var d in snap.Displays.ToList())
        {
            if (!d.ModeChanged || string.IsNullOrWhiteSpace(d.Resolution)) continue;
            if (skipModeDevice != null && string.Equals(d.Device, skipModeDevice, StringComparison.OrdinalIgnoreCase)) continue;
            bool done = _engine.SetResolution(d.Resolution!, d.RefreshRate, d.Device);
            if (done && clearFlags) d.ModeChanged = false;
            ok &= done;
        }

        if (snap.HasDriverColor && !string.IsNullOrWhiteSpace(snap.DriverVendor))
        {
            try
            {
                ok &= _engine.RestoreDriverColorSnapshot(new DriverColorSnapshot
                {
                    Vendor = snap.DriverVendor!,
                    DisplayName = snap.DriverDisplayName,
                    VibranceLevel = snap.DriverVibranceLevel,
                    NormalizedVibrance = snap.DriverNormalizedVibrance,
                    HueAngle = snap.DriverHueAngle,
                    Brightness = snap.DriverBrightness,
                    Contrast = snap.DriverContrast,
                    Saturation = snap.DriverSaturation
                });
            }
            catch (Exception ex)
            {
                ok = false;
                AppLog.Error("Snapshot driver color restore: " + ex.Message);
            }
        }
        else
        {
            try { _engine.ClearDriverTweaksIfActive(); }
            catch (Exception ex) { ok = false; AppLog.Error("Driver tweak clear: " + ex.Message); }
        }

        // Pre-game ramp of every display the session touched (never a blind identity ramp).
        foreach (var d in snap.Displays.ToList())
        {
            if (!d.HasGammaRamp) continue;
            if (DisplayNative.TryDecodeChannel(d.GammaRedB64, out var r)
                && DisplayNative.TryDecodeChannel(d.GammaGreenB64, out var g)
                && DisplayNative.TryDecodeChannel(d.GammaBlueB64, out var b))
            {
                ok &= _engine.ApplyCapturedGammaRamp(d.Device, r, g, b);
            }
            else
            {
                ok = false;
                AppLog.Error($"Snapshot gamma ramp for {d.Device} is unreadable — left as is.");
            }
        }

        AppLog.Info(ok ? "Session snapshot display restored." : "Session snapshot display restored with errors.");
        return ok;
    }

    /// <summary>Full restore: session extras + display, then delete active-session.json.</summary>
    /// <returns>True only when every step succeeded.</returns>
    public bool RestoreAll(SessionExtrasService session, ActiveSessionSnapshot snap)
    {
        bool ok;
        try
        {
            ok = session.Restore(snap);
        }
        catch (Exception ex)
        {
            ok = false;
            AppLog.Error("Session extras restore: " + ex.Message);
        }

        try
        {
            ok &= RestoreDisplay(snap);
        }
        catch (Exception ex)
        {
            ok = false;
            AppLog.Error("Session display restore: " + ex.Message);
        }

        return Finish(snap, ok);
    }

    /// <summary>
    /// End of a restore: delete the snapshot when everything succeeded, otherwise keep it for a retry.
    /// </summary>
    public bool Finish(ActiveSessionSnapshot snap, bool ok)
    {
        if (ok)
        {
            Clear();
            return true;
        }

        // Keep the snapshot (succeeded steps are already un-flagged) so the remaining steps are
        // retried by Emergency Restore or the next start — but don't retry a broken step forever.
        snap.RestoreAttempts++;
        if (snap.RestoreAttempts >= MaxRestoreAttempts)
        {
            AppLog.Warn($"Session restore still incomplete after {snap.RestoreAttempts} attempts — giving up and discarding the snapshot.");
            Clear();
        }
        else
        {
            AppLog.Warn($"Session restore incomplete (attempt {snap.RestoreAttempts}/{MaxRestoreAttempts}) — snapshot kept for retry.");
            Persist(snap);
        }
        return false;
    }

    public void Clear()
    {
        lock (_io)
        {
            _memory = null;
            try
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
            catch (Exception ex)
            {
                AppLog.Error("Delete active-session.json: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Persist from background work: only while <paramref name="snap"/> is still the live session
    /// snapshot (a session that already ended must not resurrect active-session.json).
    /// </summary>
    public void PersistIfCurrent(ActiveSessionSnapshot snap)
    {
        lock (_io)
        {
            if (!ReferenceEquals(_memory, snap)) return;
            Persist(snap);
        }
    }

    /// <summary>Writes the snapshot atomically (temp file + replace).</summary>
    public void Persist(ActiveSessionSnapshot? snap = null)
    {
        lock (_io)
        {
            snap ??= _memory;
            if (snap == null) return;
            _memory = snap;
            try
            {
                var json = JsonSerializer.Serialize(snap, JsonOptions);
                var tmp = _path + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var w = new StreamWriter(fs))
                {
                    w.Write(json);
                    w.Flush();
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Save active-session.json: " + ex.Message);
            }
        }
    }

    /// <summary>Unreadable snapshot: moved aside (never restored, never blocks the next session).</summary>
    private void QuarantineCorruptLocked(string reason)
    {
        try
        {
            var dest = _path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(_path, dest, overwrite: true);
            AppLog.Warn($"active-session.json is unreadable ({reason}) — moved to {Path.GetFileName(dest)}.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Quarantine active-session.json: " + ex.Message);
        }
    }

    /// <summary>True while <paramref name="snap"/> is the live session snapshot.</summary>
    public bool IsCurrent(ActiveSessionSnapshot snap)
    {
        lock (_io) return ReferenceEquals(_memory, snap);
    }

    // ------------------------------------------------------------------ pending FPS-cap restore

    /// <summary>
    /// The FPS cap is restored on a background queue after the session snapshot is already deleted;
    /// this small file keeps that undo information until it succeeds (retried on next start).
    /// </summary>
    private string PendingFpsPath => Path.Combine(Path.GetDirectoryName(_path)!, "pending-fps-restore.json");

    public bool HasPendingFps
    {
        get { lock (_io) return File.Exists(PendingFpsPath); }
    }

    public void SavePendingFps(ActiveSessionSnapshot snap)
    {
        if (!snap.FpsLimitChanged) return;
        var copy = new ActiveSessionSnapshot
        {
            Version = ActiveSessionSnapshot.CurrentVersion,
            ProfileId = snap.ProfileId,
            ProfileName = snap.ProfileName,
            FpsLimitChanged = true,
            FpsLimitTarget = snap.FpsLimitTarget,
            FpsLimitHadSetting = snap.FpsLimitHadSetting,
            FpsLimitWasPredefined = snap.FpsLimitWasPredefined,
            FpsLimitOriginalValue = snap.FpsLimitOriginalValue,
            FpsLimitCreatedProfile = snap.FpsLimitCreatedProfile
        };
        lock (_io)
        {
            try
            {
                var tmp = PendingFpsPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(copy, JsonOptions));
                File.Move(tmp, PendingFpsPath, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Save pending FPS restore: " + ex.Message);
            }
        }
    }

    public ActiveSessionSnapshot? LoadPendingFps()
    {
        lock (_io)
        {
            try
            {
                if (!File.Exists(PendingFpsPath)) return null;
                var snap = JsonSerializer.Deserialize<ActiveSessionSnapshot>(File.ReadAllText(PendingFpsPath), JsonOptions);
                if (snap?.FpsLimitChanged == true) return snap;
                File.Delete(PendingFpsPath);
                return null;
            }
            catch (Exception ex)
            {
                AppLog.Error("Load pending FPS restore: " + ex.Message);
                try { File.Delete(PendingFpsPath); } catch (IOException) { }
                return null;
            }
        }
    }

    public void ClearPendingFps()
    {
        lock (_io)
        {
            try
            {
                if (File.Exists(PendingFpsPath)) File.Delete(PendingFpsPath);
            }
            catch (Exception ex)
            {
                AppLog.Error("Delete pending FPS restore: " + ex.Message);
            }
        }
    }

    private ActiveSessionSnapshot? LoadFromDiskLocked()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var text = File.ReadAllText(_path);
            ActiveSessionSnapshot? snap;
            try { snap = JsonSerializer.Deserialize<ActiveSessionSnapshot>(text, JsonOptions); }
            catch (JsonException ex)
            {
                QuarantineCorruptLocked(ex.Message);
                return null;
            }
            if (snap == null)
            {
                QuarantineCorruptLocked("empty document");
                return null;
            }
            snap.Displays ??= new List<SnapshotDisplay>();
            snap.Displays.RemoveAll(d => d == null);
            if (snap.Version < ActiveSessionSnapshot.CurrentVersion)
                UpgradeLegacy(snap);
            _memory = snap;
            return snap;
        }
        catch (Exception ex)
        {
            AppLog.Error("Load active-session.json: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Display Profile Manager snapshots had no "changed" flags and always restored everything —
    /// keep that behaviour for a leftover file from the old app.
    /// </summary>
    private static void UpgradeLegacy(ActiveSessionSnapshot snap)
    {
        if (snap.Displays.Count == 0 && !string.IsNullOrWhiteSpace(snap.Resolution))
        {
            snap.Displays.Add(new SnapshotDisplay
            {
                Device = snap.DisplayDevice,
                Resolution = snap.Resolution,
                RefreshRate = snap.RefreshRate,
                ModeChanged = true,
                HasGammaRamp = snap.HasGammaRamp,
                GammaRedB64 = snap.GammaRedB64,
                GammaGreenB64 = snap.GammaGreenB64,
                GammaBlueB64 = snap.GammaBlueB64
            });
        }
        snap.PowerPlanChanged = !string.IsNullOrWhiteSpace(snap.PowerPlanGuid);
        snap.ToastOriginal = snap.ToastEnabled ? 1 : 0;
        snap.ToastChanged = true;
        snap.AutoHdrOriginal = snap.AutoHdrEnabled ? 1 : 0;
        snap.AutoHdrChanged = true;
        if (!string.IsNullOrWhiteSpace(snap.AudioDeviceId))
        {
            snap.AudioConsoleDeviceId = snap.AudioDeviceId;
            snap.AudioChanged = true;
        }
        if (snap.MonitorBrightness.HasValue)
        {
            snap.MonitorBrightnessLevels = new List<int?> { snap.MonitorBrightness };
            snap.BrightnessChanged = true;
        }
        snap.Version = ActiveSessionSnapshot.CurrentVersion;
        AppLog.Info("Upgraded a Display Profile Manager session snapshot.");
    }
}

/// <summary>
/// Dirty-session flag (session.lock in the config folder). A leftover lock means the previous
/// run did not exit cleanly; it is cleared on clean exit and on Windows logoff / shutdown.
/// </summary>
public static class SessionGuard
{
    private static string LockPath => Path.Combine(ConfigService.DefaultConfigDirectory, "session.lock");

    public static bool WasDirtyShutdown()
    {
        try { return File.Exists(LockPath); }
        catch { return false; }
    }

    public static void MarkRunning()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
            File.WriteAllText(LockPath, DateTime.UtcNow.ToString("o"));
        }
        catch (Exception ex)
        {
            AppLog.Error("SessionGuard.MarkRunning: " + ex.Message);
        }
    }

    public static void MarkCleanExit()
    {
        try
        {
            if (File.Exists(LockPath))
                File.Delete(LockPath);
        }
        catch (Exception ex)
        {
            AppLog.Error("SessionGuard.MarkCleanExit: " + ex.Message);
        }
    }
}
