using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Visor.Models;

namespace Visor.Services;

/// <summary>Shared color slider read/write, label and value formatting for MainWindow and GameOverlayWindow.</summary>
public static class ColorUiHelper
{
    public const double ShadowLiftMax = 0.4;

    public static double ShadowBoostFromLift(double lift) =>
        Math.Clamp(lift / ShadowLiftMax * 100.0, 0, 100);

    public static double ShadowLiftFromBoost(double boost) =>
        Math.Clamp(boost / 100.0 * ShadowLiftMax, 0, ShadowLiftMax);

    public static ColorBackend ReadBackendToggle(ToggleButton tog) =>
        tog.IsChecked == true ? ColorBackend.LowLevel : ColorBackend.Driver;

    /// <summary>Left = GPU driver (Content), right = Low Level (Tag) — both labels localized.</summary>
    public static void SetBackendToggle(ColorBackend backend, ToggleButton tog)
    {
        LabelBackendToggle(tog);
        tog.IsChecked = backend == ColorBackend.LowLevel;
    }

    public static void LabelBackendToggle(ToggleButton tog)
    {
        tog.Content = GpuVendorDetect.DriverLabel;
        tog.Tag = Loc.T("color.backend.low");
    }

    public static bool IsLowLevel(ColorBackend backend) => backend is ColorBackend.LowLevel or ColorBackend.Gdi;

    public static void ApplyColorSliders(
        ColorSettings c,
        Slider b, Slider co, Slider g, Slider v, Slider? shadowBoost = null, Slider? hue = null)
    {
        c.Clamp();
        ConfigureGammaRangeForBackend(c.Backend, g);
        b.Value = c.Brightness;
        co.Value = c.Contrast;
        g.Value = Math.Clamp(c.Gamma, g.Minimum, g.Maximum);
        v.Value = c.Vibrance;
        if (shadowBoost != null)
            shadowBoost.Value = ShadowBoostFromLift(c.ShadowLift);
        if (hue != null)
            hue.Value = c.Hue;
    }

    public static void ConfigureGammaRangeForBackend(ColorBackend backend, Slider gamma)
    {
        bool driver = !IsLowLevel(backend);
        double min = driver ? 0.4 : 0.5;
        double max = driver ? 2.8 : 6.0;
        if (Math.Abs(gamma.Minimum - min) < 0.001 && Math.Abs(gamma.Maximum - max) < 0.001)
            return;
        double v = gamma.Value;
        gamma.Minimum = min;
        gamma.Maximum = max;
        gamma.Value = Math.Clamp(v, min, max);
    }

    public static ColorSettings ReadColorFromSliders(
        ColorBackend backend,
        Slider b, Slider co, Slider g, Slider v,
        Slider? shadowBoost = null,
        bool lockColor = true,
        Slider? hue = null)
    {
        var c = new ColorSettings
        {
            Backend = backend,
            Brightness = b.Value,
            Contrast = co.Value,
            Gamma = g.Value,
            Vibrance = (int)v.Value,
            Hue = hue != null ? (int)hue.Value : 0,
            ShadowLift = shadowBoost != null ? ShadowLiftFromBoost(shadowBoost.Value) : 0,
            LockColor = lockColor
        };
        c.Clamp();
        return c;
    }

    // -- Labels (names) and values are separate: names never contain numbers, values never contain names.

    /// <summary>Sets the localized slider names: Brightness / Contrast / Gamma / Vibrance / Hue / Shadows.</summary>
    public static void ApplyLabelNames(TextBlock? b, TextBlock? c, TextBlock? g, TextBlock? v, TextBlock? h, TextBlock? s)
    {
        if (b != null) b.Text = Loc.T("color.brightness");
        if (c != null) c.Text = Loc.T("color.contrast");
        if (g != null) g.Text = Loc.T("color.gamma");
        if (v != null) v.Text = Loc.T("color.vibrance");
        if (h != null) h.Text = Loc.T("color.hue");
        if (s != null) s.Text = Loc.T("color.shadows");
    }

    /// <summary>Writes the numeric value next to each slider (units depend on backend).</summary>
    public static void UpdateValues(
        TextBlock valB, TextBlock valC, TextBlock valG, TextBlock? valV,
        Slider b, Slider co, Slider g, Slider v, ColorBackend backend,
        TextBlock? valH = null, Slider? h = null,
        TextBlock? valS = null, Slider? s = null)
    {
        var tmp = new ColorSettings
        {
            Brightness = b.Value,
            Contrast = co.Value,
            Gamma = g.Value,
            Vibrance = (int)v.Value,
            Hue = h != null ? (int)h.Value : 0,
            ShadowLift = s != null ? ShadowLiftFromBoost(s.Value) : 0,
            Backend = backend
        };
        valB.Text = FormatBrightness(tmp);
        valC.Text = FormatContrast(tmp);
        valG.Text = FormatGamma(tmp);
        if (valV != null) valV.Text = FormatVibrance(tmp);
        if (valH != null) valH.Text = FormatHue(tmp);
        if (valS != null && s != null) valS.Text = ((int)Math.Round(s.Value)).ToString(CultureInfo.InvariantCulture);
    }

