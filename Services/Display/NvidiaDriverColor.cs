using NvAPIWrapper.Display;
using Visor.Models;

namespace Visor.Services;

/// <summary>
/// NVIDIA NvAPI - Digital Vibrance (saturation) and HUE offset on the target display.
/// Brightness/contrast/gamma match NVIDIA Control Panel via SetDeviceGammaRamp
/// (see NvidiaControlPanelRamp) - public NVAPI has no CP gamma setters.
/// NvAPI lifetime is owned by <see cref="NvApiHost"/>.
/// </summary>
internal sealed class NvidiaDriverColor : IDisposable
{
    private Display? _display;
    private string? _targetDevice;
    /// <summary>DVC level per display (\\.\DISPLAYn) the first time Visor saw it — the "50%" point.</summary>
    private readonly Dictionary<string, double> _baselineByDisplay = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAvailable => _display != null;

    /// <summary>\.DISPLAYn of the resolved display (after TryInit).</summary>
    public string? CurrentDisplayName => _display == null ? null : DisplayName(_display);

    /// <summary>Target display (\\.\DISPLAYn); null = first connected NVIDIA display.</summary>
    public void SetTargetDevice(string? deviceName)
    {
        if (string.Equals(deviceName, _targetDevice, StringComparison.OrdinalIgnoreCase)) return;
        _targetDevice = deviceName;
        _display = null; // re-resolve on next use
    }

