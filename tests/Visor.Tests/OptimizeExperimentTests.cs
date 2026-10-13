using Visor.Services.Optimize;
using Xunit;

namespace Visor.Tests;

public class OptimizeExperimentTests
{
    private static readonly DateTime T0 = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    private static OptimizationExperiment Filled(int before, int after, int min = 3, double beforeValue = 100, double afterValue = 110)
    {
        var e = new OptimizationExperiment("gameBar.off", min);
        for (int i = 0; i < before; i++) e.Record("fps", beforeValue + i % 2, T0.AddSeconds(i));
        e.MarkApplied(T0.AddSeconds(before));
        for (int i = 0; i < after; i++) e.Record("fps", afterValue + i % 2, T0.AddSeconds(before + 1 + i));
        return e;
    }

    [Fact]
    public void Ctor_validates()
    {
        Assert.Throws<ArgumentException>(() => new OptimizationExperiment(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OptimizationExperiment("x", 0));
        Assert.Equal(OptimizationExperiment.DefaultMinSamples, new OptimizationExperiment("x").MinSamples);
    }

    [Fact]
    public void Samples_go_to_the_phase_in_effect()
    {
        var e = new OptimizationExperiment("x", 2);
        Assert.Equal(ExperimentPhase.Before, e.Phase);
        Assert.Null(e.StartedUtc);
        Assert.True(e.Record("fps", 60, T0));
        Assert.Equal(T0, e.StartedUtc);
        e.MarkApplied(T0.AddSeconds(5));
        Assert.Equal(ExperimentPhase.After, e.Phase);
        Assert.True(e.Record("fps", 61, T0.AddSeconds(6)));
        e.MarkApplied(T0.AddSeconds(9)); // second call ignored
        Assert.Equal(T0.AddSeconds(5), e.AppliedUtc);
        Assert.Equal(1, e.BeforeCount);
        Assert.Equal(1, e.AfterCount);
    }

    [Fact]
    public void Invalid_samples_and_samples_after_End_are_dropped()
    {
        var e = new OptimizationExperiment("x", 1);
        Assert.False(e.Record("", 1, T0));
        Assert.False(e.Record("fps", double.NaN, T0));
        Assert.False(e.Record("fps", double.PositiveInfinity, T0));
        Assert.True(e.Record("fps", 1, T0));
        e.End(T0.AddSeconds(1));
        Assert.False(e.Record("fps", 2, T0.AddSeconds(2)));
        Assert.Equal(T0.AddSeconds(1), e.EndedUtc);
        e.End(T0.AddSeconds(5));
        Assert.Equal(T0.AddSeconds(1), e.EndedUtc);
    }

    [Fact]
    public void Compute_flags_insufficient_data_until_both_sides_have_min_samples()
    {
        Assert.True(Filled(2, 5).Compute().InsufficientData);
        Assert.True(Filled(5, 2).Compute().InsufficientData);
        Assert.True(new OptimizationExperiment("x", 1).Compute().InsufficientData);

        var r = Filled(3, 3).Compute();
        Assert.False(r.InsufficientData);
        var m = Assert.Single(r.Metrics);
        Assert.Equal("fps", m.Name);
        Assert.Equal(3, m.BeforeCount);
        Assert.Equal(3, m.AfterCount);
        Assert.False(m.InsufficientData);
    }

    [Fact]
    public void Compute_reports_mean_median_delta_and_percent()
    {
        var e = new OptimizationExperiment("x", 3);
        foreach (var v in new[] { 100.0, 110, 90 }) e.Record("fps", v, T0);
        e.MarkApplied(T0);
        foreach (var v in new[] { 120.0, 100, 110, 130 }) e.Record("fps", v, T0);
        var m = Assert.Single(e.Compute().Metrics);
        Assert.Equal(100, m.BeforeMean);
        Assert.Equal(115, m.AfterMean);
        Assert.Equal(100, m.BeforeMedian);
        Assert.Equal(115, m.AfterMedian);
        Assert.Equal(15, m.Delta);
        Assert.Equal(15, m.DeltaPercent);
        Assert.Equal("+15.0 (+15.0 %)", m.Summary);
    }

    [Fact]
    public void Insufficient_metric_still_reports_counts_but_no_delta()
    {
        var e = Filled(3, 1, min: 3);
        var m = Assert.Single(e.Compute().Metrics);
        Assert.True(m.InsufficientData);
        Assert.Null(m.Delta);
        Assert.Null(m.DeltaPercent);
        Assert.NotNull(m.BeforeMean);
        Assert.NotNull(m.AfterMean);
        Assert.Equal("n/a", m.Summary);
    }

    [Fact]
    public void Metrics_are_grouped_by_name_case_insensitively_and_sorted()
    {
        var e = new OptimizationExperiment("x", 1);
        e.Record("FPS", 50, T0);
        e.Record("frametimeMs", 20, T0);
        e.MarkApplied(T0);
        e.Record("fps", 60, T0);
        e.Record("frametimeMs", 16, T0);
        var r = e.Compute();
        Assert.Equal(new[] { "FPS", "frametimeMs" }, r.Metrics.Select(m => m.Name).ToArray());
        Assert.Equal(10, r.Metrics[0].Delta);
        Assert.Equal(-4, r.Metrics[1].Delta);
        Assert.Equal(-20, r.Metrics[1].DeltaPercent);
        Assert.Equal("-4.0 (-20.0 %)", r.Metrics[1].Summary);
        Assert.Equal("x", r.ActionId);
        Assert.Equal(T0, r.AppliedUtc);
    }

    [Fact]
    public void Zero_baseline_has_no_percent()
    {
        var e = new OptimizationExperiment("x", 1);
        e.Record("drops", 0, T0);
        e.MarkApplied(T0);
        e.Record("drops", 3, T0);
        var m = Assert.Single(e.Compute().Metrics);
        Assert.Equal(3, m.Delta);
        Assert.Null(m.DeltaPercent);
    }

    [Fact]
    public void Median_and_mean_helpers()
    {
        Assert.Null(OptimizationExperiment.Median(Array.Empty<double>()));
        Assert.Null(OptimizationExperiment.Mean(Array.Empty<double>()));
        Assert.Equal(2, OptimizationExperiment.Median(new[] { 3.0, 1, 2 }));
        Assert.Equal(2.5, OptimizationExperiment.Median(new[] { 4.0, 1, 2, 3 }));
    }
}
