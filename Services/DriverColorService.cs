using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

/// <summary>
/// Applies GPU-driver color (NVIDIA Digital Vibrance / AMD ADL saturation)
/// based on ColorBackend. Captures a baseline for restore.
/// Does NOT initialize NvAPI/ADL until a Driver backend is actually applied.
/// </summary>
public sealed class DriverColorService : IDisposable
{
    private readonly NvidiaDriverColor _nvidia = new();
    private readonly AmdAdlColor _amd = new();
    private DriverColorSnapshot? _baseline;
    private string _activeVendor = "none";
    private bool _driverTweaksActive;

    public string ActiveVendor => _activeVendor;
    public bool DriverTweaksActive => _driverTweaksActive;
    public bool NvidiaAvailable => _nvidia.IsAvailable || _nvidia.TryInit();
    public bool AmdAvailable => _amd.IsAvailable || _amd.TryInit();

    public void CaptureBaselineIfNeeded()
    {
        if (_baseline != null) return;
        if (_nvidia.TryInit())
        {
            _baseline = _nvidia.Capture();
            _activeVendor = "nvidia";
        }
        else if (_amd.TryInit())
        {
            _baseline = _amd.Capture();
            _activeVendor = "amd";
        }
        else
        {
            _activeVendor = "none";
            AppLog.Info("No NVIDIA/AMD driver color backend available.");
        }
    }

    public void Apply(ColorSettings color)
    {
        switch (color.Backend)
        {
            case ColorBackend.Driver:
            case ColorBackend.Nvidia:
            case ColorBackend.Amd:
            {
                CaptureBaselineIfNeeded();

                var want = color.Backend == ColorBackend.Driver
                    ? GpuVendorDetect.ResolvedDriverBackend
                    : color.Backend;

                if (want == ColorBackend.Nvidia && _nvidia.TryInit())
                {
                    _activeVendor = "nvidia";
                    if (_baseline == null || _baseline.Vendor != "nvidia")
                        _baseline = _nvidia.Capture();
                    _nvidia.Apply(color.Vibrance);
                    _driverTweaksActive = true;
                    break;
                }

                if ((want == ColorBackend.Amd || want == ColorBackend.Driver) && _amd.TryInit())
                {
                    _activeVendor = "amd";
                    if (_baseline == null || _baseline.Vendor != "amd")
                        _baseline = _amd.Capture();
                    _amd.Apply(color, color.Vibrance);
                    _driverTweaksActive = true;
                    break;
                }

                if (want == ColorBackend.Driver && _nvidia.TryInit())
                {
                    _activeVendor = "nvidia";
                    if (_baseline == null || _baseline.Vendor != "nvidia")
                        _baseline = _nvidia.Capture();
                    _nvidia.Apply(color.Vibrance);
                    _driverTweaksActive = true;
                    break;
                }

                AppLog.Info("Driver backend selected but no NVIDIA/AMD API — ramp only.");
                break;
            }

            default:
                // Low Level / GDI: restore driver only if we previously changed it.
                // Never Call CaptureBaselineIfNeeded / NvAPI init here.
                ClearDriverTweaksIfActive();
                break;
        }
    }

    /// <summary>Restore baseline vibrance/sat if Driver tweaks were applied; no-op otherwise (no NvAPI init).</summary>
    public void ClearDriverTweaksIfActive()
    {
        if (!_driverTweaksActive) return;
        RestoreBaseline();
        _driverTweaksActive = false;
    }

    public void ResetDriverToNeutral()
    {
        try
        {
            if (_nvidia.TryInit())
            {
                _nvidia.Apply(50);
                AppLog.Info("NVIDIA vibrance reset to default.");
            }
            if (_amd.TryInit())
            {
                _amd.Apply(ColorSettings.Neutral, 50);
                AppLog.Info("AMD color reset toward neutral.");
            }
            _driverTweaksActive = false;
        }
        catch (Exception ex)
        {
            AppLog.Error("ResetDriverToNeutral: " + ex.Message);
        }
    }

    public void RestoreBaseline()
    {
        if (_baseline == null) return;
        try
        {
            if (_baseline.Vendor == "nvidia")
                _nvidia.Restore(_baseline);
            else if (_baseline.Vendor == "amd")
                _amd.Restore(_baseline);
            AppLog.Info("Driver color restored to baseline.");
            _driverTweaksActive = false;
        }
        catch (Exception ex)
        {
            AppLog.Error("Driver color restore failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        try { RestoreBaseline(); } catch { /* best-effort */ }
        _nvidia.Dispose();
        _amd.Dispose();
    }
}