    public static string FormatBrightness(ColorSettings c)
    {
        if (IsLowLevel(c.Backend))
            return Signed(c.ToRivaTunerUnits().Brightness);
        return $"{(int)Math.Round(c.Brightness * 100)}%";
    }

    public static string FormatContrast(ColorSettings c)
    {
        if (IsLowLevel(c.Backend))
            return Signed(c.ToRivaTunerUnits().Contrast);
        return $"{(int)Math.Round(Math.Clamp(c.Contrast / 2.0, 0, 1) * 100)}%";
    }

    public static string FormatGamma(ColorSettings c)
    {
        double g = IsLowLevel(c.Backend) ? c.ToRivaTunerUnits().Gamma : Math.Clamp(c.Gamma, 0.4, 2.8);
        return g.ToString("F2", CultureInfo.InvariantCulture);
    }

    public static string FormatVibrance(ColorSettings c) =>
        IsLowLevel(c.Backend) ? c.Vibrance.ToString(CultureInfo.InvariantCulture) : $"{c.Vibrance}%";

    public static string FormatHue(ColorSettings c) => $"{c.Hue}°";

    private static string Signed(int v) => v > 0 ? "+" + v.ToString(CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);

    /// <summary>"2560x1440" → "1440p" for 16:9, otherwise "1440×1080" (stretched resolutions stay explicit).</summary>
    public static string FormatResolution(string? res)
    {
        if (string.IsNullOrWhiteSpace(res)) return "";
        var parts = res.ToLowerInvariant().Replace('×', 'x').Split('x');
        if (parts.Length == 2
            && int.TryParse(parts[0].Trim(), out int w)
            && int.TryParse(parts[1].Trim(), out int h)
            && w > 0 && h > 0)
        {
            return w * 9 == h * 16 ? $"{h}p" : $"{w}×{h}";
        }
        return res.Trim();
    }

    /// <summary>Human-readable preset summary — only values that differ from neutral, plus resolution.</summary>
    public static string Describe(QuickPreset p)
    {
        var parts = new List<string>();
        if (p.ApplyColor && p.Color != null)
        {
            var c = p.Color.Clone();
            c.Clamp();
            bool low = IsLowLevel(c.Backend);
            if (Math.Abs(c.Brightness - 0.5) > 0.005) parts.Add($"{Loc.T("color.brightness")} {FormatBrightness(c)}");
            if (Math.Abs(c.Contrast - 1.0) > 0.005) parts.Add($"{Loc.T("color.contrast")} {FormatContrast(c)}");
            if (Math.Abs(c.Gamma - 1.0) > 0.005) parts.Add($"{Loc.T("color.gamma")} {FormatGamma(c)}");
            if (c.Vibrance != 50) parts.Add($"{Loc.T("color.vibrance")} {FormatVibrance(c)}");
            if (c.Hue != 0 && !low) parts.Add($"{Loc.T("color.hue")} {FormatHue(c)}");
            if (c.ShadowLift > 0.001 && low) parts.Add($"{Loc.T("color.shadows")} {(int)Math.Round(ShadowBoostFromLift(c.ShadowLift))}");
            if (parts.Count == 0) parts.Add(Loc.T("preset.summary.neutral"));
        }
        if (p.ApplyResolution && !string.IsNullOrWhiteSpace(p.Resolution))
            parts.Add(FormatResolution(p.Resolution));
        return parts.Count == 0 ? Loc.T("preset.summary.none") : string.Join(" · ", parts);
    }

    /// <summary>
    /// Places a topmost tool window in the top-right corner of the monitor the game (foreground window)
    /// is on — falls back to the cursor's monitor. Works in physical pixels, so mixed-DPI setups are fine.
    /// </summary>
    public static void PlaceOverlayDefault(Window win, int marginPx = 16)
    {
        win.UpdateLayout();
        if (!OverlayWin32.PlaceTopRight(win, marginPx))
        {
            // No HWND yet — DIP fallback on the primary work area.
            var area = SystemParameters.WorkArea;
            double w = win.ActualWidth > 8 ? win.ActualWidth : 300;
            win.Left = Math.Max(area.Left + marginPx, area.Right - w - marginPx);
            win.Top = area.Top + marginPx;
        }
    }
}
