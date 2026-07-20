using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DisplayProfileManager.Setup;

/// <summary>
/// Restores neutral gamma/brightness/contrast and a balanced power plan on uninstall.
/// (Installer cannot use the main app's captured factory ramp — applies a clean neutral ramp.)
/// </summary>
internal static class DisplayRestore
{
    private const string PowerBalanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref Ramp ramp);

    [StructLayout(LayoutKind.Sequential)]
    private struct Ramp
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Blue;
    }

    public static void RestoreNeutralDisplay(Action<string>? log = null)
    {
        try
        {
            log?.Invoke("Restoring color (gamma / brightness / contrast)…");
            ApplyNeutralRamp();
        }
        catch { /* best-effort */ }

        try
        {
            log?.Invoke("Restoring NVIDIA Digital Vibrance…");
            ResetNvidiaVibrance();
        }
        catch { /* best-effort / no NVIDIA */ }

        try
        {
            log?.Invoke("Restoring power plan…");
            SetBalancedPowerPlan();
        }
        catch { /* best-effort */ }
    }

    private static void ResetNvidiaVibrance()
    {
        try
        {
            NvAPIWrapper.NVIDIA.Initialize();
            var display = NvAPIWrapper.Display.Display.GetDisplays().FirstOrDefault();
            if (display != null)
                display.DigitalVibranceControl.NormalizedLevel = 0;
            NvAPIWrapper.NVIDIA.Unload();
        }
        catch
        {
            try { NvAPIWrapper.NVIDIA.Unload(); } catch { }
        }
    }

    private static void ApplyNeutralRamp()
    {
        // Neutral: brightness 0.5, contrast 1.0, gamma 1.0 (same as ColorSettings.Neutral)
        const double brightness = 0.5;
        const double contrast = 1.0;
        const double gamma = 1.0;
        double brightnessOffset = (brightness - 0.5) * 2.0;

        var ramp = new Ramp
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };

        for (int i = 0; i < 256; i++)
        {
            double normalized = i / 255.0;
            double value = (normalized - 0.5) * contrast + 0.5;
            value += brightnessOffset * 0.5;
            value = Math.Pow(Math.Clamp(value, 0.0, 1.0), 1.0 / Math.Max(0.01, gamma));
            value = Math.Clamp(value, 0.0, 1.0);
            ushort word = (ushort)Math.Clamp((int)(value * 65535.0 + 0.5), 0, 65535);
            ramp.Red[i] = word;
            ramp.Green[i] = word;
            ramp.Blue[i] = word;
        }

        var hdc = GetDC(IntPtr.Zero);
        try
        {
            SetDeviceGammaRamp(hdc, ref ramp);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private static void SetBalancedPowerPlan()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            ArgumentList = { "/setactive", PowerBalanced },
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(4000);
    }
}
