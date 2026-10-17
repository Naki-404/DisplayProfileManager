using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class MonitorBoundTests
{
    private static (BoundKind, string) C(double fps, double cv, double? gpu, double? hz, int frames = 300) =>
        BoundEstimator.Classify(new BoundInputs(frames, fps, cv, gpu, hz));

    [Fact]
    public void Gpu_at_the_ceiling_is_likely_gpu_bound_with_numbers()
    {
        var (b, e) = C(88.4, 0.08, 97, 144);
        Assert.Equal(BoundKind.LikelyGpu, b);
        Assert.Equal("GPU load 97 % at 88 FPS", e);
        Assert.Equal(BoundKind.LikelyGpu, C(60, 0.01, 95, 60).Item1);   // GPU rule comes first, even at refresh
    }

    [Fact]
    public void Fps_pinned_at_refresh_with_low_variance_is_likely_capped()
    {
        var (b, e) = C(143.6, 0.03, 70, 144);
        Assert.Equal(BoundKind.LikelyCappedOrBalanced, b);
        Assert.Contains("144 FPS ≈ 144 Hz refresh", e);
        Assert.Contains("variation 3 %", e);
        Assert.Contains("GPU load 70 %", e);
        // Works without a GPU reading too (V-Sync is visible from the frame times alone).
        Assert.Equal(BoundKind.LikelyCappedOrBalanced, C(59.9, 0.02, null, 60).Item1);
        Assert.Contains("GPU load unavailable", C(59.9, 0.02, null, 60).Item2);
    }

    [Fact]
    public void Near_refresh_but_noisy_is_not_called_capped()
    {
        // 143 FPS with 25 % variation on a 144 Hz screen and 50 % GPU: not "well below" refresh → balanced, not CPU.
        Assert.Equal(BoundKind.LikelyCappedOrBalanced, C(143, 0.25, 50, 144).Item1);
        Assert.DoesNotContain("≈", C(143, 0.25, 50, 144).Item2);
    }

    [Fact]
    public void Idle_gpu_well_below_refresh_is_likely_cpu_bound()
    {
        var (b, e) = C(72, 0.12, 60, 144);
        Assert.Equal(BoundKind.LikelyCpu, b);
        Assert.Equal("GPU load 60 % at 72 FPS, well below 144 Hz refresh, frame-time variation 12 %", e);
        Assert.Equal(BoundKind.LikelyCpu, C(100, 0.2, 75, 144).Item1);   // 75 % is inclusive
    }

    [Fact]
    public void Very_steady_pace_below_refresh_with_idle_gpu_is_a_limiter()
    {
        var (b, e) = C(120, 0.02, 55, 165);
        Assert.Equal(BoundKind.LikelyCappedOrBalanced, b);
        Assert.Contains("likely an FPS limiter", e);
    }

    [Fact]
    public void Middle_gpu_load_is_balanced()
    {
        var (b, e) = C(90, 0.1, 85, 144);
        Assert.Equal(BoundKind.LikelyCappedOrBalanced, b);
        Assert.Contains("neither GPU nor CPU clearly saturated", e);
    }

    [Fact]
    public void Unknown_without_gpu_load_or_enough_frames()
    {
        var (b, e) = C(90, 0.1, null, 144);
        Assert.Equal(BoundKind.Unknown, b);
        Assert.Contains("GPU load unavailable", e);   // the numbers still explain why
        Assert.Equal((BoundKind.Unknown, ""), C(90, 0.1, 99, 144, frames: 29));
        Assert.Equal((BoundKind.Unknown, ""), C(0, 0, 99, 144));
        Assert.Equal((BoundKind.Unknown, ""), C(double.NaN, 0, 99, 144));
    }

    [Fact]
    public void Unknown_refresh_never_claims_cpu_bound()
    {
        var (b, e) = C(72, 0.12, 40, null);
        Assert.Equal(BoundKind.LikelyCappedOrBalanced, b);
        Assert.Contains("refresh unknown", e);
        Assert.Equal(BoundKind.LikelyCappedOrBalanced, C(72, 0.12, 40, 0.5).Item1);   // absurd refresh = unknown
    }

    [Fact]
    public void Service_adds_the_estimate_to_a_snapshot_and_leaves_empty_ones_alone()
    {
        var frames = Enumerable.Range(1, 120).Select(i => new FrameRecord(i / 60.0, 1000.0 / 60, null, null, null, null, false)).ToList();
        var s = MonitorService.WithBound(FrameStats.Compute(frames), avgGpuPercent: 40, refreshHz: 60);
        Assert.Equal(BoundKind.LikelyCappedOrBalanced, s.Bound);
        Assert.Contains("60 FPS ≈ 60 Hz refresh", s.BoundEvidence);
        Assert.Same(FrameStatsSummary.Empty, MonitorService.WithBound(FrameStatsSummary.Empty, 99, 60));
    }
}
