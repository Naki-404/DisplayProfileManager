namespace Visor;

/// <summary>
/// Base for the shell pages. Pages are built in code by <see cref="MainWindow"/> with the host injected, so
/// InitializeComponent already sees a usable <see cref="Host"/> (and its editor-suppression scope).
/// </summary>
public abstract class ShellPageBase : System.Windows.Controls.UserControl
{
    protected ShellPageBase(IShellHost host)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        Focusable = false;
    }

    protected IShellHost Host { get; }

    public abstract ShellSection Section { get; }

    /// <summary>Loc key of the big page title shown in the top bar.</summary>
    public abstract string TitleKey { get; }

    /// <summary>True while the window is visible and this page is the selected section.</summary>
    protected bool IsLive => Host.IsSectionLive(Section);

    /// <summary>Relabel every static string for the current locale, then refresh derived text.</summary>
    public virtual void ApplyLocalization() { }

    /// <summary>The page just became the selected section.</summary>
    public virtual void OnActivated() { }

    /// <summary>Another section was selected (stop probes / previews that only make sense while visible).</summary>
    public virtual void OnDeactivated() { }

    /// <summary>Window shown again or page selected: rebuild whatever went stale while hidden.</summary>
    public virtual void RefreshIfStale() { }
}
