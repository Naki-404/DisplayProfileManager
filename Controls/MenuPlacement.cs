using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfPoint = System.Windows.Point;

namespace Visor;

/// <summary>
/// Anchors a ContextMenu to the control that opened it (⋯ buttons): the menu's visible right edge lines up with the
/// button's right edge, 4 DIP below it (or above when there is no room below). The default MousePoint placement put
/// the menu wherever the cursor happened to be inside the button, so it never lined up with anything.
/// </summary>
internal static class MenuPlacement
{
    /// <summary>Transparent shadow margin baked into the ContextMenu template in App.xaml (left, top, right, bottom).</summary>
    public static readonly Thickness ShadowInset = new(6, 2, 6, 8);

    public const double Gap = 4;

    public static void OpenBelowRightAligned(WpfContextMenu menu, FrameworkElement anchor)
    {
        if (menu == null || anchor == null) return;
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Custom;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 0;
        menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            // The callback's units follow the target's transform (device pixels on some DPI paths): scale the DIP
            // constants by the same factor the target size was scaled by.
            double k = anchor.ActualWidth > 0 ? targetSize.Width / anchor.ActualWidth : 1;
            if (double.IsNaN(k) || k <= 0) k = 1;
            double x = targetSize.Width - popupSize.Width + ShadowInset.Right * k;
            var below = new CustomPopupPlacement(
                new WpfPoint(x, targetSize.Height + (Gap - ShadowInset.Top) * k), PopupPrimaryAxis.Horizontal);
            var above = new CustomPopupPlacement(
                new WpfPoint(x, -popupSize.Height + (ShadowInset.Bottom - Gap) * k), PopupPrimaryAxis.Horizontal);
            return new[] { below, above };
        };
        menu.IsOpen = true;
    }
}
