using Visor.Services.Optimize;

namespace Visor.Services;

/// <summary>Metric names the monitor exports (one <see cref="MetricSample"/> per name per second).</summary>
public static class MonitorMetricNames
{
    public const string Fps = "fps";
    public const string OnePercentLow = "1%low";
    public const string PointOnePercentLow = "0.1%low";
    public const string FrameTimeMs = "frametimeMs";
    public const string FrameTimeP99Ms = "frametimeP99Ms";
    public const string Stutters = "stutters";
    /// <summary>Only emitted when PresentMon attributed input to at least one frame in the window.</summary>
    public const string ClickToPhotonMs = "clickToPhotonMs";
}

/// <summary>
/// Receiver of per-second monitor samples (<see cref="MetricSample"/> from
/// <c>Services\System\Optimize</c>). Called on a timer thread; implementations must be quick
/// and must not throw. Only measured values are exported — a metric PresentMon did not report
/// is simply absent, never zero.
/// </summary>
public interface IMetricSink
{
    void OnSample(MetricSample sample);
}

/// <summary>Feeds an <see cref="OptimizationExperiment"/> (before / after an Optimize action) from the monitor.</summary>
public sealed class ExperimentMetricSink : IMetricSink
{
    public ExperimentMetricSink(OptimizationExperiment experiment)
    {
        Experiment = experiment ?? throw new ArgumentNullException(nameof(experiment));
    }

    public OptimizationExperiment Experiment { get; }

    public void OnSample(MetricSample sample) => Experiment.Record(sample);
}
