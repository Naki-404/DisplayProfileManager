using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class MonitorFrameStatsTests
{
    private static FrameRecord F(double time, double ft, double? gpu = null, double? cpu = null, double? c2p = null, bool dropped = false) =>
        new(time, ft, gpu, cpu, null, c2p, dropped);

    /// <summary>1000 frames: 990 × 10 ms, 9 × 50 ms, 1 × 100 ms.</summary>
    private static List<FrameRecord> KnownDistribution()
    {
        var list = new List<FrameRecord>();
        double t = 0;
        for (int i = 0; i < 1000; i++)
        {
            double ft = i < 990 ? 10 : (i < 999 ? 50 : 100);
            t += ft / 1000;
            list.Add(F(t, ft));
        }
        return list;
    }

    [Fact]
    public void Percentiles_on_known_data()
    {
        var s = FrameStats.Compute(KnownDistribution());
        Assert.Equal(1000, s.FrameCount);
        // Nearest rank: p99 of 1000 → rank 990 → 10 ms (the 990th sorted value); p99.9 → rank 999 → 50 ms.
        Assert.Equal(10, s.P99FrameTimeMs);
        Assert.Equal(100, s.OnePercentLowFps, 6);
        Assert.Equal(20, s.PointOnePercentLowFps, 6);
        Assert.Equal(100, s.MaxFrameTimeMs);
        Assert.Equal(10, s.MedianFrameTimeMs);
        // avg frame time = (9900 + 450 + 100) / 1000 = 10.45 ms → 95.69 fps
        Assert.Equal(10.45, s.AvgFrameTimeMs, 6);
        Assert.Equal(1000.0 / 10.45, s.AvgFps, 6);
    }

    [Fact]
    public void PercentileSorted_nearest_rank_edges()
    {
        var v = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();
        Assert.Equal(19, FrameStats.PercentileSorted(v, 95));
        Assert.Equal(20, FrameStats.PercentileSorted(v, 99));
        Assert.Equal(10, FrameStats.PercentileSorted(v, 50));
        Assert.Equal(1, FrameStats.PercentileSorted(v, 0));
        Assert.Equal(20, FrameStats.PercentileSorted(v, 100));
        Assert.Equal(0, FrameStats.PercentileSorted(Array.Empty<double>(), 99));
    }

    [Fact]
    public void One_percent_low_is_fps_of_p99_frame_time()
    {
        // 100 frames at 10 ms, one at 40 ms → p99 (rank 100 of 101) = 10 ms? rank = ceil(0.99*101)=100 → 10 ms.
        var frames = Enumerable.Range(1, 100).Select(i => F(i * 0.01, 10)).ToList();
        frames.Add(F(1.05, 40));
        var s = FrameStats.Compute(frames);
        Assert.Equal(100, s.OnePercentLowFps, 6);
        // 0.1 % low: rank ceil(0.999*101)=101 → 40 ms → 25 fps
        Assert.Equal(25, s.PointOnePercentLowFps, 6);
    }

    [Fact]
    public void Stutter_is_frame_over_twice_median()
    {
        var frames = new List<FrameRecord>();
        double t = 0;
        for (int i = 0; i < 50; i++) { t += 0.016; frames.Add(F(t, 16)); }
        frames.Add(F(t += 0.033, 33));   // 2.06× → stutter
        frames.Add(F(t += 0.032, 32));   // exactly 2× → not a stutter (strictly greater)
        frames.Add(F(t += 0.1, 100));    // stutter
        var s = FrameStats.Compute(frames);
        Assert.Equal(16, s.MedianFrameTimeMs);
        Assert.Equal(2, s.StutterCount);
    }

    [Fact]
    public void Dropped_frames_are_counted()
    {
        var frames = new List<FrameRecord> { F(0.01, 10), F(0.02, 10, dropped: true), F(0.03, 10, dropped: true) };
        Assert.Equal(2, FrameStats.Compute(frames).DroppedCount);
    }

    [Fact]
    public void Bound_gpu_when_gpu_busy_dominates()
    {
        var frames = Enumerable.Range(1, 60).Select(i => F(i * 0.0166, 16.6, gpu: 15.5, cpu: 6)).ToList();
        var s = FrameStats.Compute(frames);
        Assert.Equal(BoundKind.LikelyGpu, s.Bound);
        Assert.Contains("GPU busy", s.BoundEvidence);
        Assert.Contains("93", s.BoundEvidence);
    }

    [Fact]
    public void Bound_cpu_when_gpu_idles_and_cpu_busy_dominates()
    {
        var frames = Enumerable.Range(1, 60).Select(i => F(i * 0.02, 20, gpu: 6, cpu: 18)).ToList();
        var s = FrameStats.Compute(frames);
        Assert.Equal(BoundKind.LikelyCpu, s.Bound);
        Assert.Contains("CPU busy", s.BoundEvidence);
        Assert.Contains("GPU busy", s.BoundEvidence);
    }

    [Fact]
    public void Bound_capped_when_both_idle()
    {
        var frames = Enumerable.Range(1, 60).Select(i => F(i * 0.0166, 16.6, gpu: 5, cpu: 4)).ToList();
        Assert.Equal(BoundKind.LikelyCappedOrBalanced, FrameStats.Compute(frames).Bound);
    }

    [Fact]
    public void Bound_unknown_without_gpu_data_or_too_few_frames()
    {
        var noGpu = Enumerable.Range(1, 60).Select(i => F(i * 0.0166, 16.6, cpu: 4)).ToList();
        var s = FrameStats.Compute(noGpu);
        Assert.Equal(BoundKind.Unknown, s.Bound);
        Assert.Equal("", s.BoundEvidence);
        Assert.Null(s.AvgGpuBusyMs);
        Assert.Equal(4, s.AvgCpuBusyMs);

        var few = Enumerable.Range(1, 10).Select(i => F(i * 0.0166, 16.6, gpu: 16, cpu: 4)).ToList();
        Assert.Equal(BoundKind.Unknown, FrameStats.Compute(few).Bound);
    }

    [Fact]
    public void Bound_1x_without_cpu_column_uses_gpu_only()
    {
        var frames = Enumerable.Range(1, 60).Select(i => F(i * 0.02, 20, gpu: 5)).ToList();
        var s = FrameStats.Compute(frames);
        Assert.Equal(BoundKind.LikelyCpu, s.Bound);
        Assert.Contains("no CPUBusy", s.BoundEvidence);
    }

    [Fact]
    public void ClickToPhoton_average_only_over_frames_that_have_it()
    {
        var frames = new List<FrameRecord> { F(0.01, 10, c2p: 30), F(0.02, 10), F(0.03, 10, c2p: 50), F(0.04, 10) };
        var s = FrameStats.Compute(frames);
        Assert.Equal(40, s.AvgClickToPhotonMs);
        Assert.Equal(2, s.ClickToPhotonSamples);
        Assert.Null(FrameStats.Compute(new[] { F(0.01, 10) }).AvgClickToPhotonMs);
    }

    [Fact]
    public void Window_trims_by_frame_time_not_count()
    {
        var stats = new FrameStats(windowSeconds: 2);
        for (int i = 0; i < 100; i++) stats.Add(F(i * 0.125, 125));  // 12.4 s of 8 fps (0.125 is exact in binary)
        Assert.Equal(17, stats.Count);                                // 12.375 - 2 = 10.375 → frames at 10.375..12.375 inclusive
        Assert.Equal(100, stats.TotalAdded);
        var s = stats.Snapshot();
        Assert.Equal(17, s.FrameCount);
        Assert.Equal(2.0, s.CoveredSeconds, 6);
        Assert.Equal(8, s.AvgFps, 6);
        Assert.Equal(8, s.LastFps, 6);
        stats.Clear();
        Assert.Equal(0, stats.Count);
        Assert.Same(FrameStatsSummary.Empty, stats.Snapshot());
    }

    [Fact]
    public void Max_frames_cap_applies()
    {
        var stats = new FrameStats(windowSeconds: 1000, maxFrames: 5);
        for (int i = 0; i < 10; i++) stats.Add(F(i, 1));
        Assert.Equal(5, stats.Count);
        Assert.Equal(5, stats.Frames()[0].Time);
    }

    [Fact]
    public void Empty_and_invalid_input()
    {
        Assert.Same(FrameStatsSummary.Empty, FrameStats.Compute(Array.Empty<FrameRecord>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameStats(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameStats(1, 0));
        var zero = FrameStats.Compute(new[] { F(0, 0) });
        Assert.Equal(0, zero.AvgFps);
        Assert.Equal(0, zero.OnePercentLowFps);
        Assert.Equal(0, zero.StutterCount);
    }

    [Fact]
    public void ToMetricSamples_exports_named_measured_values_only()
    {
        var s = FrameStats.Compute(KnownDistribution());
        var when = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var m = s.ToMetricSamples(when);
        Assert.Equal(6, m.Count);                                   // no ClickToPhoton in the data -> not exported
        Assert.All(m, x => Assert.Equal(when, x.Utc));
        var byName = m.ToDictionary(x => x.Name, x => x.Value);
        Assert.Equal(s.AvgFps, byName[MonitorMetricNames.Fps]);
        Assert.Equal(s.OnePercentLowFps, byName[MonitorMetricNames.OnePercentLow]);
        Assert.Equal(s.PointOnePercentLowFps, byName[MonitorMetricNames.PointOnePercentLow]);
        Assert.Equal(s.AvgFrameTimeMs, byName[MonitorMetricNames.FrameTimeMs]);
        Assert.Equal(s.P99FrameTimeMs, byName[MonitorMetricNames.FrameTimeP99Ms]);
        Assert.Equal(s.StutterCount, byName[MonitorMetricNames.Stutters]);
        Assert.False(byName.ContainsKey(MonitorMetricNames.ClickToPhotonMs));

        var withInput = FrameStats.Compute(new[] { F(0.01, 10, c2p: 30), F(0.02, 10, c2p: 50) }).ToMetricSamples(when);
        Assert.Equal(40, withInput.Single(x => x.Name == MonitorMetricNames.ClickToPhotonMs).Value);
        Assert.Empty(FrameStatsSummary.Empty.ToMetricSamples(when));
    }

    [Fact]
    public void ExperimentMetricSink_records_into_the_experiment()
    {
        var exp = new Visor.Services.Optimize.OptimizationExperiment("test", minSamples: 1);
        var sink = new ExperimentMetricSink(exp);
        foreach (var sample in FrameStats.Compute(KnownDistribution()).ToMetricSamples()) sink.OnSample(sample);
        Assert.Equal(6, exp.BeforeCount);
        Assert.Throws<ArgumentNullException>(() => new ExperimentMetricSink(null!));
    }
}
