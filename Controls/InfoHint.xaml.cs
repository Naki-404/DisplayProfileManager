using System.Windows;

namespace Visor;

public partial class InfoHint : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty TipProperty =
        DependencyProperty.Register(
            nameof(Tip),
            typeof(string),
            typeof(InfoHint),
            new PropertyMetadata(null, OnTipChanged));

    public InfoHint()
    {
        InitializeComponent();
        // Keyboard users: show the tip while the "i" has focus (mouse users get the normal hover tooltip).
        GotKeyboardFocus += (_, _) => SetTipOpen(true);
        LostKeyboardFocus += (_, _) => SetTipOpen(false);
        MouseEnter += (_, _) => Dot.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        MouseLeave += (_, _) => Dot.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BorderBrush");
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
        }
    }
}
