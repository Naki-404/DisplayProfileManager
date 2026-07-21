using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

/// <summary>Shared color slider read/write and label formatting for MainWindow and GameOverlayWindow.</summary>
public static class ColorUiHelper
{
    public const double ShadowLiftMax = 0.4;

    public static double ShadowBoostFromLift(double lift) =>
        Math.Clamp(lift / ShadowLiftMax * 100.0, 0, 100);

    public static double ShadowLiftFromBoost(double boost) =>
        Math.Clamp(boost / 100.0 * ShadowLiftMax, 0, ShadowLiftMax);

    public static ColorBackend ReadBackendToggle(ToggleButton tog) =>
        tog.IsChecked == true ? ColorBackend.LowLevel : ColorBackend.Driver;

    public static void SetBackendToggle(ColorBackend backend, ToggleButton tog)
    {
        tog.Content = GpuVendorDetect.DriverLabel;
        tog.IsChecked = backend == ColorBackend.LowLevel;
    }

    public static void ApplyColorSliders(
        ColorSettings c,
        Slider b, Slider co, Slider g, Slider v, Slider? shadowBoost = null)
    {
        ConfigureGammaRangeForBackend(c.Backend, g);
        b.Value = c.Brightness;
        co.Value = c.Contrast;
        g.Value = Math.Clamp(c.Gamma, g.Minimum, g.Maximum);
        v.Value = c.Vibrance;
        if (shadowBoost != null)
            shadowBoost.Value = ShadowBoostFromLift(c.ShadowLift);
    }

    public static void ConfigureGammaRangeForBackend(ColorBackend backend, Slider gamma)
    {
        bool driver = backend is not ColorBackend.LowLevel and not ColorBackend.Gdi;
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
        bool lockColor = true)
    {
        var c = new ColorSettings
        {
            Backend = backend,
            Brightness = b.Value,
            Contrast = co.Value,
            Gamma = g.Value,
            Vibrance = (int)v.Value,
            ShadowLift = shadowBoost != null ? ShadowLiftFromBoost(shadowBoost.Value) : 0,
            LockColor = lockColor
        };
        c.Clamp();
        return c;
    }

    public static void UpdateLabels(
        TextBlock lblB, TextBlock lblC, TextBlock lblG, TextBlock? lblV,
        Slider b, Slider co, Slider g, Slider v, ColorBackend backend)
    {
        var tmp = new ColorSettings
        {
            Brightness = b.Value,
            Contrast = co.Value,
            Gamma = g.Value,
            Backend = backend
        };
        if (backend == ColorBackend.LowLevel)
        {
            var (rb, rc, rg) = tmp.ToRivaTunerUnits();
            lblB.Text = $"B {rb}";
            lblC.Text = $"C {rc}";
            lblG.Text = $"G {rg:F2}";
        }
        else
        {
            lblB.Text = $"B {(int)Math.Round(tmp.Brightness * 100)}%";
            lblC.Text = $"C {(int)Math.Round(Math.Clamp(tmp.Contrast / 2.0, 0, 1) * 100)}%";
            lblG.Text = $"G {Math.Clamp(tmp.Gamma, 0.4, 2.8):F2}";
        }
        if (lblV != null)
        {
            lblV.Text = backend == ColorBackend.LowLevel
                ? $"V {(int)v.Value}"
                : $"V {(int)v.Value}%";
        }
    }

    public static void PlaceOverlayDefault(Window win, double margin = 16)
    {
        var area = SystemParameters.WorkArea;
        win.Left = area.Right - win.Width - margin;
        win.Top = area.Top + margin;
    }
}
