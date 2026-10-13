using System.Globalization;

namespace Visor.Services.Optimize;

/// <summary>One measured value ("fps", "frametimeMs", "1%low") at a point in time.</summary>
public readonly record struct MetricSample(string Name, double Value, DateTime Utc);

public enum ExperimentPhase
{
    /// <summary>Collecting the baseline; the action has not been applied yet.</summary>
    Before,
    /// <summary>The action was applied; samples now go to the "after" set.</summary>
    After
}

/// <summary>Per-metric comparison. Deltas are null while either side has fewer than the required samples.</summary>
public sealed record MetricDelta(
    string Name,
    int BeforeCount, int AfterCount,
    double? BeforeMean, double? AfterMean,
    double? BeforeMedian, double? AfterMedian,
    double? Delta, double? DeltaPercent,
    bool InsufficientData)
{
    /// <summary>"+3.2 (+4.1 %)" / "n/a" — invariant culture, for logs and tests; the UI formats its own.</summary>
    public string Summary => InsufficientData || Delta == null
        ? "n/a"
        : string.Format(CultureInfo.InvariantCulture, "{0:+0.0;-0.0;0.0} ({1:+0.0;-0.0;0.0} %)", Delta, DeltaPercent ?? 0);
}

public sealed record ExperimentReport(
    string ActionId,
    DateTime? StartedUtc,
    DateTime? AppliedUtc,
    DateTime? EndedUtc,
    int MinSamples,
    IReadOnlyList<MetricDelta> Metrics)
{
    /// <summary>True when no metric has enough samples on both sides.</summary>
    public bool InsufficientData => Metrics.Count == 0 || Metrics.All(m => m.InsufficientData);
}

/// <summary>
/// Pure before/after bookkeeping for one Optimize action: record samples, mark the apply moment, compute
/// deltas. Never claims an improvement — the UI shows numbers with "insufficient data" when the window is
/// too short. The Monitor stage (PresentMon) will feed FPS / frame time; any metric name works.
/// </summary>
public sealed class OptimizationExperiment
{
    public const int DefaultMinSamples = 10;

    private readonly List<MetricSample> _before = new();
    private readonly List<MetricSample> _after = new();
    private readonly object _gate = new();

    public OptimizationExperiment(string actionId, int minSamples = DefaultMinSamples)
    {
        if (string.IsNullOrWhiteSpace(actionId)) throw new ArgumentException("action id required", nameof(actionId));
        if (minSamples < 1) throw new ArgumentOutOfRangeException(nameof(minSamples));
        ActionId = actionId;
        MinSamples = minSamples;
    }

    public string ActionId { get; }
    public int MinSamples { get; }
    public ExperimentPhase Phase { get; private set; } = ExperimentPhase.Before;
    public DateTime? StartedUtc { get; private set; }
    public DateTime? AppliedUtc { get; private set; }
    public DateTime? EndedUtc { get; private set; }

    public int BeforeCount { get { lock (_gate) return _before.Count; } }
    public int AfterCount { get { lock (_gate) return _after.Count; } }

    /// <summary>Adds a sample to the current phase. Ignored after <see cref="End"/>; NaN / infinity are dropped.</summary>
    public bool Record(MetricSample sample)
    {
        if (string.IsNullOrWhiteSpace(sample.Name)) return false;
        if (double.IsNaN(sample.Value) || double.IsInfinity(sample.Value)) return false;
        lock (_gate)
        {
            if (EndedUtc != null) return false;
            StartedUtc ??= sample.Utc;
            (Phase == ExperimentPhase.Before ? _before : _after).Add(sample);
            return true;
        }
    }

    public bool Record(string name, double value, DateTime utc) => Record(new MetricSample(name, value, utc));

    /// <summary>Call right after the SafeAction applied: later samples count as "after".</summary>
    public void MarkApplied(DateTime utc)
    {
        lock (_gate)
        {
            if (Phase == ExperimentPhase.After) return;
            Phase = ExperimentPhase.After;
            AppliedUtc = utc;
            StartedUtc ??= utc;
        }
    }

    /// <summary>Stops recording (the game exited / the user closed the panel).</summary>
    public void End(DateTime utc)
    {
        lock (_gate) EndedUtc ??= utc;
    }

    public ExperimentReport Compute()
    {
        List<MetricSample> before, after;
        lock (_gate)
        {
            before = _before.ToList();
            after = _after.ToList();
        }
        var names = before.Select(s => s.Name).Concat(after.Select(s => s.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        var metrics = new List<MetricDelta>();
        foreach (var name in names)
        {
            var b = before.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(s => s.Value).ToList();
            var a = after.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(s => s.Value).ToList();
            bool insufficient = b.Count < MinSamples || a.Count < MinSamples;
            double? bMean = Mean(b), aMean = Mean(a);
            double? delta = insufficient || bMean == null || aMean == null ? null : aMean - bMean;
            double? deltaPct = delta == null || bMean == null || Math.Abs(bMean.Value) < 1e-9 ? null : delta / Math.Abs(bMean.Value) * 100.0;
            metrics.Add(new MetricDelta(name, b.Count, a.Count, bMean, aMean, Median(b), Median(a), delta, deltaPct, insufficient));
        }
        return new ExperimentReport(ActionId, StartedUtc, AppliedUtc, EndedUtc, MinSamples, metrics);
    }

    public static double? Mean(IReadOnlyList<double> values) => values.Count == 0 ? null : values.Average();

    public static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
