using System.Management;
using Visor.Models;

namespace Visor.Services;

/// <summary>Detect discrete GPU vendor for the Driver backend auto-switch (WMI only — never touches NvAPI).</summary>
public static class GpuVendorDetect
{
    private static string? _cachedLabel;
    private static ColorBackend? _cachedDriverBackend;
    private static bool _hasNvidia;
    private static bool _hasAmd;
    private static volatile bool _done;

    public static string DriverLabel
    {
        get
        {
            Ensure();
            return _cachedLabel ?? "GPU";
        }
    }

    public static ColorBackend ResolvedDriverBackend
    {
        get
        {
            Ensure();
            return _cachedDriverBackend ?? ColorBackend.Gdi;
        }
    }

    public static bool IsDriverSide(ColorBackend backend) =>
        backend is ColorBackend.Driver or ColorBackend.Nvidia or ColorBackend.Amd;

    private static readonly object Gate = new();

    private static void Ensure()
    {
        if (_done) return;
        lock (Gate)
        {
            if (_done) return;
            Detect();
            _done = true;
        }
    }

    private static void Detect()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = (obj["Name"]?.ToString() ?? "").ToLowerInvariant();
                if (name.Contains("nvidia") || name.Contains("geforce") || name.Contains("rtx") || name.Contains("quadro"))
                    _hasNvidia = true;
                if (name.Contains("amd") || name.Contains("radeon") || name.Contains("ati "))
                    _hasAmd = true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Info("GPU detect WMI failed: " + ex.Message);
        }

        // Prefer NVIDIA when both present (typical laptop hybrid).
        if (_hasNvidia)
        {
            _cachedLabel = "NVIDIA";
            _cachedDriverBackend = ColorBackend.Nvidia;
        }
        else if (_hasAmd)
        {
            _cachedLabel = "AMD";
            _cachedDriverBackend = ColorBackend.Amd;
        }
        else
        {
            _cachedLabel = "GPU";
            _cachedDriverBackend = ColorBackend.Gdi;
        }

        AppLog.Info($"GPU detect: label={_cachedLabel}, nvidia={_hasNvidia}, amd={_hasAmd}");
    }
}
