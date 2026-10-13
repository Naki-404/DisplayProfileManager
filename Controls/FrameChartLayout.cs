namespace Visor.Controls;

/// <summary>
/// Pure layout math for <see cref="FrameChart"/> (no WPF types) so the axis / scale rules can be unit-tested.
/// Values are frame times in milliseconds; the secondary axis is the FPS equivalent (1000 / ms).
/// </summary>
public static class FrameChartLayout
{
    /// <summary>Smallest Y range shown even for a very fast game (ms).</summary>
    public const double MinRangeMs = 4;

    /// <summary>Candidate "nice" axis maxima — the first one ≥ the requested value wins (ms).</summary>
    private static readonly double[] NiceSteps = { 4, 5, 8, 10, 12, 16, 20, 25, 33, 40, 50, 66, 80, 100, 125, 150, 200, 250, 333, 500, 1000 };

    /// <summary>Rounds a frame-time range up to a readable axis maximum; never below <see cref="MinRangeMs"/>.</summary>
    public static double NiceCeiling(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return MinRangeMs;
        foreach (var step in NiceSteps)
            if (step >= value) return step;
        // Beyond the table: next multiple of 1000 ms.
        return Math.Ceiling(value / 1000.0) * 1000.0;
    }

    /// <summary>
    /// Axis maximum for a series: enough headroom over the p99 line and the bulk of the frames, but a
    /// single huge hitch must not flatten the rest of the chart — the top is capped at 3× p99 (or 3× the
    /// median when there is no p99). Frames above the cap are drawn clipped at the top edge.
    /// </summary>
    public static double AxisMax(IReadOnlyList<double> frameTimesMs, double p99Ms)
    {
        double max = 0, p99 = p99Ms > 0 ? p99Ms : 0;
        int n = frameTimesMs?.Count ?? 0;
        for (int i = 0; i < n; i++)
        {
            double v = frameTimesMs![i];
            if (v > max) max = v;
        }
        if (n == 0 && p99 <= 0) return MinRangeMs;
        double reference = p99 > 0 ? p99 : max;
        double cap = reference > 0 ? reference * 3 : max;
        double wanted = Math.Min(Math.Max(max, p99 * 1.15), Math.Max(cap, MinRangeMs));
        return NiceCeiling(Math.Max(wanted, MinRangeMs));
    }

    /// <summary>Y pixel (0 = top) for a frame time, clipped into the plot height.</summary>
    public static double MapY(double ms, double axisMaxMs, double plotHeight)
    {
        if (axisMaxMs <= 0 || plotHeight <= 0) return plotHeight;
        double f = Math.Clamp(ms / axisMaxMs, 0, 1);
        return plotHeight - f * plotHeight;
    }

    /// <summary>X pixel for sample <paramref name="index"/> of <paramref name="count"/> across the plot width.</summary>
    public static double MapX(int index, int count, double plotWidth)
    {
        if (count <= 1 || plotWidth <= 0) return 0;
        return index * plotWidth / (count - 1);
    }

    /// <summary>Gridline values (ms) from bottom to top: 0, 1/4, 1/2, 3/4 and the axis maximum.</summary>
    public static double[] GridLines(double axisMaxMs) =>
        new[] { 0, axisMaxMs * 0.25, axisMaxMs * 0.5, axisMaxMs * 0.75, axisMaxMs };

    /// <summary>FPS label for a gridline ("144" / "∞" is never shown: 0 ms yields an empty label).</summary>
    public static string FpsLabel(double ms)
    {
        if (ms <= 0) return "";
        double fps = 1000.0 / ms;
        return fps >= 100 ? Math.Round(fps).ToString(System.Globalization.CultureInfo.InvariantCulture)
                          : fps.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Millisecond label for a gridline.</summary>
    public static string MsLabel(double ms) =>
        ms >= 100 ? Math.Round(ms).ToString(System.Globalization.CultureInfo.InvariantCulture)
                  : ms.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
