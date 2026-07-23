using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

/// <summary>
/// RivaTuner / SoftRiva low-level LUT.
/// Order: contrast + brightness, then gamma (NOT gamma-first — that washed the image white).
/// </summary>
internal static class LowLevelColorRamp
{
    public const int BrightnessMin = -125;
    public const int BrightnessMax = 125;
    public const int ContrastMin = -82;
    public const int ContrastMax = 82;
    public const double GammaMin = 0.5;
    public const double GammaMax = 6.0;

    public static void Fill(ushort[] red, ushort[] green, ushort[] blue, ColorSettings color)
    {
        var (b, c, g) = color.ToRivaTunerUnits();
        FillRt(red, green, blue, b, c, g, color.ShadowLift);
    }

    public static void FillRt(
        ushort[] red,
        ushort[] green,
        ushort[] blue,
        int brightnessRt,
        int contrastRt,
        double gammaRt,
        double shadowLift = 0)
    {
        brightnessRt = Math.Clamp(brightnessRt, BrightnessMin, BrightnessMax);
        contrastRt = Math.Clamp(contrastRt, ContrastMin, ContrastMax);
        gammaRt = Math.Clamp(gammaRt, GammaMin, GammaMax);
        shadowLift = Math.Clamp(shadowLift, 0.0, 0.4);

        double contrast = (contrastRt + 100.0) / 100.0;
        if (contrast < 0.01) contrast = 0.01;
        double brightness = brightnessRt / 255.0;
        double invGamma = 1.0 / Math.Max(0.01, gammaRt);

        for (int i = 0; i < 256; i++)
        {
            double f = i / 255.0;

            // SoftRiva / classic RT: contrast & brightness first…
            f = (f - 0.5) * contrast + 0.5 + brightness;
            if (f < 0.0) f = 0.0;
            else if (f > 1.0) f = 1.0;

            // …then gamma (pow 1/g). Gamma-first washed the picture white.
            f = Math.Pow(f, invGamma);

            if (shadowLift > 0.0001)
            {
                double lift = shadowLift * (1.0 - f) * (1.0 - f);
                f = Math.Clamp(f + lift, 0.0, 1.0);
            }

            ushort w = PackRampFloat(f);
            red[i] = w;
            green[i] = w;
            blue[i] = w;
        }
    }

    public static void FillIdentity(ushort[] red, ushort[] green, ushort[] blue)
    {
        for (int i = 0; i < 256; i++)
        {
            ushort w = PackRampFloat(i / 255.0);
            red[i] = w;
            green[i] = w;
            blue[i] = w;
        }
    }

    public static ushort PackRampFloat(double normalized01)
    {
        double f = Math.Clamp(normalized01, 0.0, 1.0);
        return (ushort)Math.Clamp((int)Math.Round(f * 65535.0), 0, 65535);
    }

    public static ushort PackRampByte(int level0To255) =>
        PackRampFloat(Math.Clamp(level0To255, 0, 255) / 255.0);

    public static int UnpackRampByte(ushort word) =>
        (int)Math.Clamp(Math.Round(word * 255.0 / 65535.0), 0, 255);
}
