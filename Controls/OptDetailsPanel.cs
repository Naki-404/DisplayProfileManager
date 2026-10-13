using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Visor.Services;
using WpfButton = System.Windows.Controls.Button;

namespace Visor.Controls;

/// <summary>
/// Collapsible "Why / Risk / Undo" block under an Optimize card or suggestion. A link-style toggle shows
/// the three localized paragraphs (any of them may be absent); the whole panel collapses when none is set.
/// Texts are passed already localized so per-game items can format the exe into them.
/// </summary>
public sealed class OptDetailsPanel : StackPanel
{
    private readonly WpfButton _toggle = new();
    private readonly StackPanel _body = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
    private string? _why, _risk, _undo;

    public OptDetailsPanel()
    {
        Margin = new Thickness(0, 4, 0, 0);
        _toggle.SetResourceReference(StyleProperty, "LinkButton");
        _toggle.Padding = new Thickness(0, 1, 0, 1);
        _toggle.Click += (_, _) => IsOpen = !IsOpen;
        Children.Add(_toggle);
        Children.Add(_body);
        Visibility = Visibility.Collapsed;
        UpdateToggleText();
    }

    public bool IsOpen
    {
        get => _body.Visibility == Visibility.Visible;
        set
        {
            _body.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            UpdateToggleText();
        }
    }

    /// <summary>Sets the three paragraphs (null / empty = omitted). Hides the panel when all are empty.</summary>
    public void SetTexts(string? why, string? risk, string? undo)
    {
        _why = why;
        _risk = risk;
        _undo = undo;
        _body.Children.Clear();
        AddRow("optimize.ui.why", _why);
        AddRow("optimize.ui.risk", _risk);
        AddRow("optimize.ui.undoHow", _undo);
        Visibility = _body.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Re-reads the toggle / row labels after a locale change (texts are re-set by the page).</summary>
    public void Relabel()
    {
        UpdateToggleText();
        SetTexts(_why, _risk, _undo);
    }

    private void AddRow(string labelKey, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        tb.SetResourceReference(StyleProperty, "Hint");
        tb.Inlines.Add(new Run(Loc.T(labelKey) + ": ") { FontWeight = FontWeights.SemiBold });
        tb.Inlines.Add(new Run(text));
        _body.Children.Add(tb);
    }

    private void UpdateToggleText() =>
        _toggle.Content = Loc.T(IsOpen ? "optimize.ui.details.hide" : "optimize.ui.details.show");
}
