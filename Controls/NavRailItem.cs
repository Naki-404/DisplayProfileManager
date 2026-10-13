using System.Windows;
using System.Windows.Media;
using WpfRadioButton = System.Windows.Controls.RadioButton;

namespace Visor.Controls;

/// <summary>
/// Sidebar navigation entry: vector icon + label, selected state as an accent-tinted card with a left accent
/// bar, icons-only when <see cref="IsCompact"/>. Template in App.xaml; the hover wash fades in code so it
/// follows <see cref="UiMotion.Reduced"/>.
/// </summary>
public sealed class NavRailItem : WpfRadioButton
{
    static NavRailItem()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(NavRailItem), new FrameworkPropertyMetadata(typeof(NavRailItem)));
    }

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Geometry), typeof(NavRailItem), new PropertyMetadata(null));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(NavRailItem),
        new PropertyMetadata("", (d, _) => ((NavRailItem)d).UpdateTooltipAndName()));

    public static readonly DependencyProperty ShortcutProperty = DependencyProperty.Register(
        nameof(Shortcut), typeof(string), typeof(NavRailItem),
        new PropertyMetadata(null, (d, _) => ((NavRailItem)d).UpdateTooltipAndName()));

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(NavRailItem),
        new PropertyMetadata(false, (d, _) => ((NavRailItem)d).UpdateTooltipAndName()));

    private FrameworkElement? _hoverWash;

    public Geometry? Icon { get => (Geometry?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    /// <summary>"Ctrl+1" — shown in the tooltip.</summary>
    public string? Shortcut { get => (string?)GetValue(ShortcutProperty); set => SetValue(ShortcutProperty, value); }
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _hoverWash = GetTemplateChild("HoverWash") as FrameworkElement;
        if (_hoverWash != null) _hoverWash.Opacity = 0;
    }

    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (_hoverWash != null && IsEnabled)
            UiMotion.FadeTo(_hoverWash, 1, ms: UiMotion.FastMs);
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverWash != null)
            UiMotion.FadeTo(_hoverWash, 0, ms: UiMotion.FastMs);
    }

    private void UpdateTooltipAndName()
    {
        string label = Label ?? "";
        string tip = string.IsNullOrWhiteSpace(Shortcut) ? label : label + " (" + Shortcut + ")";
        // Expanded: the label is visible, the tooltip only adds the shortcut. Compact: the tooltip is the label.
        ToolTip = string.IsNullOrWhiteSpace(label) ? null : tip;
        System.Windows.Automation.AutomationProperties.SetName(this, label);
        System.Windows.Automation.AutomationProperties.SetAcceleratorKey(this, Shortcut ?? "");
    }
}
