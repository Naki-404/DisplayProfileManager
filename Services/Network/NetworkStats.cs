namespace Visor.Services;

/// <summary>
/// Pure latency / loss / jitter math for probe samples (null = timeout / no reply).
/// Jitter = mean absolute consecutive RTT delta (same idea as RFC 3550 §6.4.1 simplified).
/// </summary>
public static class NetworkStats
{
    public sealed record Summary(
        int Sent,
        int Received,
        int Lost,
        double LossPercent,
        double? MinMs,
        double? MaxMs,
        double? AvgMs,
        double? MedianMs,
        double? JitterMs);

    public static Summary Summarize(IReadOnlyList<double?> samples)
    {
        int sent = samples?.Count ?? 0;
        if (sent == 0)
            return new Summary(0, 0, 0, 0, null, null, null, null, null);

        var ok = new List<double>(sent);
        foreach (var s in samples!)
        {
            if (s is >= 0)
                ok.Add(s.Value);
        }

        int received = ok.Count;
        int lost = sent - received;
        double lossPct = sent == 0 ? 0 : 100.0 * lost / sent;

        if (received == 0)
            return new Summary(sent, 0, lost, lossPct, null, null, null, null, null);

        var ordered = ok.ToList(); // success order preserved for jitter
        ok.Sort();
        double min = ok[0];
        double max = ok[^1];
        double avg = ok.Average();
        double median = MedianSorted(ok);
        double? jitter = Jitter(ordered);

        return new Summary(sent, received, lost, lossPct, min, max, avg, median, jitter);
    }

    /// <summary>Mean |RTT[i] − RTT[i−1]| over consecutive successful samples; null if &lt; 2 samples.</summary>
    public static double? Jitter(IReadOnlyList<double> successiveRtts)
    {
        if (successiveRtts == null || successiveRtts.Count < 2) return null;
        double sum = 0;
        for (int i = 1; i < successiveRtts.Count; i++)
            sum += Math.Abs(successiveRtts[i] - successiveRtts[i - 1]);
        return sum / (successiveRtts.Count - 1);
    }

    /// <summary>
    /// Nearest-rank percentile (p in 0..100) over an ascending list; null when empty.
    /// p95 of 20 samples = the 19th value — no interpolation, so a single outlier maps to p100 only.
    /// </summary>
    public static double? PercentileSorted(IReadOnlyList<double> sorted, double p)
    {
        if (sorted == null || sorted.Count == 0) return null;
        p = Math.Clamp(p, 0, 100);
        int rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
        int idx = Math.Clamp(rank - 1, 0, sorted.Count - 1);
        return sorted[idx];
    }

    /// <summary>Convenience: sorts a copy and applies <see cref="PercentileSorted"/>.</summary>
    public static double? Percentile(IEnumerable<double> values, double p)
    {
        var list = values?.ToList() ?? new List<double>();
        list.Sort();
        return PercentileSorted(list, p);
    }

    private static double MedianSorted(IReadOnlyList<double> sorted)
    {
        int n = sorted.Count;
        if ((n & 1) == 1) return sorted[n / 2];
        return (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }
}
