using System.Windows;
using System.Windows.Threading;
using DisplayProfileManager.Services;

namespace DisplayProfileManager;

/// <summary>Compact system metrics line only.</summary>
public partial class TabBuddy : System.Windows.Controls.UserControl
{
    private DispatcherTimer? _sysTimer;
    private readonly Random _rng = new();
    private LightSysInfoService? _sys;
    private int _tipIndex;

    private static readonly string[] Tips =
    {
        "Tip: Apply pushes current defaults to the display",
        "Tip: Scan finds installed + running games",
        "Tip: Presets are per-game — pick a game first",
        "Tip: Hotkeys work while a game profile is active",
        "Tip: Reset settings restores factory color & resolution"
    };

    public TabBuddy()
    {
        InitializeComponent();
        Loaded += TabBuddy_Loaded;
        Unloaded += (_, _) =>
        {
            _sysTimer?.Stop();
            _sys?.Dispose();
            _sys = null;
        };
    }

    private void TabBuddy_Loaded(object sender, RoutedEventArgs e)
    {
        try { _sys = new LightSysInfoService(); }
        catch { _sys = null; }

        RefreshSysLine(forceTip: false);
        _sysTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _sysTimer.Tick += (_, _) => RefreshSysLine(forceTip: false);
        _sysTimer.Start();
    }

    private void RefreshSysLine(bool forceTip)
    {
        try
        {
            if (forceTip || _rng.Next(6) == 0)
            {
                SysLine.Text = Tips[_tipIndex++ % Tips.Length];
                return;
            }
            var line = _sys?.Sample() ?? "";
            if (!string.IsNullOrWhiteSpace(line))
                SysLine.Text = line;
        }
        catch { }
    }
}
