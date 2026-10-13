using System.Windows;
using System.Windows.Controls;

namespace Visor;

public partial class InfoHint : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty TipProperty =
        DependencyProperty.Register(
            nameof(Tip),
            typeof(string),
            typeof(InfoHint),
            new PropertyMetadata(null, OnTipChanged));

    /// <summary>Marks sibling tooltips this hint attached in tooltip mode so only those are removed again.</summary>
    private static readonly DependencyProperty AttachedByHintProperty =
        DependencyProperty.RegisterAttached("AttachedByHint", typeof(InfoHint), typeof(InfoHint), new PropertyMetadata(null));

    private static bool _tooltipMode;
    private static event Action? TooltipModeChanged;

    /// <summary>
    /// Tooltip mode (top bar toggle): every control that sits next to an InfoHint shows the same explanation
    /// on hover. Off = only the ⓘ glyph has it.
    /// </summary>
    public static bool TooltipMode
    {
        get => _tooltipMode;
        set
        {
            if (_tooltipMode == value) return;
            _tooltipMode = value;
            TooltipModeChanged?.Invoke();
        }
    }

    public InfoHint()
    {
        InitializeComponent();
        // Keyboard users: show the tip while the "i" has focus (mouse users get the normal hover tooltip).
        GotKeyboardFocus += (_, _) => SetTipOpen(true);
        LostKeyboardFocus += (_, _) => SetTipOpen(false);
        MouseEnter += (_, _) => Dot.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        MouseLeave += (_, _) => Dot.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BorderBrush");
        Loaded += (_, _) =>
        {
            TooltipModeChanged += ApplyTooltipMode;
            ApplyTooltipMode();
        };
        Unloaded += (_, _) =>
        {
            TooltipModeChanged -= ApplyTooltipMode;
            DetachSiblingTips();
        };
    }

    private void SetTipOpen(bool open)
    {
        if (ToolTip is not System.Windows.Controls.ToolTip tt)
        {
            if (string.IsNullOrWhiteSpace(Tip)) return;
            tt = new System.Windows.Controls.ToolTip { Content = Tip };
            ToolTip = tt;
        }
        tt.PlacementTarget = this;
        tt.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        tt.IsOpen = open;
    }

    public string? Tip
    {
        get => (string?)GetValue(TipProperty);
        set => SetValue(TipProperty, value);
    }

    private static void OnTipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is InfoHint hint)
        {
            hint.ToolTip = string.IsNullOrWhiteSpace(e.NewValue as string)
                ? null
                : new System.Windows.Controls.ToolTip { Content = e.NewValue as string };
            System.Windows.Automation.AutomationProperties.SetHelpText(hint, e.NewValue as string ?? "");
            // The "i" glyph has no text of its own — the tip doubles as its accessible name.
            System.Windows.Automation.AutomationProperties.SetName(hint, e.NewValue as string ?? "");
            if (hint.IsLoaded) hint.ApplyTooltipMode();
        }
    }

    /// <summary>Siblings in the same panel (the labeled control the hint explains).</summary>
    private IEnumerable<FrameworkElement> Siblings()
    {
        if (Parent is not System.Windows.Controls.Panel panel) yield break;
        foreach (var child in panel.Children)
        {
            if (child is FrameworkElement fe && !ReferenceEquals(fe, this))
                yield return fe;
        }
    }

    private void ApplyTooltipMode()
    {
        if (!_tooltipMode || string.IsNullOrWhiteSpace(Tip))
        {
            DetachSiblingTips();
            return;
        }
        foreach (var sib in Siblings())
        {
            bool ours = ReferenceEquals(sib.GetValue(AttachedByHintProperty), this);
            if (sib.ToolTip != null && !ours) continue; // keep an explicit tooltip the control already has
            sib.ToolTip = Tip;
            sib.SetValue(AttachedByHintProperty, this);
            ToolTipService.SetInitialShowDelay(sib, 350);
            ToolTipService.SetShowDuration(sib, 30000);
        }
    }

    private void DetachSiblingTips()
    {
        foreach (var sib in Siblings())
        {
            if (!ReferenceEquals(sib.GetValue(AttachedByHintProperty), this)) continue;
            sib.ToolTip = null;
            sib.ClearValue(AttachedByHintProperty);
        }
    }
}
