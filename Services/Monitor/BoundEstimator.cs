using System.Globalization;

namespace Visor.Services;

/// <summary>Inputs of the limiter estimate: the stats window plus outside signals sampled while capturing.</summary>
/// <param name="FrameCount">Frames in the window.</param>
/// <param name="AvgFps">Average FPS of the window.</param>
/// <param name="FrameTimeCv">Coefficient of variation of frame times (stddev / mean; 0.02 = 2 %).</param>
/// <param name="GpuUtilizationPercent">Average GPU load over the window (busiest GPU), null when not available (non-NVIDIA, NvAPI missing).</param>
/// <param name="RefreshHz">Refresh rate of the display, null when unknown.</param>
public readonly record struct BoundInputs(int FrameCount, double AvgFps, double FrameTimeCv, double? GpuUtilizationPercent, double? RefreshHz);

/// <summary>
/// Pure "where is the frame rate limited" heuristic. Labels are always <i>likely</i>; the evidence
/// string carries the numbers that produced them. Order of the rules:
/// <list type="number">
/// <item>GPU load ≥ <see cref="GpuBoundPercent"/> % → likely GPU-bound.</item>
/// <item>FPS within ±<see cref="RefreshTolerance"/> of the refresh rate with frame-time variation ≤ <see cref="PinnedMaxCv"/> → likely capped (V-Sync / limiter at refresh).</item>
/// <item>GPU load unknown → unknown (the numbers are still shown).</item>
/// <item>GPU load ≤ <see cref="CpuBoundGpuMaxPercent"/> % and FPS below <see cref="WellBelowRefreshRatio"/> × refresh →
///   likely capped when the pace is very steady (variation ≤ <see cref="LimiterMaxCv"/>: an FPS limiter), otherwise likely CPU-bound.</item>
/// <item>Anything else → neither side clearly saturated (capped / balanced).</item>
/// </list>
/// </summary>
public static class BoundEstimator
{
    public const int MinFrames = 30;
    public const double GpuBoundPercent = 95;
    public const double CpuBoundGpuMaxPercent = 75;
    public const double RefreshTolerance = 0.03;
    public const double PinnedMaxCv = 0.10;
    public const double WellBelowRefreshRatio = 0.90;
    public const double LimiterMaxCv = 0.04;

    public static (BoundKind Bound, string Evidence) Classify(in BoundInputs x)
    {
        if (x.FrameCount < MinFrames || !(x.AvgFps > 0) || !double.IsFinite(x.AvgFps))
            return (BoundKind.Unknown, "");

        var ci = CultureInfo.InvariantCulture;
        double cvPct = Math.Max(0, x.FrameTimeCv) * 100;
        string fps = x.AvgFps.ToString("F0", ci) + " FPS";
        string variation = "frame-time variation " + cvPct.ToString("F0", ci) + " %";
        double? refresh = x.RefreshHz is > 1 and < 1000 ? x.RefreshHz : null;
        string refreshPart = refresh is double r ? r.ToString("F0", ci) + " Hz refresh" : "refresh unknown";
        double? gpu = x.GpuUtilizationPercent is double g && double.IsFinite(g) ? Math.Clamp(g, 0, 100) : null;
        string gpuPart = gpu is double gp ? "GPU load " + gp.ToString("F0", ci) + " %" : "GPU load unavailable";

        if (gpu >= GpuBoundPercent)
            return (BoundKind.LikelyGpu, $"{gpuPart} at {fps}");

        bool pinned = refresh is double rr && Math.Abs(x.AvgFps - rr) <= rr * RefreshTolerance && x.FrameTimeCv <= PinnedMaxCv;
        if (pinned)
            return (BoundKind.LikelyCappedOrBalanced, $"{fps} ≈ {refreshPart}, {variation}; {gpuPart}");

        if (gpu is not double load)
            return (BoundKind.Unknown, $"{gpuPart}; {fps}, {refreshPart}");

        if (load <= CpuBoundGpuMaxPercent)
        {
            if (refresh is double hz && x.AvgFps < hz * WellBelowRefreshRatio)
            {
                return x.FrameTimeCv <= LimiterMaxCv
                    ? (BoundKind.LikelyCappedOrBalanced, $"steady {fps} ({variation}) below {refreshPart} with {gpuPart} — likely an FPS limiter")
                    : (BoundKind.LikelyCpu, $"{gpuPart} at {fps}, well below {refreshPart}, {variation}");
            }
            return (BoundKind.LikelyCappedOrBalanced, $"{gpuPart} at {fps}, {refreshPart}");
        }
        return (BoundKind.LikelyCappedOrBalanced, $"{gpuPart} at {fps} — neither GPU nor CPU clearly saturated");
    }
}

/// <summary>Outside signals for <see cref="BoundEstimator"/> (seam: tests use a fake).</summary>
public interface IBoundSignals
{
    /// <summary>Called when a capture starts; the returned token (may be null) is disposed when it ends.</summary>
    IDisposable? BeginCapture();
    /// <summary>Current GPU load in percent of the busiest GPU, or null.</summary>
    double? GpuUtilizationPercent();
    /// <summary>Display refresh rate in Hz, or null.</summary>
    double? DisplayRefreshHz();
}

/// <summary>Production signals: <see cref="SensorService"/> (NvAPI GPU load, sampled while subscribed) and the primary display mode.</summary>
internal sealed class SystemBoundSignals : IBoundSignals
{
    private double? _refresh;

    public IDisposable? BeginCapture()
    {
        try
        {
            int hz = DisplayEngine.GetCurrentMode().RefreshRate;
            _refresh = hz > 1 ? hz : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Monitor: refresh rate");
            _refresh = null;
        }
        try { return App.Services?.Sensors?.Subscribe(); }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Monitor: sensor subscription");
            return null;
        }
    }

    public double? GpuUtilizationPercent()
    {
        try
        {
            var snap = App.Services?.Sensors?.Latest;
            if (snap == null) return null;
            double? best = null;
            foreach (var g in snap.Gpus)
                if (g.UtilizationPercent.Value is int u && (best == null || u > best)) best = u;
            return best;
        }
        catch (Exception) { return null; }
    }

    public double? DisplayRefreshHz() => _refresh;
}
