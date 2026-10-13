using System.Management;
using Visor.Models;

namespace Visor.Services;

/// <summary>
/// Detect discrete GPU vendor for the Driver backend auto-switch (WMI only — never touches NvAPI).
/// The WMI query takes hundreds of milliseconds, so it runs once on the thread pool
/// (<see cref="BeginDetect"/>, kicked off at startup). <see cref="DriverLabel"/> never blocks —
/// it answers "GPU" until the result is in (the UI relabels when <see cref="WhenDetected"/>
/// completes); <see cref="ResolvedDriverBackend"/> waits, because an apply needs the real answer.
/// </summary>
public static class GpuVendorDetect
{
    private static string? _cachedLabel;
    private static ColorBackend? _cachedDriverBackend;
    private static bool _hasNvidia;
    private static bool _hasAmd;
    private static volatile bool _done;
    private static int _started;
    private static readonly object Gate = new();
    private static readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True once detection has finished (success or failure).</summary>
    public static bool IsDetected => _done;

    /// <summary>Completes when detection has finished — use to refresh labels shown before that.</summary>
    public static Task WhenDetected => Completed.Task;

    /// <summary>Starts detection on the thread pool (idempotent, returns immediately).</summary>
    public static void BeginDetect()
    {
        if (_done || Interlocked.Exchange(ref _started, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ => Ensure());
    }

    /// <summary>Non-blocking: "NVIDIA" / "AMD" / "GPU" ("GPU" also while detection is still running).</summary>
    public static string DriverLabel
    {
        get
        {
            if (!_done)
            {
                BeginDetect();
                return "GPU";
            }
            return _cachedLabel ?? "GPU";
        }
    }

    /// <summary>Blocking (waits for a running detection): the backend "Driver" resolves to.</summary>
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

    private static void Ensure()
    {
        if (_done) return;
        lock (Gate)
        {
            if (_done) return;
            try
            {
                Detect();
            }
            finally
            {
                _done = true;
                Completed.TrySetResult();
            }
        }
    }

    private static void Detect()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    var name = (obj["Name"]?.ToString() ?? "").ToLowerInvariant();
                    if (name.Contains("nvidia") || name.Contains("geforce") || name.Contains("rtx") || name.Contains("quadro"))
                        _hasNvidia = true;
                    if (name.Contains("amd") || name.Contains("radeon") || name.Contains("ati "))
                        _hasAmd = true;
                }
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
