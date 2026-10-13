using System.Windows;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfSize = System.Windows.Size;

namespace Visor.Controls;

/// <summary>4-DIP rounded progress bar (value / maximum) drawn with theme brushes. Static — no animation.</summary>
public sealed class ThinBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ThinBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ThinBar)d).UpdateAccessibleName()));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ThinBar),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ThinBar)d).UpdateAccessibleName()));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(WpfBrush), typeof(ThinBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(WpfBrush), typeof(ThinBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ThinBar),
        new PropertyMetadata(null, (d, _) => ((ThinBar)d).UpdateAccessibleName()));

    public ThinBar()
    {
        Height = 4;
        MinWidth = 24;
        SetResourceReference(FillProperty, "AccentGradientBrush");
        SetResourceReference(TrackBrushProperty, "TrackBrush");
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public WpfBrush? Fill { get => (WpfBrush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public WpfBrush? TrackBrush { get => (WpfBrush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    /// <summary>Accessible name prefix ("Memory").</summary>
    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public double Fraction => Maximum <= 0 ? 0 : Math.Clamp(Value / Maximum, 0, 1);

    private void UpdateAccessibleName()
    {
        string pct = Math.Round(Fraction * 100).ToString(System.Globalization.CultureInfo.CurrentCulture) + "%";
        System.Windows.Automation.AutomationProperties.SetName(this, string.IsNullOrWhiteSpace(Label) ? pct : Label + ": " + pct);
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        double h = double.IsNaN(Height) ? 4 : Height;
        double w = double.IsInfinity(availableSize.Width) ? MinWidth : Math.Max(MinWidth, availableSize.Width);
        return new WpfSize(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double radius = h / 2;
        dc.DrawRoundedRectangle(TrackBrush ?? WpfBrushes.Gray, null, new Rect(0, 0, w, h), radius, radius);
        double fw = w * Fraction;
        if (fw >= 1)
            dc.DrawRoundedRectangle(Fill ?? WpfBrushes.DeepSkyBlue, null, new Rect(0, 0, Math.Max(fw, h), h), radius, radius);
    }
}
