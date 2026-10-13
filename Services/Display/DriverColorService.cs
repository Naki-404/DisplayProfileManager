using Visor.Models;

namespace Visor.Services;

/// <summary>
/// Applies GPU-driver color (NVIDIA Digital Vibrance / AMD ADL saturation) based on ColorBackend,
/// on the display Visor targets. Keeps a baseline per display (captured before Visor first
/// changes it) for restore. Does NOT initialize NvAPI/ADL for Low Level / GDI applies.
/// </summary>
/// <remarks>
/// Every driver call takes <c>_sync</c>: the startup baseline capture runs on the thread pool
/// (NvAPI init is slow) while the UI thread may already apply a preset — the lock keeps
/// "capture the baseline before the first write" true regardless of who gets there first.
/// The two members the UI thread calls on every session change — <see cref="SetTargetDevice"/>
/// and <see cref="ClearDriverTweaksIfActive"/> — never wait on it: the target is stored and
/// handed to the vendor backends lazily by the next locked call, and the clear returns at once
/// while no tweak is active. Vendor-detect results are read before the lock for the same reason.
/// </remarks>
public sealed class DriverColorService : IDisposable
{
    private readonly object _sync = new();
    /// <summary>Guards only the pending target (never held while a driver call runs).</summary>
    private readonly object _targetSync = new();
    private readonly NvidiaDriverColor _nvidia = new();
    private readonly AmdAdlColor _amd = new();
    /// <summary>vendor|display → state before Visor touched that display.</summary>
    private readonly Dictionary<string, DriverColorSnapshot> _baselines = new(StringComparer.OrdinalIgnoreCase);
    private string _activeVendor = "none";
    private volatile bool _driverTweaksActive;
    private string? _pendingTarget;
    private bool _targetDirty;

    public string ActiveVendor
    {
        get { lock (_sync) return _activeVendor; }
    }

    /// <summary>Driver color per display before Visor changed anything (persisted with the display baseline).</summary>
    public IReadOnlyList<DriverColorSnapshot> Baselines
    {
        get { lock (_sync) return _baselines.Values.ToList(); }
    }

    /// <summary>
    /// \\.\DISPLAYn the driver calls should target (null = first connected display). Fast path:
    /// only records the target; the backends receive it right before the next driver call.
    /// </summary>
    public void SetTargetDevice(string? deviceName)
    {
        lock (_targetSync)
        {
            _pendingTarget = deviceName;
            _targetDirty = true;
        }
    }

    /// <summary>Hands a target set since the last driver call to the vendor backends. Call under <c>_sync</c>.</summary>
    private void ApplyPendingTargetLocked()
    {
        string? target;
        lock (_targetSync)
        {
            if (!_targetDirty) return;
            target = _pendingTarget;
            _targetDirty = false;
        }
        _nvidia.SetTargetDevice(target);
        _amd.SetTargetDevice(target);
    }

    private static string Key(string vendor, string? display) => vendor + "|" + (display ?? "");

    private void RememberBaseline(DriverColorSnapshot? snap)
    {
        if (snap == null) return;
        _baselines.TryAdd(Key(snap.Vendor, snap.DisplayName), snap);
    }

    /// <summary>First-time capture (may init NvAPI / ADL — slow; safe to call from a background thread).</summary>
    public void CaptureBaselineIfNeeded()
    {
        // Vendor detection may itself be waiting on WMI — never do that while holding _sync.
        bool preferAmd = GpuVendorDetect.ResolvedDriverBackend == ColorBackend.Amd;
        lock (_sync)
        {
            if (_baselines.Count > 0) return;
            ApplyPendingTargetLocked();
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
        // Resolved outside the lock — detection may block on WMI.
        var want = color.Backend == ColorBackend.Driver
            ? GpuVendorDetect.ResolvedDriverBackend
            : color.Backend;

        lock (_sync)
        {
            ApplyPendingTargetLocked();

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
    }

    /// <summary>
    /// Restore baseline vibrance/sat if Driver tweaks were applied; no-op otherwise (no NvAPI init).
    /// Returns without taking the driver lock while nothing is active, so the UI thread never
    /// waits behind a slow baseline capture here.
    /// </summary>
    public void ClearDriverTweaksIfActive()
    {
        if (!_driverTweaksActive) return;
        lock (_sync)
        {
            if (!_driverTweaksActive) return;
            RestoreBaseline();
        }
    }

    /// <summary>Writes every display's baseline back.</summary>
    /// <returns>False if any display could not be restored (tweaks stay marked active for a retry).</returns>
    public bool RestoreBaseline()
    {
        lock (_sync)
        {
            if (_baselines.Count == 0) return true;
            ApplyPendingTargetLocked();
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
        lock (_sync)
        {
            try
            {
                ApplyPendingTargetLocked();
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
    }

    /// <summary>Write a captured state back and adopt it as that display's baseline.</summary>
    /// <returns>True when the driver accepted the values (only then is it adopted as baseline).</returns>
    public bool RestoreFromSnapshot(DriverColorSnapshot snap)
    {
        lock (_sync)
        {
            try
            {
                ApplyPendingTargetLocked();
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
    }

    /// <summary>Undo tweaks still active (RestoreBaseline runs at most once here); NvAPI is unloaded by the engine.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            try { ClearDriverTweaksIfActive(); }
            catch (Exception ex) { AppLog.Warn(ex, "Driver color clear on dispose"); }
            _nvidia.Dispose();
            _amd.Dispose();
        }
    }
}
