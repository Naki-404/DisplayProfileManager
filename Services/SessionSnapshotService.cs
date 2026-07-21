using System.IO;
using System.Text.Json;
using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

/// <summary>
/// Captures PC display/power/color state before a game profile applies,
/// persists to active-session.json, and restores on exit / emergency / crash.
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
    private ActiveSessionSnapshot? _memory;

    public SessionSnapshotService(DisplayEngine engine, string configDirectory)
    {
        _engine = engine;
        _path = System.IO.Path.Combine(configDirectory, "active-session.json");
    }

    public string SnapshotPath => _path;
    public bool HasSnapshot => _memory != null || File.Exists(_path);
    public ActiveSessionSnapshot? Current => _memory ?? TryLoadFromDisk();

    public ActiveSessionSnapshot Capture(GameProfile? profile, SessionExtrasService session)
    {
        var (res, hz, device) = DisplayEngine.GetCurrentMode(profile?.DisplayDevice);
        var snap = new ActiveSessionSnapshot
        {
            CapturedUtc = DateTime.UtcNow,
            ProfileId = profile?.Id,
            ProfileName = profile?.Name,
            Resolution = res,
            RefreshRate = hz,
            DisplayDevice = device,
            PowerPlanGuid = DisplayEngine.GetActivePowerPlanGuid()
        };

        if (_engine.TryCaptureGammaRamp(out var r, out var g, out var b))
        {
            snap.HasGammaRamp = true;
            snap.GammaRedB64 = EncodeRamp(r);
            snap.GammaGreenB64 = EncodeRamp(g);
            snap.GammaBlueB64 = EncodeRamp(b);
        }

        try
        {
            var drv = _engine.CaptureDriverColorCurrent();
            if (drv != null)
            {
                snap.HasDriverColor = true;
                snap.DriverVendor = drv.Vendor;
                snap.DriverVibranceLevel = drv.VibranceLevel;
                snap.DriverNormalizedVibrance = drv.NormalizedVibrance;
                snap.DriverBrightness = drv.Brightness;
                snap.DriverContrast = drv.Contrast;
                snap.DriverSaturation = drv.Saturation;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Snapshot driver color: " + ex.Message);
        }

        session.FillSnapshotExtras(snap);
        _memory = snap;
        SaveToDisk(snap);
        AppLog.Info($"Session snapshot saved ({res}@{hz}, power={snap.PowerPlanGuid ?? "?"}).");
        return snap;
    }

    /// <summary>Restore resolution / power / gamma from snapshot. Session extras restored separately.</summary>
    public bool RestoreDisplay(DefaultSettings? fallbackDefaults = null)
    {
        var snap = _memory ?? TryLoadFromDisk();
        if (snap == null)
        {
            if (fallbackDefaults != null)
            {
                _engine.RestoreDefaults(fallbackDefaults);
                return true;
            }
            return false;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(snap.PowerPlanGuid))
                _engine.SetPowerPlan(snap.PowerPlanGuid!);

            if (!string.IsNullOrWhiteSpace(snap.Resolution))
                _engine.SetResolution(snap.Resolution!, snap.RefreshRate, snap.DisplayDevice);

            // Restore pre-game vibrance — never force neutral 50 (that was wiping desktop DV).
            if (snap.HasDriverColor && !string.IsNullOrWhiteSpace(snap.DriverVendor))
            {
                try
                {
                    _engine.RestoreDriverColorSnapshot(new DriverColorSnapshot
                    {
                        Vendor = snap.DriverVendor!,
                        VibranceLevel = snap.DriverVibranceLevel,
                        NormalizedVibrance = snap.DriverNormalizedVibrance,
                        Brightness = snap.DriverBrightness,
                        Contrast = snap.DriverContrast,
                        Saturation = snap.DriverSaturation
                    });
                }
                catch { }
            }
            else
            {
                try { _engine.ClearDriverTweaksIfActive(); } catch { }
            }

            if (snap.HasGammaRamp
                && TryDecodeRamp(snap.GammaRedB64, out var r)
                && TryDecodeRamp(snap.GammaGreenB64, out var g)
                && TryDecodeRamp(snap.GammaBlueB64, out var b))
            {
                _engine.ApplyCapturedGammaRamp(r, g, b);
            }
            else if (fallbackDefaults != null)
            {
                _engine.ApplyColor(fallbackDefaults.Color.Clone());
            }
            else
            {
                _engine.ApplyIdentityColor();
            }

            AppLog.Info("Session snapshot display restored.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Session snapshot restore failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Full restore: session extras + display, then delete active-session.json.</summary>
    public bool RestoreAll(SessionExtrasService session, DefaultSettings? fallbackDefaults = null)
    {
        var snap = _memory ?? TryLoadFromDisk();
        // Prefer in-memory SessionExtras restore (has topology/scaling from this run).
        try { session.Restore(); } catch { }
        // If SessionExtras had nothing (crash restart), use disk extras.
        if (snap != null && (snap.TopologySaved || snap.ScalingSaved || !string.IsNullOrWhiteSpace(snap.AudioDeviceId)))
        {
            // Only apply disk extras fields that SessionExtras.Restore may have skipped.
            try { session.RestoreFromSnapshot(snap); } catch { }
        }

        bool ok = RestoreDisplay(fallbackDefaults);
        Clear();
        return ok || snap != null;
    }

    public void Clear()
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

    public ActiveSessionSnapshot? TryLoadFromDisk()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var json = File.ReadAllText(_path);
            var snap = JsonSerializer.Deserialize<ActiveSessionSnapshot>(json, JsonOptions);
            _memory = snap;
            return snap;
        }
        catch (Exception ex)
        {
            AppLog.Error("Load active-session.json: " + ex.Message);
            return null;
        }
    }

    public void Persist(ActiveSessionSnapshot snap)
    {
        _memory = snap;
        SaveToDisk(snap);
    }

    private void SaveToDisk(ActiveSessionSnapshot snap)
    {
        try
        {
            var json = JsonSerializer.Serialize(snap, JsonOptions);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Copy(tmp, _path, true);
            try { File.Delete(tmp); } catch { }
        }
        catch (Exception ex)
        {
            AppLog.Error("Save active-session.json: " + ex.Message);
        }
    }

    private static string EncodeRamp(ushort[] data)
    {
        var bytes = new byte[data.Length * 2];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    private static bool TryDecodeRamp(string? b64, out ushort[] data)
    {
        data = Array.Empty<ushort>();
        if (string.IsNullOrWhiteSpace(b64)) return false;
        try
        {
            var bytes = Convert.FromBase64String(b64);
            if (bytes.Length != 512) return false;
            data = new ushort[256];
            Buffer.BlockCopy(bytes, 0, data, 0, 512);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
