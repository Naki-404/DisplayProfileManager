using Visor.Models;

namespace Visor.Services;

/// <summary>
/// Applies GPU-driver color (NVIDIA Digital Vibrance / AMD ADL saturation) based on ColorBackend,
/// on the display Visor targets. Keeps a baseline per display (captured before Visor first
/// changes it) for restore. Does NOT initialize NvAPI/ADL for Low Level / GDI applies.
/// </summary>
public sealed class DriverColorService : IDisposable
{
    private readonly NvidiaDriverColor _nvidia = new();
    private readonly AmdAdlColor _amd = new();
    /// <summary>vendor|display → state before Visor touched that display.</summary>
    private readonly Dictionary<string, DriverColorSnapshot> _baselines = new(StringComparer.OrdinalIgnoreCase);
    private string _activeVendor = "none";
    private bool _driverTweaksActive;

    public string ActiveVendor => _activeVendor;

    /// <summary>Driver color per display before Visor changed anything (persisted with the display baseline).</summary>
    public IReadOnlyList<DriverColorSnapshot> Baselines => _baselines.Values.ToList();

    /// <summary>\\.\DISPLAYn the driver calls should target (null = first connected display).</summary>
    public void SetTargetDevice(string? deviceName)
    {
        _nvidia.SetTargetDevice(deviceName);
        _amd.SetTargetDevice(deviceName);
    }

    private static string Key(string vendor, string? display) => vendor + "|" + (display ?? "");

    private void RememberBaseline(DriverColorSnapshot? snap)
    {
        if (snap == null) return;
        _baselines.TryAdd(Key(snap.Vendor, snap.DisplayName), snap);
    }

    public void CaptureBaselineIfNeeded()
    {
        if (_baselines.Count > 0) return;
        bool preferAmd = GpuVendorDetect.ResolvedDriverBackend == ColorBackend.Amd;
        if (!preferAmd && _nvidia.TryInit())
        {
            RememberBaseline(_nvidia.Capture());
            _activeVendor = "nvidia";
        }
        else if (_amd.TryInit())
        {
            RememberBaseline(_amd.Capture());
            _activeVendor = "amd";
        }
        else if (preferAmd && _nvidia.TryInit())
        {
            RememberBaseline(_nvidia.Capture());
            _activeVendor = "nvidia";
        }
        else
        {
            _activeVendor = "none";
            AppLog.Info("No NVIDIA/AMD driver color backend available.");
        }
    }

    public void Apply(ColorSettings color)
    {
        if (!GpuVendorDetect.IsDriverSide(color.Backend))
        {
            // Low Level / GDI: restore driver only if we previously changed it — never init NvAPI here.
            ClearDriverTweaksIfActive();
            return;
        }

        // "Driver" resolves to the detected vendor (Nvidia / Amd / Gdi); explicit backends are kept.
        var want = color.Backend == ColorBackend.Driver
            ? GpuVendorDetect.ResolvedDriverBackend
            : color.Backend;

        if (want == ColorBackend.Nvidia && _nvidia.TryInit())
        {
            _activeVendor = "nvidia";
            if (!_baselines.ContainsKey(Key("nvidia", _nvidia.CurrentDisplayName)))
                RememberBaseline(_nvidia.Capture());
            _nvidia.Apply(color.Vibrance);
            _nvidia.TrySetHue(color.Hue);
            _driverTweaksActive = true;
            return;
        }

        if (want == ColorBackend.Amd && _amd.TryInit())
        {
            _activeVendor = "amd";
            if (!_baselines.ContainsKey(Key("amd", _amd.CurrentDisplayName)))
                RememberBaseline(_amd.Capture());
            _amd.Apply(color.Vibrance);
            _driverTweaksActive = true;
            return;
        }

        AppLog.Info("Driver backend selected but no NVIDIA/AMD API — ramp only.");
    }

    /// <summary>Restore baseline vibrance/sat if Driver tweaks were applied; no-op otherwise (no NvAPI init).</summary>
    public void ClearDriverTweaksIfActive()
    {
        if (!_driverTweaksActive) return;
        RestoreBaseline();
    }

    /// <summary>Writes every display's baseline back.</summary>
    /// <returns>False if any display could not be restored (tweaks stay marked active for a retry).</returns>
    public bool RestoreBaseline()
    {
        if (_baselines.Count == 0) return true;
        bool ok = true;
        foreach (var snap in _baselines.Values)
        {
            try
            {
                ok &= RestoreVendor(snap);
            }
            catch (Exception ex)
            {
                ok = false;
                AppLog.Error($"Driver color restore failed ({snap.DisplayName ?? snap.Vendor}): {ex.Message}");
            }
        }
        if (ok)
        {
            _driverTweaksActive = false;
            AppLog.Info("Driver color restored to baseline.");
        }
        return ok;
    }

    private bool RestoreVendor(DriverColorSnapshot snap) => snap.Vendor switch
    {
        "nvidia" => _nvidia.Restore(snap),
        "amd" => _amd.Restore(snap),
        _ => false
    };

    /// <summary>Best-effort capture of the target display's driver color (may init NvAPI/ADL).</summary>
    public DriverColorSnapshot? CaptureCurrent()
    {
        try
        {
            if (_activeVendor != "amd" && _nvidia.TryInit())
                return _nvidia.Capture();
            if (_amd.TryInit())
                return _amd.Capture();
        }
        catch (Exception ex)
        {
            AppLog.Error("Driver color capture: " + ex.Message);
        }
        return null;
    }

    /// <summary>Write a captured state back and adopt it as that display's baseline.</summary>
    /// <returns>True when the driver accepted the values (only then is it adopted as baseline).</returns>
    public bool RestoreFromSnapshot(DriverColorSnapshot snap)
    {
        try
        {
            if (!RestoreVendor(snap))
            {
                AppLog.Warn($"Driver color snapshot not restored ({snap.Vendor}).");
                return false;
            }

            _baselines[Key(snap.Vendor, snap.DisplayName)] = snap;
            _activeVendor = snap.Vendor;
            _driverTweaksActive = false;
            AppLog.Info($"Driver color restored from snapshot ({snap.Vendor}).");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Driver snapshot restore: " + ex.Message);
            return false;
        }
    }

    /// <summary>Undo tweaks still active (RestoreBaseline runs at most once here); NvAPI is unloaded by the engine.</summary>
    public void Dispose()
    {
        try { ClearDriverTweaksIfActive(); }
        catch (Exception ex) { AppLog.Warn(ex, "Driver color clear on dispose"); }
        _nvidia.Dispose();
        _amd.Dispose();
    }
}
