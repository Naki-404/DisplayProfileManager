using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

/// <summary>
/// NVIDIA Control Panel style brightness / contrast / gamma LUT.
/// Same algorithm NVIDIA CP pushes through SetDeviceGammaRamp
/// (see NvAPIWrapper issue #20 / falahati). Used for Driver/NVIDIA/AMD backend
/// together with Digital Vibrance / ADL saturation.
/// </summary>
internal static class NvidiaControlPanelRamp
{
    /// <summary>
    /// DPM ColorSettings → NVIDIA CP curve.
    /// Brightness 0..1 (0.5 neutral), Contrast 0..2 (1.0 neutral → CP 0.5),
    /// Gamma clamped to CP range 0.4..2.8.
    /// </summary>
    public static void Fill(ushort[] red, ushort[] green, ushort[] blue, ColorSettings color)
    {
        double brightness = Math.Clamp(color.Brightness, 0.0, 1.0);           // 0.5 = neutral
        double contrastCp = Math.Clamp(color.Contrast / 2.0, 0.0, 1.0);       // 1.0 → 0.5
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
        // Normalize contrast & brightness to −1..1 around 0.5
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
