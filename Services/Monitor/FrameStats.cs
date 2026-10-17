namespace Visor.Services;

/// <summary>Where the frame rate most likely is limited, with the evidence that produced the label.</summary>
public enum BoundKind
{
    /// <summary>Not enough frames, or no GPU-load reading to judge by (see <see cref="BoundEstimator"/>).</summary>
    Unknown,
    /// <summary>GPU load at the ceiling while capturing — the GPU is the likely limiter.</summary>
    LikelyGpu,
    /// <summary>GPU has headroom and FPS stays well below the refresh rate — the CPU (game thread) is the likely limiter.</summary>
    LikelyCpu,
    /// <summary>FPS pinned at the refresh / a steady limiter, or neither side clearly saturated.</summary>
    LikelyCappedOrBalanced
}

/// <summary>Snapshot of the stats window. All FPS values derive from frame times; nothing is extrapolated.</summary>
public sealed record FrameStatsSummary
{
    public int FrameCount { get; init; }
    public int DroppedCount { get; init; }
    /// <summary>Seconds actually covered by the frames in the window (≤ the configured window).</summary>
    public double CoveredSeconds { get; init; }
    public double AvgFps { get; init; }
    public double AvgFrameTimeMs { get; init; }
    public double MedianFrameTimeMs { get; init; }
    /// <summary>FPS of the 99th-percentile frame time ("1 % low").</summary>
    public double OnePercentLowFps { get; init; }
    /// <summary>FPS of the 99.9th-percentile frame time ("0.1 % low").</summary>
    public double PointOnePercentLowFps { get; init; }
    public double P99FrameTimeMs { get; init; }
    public double MaxFrameTimeMs { get; init; }
    /// <summary>Frames slower than 2× the median frame time.</summary>
    public int StutterCount { get; init; }
    /// <summary>Population standard deviation of the frame times.</summary>
    public double FrameTimeStdDevMs { get; init; }
    /// <summary>Frame-time variation: stddev / mean (0 when empty).</summary>
    public double FrameTimeCv => AvgFrameTimeMs > 0 ? FrameTimeStdDevMs / AvgFrameTimeMs : 0;
    public BoundKind Bound { get; init; }
    /// <summary>Human-readable evidence for <see cref="Bound"/> (the numbers behind the label); may be set even when Unknown.</summary>
    public string BoundEvidence { get; init; } = "";
    public double? AvgGpuBusyMs { get; init; }
    public double? AvgCpuBusyMs { get; init; }
    public double? AvgDisplayLatencyMs { get; init; }
    /// <summary>Mean of the frames that carried a ClickToPhoton value; null when none did.</summary>
    public double? AvgClickToPhotonMs { get; init; }
    /// <summary>How many frames carried an input-latency value.</summary>
    public int ClickToPhotonSamples { get; init; }
    /// <summary>Newest frame's instantaneous FPS (0 when empty).</summary>
    public double LastFps { get; init; }

    public static FrameStatsSummary Empty { get; } = new();

    /// <summary>
    /// Named samples for <see cref="IMetricSink"/> / <c>OptimizationExperiment</c>. Empty when the
    /// window has no frames; the input-latency metric is present only when it was measured.
    /// </summary>
    public IReadOnlyList<Visor.Services.Optimize.MetricSample> ToMetricSamples(DateTime? utcNow = null)
    {
        if (FrameCount == 0) return Array.Empty<Visor.Services.Optimize.MetricSample>();
        var utc = utcNow ?? DateTime.UtcNow;
        var list = new List<Visor.Services.Optimize.MetricSample>(7)
        {
            new(MonitorMetricNames.Fps, AvgFps, utc),
            new(MonitorMetricNames.OnePercentLow, OnePercentLowFps, utc),
            new(MonitorMetricNames.PointOnePercentLow, PointOnePercentLowFps, utc),
            new(MonitorMetricNames.FrameTimeMs, AvgFrameTimeMs, utc),
            new(MonitorMetricNames.FrameTimeP99Ms, P99FrameTimeMs, utc),
            new(MonitorMetricNames.Stutters, StutterCount, utc),
        };
        if (AvgClickToPhotonMs is double c2p)
            list.Add(new(MonitorMetricNames.ClickToPhotonMs, c2p, utc));
        return list;
    }
}

/// <summary>
/// Pure statistics over the last <see cref="WindowSeconds"/> seconds of frames (by the frames'
/// own timestamps, not wall clock). Thread-safe: one writer (<see cref="Add"/>) and any number
/// of <see cref="Snapshot"/> readers. Percentiles use the nearest-rank method on frame times;
/// "1 % low" is the FPS equivalent of the p99 frame time — the convention used by most
/// benchmarking tools that report percentile lows.
/// </summary>
public sealed class FrameStats
{
    /// <summary>Frames slower than this multiple of the median count as stutters.</summary>
    public const double StutterFactor = 2.0;

    private readonly object _gate = new();
    private readonly LinkedList<FrameRecord> _frames = new();
    private readonly int _maxFrames;

