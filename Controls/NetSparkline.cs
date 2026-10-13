using System.Windows;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace Visor.Controls;

/// <summary>
/// Static sparkline for the Network page: a primary series (area + line) and an optional secondary line,
/// drawn in OnRender with theme brushes. Null samples are gaps (lost pings) and get a small marker on the
/// baseline. No animation; newest sample on the right. With <see cref="Capacity"/> &gt; 0 the x scale is
/// fixed so the chart scrolls instead of stretching while the ring fills up.
/// </summary>
public sealed class NetSparkline : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<double?>), typeof(NetSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((NetSparkline)d).UpdateAccessibleName()));

    public static readonly DependencyProperty SecondarySeriesProperty = DependencyProperty.Register(
        nameof(SecondarySeries), typeof(IReadOnlyList<double?>), typeof(NetSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Fixed y maximum; NaN / 0 = scale to the largest sample.</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(NetSparkline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Samples that span the full width (0 = fit whatever is in the series).</summary>
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(NetSparkline),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(WpfBrush), typeof(NetSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryStrokeProperty = DependencyProperty.Register(
        nameof(SecondaryStroke), typeof(WpfBrush), typeof(NetSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(WpfBrush), typeof(NetSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LostBrushProperty = DependencyProperty.Register(
        nameof(LostBrush), typeof(WpfBrush), typeof(NetSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(NetSparkline),
        new PropertyMetadata(null, (d, _) => ((NetSparkline)d).UpdateAccessibleName()));

    /// <summary>Unit appended to the accessible name ("ms", "Mbps").</summary>
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(NetSparkline),
        new PropertyMetadata(null, (d, _) => ((NetSparkline)d).UpdateAccessibleName()));

    public NetSparkline()
    {
        Height = 56;
        MinWidth = 80;
        SetResourceReference(StrokeProperty, "AccentBrush");
        SetResourceReference(SecondaryStrokeProperty, "AccentAltBrush");
        SetResourceReference(TrackBrushProperty, "TrackBrush");
        SetResourceReference(LostBrushProperty, "DangerBrush");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(this, System.Windows.Automation.AutomationLiveSetting.Off);
    }

    public IReadOnlyList<double?>? Series { get => (IReadOnlyList<double?>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public IReadOnlyList<double?>? SecondarySeries { get => (IReadOnlyList<double?>?)GetValue(SecondarySeriesProperty); set => SetValue(SecondarySeriesProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public WpfBrush? Stroke { get => (WpfBrush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public WpfBrush? SecondaryStroke { get => (WpfBrush?)GetValue(SecondaryStrokeProperty); set => SetValue(SecondaryStrokeProperty, value); }
    public WpfBrush? TrackBrush { get => (WpfBrush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public WpfBrush? LostBrush { get => (WpfBrush?)GetValue(LostBrushProperty); set => SetValue(LostBrushProperty, value); }
    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? Unit { get => (string?)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    /// <summary>Convenience for callers holding plain doubles.</summary>
    public void SetSeries(IEnumerable<double> values) => Series = values.Select(v => (double?)v).ToList();

    private void UpdateAccessibleName()
    {
        var s = Series;
        string last = s is { Count: > 0 } && s[^1] is double v
            ? v.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + (string.IsNullOrEmpty(Unit) ? "" : " " + Unit)
            : "";
        string name = string.IsNullOrWhiteSpace(Label) ? last : (last.Length == 0 ? Label! : Label + ": " + last);
        System.Windows.Automation.AutomationProperties.SetName(this, name);
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        double h = double.IsNaN(Height) ? 56 : Height;
        double w = double.IsInfinity(availableSize.Width) ? MinWidth : Math.Max(MinWidth, availableSize.Width);
        return new WpfSize(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        const double pad = 2;
        double baseline = h - pad;
        var track = TrackBrush ?? WpfBrushes.Gray;

        // Baseline + a faint mid line so an empty chart still reads as a chart.
        var gridPen = new WpfPen(track, 1) { DashStyle = DashStyles.Dot };
        gridPen.Freeze();
        dc.DrawLine(new WpfPen(track, 1), new WpfPoint(pad, baseline), new WpfPoint(w - pad, baseline));
        dc.DrawLine(gridPen, new WpfPoint(pad, pad + (h - 2 * pad) / 2), new WpfPoint(w - pad, pad + (h - 2 * pad) / 2));

        var primary = Series;
        var secondary = SecondarySeries;
        int n = Math.Max(primary?.Count ?? 0, secondary?.Count ?? 0);
        if (n == 0) return;

        double max = Maximum;
        if (double.IsNaN(max) || max <= 0)
        {
            max = 0;
            if (primary != null) foreach (var v in primary) if (v is double d && d > max) max = d;
            if (secondary != null) foreach (var v in secondary) if (v is double d && d > max) max = d;
            if (max <= 0) max = 1;
            max *= 1.05; // headroom so the peak is not clipped by the stroke
        }

        int span = Capacity > 0 ? Math.Max(Capacity, n) : Math.Max(n, 2);
        double step = (w - 2 * pad) / Math.Max(1, span - 1);
        double X(int count, int i) => w - pad - (count - 1 - i) * step;
        double Y(double v) => pad + (h - 2 * pad) * (1 - Math.Clamp(v / max, 0, 1));

        if (secondary is { Count: > 0 })
        {
            var pen = new WpfPen(SecondaryStroke ?? WpfBrushes.Gray, 1) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            DrawLine(dc, secondary, pen, null, X, Y, baseline);
        }
        if (primary is { Count: > 0 })
        {
            var stroke = Stroke ?? WpfBrushes.DeepSkyBlue;
            var pen = new WpfPen(stroke, 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            WpfBrush? fill = null;
            if (stroke is SolidColorBrush scb)
            {
                fill = new SolidColorBrush(scb.Color) { Opacity = 0.16 };
                fill.Freeze();
            }
            DrawLine(dc, primary, pen, fill, X, Y, baseline);

            var lost = LostBrush ?? WpfBrushes.Red;
            for (int i = 0; i < primary.Count; i++)
            {
                if (primary[i] != null) continue;
                double x = X(primary.Count, i);
                dc.DrawRectangle(lost, null, new Rect(x - 1.5, baseline - 4, 3, 4));
            }
        }
    }

    /// <summary>One polyline per contiguous run of non-null samples (and an area under it when a fill is given).</summary>
    private static void DrawLine(DrawingContext dc, IReadOnlyList<double?> series, WpfPen pen, WpfBrush? fill,
        Func<int, int, double> x, Func<double, double> y, double baseline)
    {
        int count = series.Count;
        int i = 0;
        while (i < count)
        {
            while (i < count && series[i] == null) i++;
            if (i >= count) break;
            int start = i;
            while (i < count && series[i] != null) i++;
            int end = i; // exclusive

            if (end - start == 1)
            {
                // A lone sample has no line: draw a dot so the value is still visible.
                var p = new WpfPoint(x(count, start), y(series[start]!.Value));
                dc.DrawEllipse(pen.Brush, null, p, pen.Thickness, pen.Thickness);
                continue;
            }

            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(new WpfPoint(x(count, start), y(series[start]!.Value)), false, false);
                for (int k = start + 1; k < end; k++)
                    g.LineTo(new WpfPoint(x(count, k), y(series[k]!.Value)), true, true);
            }
            line.Freeze();

            if (fill != null)
            {
                var area = new StreamGeometry();
                using (var g = area.Open())
                {
                    g.BeginFigure(new WpfPoint(x(count, start), baseline), true, true);
                    for (int k = start; k < end; k++)
                        g.LineTo(new WpfPoint(x(count, k), y(series[k]!.Value)), false, false);
                    g.LineTo(new WpfPoint(x(count, end - 1), baseline), false, false);
                }
                area.Freeze();
                dc.DrawGeometry(fill, null, area);
            }
            dc.DrawGeometry(null, pen, line);
        }
    }
}
