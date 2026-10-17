using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WpfRadioButton = System.Windows.Controls.RadioButton;

namespace Visor.Controls;

/// <summary>
/// Keyboard navigation for a strip of <c>SegmentRadio</c> sub-section buttons: Left / Right (and Home / End)
/// move to the previous / next enabled segment, check it and focus it — like a tab strip. Set
/// <c>controls:SegmentNav.ArrowKeys="True"</c> on the panel that holds the radio buttons.
/// </summary>
public static class SegmentNav
{
    public static readonly DependencyProperty ArrowKeysProperty = DependencyProperty.RegisterAttached(
        "ArrowKeys", typeof(bool), typeof(SegmentNav), new PropertyMetadata(false, OnArrowKeysChanged));

    public static bool GetArrowKeys(DependencyObject d) => (bool)d.GetValue(ArrowKeysProperty);

    public static void SetArrowKeys(DependencyObject d, bool value) => d.SetValue(ArrowKeysProperty, value);

    private static void OnArrowKeysChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.PreviewKeyDown -= OnPreviewKeyDown;
        if (e.NewValue is true)
            el.PreviewKeyDown += OnPreviewKeyDown;
    }

    private static void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.Panel panel || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        var items = panel.Children.OfType<WpfRadioButton>()
            .Where(r => r.IsEnabled && r.Visibility == Visibility.Visible)
            .ToList();
        if (items.Count == 0) return;
        int current = items.FindIndex(r => r.IsKeyboardFocusWithin);
        if (current < 0) current = items.FindIndex(r => r.IsChecked == true);
        if (current < 0) return; // focus is not on the strip
        int next = e.Key switch
        {
            Key.Left => (current - 1 + items.Count) % items.Count,
            Key.Right => (current + 1) % items.Count,
            Key.Home => 0,
            _ => items.Count - 1
        };
        var target = items[next];
        target.IsChecked = true;
        target.Focus();
        e.Handled = true;
    }

    /// <summary>Index of the checked radio button in <paramref name="panel"/>, or -1.</summary>
    public static int CheckedIndex(System.Windows.Controls.Panel panel) =>
        panel.Children.OfType<WpfRadioButton>().ToList().FindIndex(r => r.IsChecked == true);
}
