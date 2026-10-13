using System.Windows;
using System.Windows.Automation;
using Visor.Services;
using WpfControl = System.Windows.Controls.Control;

namespace Visor.Controls;

public enum StatusKind
{
    Unknown,
    Ok,
    Warn,
    Bad
}

/// <summary>
/// 8-DIP status dot colored by theme (Success / Warning / Danger / Muted) with a soft halo.
/// Template lives in App.xaml. Exposes a localized accessible name ("OK", "Attention", …) — optionally
/// prefixed by <see cref="Label"/> so a screen reader hears "Monitoring: OK".
/// </summary>
public sealed class StatusDot : WpfControl
{
    static StatusDot()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusDot), new FrameworkPropertyMetadata(typeof(StatusDot)));
        FocusableProperty.OverrideMetadata(typeof(StatusDot), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(StatusKind), typeof(StatusDot),
        new PropertyMetadata(StatusKind.Unknown, (d, _) => ((StatusDot)d).UpdateAccessibleName()));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(StatusDot),
        new PropertyMetadata(null, (d, _) => ((StatusDot)d).UpdateAccessibleName()));

    public StatusDot()
    {
        Width = 8;
        Height = 8;
        IsTabStop = false;
        UpdateAccessibleName();
    }

    public StatusKind Status
    {
        get => (StatusKind)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>What the dot describes ("Monitoring", "Game Mode") — prefixes the accessible name.</summary>
    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static StatusKind FromBool(bool? ok, StatusKind whenFalse = StatusKind.Warn) =>
        ok == null ? StatusKind.Unknown : ok.Value ? StatusKind.Ok : whenFalse;

    public static string StatusText(StatusKind kind) => Loc.T(kind switch
    {
        StatusKind.Ok => "status.dot.ok",
        StatusKind.Warn => "status.dot.warn",
        StatusKind.Bad => "status.dot.bad",
        _ => "status.dot.unknown"
    });

    private void UpdateAccessibleName()
    {
        string text = StatusText(Status);
        AutomationProperties.SetName(this, string.IsNullOrWhiteSpace(Label) ? text : Label + ": " + text);
    }
}
