using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using WpfTabControl = System.Windows.Controls.TabControl;

namespace Visor.Controls;

/// <summary>
/// Headerless tab host driven by the sidebar. Exposes content as a pane in UIA —
/// not a Tab control with nameless TabItem peers.
/// </summary>
public sealed class ShellTabControl : WpfTabControl
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ShellTabControlAutomationPeer(this);

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (UIElementAutomationPeer.FromElement(this) is ShellTabControlAutomationPeer peer)
            peer.NotifySelectionChanged();
    }

    private sealed class ShellTabControlAutomationPeer : FrameworkElementAutomationPeer
    {
        public ShellTabControlAutomationPeer(ShellTabControl owner) : base(owner) { }

        protected override string GetClassNameCore() => nameof(ShellTabControl);

        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.Pane;

        protected override List<AutomationPeer> GetChildrenCore()
        {
            if (Owner is not ShellTabControl shell) return new List<AutomationPeer>();
            if (shell.SelectedContent is not UIElement content) return new List<AutomationPeer>();
            var peer = CreatePeerForElement(content);
            return peer == null ? new List<AutomationPeer>() : new List<AutomationPeer> { peer };
        }

        public void NotifySelectionChanged()
        {
            ResetChildrenCache();
            RaiseAutomationEvent(AutomationEvents.StructureChanged);
        }
    }
}