    public bool TryInit()
    {
        if (_display != null) return true;
        if (!NvApiHost.TryInitialize()) return false;
        try
        {
            _display = FindDisplay(_targetDevice)
                       ?? throw new InvalidOperationException("No connected NVIDIA display");
            var name = DisplayName(_display);
            var dvc = _display.DigitalVibranceControl;
            if (!_baselineByDisplay.ContainsKey(name))
                _baselineByDisplay[name] = dvc.NormalizedLevel;
            AppLog.Info($"NVIDIA NvAPI ready on {name}: DVC baseline={_baselineByDisplay[name]:F2} (norm), range {dvc.MinimumLevel}..{dvc.MaximumLevel}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info("NVIDIA display color not available: " + ex.Message);
            _display = null;
            return false;
        }
    }

    /// <summary>The requested display when NVIDIA drives it, else the first connected one.</summary>
    private static Display? FindDisplay(string? deviceName)
    {
        var displays = Display.GetDisplays();
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var hit = displays.FirstOrDefault(d =>
                string.Equals(DisplayName(d), deviceName, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return displays.FirstOrDefault(IsConnected) ?? displays.FirstOrDefault();
    }

    private static string DisplayName(Display d)
    {
        try { return d.Name ?? ""; }
        catch { return ""; }
    }

    private static bool IsConnected(Display d)
    {
        try { return d.DisplayDevice.IsConnected; }
        catch { return true; }
    }

    public DriverColorSnapshot? Capture()
    {
        if (!TryInit()) return null;
        try
        {
            double n = _display!.DigitalVibranceControl.NormalizedLevel;
            var snap = new DriverColorSnapshot
            {
                Vendor = "nvidia",
                DisplayName = DisplayName(_display),
                VibranceLevel = (int)Math.Round(n * 1000),
                NormalizedVibrance = (float)n
            };
            if (TryGetHue(out int hue))
                snap.HueAngle = hue;
            return snap;
        }
        catch (Exception ex)
        {
            AppLog.Error("NVIDIA capture failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Read the current NVIDIA HUE offset angle (0..359). False if unavailable.</summary>
    public bool TryGetHue(out int hue)
    {
        hue = 0;
        if (!TryInit()) return false;
        try
        {
            hue = _display!.HUEControl.CurrentAngle;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info("NVIDIA HUE read not available: " + ex.Message);
            return false;
        }
    }

    /// <summary>Set the NVIDIA HUE offset angle (0..359, wraps). False if unavailable.</summary>
    public bool TrySetHue(int hue) => TryInit() && TrySetHue(_display!, hue);

    private static bool TrySetHue(Display display, int hue)
    {
        try
        {
            hue = ((hue % 360) + 360) % 360;
            if (display.HUEControl.CurrentAngle == hue) return true;
            display.HUEControl.CurrentAngle = hue;
            AppLog.Info($"NVIDIA HUE -> {hue} deg");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info("NVIDIA HUE set not available: " + ex.Message);
            return false;
        }
    }

    /// <param name="vibrancePercent">
    /// 0..100 UI scale. 50 = leave at the display's captured driver baseline (NOT forced to
    /// NormalizedLevel 0, which can look washed/gray on some drivers). 0 = min, 100 = max.
    /// </param>
    public void Apply(int vibrancePercent)
    {
        if (vibrancePercent < 0) return;
        if (!TryInit()) return;
        try
        {
            vibrancePercent = Math.Clamp(vibrancePercent, 0, 100);
            var name = DisplayName(_display!);
            double baseline = _baselineByDisplay.TryGetValue(name, out var b) ? b : 0;
            double normalized = MapVibrancePercent(vibrancePercent, baseline);
            _display!.DigitalVibranceControl.NormalizedLevel = normalized;
            AppLog.Info($"NVIDIA Digital Vibrance -> {normalized:F2} (UI {vibrancePercent}%, baseline {baseline:F2})");
        }
        catch (Exception ex)
        {
            AppLog.Error("NVIDIA apply failed: " + ex.Message);
        }
    }

    /// <summary>
    /// UI 0..100 → NvAPI normalized level (-1..1): 50 = baseline, 0 = minimum, 100 = maximum,
    /// linear on each side of the baseline.
    /// </summary>
    internal static double MapVibrancePercent(int percent, double baseline)
    {
        percent = Math.Clamp(percent, 0, 100);
        baseline = Math.Clamp(baseline, -1.0, 1.0);
        double t = (percent - 50) / 50.0;
        double level = t >= 0
            ? baseline + (1.0 - baseline) * t
            : baseline + (baseline + 1.0) * t;
        return Math.Clamp(level, -1.0, 1.0);
    }

    /// <returns>False when NvAPI or the display is unavailable, or the write failed.</returns>
    public bool Restore(DriverColorSnapshot snap)
    {
        if (snap.Vendor != "nvidia") return false;
        if (!NvApiHost.TryInitialize())
        {
            AppLog.Warn("NVIDIA restore skipped: NvAPI not available.");
            return false;
        }
        try
        {
            var display = FindDisplay(snap.DisplayName ?? _targetDevice);
            if (display == null)
            {
                AppLog.Warn($"NVIDIA restore skipped: display {snap.DisplayName ?? "(default)"} not found.");
                return false;
            }
            double n = snap.NormalizedVibrance
                       ?? (snap.VibranceLevel.HasValue ? snap.VibranceLevel.Value / 1000.0 : 0);
            display.DigitalVibranceControl.NormalizedLevel = Math.Clamp(n, -1.0, 1.0);
            if (snap.HueAngle is int hue)
                TrySetHue(display, hue);
            AppLog.Info($"NVIDIA Digital Vibrance restored on {DisplayName(display)}.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("NVIDIA restore failed: " + ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        _display = null;
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
    /// Visor ColorSettings -> NVIDIA CP curve.
    /// Brightness 0..1 (0.5 neutral), Contrast 0..2 (1.0 neutral -> CP 0.5),
    /// Gamma clamped to CP range 0.4..2.8.
    /// </summary>
    public static void Fill(ushort[] red, ushort[] green, ushort[] blue, ColorSettings color)
    {
        double brightness = Math.Clamp(color.Brightness, 0.0, 1.0);           // 0.5 = neutral
        double contrastCp = Math.Clamp(color.Contrast / 2.0, 0.0, 1.0);       // 1.0 -> 0.5
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
        // Normalize contrast & brightness to -1..1 around 0.5
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

