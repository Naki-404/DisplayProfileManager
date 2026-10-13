using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Visor.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace Visor.Controls;

/// <summary>
/// Frame-time chart for the Monitor page: one polyline of frame times (ms, left axis) over the stats
/// window, a dashed p99 line, dropped frames as ticks along the top, and the FPS equivalent on the right
/// axis. Theme brushes, no animation; the page pushes data at most twice a second via <see cref="SetSeries"/>.
/// Draws in OnRender — no visual tree.
/// </summary>
public sealed class FrameChart : FrameworkElement
{
    private const double PadLeft = 34, PadRight = 40, PadTop = 10, PadBottom = 18;

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(WpfBrush), typeof(FrameChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty P99BrushProperty = DependencyProperty.Register(
        nameof(P99Brush), typeof(WpfBrush), typeof(FrameChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DroppedBrushProperty = DependencyProperty.Register(
        nameof(DroppedBrush), typeof(WpfBrush), typeof(FrameChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(WpfBrush), typeof(FrameChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(WpfBrush), typeof(FrameChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(FrameChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private double[] _times = Array.Empty<double>();
    private bool[] _dropped = Array.Empty<bool>();
    private double _p99;
    private double _axisMax = FrameChartLayout.MinRangeMs;

    public FrameChart()
    {
        MinHeight = 120;
        SetResourceReference(LineBrushProperty, "AccentBrush");
        SetResourceReference(P99BrushProperty, "WarningBrush");
        SetResourceReference(DroppedBrushProperty, "DangerBrush");
        SetResourceReference(GridBrushProperty, "BorderBrush");
        SetResourceReference(LabelBrushProperty, "MutedBrush");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(this, System.Windows.Automation.AutomationLiveSetting.Off);
    }

    public WpfBrush? LineBrush { get => (WpfBrush?)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public WpfBrush? P99Brush { get => (WpfBrush?)GetValue(P99BrushProperty); set => SetValue(P99BrushProperty, value); }
    public WpfBrush? DroppedBrush { get => (WpfBrush?)GetValue(DroppedBrushProperty); set => SetValue(DroppedBrushProperty, value); }
    public WpfBrush? GridBrush { get => (WpfBrush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public WpfBrush? LabelBrush { get => (WpfBrush?)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    /// <summary>Shown centered when there is no data.</summary>
    public string? EmptyText { get => (string?)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    public int SampleCount => _times.Length;
    public double AxisMaxMs => _axisMax;

    /// <summary>Replaces the series (oldest first) and redraws once. Call on the UI thread.</summary>
    public void SetSeries(IReadOnlyList<FrameRecord> frames, double p99Ms)
    {
        int n = frames?.Count ?? 0;
        var times = new double[n];
        var dropped = new bool[n];
        for (int i = 0; i < n; i++)
        {
            var f = frames![i];
            times[i] = f.FrameTimeMs > 0 ? f.FrameTimeMs : 0;
            dropped[i] = f.Dropped;
        }
        _times = times;
        _dropped = dropped;
        _p99 = p99Ms > 0 ? p99Ms : 0;
        _axisMax = FrameChartLayout.AxisMax(times, _p99);
        UpdateAccessibleName();
        InvalidateVisual();
    }

    public void Clear()
    {
        _times = Array.Empty<double>();
        _dropped = Array.Empty<bool>();
        _p99 = 0;
        _axisMax = FrameChartLayout.MinRangeMs;
        UpdateAccessibleName();
        InvalidateVisual();
    }

    private void UpdateAccessibleName()
    {
        string name = _times.Length == 0
            ? (EmptyText ?? "")
            : string.Format(CultureInfo.CurrentCulture, "{0} frames, p99 {1:F1} ms, axis 0–{2} ms", _times.Length, _p99, _axisMax);
        System.Windows.Automation.AutomationProperties.SetName(this, name);
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? Math.Max(MinHeight, 160) : availableSize.Height;
        return new WpfSize(Math.Max(0, w), Math.Max(MinHeight, h));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= PadLeft + PadRight + 10 || h <= PadTop + PadBottom + 10) return;

        double plotW = w - PadLeft - PadRight;
        double plotH = h - PadTop - PadBottom;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var labelBrush = LabelBrush ?? WpfBrushes.Gray;
        var typeface = new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        // Grid + both axes (ms left, FPS right).
        var gridPen = new WpfPen(GridBrush ?? WpfBrushes.DimGray, 1);
        gridPen.Freeze();
        foreach (var ms in FrameChartLayout.GridLines(_axisMax))
        {
            double y = PadTop + FrameChartLayout.MapY(ms, _axisMax, plotH);
            double ys = Math.Round(y) + 0.5;
            dc.DrawLine(gridPen, new WpfPoint(PadLeft, ys), new WpfPoint(PadLeft + plotW, ys));

            var msText = new FormattedText(FrameChartLayout.MsLabel(ms), CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight, typeface, 10, labelBrush, dip);
            dc.DrawText(msText, new WpfPoint(PadLeft - 6 - msText.Width, y - msText.Height / 2));

            string fpsLabel = FrameChartLayout.FpsLabel(ms);
            if (fpsLabel.Length > 0)
            {
                var fpsText = new FormattedText(fpsLabel, CultureInfo.CurrentUICulture,
                    System.Windows.FlowDirection.LeftToRight, typeface, 10, labelBrush, dip);
                dc.DrawText(fpsText, new WpfPoint(PadLeft + plotW + 6, y - fpsText.Height / 2));
            }
        }
        var axisMs = new FormattedText("ms", CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, typeface, 10, labelBrush, dip);
        dc.DrawText(axisMs, new WpfPoint(PadLeft - 6 - axisMs.Width, h - PadBottom + 3));
        var axisFps = new FormattedText("FPS", CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, typeface, 10, labelBrush, dip);
        dc.DrawText(axisFps, new WpfPoint(PadLeft + plotW + 6, h - PadBottom + 3));

        if (_times.Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(EmptyText))
            {
                var empty = new FormattedText(EmptyText!, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
                    typeface, 12, labelBrush, dip) { MaxTextWidth = plotW, TextAlignment = TextAlignment.Center };
                dc.DrawText(empty, new WpfPoint(PadLeft, PadTop + plotH / 2 - empty.Height / 2));
            }
            return;
        }

        // Clip the plot so a clipped-at-top hitch cannot draw over the labels.
        dc.PushClip(new RectangleGeometry(new Rect(PadLeft, PadTop - 1, plotW, plotH + 2)));

        // p99 reference (dashed).
        if (_p99 > 0 && _p99 <= _axisMax)
        {
            var p99Pen = new WpfPen(P99Brush ?? WpfBrushes.Orange, 1) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
            p99Pen.Freeze();
            double y = Math.Round(PadTop + FrameChartLayout.MapY(_p99, _axisMax, plotH)) + 0.5;
            dc.DrawLine(p99Pen, new WpfPoint(PadLeft, y), new WpfPoint(PadLeft + plotW, y));
        }

        // Frame-time polyline.
        int n = _times.Length;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            if (n == 1)
            {
                double y = PadTop + FrameChartLayout.MapY(_times[0], _axisMax, plotH);
                g.BeginFigure(new WpfPoint(PadLeft, y), false, false);
                g.LineTo(new WpfPoint(PadLeft + plotW, y), true, false);
            }
            else
            {
                // Down-sample to ~1 point per pixel column: keep the slowest frame of each column so stutters survive.
                int cols = Math.Max(2, (int)plotW);
                if (n <= cols)
                {
                    g.BeginFigure(new WpfPoint(PadLeft, PadTop + FrameChartLayout.MapY(_times[0], _axisMax, plotH)), false, false);
                    for (int i = 1; i < n; i++)
                        g.LineTo(new WpfPoint(PadLeft + FrameChartLayout.MapX(i, n, plotW), PadTop + FrameChartLayout.MapY(_times[i], _axisMax, plotH)), true, false);
                }
                else
                {
                    bool started = false;
                    for (int c = 0; c < cols; c++)
                    {
                        int from = (int)((long)c * n / cols), to = (int)((long)(c + 1) * n / cols);
                        if (to <= from) continue;
                        double worst = 0;
                        for (int i = from; i < to; i++) if (_times[i] > worst) worst = _times[i];
                        var pt = new WpfPoint(PadLeft + FrameChartLayout.MapX(c, cols, plotW), PadTop + FrameChartLayout.MapY(worst, _axisMax, plotH));
                        if (!started) { g.BeginFigure(pt, false, false); started = true; }
                        else g.LineTo(pt, true, false);
                    }
                }
            }
        }
        geo.Freeze();
        var linePen = new WpfPen(LineBrush ?? WpfBrushes.DeepSkyBlue, 1.5) { LineJoin = PenLineJoin.Round };
        linePen.Freeze();
        dc.DrawGeometry(null, linePen, geo);

        // Dropped frames: short ticks along the top edge.
        var dropPen = new WpfPen(DroppedBrush ?? WpfBrushes.Red, 1.5);
        dropPen.Freeze();
        for (int i = 0; i < n; i++)
        {
            if (!_dropped[i]) continue;
            double x = Math.Round(PadLeft + FrameChartLayout.MapX(i, n, plotW)) + 0.5;
            dc.DrawLine(dropPen, new WpfPoint(x, PadTop), new WpfPoint(x, PadTop + 6));
        }

        dc.Pop();
    }
}
