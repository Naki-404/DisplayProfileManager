using System.Windows.Controls;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace Visor.Services;

/// <summary>ComboBox tag selection + power plan labels shared by the Games, Optimize, Settings and Home pages.</summary>
public static class ComboUi
{
    public static void SelectByTag(WpfComboBox cmb, string tag)
    {
        foreach (var item in cmb.Items)
        {
            if (item is ComboBoxItem ci && string.Equals(ci.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                cmb.SelectedItem = item;
                return;
            }
        }
        if (cmb.Items.Count > 0) cmb.SelectedIndex = 0;
    }

    /// <summary>
    /// Selects a power plan by tag. null/empty → <paramref name="fallbackTag"/>. Unknown values (custom plan GUIDs
    /// captured from Windows) get a "Current plan (custom)" item so they survive a save instead of being replaced.
    /// </summary>
    public static void SelectPowerPlan(WpfComboBox cmb, string? plan, string fallbackTag)
    {
        foreach (var stale in cmb.Items.OfType<ComboBoxItem>().Where(i => i.Uid == "customPlan").ToList())
            cmb.Items.Remove(stale);

        if (string.IsNullOrWhiteSpace(plan))
        {
            SelectByTag(cmb, fallbackTag);
            return;
        }
        bool known = cmb.Items.OfType<ComboBoxItem>()
            .Any(i => string.Equals(i.Tag?.ToString(), plan, StringComparison.OrdinalIgnoreCase));
        if (!known)
            cmb.Items.Add(new ComboBoxItem { Content = Loc.T("power.custom"), Tag = plan, Uid = "customPlan", ToolTip = plan });
        SelectByTag(cmb, plan);
    }

    /// <summary>Defaults editor label: null/empty means "keep the current plan".</summary>
    public static string PowerPlanLabel(string? plan) => plan?.ToLowerInvariant() switch
    {
        null or "" => Loc.T("power.keep"),
        "highperformance" => Loc.T("power.high"),
        "balanced" => Loc.T("power.balanced"),
        _ => Loc.T("power.custom")
    };

    /// <summary>Label for a plan alias or GUID as read from Windows (Optimize / Home cards).</summary>
    public static string PowerPlanStateLabel(string aliasOrGuid) => aliasOrGuid switch
    {
        "highPerformance" => Loc.T("power.high"),
        "balanced" => Loc.T("power.balanced"),
        "powerSaver" => Loc.T("power.saver"),
        "ultimate" => Loc.T("power.ultimate"),
        _ => aliasOrGuid
    };
}
