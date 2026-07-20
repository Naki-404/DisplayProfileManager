using System.Windows;

namespace DisplayProfileManager;

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
    }

    public string? Tip
    {
        get => (string?)GetValue(TipProperty);
        set => SetValue(TipProperty, value);
    }

    private static void OnTipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is InfoHint hint)
            hint.ToolTip = e.NewValue as string;
    }
}
