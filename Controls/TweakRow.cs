using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Visor.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace Visor.Controls;

/// <summary>
/// One reversible-setting row shared by the Optimize and Network pages: status dot · title / status /
/// badges / extra lines / "Windows will ask for administrator permission" note / Why·Risk·Undo · action
/// buttons. Actions are async: while one runs the row is busy (every button disabled, a status line says
/// what it waits for); the owning page re-reads the real state afterwards and rebuilds the row.
/// Built in code so both pages render the same targets identically.
/// </summary>
public sealed class TweakRow : Grid
{
    private readonly StatusDot _dot;
    private readonly StackPanel _text;
    private readonly TextBlock _title;
    private readonly TextBlock _status;
    private readonly WrapPanel _badges;
    private readonly StackPanel _lines;
    private readonly DockPanel _uacNote;
    private readonly OptDetailsPanel _details;
    private readonly TextBlock _busy;
    private readonly StackPanel _buttons;
    private readonly List<WpfButton> _actions = new();
    private bool _busyState;

    public TweakRow(string title)
    {
        Margin = new Thickness(0, 0, 0, 10);
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _dot = new StatusDot { Label = title, Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        Children.Add(_dot);

        _text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        _title = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap };
        _title.SetResourceReference(StyleProperty, "CardTitle");
        _text.Children.Add(_title);
        _status = Hint("", new Thickness(0, 2, 0, 0));
        _text.Children.Add(_status);
        _badges = new WrapPanel { Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        _text.Children.Add(_badges);
        _lines = new StackPanel();
        _text.Children.Add(_lines);

        // "Windows will ask for administrator permission once" — visible before the button is pressed.
        _uacNote = new DockPanel { Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        var shield = Badge(Loc.T("optimize.ui.adminBadge"), "WarningBrush", "WarningBrush");
        shield.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(shield, Dock.Left);
        _uacNote.Children.Add(shield);
        _uacNote.Children.Add(Hint(Loc.T("net.tweak.status.uacHint"), new Thickness(0)));
        _text.Children.Add(_uacNote);

        _busy = Hint("", new Thickness(0, 4, 0, 0));
        _busy.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        _busy.Visibility = Visibility.Collapsed;
        _text.Children.Add(_busy);

        _details = new OptDetailsPanel();
        _text.Children.Add(_details);
        SetColumn(_text, 1);
        Children.Add(_text);

        _buttons = new StackPanel { Orientation = WpfOrientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        SetColumn(_buttons, 2);
        Children.Add(_buttons);
    }

    public string Title => _title.Text;

    /// <summary>Status line under the title + dot color.</summary>
    public TweakRow SetStatus(string text, StatusKind kind)
    {
        _status.Text = text;
        _status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        _dot.Status = kind;
        return this;
    }

    /// <summary>Small pill under the status (e.g. "Restart pending"). Colors are theme tokens.</summary>
    public TweakRow AddBadge(string text, string brushKey = "BorderBrush", string foregroundKey = "MutedBrush", string? tooltip = null)
    {
        var b = Badge(text, brushKey, foregroundKey);
        b.Margin = new Thickness(0, 0, 6, 2);
        if (tooltip != null) b.ToolTip = tooltip;
        _badges.Children.Add(b);
        _badges.Visibility = Visibility.Visible;
        return this;
    }

    /// <summary>Extra hint line (exe path, adapter, raw values). <paramref name="trim"/> = single line with ellipsis.</summary>
    public TweakRow AddLine(string text, bool trim = false, string? tooltip = null)
    {
        var tb = Hint(text, new Thickness(0, 2, 0, 0));
        if (trim)
        {
            tb.TextWrapping = TextWrapping.NoWrap;
            tb.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        if (tooltip != null) tb.ToolTip = tooltip;
        _lines.Children.Add(tb);
        return this;
    }

    /// <summary>Raw measured facts as chips (never localized, never invented).</summary>
    public TweakRow AddChips(IEnumerable<string> items)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var item in items)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 6, 4),
                BorderThickness = new Thickness(1)
            };
            chip.SetResourceReference(Border.BackgroundProperty, "PillBgBrush");
            chip.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            var tb = new TextBlock { Text = item, FontSize = 10 };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            chip.Child = tb;
            wrap.Children.Add(chip);
        }
        if (wrap.Children.Count > 0) _lines.Children.Add(wrap);
        return this;
    }

    /// <summary>Shows the UAC note (call for every action that goes through the elevated helper).</summary>
    public TweakRow SetNeedsAdmin(bool needsAdmin)
    {
        _uacNote.Visibility = needsAdmin ? Visibility.Visible : Visibility.Collapsed;
        return this;
    }

    public TweakRow SetDetails(string? why, string? risk, string? undo)
    {
        _details.SetTexts(why, risk, undo);
        return this;
    }

    /// <summary>
    /// Adds an action button. <paramref name="run"/> is awaited with the row busy; exceptions are logged
    /// (the action itself reports failures as toasts). <paramref name="busyText"/> is shown while it runs.
    /// </summary>
    public WpfButton AddAction(string text, bool accent, Func<Task> run, bool enabled = true, string? tooltip = null,
        string? busyText = null, bool link = false)
    {
        var b = new WpfButton
        {
            Content = text,
            Margin = new Thickness(_buttons.Children.Count == 0 ? 0 : 6, 0, 0, 0),
            IsEnabled = enabled,
            ToolTip = tooltip,
            VerticalAlignment = VerticalAlignment.Top
        };
        b.SetResourceReference(StyleProperty, link ? "LinkButton" : accent ? "CompactAccentButton" : "CompactGhostButton");
        AutomationProperties.SetName(b, text + " · " + _title.Text);
        ToolTipService.SetShowOnDisabled(b, true);
        b.Tag = enabled;
        b.Click += async (_, _) =>
        {
            if (_busyState) return;
            SetBusy(true, busyText);
            try
            {
                await run();
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "Tweak action " + _title.Text);
            }
            finally
            {
                SetBusy(false, null);
            }
        };
        _actions.Add(b);
        _buttons.Children.Add(b);
        return b;
    }

    public bool IsBusy => _busyState;

    /// <summary>Busy: every action disabled and a status line ("Waiting for Windows…").</summary>
    public void SetBusy(bool busy, string? text)
    {
        _busyState = busy;
        foreach (var b in _actions)
            b.IsEnabled = !busy && b.Tag is true;
        _busy.Text = text ?? Loc.T("optimize.ui.busy");
        _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Severity / admin pill colored by theme tokens (same look as the suggestion badges).</summary>
    public static Border Badge(string text, string borderBrushKey, string foregroundKey)
    {
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1)
        };
        badge.SetResourceReference(Border.BorderBrushProperty, borderBrushKey);
        badge.SetResourceReference(Border.BackgroundProperty, "PillBgBrush");
        var tb = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold };
        tb.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);
        badge.Child = tb;
        return badge;
    }

    public static TextBlock Hint(string text, Thickness margin)
    {
        var tb = new TextBlock { Text = text, Margin = margin, TextWrapping = TextWrapping.Wrap };
        tb.SetResourceReference(StyleProperty, "Hint");
        return tb;
    }
}
