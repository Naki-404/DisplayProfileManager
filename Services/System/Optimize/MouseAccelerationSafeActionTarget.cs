using System.Globalization;
using System.Runtime.InteropServices;

namespace Visor.Services.Optimize;

/// <summary>
/// Windows mouse acceleration ("Enhance pointer precision") off: <c>HKCU\Control Panel\Mouse</c>
/// <c>MouseSpeed</c> = "0", <c>MouseThreshold1</c> = "0", <c>MouseThreshold2</c> = "0" (all REG_SZ, Windows
/// defaults "1" / "6" / "10"). After every write (apply and undo) the live setting is pushed with
/// <c>SystemParametersInfo(SPI_SETMOUSE, SPIF_SENDCHANGE)</c> so no logoff is needed. Undo restores the exact
/// strings (or deletes absent values) and broadcasts again.
/// </summary>
public sealed class MouseAccelerationSafeActionTarget : RegistrySafeActionTarget
{
    public const string Id = "mouseAccel.off";
    public const string MouseKey = @"Control Panel\Mouse";

    public static readonly RegAddress Speed = new(MouseKey, "MouseSpeed");
    public static readonly RegAddress Threshold1 = new(MouseKey, "MouseThreshold1");
    public static readonly RegAddress Threshold2 = new(MouseKey, "MouseThreshold2");
    private static readonly RegAddress[] All = { Speed, Threshold1, Threshold2 };

    /// <summary>Windows defaults when a value is absent.</summary>
    public const int DefaultSpeed = 1, DefaultThreshold1 = 6, DefaultThreshold2 = 10;

    private readonly Action<int, int, int> _broadcast;

    /// <param name="broadcast">(threshold1, threshold2, speed) → live apply; default = SPI_SETMOUSE. Tests inject a recorder.</param>
    public MouseAccelerationSafeActionTarget(IOptimizeRegistry? registry = null, Action<int, int, int>? broadcast = null)
        : base(registry)
    {
        _broadcast = broadcast ?? BroadcastSpiSetMouse;
    }

    public override string ActionId => Id;
    protected override IReadOnlyList<RegAddress> Addresses => All;
    protected override string Label => "Mouse acceleration";

    /// <summary>Parses a REG_SZ number; absent → the Windows default; unparsable → null.</summary>
    public static int? ParseValue(RegValue? value, int whenAbsent)
    {
        if (value == null) return whenAbsent;
        if (value.Kind == RegValueKind.Dword) return value.AsDword;
        return int.TryParse(value.Data.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;
    }

    /// <summary>True when acceleration is on (MouseSpeed ≠ 0, i.e. Windows applies the threshold curve).</summary>
    public static bool IsAccelerationOn(RegistryValueSet set)
    {
        int? speed = ParseValue(set.Get(Speed), DefaultSpeed);
        // Unparsable = unknown; treat as "on" so the user can still apply the known-good zeros.
        return speed != 0;
    }

    protected override bool Satisfies(RegistryValueSet current)
    {
        return ParseValue(current.Get(Speed), DefaultSpeed) == 0
               && ParseValue(current.Get(Threshold1), DefaultThreshold1) == 0
               && ParseValue(current.Get(Threshold2), DefaultThreshold2) == 0;
    }

    protected override RegistryValueSet BuildTarget(RegistryValueSet current) =>
        new RegistryValueSet()
            .Add(Speed, RegValue.String("0"))
            .Add(Threshold1, RegValue.String("0"))
            .Add(Threshold2, RegValue.String("0"));

    protected override void AfterWrite(RegistryValueSet written)
    {
        int t1 = ParseValue(written.Get(Threshold1), DefaultThreshold1) ?? DefaultThreshold1;
        int t2 = ParseValue(written.Get(Threshold2), DefaultThreshold2) ?? DefaultThreshold2;
        int speed = ParseValue(written.Get(Speed), DefaultSpeed) ?? DefaultSpeed;
        try { _broadcast(t1, t2, speed); }
        catch (Exception ex) { AppLog.Warn(ex, "SPI_SETMOUSE"); }
    }

    /// <summary>Status for the UI / recommendations: true when acceleration is on; null when unreadable.</summary>
    public static bool? ReadAccelerationOn(IOptimizeRegistry? registry = null)
    {
        var set = RegistryValueSet.Read(registry ?? HkcuOptimizeRegistry.Instance, All, "Mouse acceleration");
        return set == null ? null : IsAccelerationOn(set);
    }

    private const uint SPI_SETMOUSE = 0x0004;
    private const uint SPIF_SENDCHANGE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, int[] pvParam, uint fWinIni);

    /// <summary>Live apply without SPIF_UPDATEINIFILE: the registry already holds the exact strings we wrote.</summary>
    private static void BroadcastSpiSetMouse(int threshold1, int threshold2, int speed)
    {
        var p = new[] { threshold1, threshold2, speed };
        if (!SystemParametersInfo(SPI_SETMOUSE, 0, p, SPIF_SENDCHANGE))
            AppLog.Warn(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()), "SPI_SETMOUSE");
    }
}
