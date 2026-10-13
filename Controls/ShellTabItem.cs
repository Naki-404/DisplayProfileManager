using System.Windows.Automation.Peers;
using WpfTabItem = System.Windows.Controls.TabItem;

namespace Visor.Controls;

/// <summary>
/// TabItem used by the headerless shell host. Sidebar owns navigation UIA;
/// these items stay in the logical tree for content switching only.
/// </summary>
public sealed class ShellTabItem : WpfTabItem
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