    public FrameStats(double windowSeconds = 10, int maxFrames = 20000)
    {
        if (windowSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        if (maxFrames <= 0) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        WindowSeconds = windowSeconds;
        _maxFrames = maxFrames;
    }

    public double WindowSeconds { get; }

    public int Count { get { lock (_gate) return _frames.Count; } }

    /// <summary>Frames added since the last <see cref="Clear"/> (survives trimming).</summary>
    public long TotalAdded { get { lock (_gate) return _total; } private set { lock (_gate) _total = value; } }
    private long _total;

    public void Add(in FrameRecord frame)
    {
        lock (_gate)
        {
            _frames.AddLast(frame);
            _total++;
            double cutoff = frame.Time - WindowSeconds;
            while (_frames.Count > 0 && (_frames.First!.Value.Time < cutoff || _frames.Count > _maxFrames))
                _frames.RemoveFirst();
        }
    }

    /// <summary>Drops the window and resets <see cref="TotalAdded"/> (one capture = one counter).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _frames.Clear();
            _total = 0;
        }
    }

    /// <summary>Copy of the window, oldest first (for charts).</summary>
    public FrameRecord[] Frames()
    {
        lock (_gate) return _frames.ToArray();
    }

    public FrameStatsSummary Snapshot()
    {
        FrameRecord[] frames;
        lock (_gate) frames = _frames.ToArray();
        return Compute(frames);
    }

    /// <summary>Stats for an arbitrary frame list (used by Snapshot and by tests).</summary>
    public static FrameStatsSummary Compute(IReadOnlyList<FrameRecord> frames)
    {
        if (frames == null || frames.Count == 0) return FrameStatsSummary.Empty;

        int n = frames.Count;
        var times = new double[n];
        double sum = 0, max = 0;
        double gpuSum = 0, cpuSum = 0, dispSum = 0, c2pSum = 0;
        int gpuN = 0, cpuN = 0, dispN = 0, c2pN = 0, dropped = 0;
        for (int i = 0; i < n; i++)
        {
            var f = frames[i];
            double ft = f.FrameTimeMs > 0 ? f.FrameTimeMs : 0;
            times[i] = ft;
            sum += ft;
            if (ft > max) max = ft;
            if (f.Dropped) dropped++;
            if (f.GpuBusyMs is double g) { gpuSum += g; gpuN++; }
            if (f.CpuBusyMs is double c) { cpuSum += c; cpuN++; }
            if (f.DisplayLatencyMs is double d) { dispSum += d; dispN++; }
            if (f.ClickToPhotonMs is double k) { c2pSum += k; c2pN++; }
        }

        var sorted = (double[])times.Clone();
        Array.Sort(sorted);
        double median = MedianSorted(sorted);
        double p99 = PercentileSorted(sorted, 99);
        double p999 = PercentileSorted(sorted, 99.9);
        double avgFt = sum / n;

        int stutters = 0;
        if (median > 0)
        {
            double threshold = median * StutterFactor;
            for (int i = 0; i < n; i++)
                if (times[i] > threshold) stutters++;
        }

        double? avgGpu = gpuN > 0 ? gpuSum / gpuN : null;
        double? avgCpu = cpuN > 0 ? cpuSum / cpuN : null;

        double variance = 0;
        for (int i = 0; i < n; i++)
        {
            double d = times[i] - avgFt;
            variance += d * d;
        }
        double stdDev = n > 1 ? Math.Sqrt(variance / n) : 0;

        double covered = n > 1 ? Math.Max(0, frames[n - 1].Time - frames[0].Time) : 0;
        return new FrameStatsSummary
        {
            FrameCount = n,
            DroppedCount = dropped,
            CoveredSeconds = covered,
            AvgFps = avgFt > 0 ? 1000.0 / avgFt : 0,
            AvgFrameTimeMs = avgFt,
            MedianFrameTimeMs = median,
            OnePercentLowFps = p99 > 0 ? 1000.0 / p99 : 0,
            PointOnePercentLowFps = p999 > 0 ? 1000.0 / p999 : 0,
            P99FrameTimeMs = p99,
            MaxFrameTimeMs = max,
            StutterCount = stutters,
            FrameTimeStdDevMs = stdDev,
            // The limiter estimate needs signals from outside the frame list (GPU load, refresh):
            // MonitorService fills Bound / BoundEvidence via BoundEstimator.
            Bound = BoundKind.Unknown,
            BoundEvidence = "",
            AvgGpuBusyMs = avgGpu,
            AvgCpuBusyMs = avgCpu,
            AvgDisplayLatencyMs = dispN > 0 ? dispSum / dispN : null,
            AvgClickToPhotonMs = c2pN > 0 ? c2pSum / c2pN : null,
            ClickToPhotonSamples = c2pN,
            LastFps = frames[n - 1].Fps
        };
    }

    /// <summary>Nearest-rank percentile on an ascending array; 0 for an empty array.</summary>
    public static double PercentileSorted(double[] sorted, double p)
    {
        if (sorted == null || sorted.Length == 0) return 0;
        p = Math.Clamp(p, 0, 100);
        // Round away binary noise first: 99.9 / 100 * 1000 is 999.0000000000001, which Ceiling would push to rank 1000.
        int rank = (int)Math.Ceiling(Math.Round(p / 100.0 * sorted.Length, 9));
        int idx = Math.Clamp(rank - 1, 0, sorted.Length - 1);
        return sorted[idx];
    }

    private static double MedianSorted(double[] sorted)
    {
        int n = sorted.Length;
        if (n == 0) return 0;
        if ((n & 1) == 1) return sorted[n / 2];
        return (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }
}
