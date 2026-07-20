using NvAPIWrapper;
using NvAPIWrapper.Display;

namespace DisplayProfileManager.Services;

/// <summary>
/// NVIDIA NvAPI — Digital Vibrance (saturation) only.
/// Brightness/contrast/gamma match NVIDIA Control Panel via SetDeviceGammaRamp
/// (see NvidiaControlPanelRamp) — public NVAPI has no CP gamma setters.
/// </summary>
internal sealed class NvidiaDriverColor : IDisposable
{
    private bool _ready;
    private bool _initialized;
    private Display? _display;
    private double _baselineNormalized;

    public bool IsAvailable => _ready;

    public bool TryInit()
    {
        if (_ready) return true;
        try
        {
            if (!_initialized)
            {
                NVIDIA.Initialize();
                _initialized = true;
            }

            _display = Display.GetDisplays().FirstOrDefault()
                       ?? throw new InvalidOperationException("No NVIDIA display");
            var dvc = _display.DigitalVibranceControl;
            _baselineNormalized = dvc.NormalizedLevel;
            _ready = true;
            AppLog.Info($"NVIDIA NvAPI ready: DVC baseline={_baselineNormalized:F2} (norm), range {dvc.MinimumLevel}..{dvc.MaximumLevel}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info("NVIDIA NvAPI not available: " + ex.Message);
            _ready = false;
            return false;
        }
    }

    public DriverColorSnapshot? Capture()
    {
        if (!_ready && !TryInit()) return null;
        try
        {
            double n = _display!.DigitalVibranceControl.NormalizedLevel;
            _baselineNormalized = n; // keep baseline fresh
            return new DriverColorSnapshot
            {
                Vendor = "nvidia",
                VibranceLevel = (int)Math.Round(n * 1000),
                NormalizedVibrance = (float)n
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("NVIDIA capture failed: " + ex.Message);
            return null;
        }
    }

    /// <param name="vibrancePercent">
    /// 0..100 UI scale. 50 = leave at captured driver baseline (NOT forced to NormalizedLevel 0,
    /// which can look washed/gray on some drivers). 0 = min, 100 = max.
    /// </param>
    public void Apply(int vibrancePercent)
    {
        if (vibrancePercent < 0) return;
        if (!_ready && !TryInit()) return;
        try
        {
            vibrancePercent = Math.Clamp(vibrancePercent, 0, 100);
            double normalized = MapVibrancePercent(vibrancePercent, _baselineNormalized);
            _display!.DigitalVibranceControl.NormalizedLevel = normalized;
            AppLog.Info($"NVIDIA Digital Vibrance → {normalized:F2} (UI {vibrancePercent}%, baseline {_baselineNormalized:F2})");
        }
        catch (Exception ex)
        {
            AppLog.Error("NVIDIA apply failed: " + ex.Message);
        }
    }

    /// <summary>
    /// NVIDIA Control Panel Digital Vibrance scale: 0..100%, 50% = NormalizedLevel 0 (CP default).
    /// </summary>
    internal static double MapVibrancePercent(int percent, double baseline = 0)
    {
        _ = baseline;
        return Math.Clamp((Math.Clamp(percent, 0, 100) - 50) / 50.0, -1.0, 1.0);
    }

    public void Restore(DriverColorSnapshot snap)
    {
        if (!_ready || snap.Vendor != "nvidia") return;
        try
        {
            double n = snap.NormalizedVibrance
                       ?? (snap.VibranceLevel.HasValue ? snap.VibranceLevel.Value / 1000.0 : _baselineNormalized);
            _display!.DigitalVibranceControl.NormalizedLevel = Math.Clamp(n, -1.0, 1.0);
            AppLog.Info("NVIDIA Digital Vibrance restored.");
        }
        catch (Exception ex)
        {
            AppLog.Error("NVIDIA restore failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _display = null;
        _ready = false;
        if (_initialized)
        {
            try { NVIDIA.Unload(); } catch { }
            _initialized = false;
        }
    }
}
