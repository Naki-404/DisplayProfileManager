using NvAPIWrapper;
using NvAPIWrapper.Display;
using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

/// <summary>
/// NVIDIA NvAPI â€” Digital Vibrance (saturation) only.
/// Brightness/contrast/gamma match NVIDIA Control Panel via SetDeviceGammaRamp
/// (see NvidiaControlPanelRamp) â€” public NVAPI has no CP gamma setters.
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
            AppLog.Info($"NVIDIA Digital Vibrance â†’ {normalized:F2} (UI {vibrancePercent}%, baseline {_baselineNormalized:F2})");
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

/// <summary>
/// NVIDIA Control Panel style brightness / contrast / gamma LUT.
/// Same algorithm NVIDIA CP pushes through SetDeviceGammaRamp
/// (see NvAPIWrapper issue #20 / falahati). Used for Driver/NVIDIA/AMD backend
/// together with Digital Vibrance / ADL saturation.
/// </summary>
internal static class NvidiaControlPanelRamp
{
    /// <summary>
    /// DPM ColorSettings â†’ NVIDIA CP curve.
    /// Brightness 0..1 (0.5 neutral), Contrast 0..2 (1.0 neutral â†’ CP 0.5),
    /// Gamma clamped to CP range 0.4..2.8.
    /// </summary>
    public static void Fill(ushort[] red, ushort[] green, ushort[] blue, ColorSettings color)
    {
        double brightness = Math.Clamp(color.Brightness, 0.0, 1.0);           // 0.5 = neutral
        double contrastCp = Math.Clamp(color.Contrast / 2.0, 0.0, 1.0);       // 1.0 â†’ 0.5
        double gamma = Math.Clamp(color.Gamma, 0.4, 2.8);

        FillRaw(red, green, blue, brightness, contrastCp, gamma);
    }

    /// <param name="brightness">0..1, 0.5 neutral</param>
    /// <param name="contrast">0..1, 0.5 neutral (NVIDIA CP scale)</param>
    /// <param name="gamma">0.4..2.8, 1.0 neutral</param>
    public static void FillRaw(
        ushort[] red,
        ushort[] green,
        ushort[] blue,
        double brightness,
        double contrast,
        double gamma)
    {
        const int dataPoints = 256;

        gamma = Math.Clamp(gamma, 0.4, 2.8);
        // Normalize contrast & brightness to âˆ’1..1 around 0.5
        contrast = (Math.Clamp(contrast, 0.0, 1.0) - 0.5) * 2.0;
        brightness = (Math.Clamp(brightness, 0.0, 1.0) - 0.5) * 2.0;

        double offset = contrast > 0 ? contrast * -25.4 : contrast * -32.0;
        double range = (dataPoints - 1) + offset * 2.0;
        if (Math.Abs(range) < 1.0) range = 1.0;
        offset += brightness * (range / 5.0);

        double invGamma = 1.0 / Math.Max(0.01, gamma);

        for (int i = 0; i < dataPoints; i++)
        {
            double factor = (i + offset) / range;
            factor = Math.Pow(Math.Clamp(factor, 0.0, 1.0), invGamma);
            factor = Math.Clamp(factor, 0.0, 1.0);
            ushort w = (ushort)Math.Clamp((int)Math.Round(factor * 65535.0), 0, 65535);
            red[i] = w;
            green[i] = w;
            blue[i] = w;
        }
    }
}

