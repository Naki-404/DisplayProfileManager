namespace Visor.Services;

/// <summary>
/// Pure shell sizing for MainWindow against a monitor work area in DIPs.
/// Targets (DIP work areas ≈ full screen / scale − ~40–48 px taskbar):
/// 1366×768 @125% (~1093×566), 1920×1080 @150% (~1280×672), plus 100% baselines.
/// </summary>
public static class ShellFit
{
    public const double PreferredMinWidth = 1100;
    public const double AbsoluteMinWidth = 720;
    public const double PreferredMinHeight = 520;
    public const double AbsoluteMinHeight = 420;
    public const double PreferredWidth = 1180;
    public const double PreferredHeight = 720;
    /// <summary>Nav rail width (DIP) expanded / icons-only.</summary>
    public const double NavRailWidth = 200;
    public const double NavRailCompactWidth = 56;

    public static double MinWidthFor(double workAreaWidthDip) =>
        workAreaWidthDip - 16 >= PreferredMinWidth
            ? PreferredMinWidth
            : Math.Max(AbsoluteMinWidth, workAreaWidthDip - 16);

    public static double MinHeightFor(double workAreaHeightDip) =>
        Math.Max(AbsoluteMinHeight, Math.Min(PreferredMinHeight, workAreaHeightDip - 16));

    public static (double Width, double Height) DefaultSize(double workAreaWidthDip, double workAreaHeightDip)
    {
        double minW = MinWidthFor(workAreaWidthDip);
        double minH = MinHeightFor(workAreaHeightDip);
        double w = Math.Max(minW, Math.Min(PreferredWidth, workAreaWidthDip - 48));
        double h = Math.Max(minH, Math.Min(PreferredHeight, workAreaHeightDip - 48));
        return (w, h);
    }

    /// <summary>True when the default shell and mins fit inside the work area (with 16 DIP margins).</summary>
    public static bool FitsWorkArea(double workAreaWidthDip, double workAreaHeightDip)
    {
        var (w, h) = DefaultSize(workAreaWidthDip, workAreaHeightDip);
        return w <= workAreaWidthDip - 16 && h <= workAreaHeightDip - 16
               && MinWidthFor(workAreaWidthDip) <= workAreaWidthDip - 16
               && MinHeightFor(workAreaHeightDip) <= workAreaHeightDip - 16;
    }
}
