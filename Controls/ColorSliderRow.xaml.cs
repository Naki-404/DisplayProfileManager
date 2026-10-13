using System.Windows;
using System.Windows.Controls;

namespace Visor.Controls;

/// <summary>
/// Label · slider · value row used by the Games → Color and Presets editors.
/// <see cref="Slider"/>, <see cref="LabelBlock"/> and <see cref="ValueBlock"/> expose the parts so the
/// shared <c>ColorUiHelper</c> read/write code keeps working on plain <see cref="System.Windows.Controls.Slider"/>s.
/// </summary>
public partial class ColorSliderRow : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ColorSliderRow),
        new PropertyMetadata("", (d, e) => ((ColorSliderRow)d).PART_Label.Text = e.NewValue as string ?? ""));

    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(
        nameof(ValueText), typeof(string), typeof(ColorSliderRow),
        new PropertyMetadata("", (d, e) => ((ColorSliderRow)d).PART_Value.Text = e.NewValue as string ?? ""));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ColorSliderRow),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(ColorSliderRow),
        new PropertyMetadata(0.0, (d, e) => ((ColorSliderRow)d).PART_Slider.Minimum = (double)e.NewValue));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ColorSliderRow),
        new PropertyMetadata(1.0, (d, e) => ((ColorSliderRow)d).PART_Slider.Maximum = (double)e.NewValue));

    /// <summary>Tick / keyboard step (TickFrequency and SmallChange).</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(ColorSliderRow),
        new PropertyMetadata(0.01, (d, e) =>
        {
            var s = ((ColorSliderRow)d).PART_Slider;
            s.TickFrequency = (double)e.NewValue;
            s.SmallChange = (double)e.NewValue;
        }));

    public static readonly DependencyProperty LargeChangeProperty = DependencyProperty.Register(
        nameof(LargeChange), typeof(double), typeof(ColorSliderRow),
        new PropertyMetadata(0.05, (d, e) => ((ColorSliderRow)d).PART_Slider.LargeChange = (double)e.NewValue));

    /// <summary>Raised after the slider value changes (user or code); same payload as <see cref="RangeBase.ValueChanged"/>.</summary>
    public static readonly RoutedEvent ValueChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(ValueChanged), RoutingStrategy.Bubble, typeof(RoutedPropertyChangedEventHandler<double>), typeof(ColorSliderRow));

    public ColorSliderRow()
    {
        InitializeComponent();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string ValueText
    {
        get => (string)GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public double LargeChange
    {
        get => (double)GetValue(LargeChangeProperty);
        set => SetValue(LargeChangeProperty, value);
    }

    public event RoutedPropertyChangedEventHandler<double> ValueChanged
    {
        add => AddHandler(ValueChangedEvent, value);
        remove => RemoveHandler(ValueChangedEvent, value);
    }

    /// <summary>The inner slider (range may be re-configured by <c>ColorUiHelper.ConfigureGammaRangeForBackend</c>).</summary>
    public Slider Slider => PART_Slider;

    /// <summary>The name label (localized by the host; tooltip and opacity are set there too).</summary>
    public TextBlock LabelBlock => PART_Label;

    /// <summary>The formatted value text on the right.</summary>
    public TextBlock ValueBlock => PART_Value;

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (ColorSliderRow)d;
        double v = (double)e.NewValue;
        if (Math.Abs(row.PART_Slider.Value - v) > double.Epsilon)
            row.PART_Slider.Value = v;
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Math.Abs(Value - e.NewValue) > double.Epsilon)
            SetCurrentValue(ValueProperty, e.NewValue);
        RaiseEvent(new RoutedPropertyChangedEventArgs<double>(e.OldValue, e.NewValue, ValueChangedEvent));
    }
}
