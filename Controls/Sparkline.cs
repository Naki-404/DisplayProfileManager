using System.Windows;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace Visor.Controls;

/// <summary>
/// Tiny rolling line chart (last <see cref="Capacity"/> samples, 0..<see cref="Maximum"/>) for a 1 Hz sensor.
/// Missing samples (<c>null</c>) leave a gap instead of being drawn as zero. Draws in OnRender with theme
/// brushes; no animation, no visual tree — cheap enough for every hardware card.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((Sparkline)d).Trim()));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(WpfBrush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(WpfBrush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(Sparkline),
        new PropertyMetadata(null, (d, _) => ((Sparkline)d).UpdateAccessibleName()));

    private readonly List<double?> _values = new(64);

    public Sparkline()
    {
        Height = 28;
        MinWidth = 40;
        SetResourceReference(StrokeProperty, "AccentBrush");
        SetResourceReference(TrackBrushProperty, "TrackBrush");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(this, System.Windows.Automation.AutomationLiveSetting.Off);
        UpdateAccessibleName();
    }

    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    /// <summary>How many samples are kept (oldest drop off the left edge).</summary>
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public WpfBrush? Stroke { get => (WpfBrush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public WpfBrush? TrackBrush { get => (WpfBrush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    /// <summary>Accessible name prefix ("CPU").</summary>
    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public int Count => _values.Count;

    /// <summary>Latest non-missing sample, or null.</summary>
    public double? Last
    {
        get
        {
            for (int i = _values.Count - 1; i >= 0; i--)
                if (_values[i] is { } v) return v;
            return null;
        }
    }

    /// <summary>Appends one sample (<c>null</c> = missing → gap). Trims to <see cref="Capacity"/>.</summary>
    public void Push(double? value)
    {
        _values.Add(value);
        Trim();
        UpdateAccessibleName();
        InvalidateVisual();
    }

    public void Clear()
    {
        _values.Clear();
        UpdateAccessibleName();
        InvalidateVisual();
    }

    private void Trim()
    {
        int cap = Math.Max(2, Capacity);
        if (_values.Count > cap) _values.RemoveRange(0, _values.Count - cap);
    }

    private void UpdateAccessibleName()
    {
        string value = Last is { } v ? Math.Round(v).ToString(System.Globalization.CultureInfo.CurrentCulture) : "—";
        System.Windows.Automation.AutomationProperties.SetName(this, string.IsNullOrWhiteSpace(Label) ? value : Label + ": " + value);
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        double h = double.IsNaN(Height) ? 28 : Height;
        double w = double.IsInfinity(availableSize.Width) ? MinWidth : Math.Max(MinWidth, availableSize.Width);
        return new WpfSize(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Baseline so an empty / all-missing chart still reads as a chart, not a blank.
        var track = TrackBrush ?? WpfBrushes.Gray;
        dc.DrawRectangle(track, null, new Rect(0, h - 1, w, 1));

        int cap = Math.Max(2, Capacity);
        if (_values.Count < 2 || Maximum <= 0) return;
        double stepX = w / (cap - 1);
        double x0 = w - stepX * (_values.Count - 1);   // newest sample pinned to the right edge
        var pen = new WpfPen(Stroke ?? WpfBrushes.DeepSkyBlue, 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            bool open = false;
            for (int i = 0; i < _values.Count; i++)
            {
                if (_values[i] is not { } v)
                {
                    open = false;
                    continue;
                }
                double frac = Math.Clamp(v / Maximum, 0, 1);
                var p = new WpfPoint(x0 + stepX * i, 1 + (h - 2) * (1 - frac));
                if (!open)
                {
                    g.BeginFigure(p, false, false);
                    open = true;
                }
                else
                {
                    g.LineTo(p, true, true);
                }
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
