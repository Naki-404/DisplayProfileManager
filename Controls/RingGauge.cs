using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace Visor.Controls;

/// <summary>
/// Ring gauge (0..<see cref="Maximum"/>) with text in the middle. 72–96 DIP, theme brushes, animated sweep
/// (≤ 300 ms) that is skipped when Windows animations are off (<see cref="UiMotion.Reduced"/>).
/// Draws in OnRender — no template, no visual tree, cheap to put on every card.
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    public const int SweepMs = 280;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new PropertyMetadata(0d, (d, e) => ((RingGauge)d).OnValueChanged((double)e.OldValue, (double)e.NewValue)));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((RingGauge)d).UpdateAccessibleName()));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((RingGauge)d).UpdateAccessibleName()));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(6d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(WpfBrush), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(WpfBrush), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(WpfBrush), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionBrushProperty = DependencyProperty.Register(
        nameof(CaptionBrush), typeof(WpfBrush), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Animated copy of <see cref="Value"/> — what is actually drawn.</summary>
    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public RingGauge()
    {
        Width = 80;
        Height = 80;
        SetResourceReference(StrokeProperty, "AccentGradientBrush");
        SetResourceReference(TrackBrushProperty, "TrackBrush");
        SetResourceReference(TextBrushProperty, "TextBrush");
        SetResourceReference(CaptionBrushProperty, "MutedBrush");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(this, System.Windows.Automation.AutomationLiveSetting.Off);
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    /// <summary>Big label in the middle ("42%", "7.2 GB").</summary>
    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>Small caption under the text ("CPU").</summary>
    public string? Caption { get => (string?)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public WpfBrush? Stroke { get => (WpfBrush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public WpfBrush? TrackBrush { get => (WpfBrush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public WpfBrush? TextBrush { get => (WpfBrush?)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public WpfBrush? CaptionBrush { get => (WpfBrush?)GetValue(CaptionBrushProperty); set => SetValue(CaptionBrushProperty, value); }

    /// <summary>0..1 share of the ring that is filled (clamped).</summary>
    public double Fraction => Maximum <= 0 ? 0 : Math.Clamp(Value / Maximum, 0, 1);

    private void OnValueChanged(double from, double to)
    {
        UpdateAccessibleName();
        if (UiMotion.Reduced || !IsLoaded)
        {
            BeginAnimation(DisplayValueProperty, null);
            SetValue(DisplayValueProperty, to);
            return;
        }
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(SweepMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(DisplayValueProperty, anim);
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        SetValue(DisplayValueProperty, Value);
    }

    private void UpdateAccessibleName()
    {
        string name = string.IsNullOrWhiteSpace(Caption) ? (Text ?? "") : Caption + ": " + (Text ?? "");
        System.Windows.Automation.AutomationProperties.SetName(this, name);
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        double w = double.IsNaN(Width) ? 80 : Width;
        double h = double.IsNaN(Height) ? w : Height;
        return new WpfSize(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        double t = Math.Max(1, Math.Min(Thickness, size / 4));
        double r = (size - t) / 2;
        var c = new WpfPoint(ActualWidth / 2, ActualHeight / 2);

        var track = TrackBrush ?? WpfBrushes.Gray;
        dc.DrawEllipse(null, new WpfPen(track, t), c, r, r);

        double shown = (double)GetValue(DisplayValueProperty);
        double frac = Maximum <= 0 ? 0 : Math.Clamp(shown / Maximum, 0, 1);
        var stroke = Stroke ?? WpfBrushes.DeepSkyBlue;
        if (frac >= 0.9995)
        {
            dc.DrawEllipse(null, new WpfPen(stroke, t), c, r, r);
        }
        else if (frac > 0.0005)
        {
            double angle = frac * 360.0;
            var start = PointOnCircle(c, r, -90);
            var end = PointOnCircle(c, r, -90 + angle);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(start, false, false);
                g.ArcTo(end, new WpfSize(r, r), 0, angle > 180, SweepDirection.Clockwise, true, false);
            }
            geo.Freeze();
            var pen = new WpfPen(stroke, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, geo);
        }

        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        bool hasCaption = !string.IsNullOrWhiteSpace(Caption);
        if (!string.IsNullOrWhiteSpace(Text))
        {
            double fs = Math.Max(10, size * (hasCaption ? 0.22 : 0.26));
            var ft = new FormattedText(Text!, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, typeface, fs,
                TextBrush ?? WpfBrushes.White, dip)
            {
                MaxTextWidth = size - t * 2 - 4,
                Trimming = TextTrimming.CharacterEllipsis,
                MaxLineCount = 1
            };
            double y = hasCaption ? c.Y - ft.Height / 2 - size * 0.06 : c.Y - ft.Height / 2;
            dc.DrawText(ft, new WpfPoint(c.X - ft.Width / 2, y));
            if (hasCaption)
            {
                var cap = new FormattedText(Caption!, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
                    new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), Math.Max(9, size * 0.13), CaptionBrush ?? WpfBrushes.Gray, dip)
                {
                    MaxTextWidth = size - t * 2 - 4,
                    Trimming = TextTrimming.CharacterEllipsis,
                    MaxLineCount = 1
                };
                dc.DrawText(cap, new WpfPoint(c.X - cap.Width / 2, y + ft.Height - size * 0.02));
            }
        }
    }

    private static WpfPoint PointOnCircle(WpfPoint c, double r, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        return new WpfPoint(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }
}
