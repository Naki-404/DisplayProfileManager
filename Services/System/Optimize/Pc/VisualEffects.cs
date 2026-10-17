using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Visor.Services.Optimize.Pc;

/// <summary>
/// Live side of <see cref="VisualEffectsSafeActionTarget"/>: pushes the values just written to the registry into the
/// running session. Tests inject a recorder; production is <see cref="SpiVisualEffectsLive"/>.
/// </summary>
public interface IVisualEffectsLive
{
    /// <summary><c>SystemParametersInfo(spiSetAction, 0, (PVOID)value, SPIF_SENDCHANGE)</c> for a user-preference BOOL.</summary>
    void SetPreference(uint spiSetAction, bool value);

    /// <summary><c>SystemParametersInfo(SPI_SETANIMATION, sizeof(ANIMATIONINFO), &amp;{iMinAnimate}, SPIF_SENDCHANGE)</c>.</summary>
    void SetMinimizeAnimation(bool on);

    /// <summary><c>WM_SETTINGCHANGE</c> broadcast for the values that have no SPI (taskbar animations, Peek).</summary>
    void BroadcastSettingChange();
}

/// <summary>
/// "Visual effects: best performance, keep it readable" (HKCU only, no administrator rights).
/// <para>
/// Turns off: window minimize / maximize animation (<c>WindowMetrics\MinAnimate</c>), menu / combo-box / list-box
/// animations, menu fade, selection fade, tooltip animation and fade, the shadow under the mouse pointer and under
/// windows (bits of <c>Control Panel\Desktop\UserPreferencesMask</c>), taskbar animations
/// (<c>Explorer\Advanced\TaskbarAnimations</c>) and Peek (<c>DWM\EnableAeroPeek</c>). Sets
/// <c>Explorer\VisualEffects\VisualFXSetting</c> = 3 ("Custom") so Windows' Performance Options dialog shows the
/// real state.
/// </para>
/// <para>
/// Deliberately left on (readability / usability, no measurable cost on any GPU that runs games): ClearType / font
/// smoothing, thumbnails instead of icons, window contents while dragging, the translucent selection rectangle,
/// drop shadows for desktop icon labels (<c>ListviewShadow</c> — without it labels are hard to read on a light
/// wallpaper), and the UI-effects master switch (<c>SPI_SETUIEFFECTS</c>; turning it off also kills hot-tracking
/// and other cues).
/// </para>
/// <para>
/// Every touched value is journaled exactly (<see cref="RegistryValueSet"/>: kind + bytes, absent = delete on undo).
/// After every write — apply and undo — the matching <c>SystemParametersInfo</c> calls run with the values that are
/// now in the registry (no <c>SPIF_UPDATEINIFILE</c>: the registry already holds the exact bytes) and a
/// <c>WM_SETTINGCHANGE</c> is broadcast. The SafeAction service then re-reads the registry to verify.
/// Desktop / Explorer snappiness only: no effect on in-game FPS.
/// </para>
/// </summary>
public sealed class VisualEffectsSafeActionTarget : RegistrySafeActionTarget
{
    public const string Id = "visualFx.performance";
    public const string Key = "visualFx";

    public const string DesktopKey = @"Control Panel\Desktop";
    public const string WindowMetricsKey = @"Control Panel\Desktop\WindowMetrics";
    public const string ExplorerAdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    public const string DwmKey = @"Software\Microsoft\Windows\DWM";
    public const string VisualEffectsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";

    public static readonly RegAddress PreferencesMask = new(DesktopKey, "UserPreferencesMask");
    public static readonly RegAddress MinAnimate = new(WindowMetricsKey, "MinAnimate");
    public static readonly RegAddress TaskbarAnimations = new(ExplorerAdvancedKey, "TaskbarAnimations");
    public static readonly RegAddress AeroPeek = new(DwmKey, "EnableAeroPeek");
    public static readonly RegAddress VisualFxSetting = new(VisualEffectsKey, "VisualFXSetting");

    private static readonly RegAddress[] All = { PreferencesMask, MinAnimate, TaskbarAnimations, AeroPeek, VisualFxSetting };

    /// <summary>"Custom" in the Performance Options dialog (0 = let Windows choose, 1 = appearance, 2 = performance).</summary>
    public const int VisualFxCustom = 3;

    /// <summary>Windows 10 / 11 default mask, used only when the value is absent (to know what is live right now).</summary>
    public static readonly byte[] DefaultMask = { 0x9E, 0x3E, 0x07, 0x80, 0x12, 0x00, 0x00, 0x00 };

    // SPI_SET* user-preference actions. Their bit in UserPreferencesMask is (action - 0x1001) / 2.
    public const uint SPI_SETMENUANIMATION = 0x1003;
    public const uint SPI_SETCOMBOBOXANIMATION = 0x1005;
    public const uint SPI_SETLISTBOXSMOOTHSCROLLING = 0x1007;
    public const uint SPI_SETMENUFADE = 0x1013;
    public const uint SPI_SETSELECTIONFADE = 0x1015;
    public const uint SPI_SETTOOLTIPANIMATION = 0x1017;
    public const uint SPI_SETTOOLTIPFADE = 0x1019;
    public const uint SPI_SETCURSORSHADOW = 0x101B;
    public const uint SPI_SETDROPSHADOW = 0x1025;

    /// <summary>The effects this target turns off, in the order their SPIs are called.</summary>
    public static readonly IReadOnlyList<uint> TurnedOff = new[]
    {
        SPI_SETMENUANIMATION, SPI_SETCOMBOBOXANIMATION, SPI_SETLISTBOXSMOOTHSCROLLING,
        SPI_SETMENUFADE, SPI_SETSELECTIONFADE, SPI_SETTOOLTIPANIMATION, SPI_SETTOOLTIPFADE,
        SPI_SETCURSORSHADOW, SPI_SETDROPSHADOW
    };

    private readonly IVisualEffectsLive _live;

    public VisualEffectsSafeActionTarget(IOptimizeRegistry? registry = null, IVisualEffectsLive? live = null) : base(registry)
    {
        _live = live ?? SpiVisualEffectsLive.Instance;
    }

    public override string ActionId => Id;
    public override string JournalKey => Key;
    public override string LocPrefix => "optimize.visualFx";
    protected override IReadOnlyList<RegAddress> Addresses => All;
    protected override string Label => "Visual effects";

    // ---------------------------------------------------------------- pure helpers

    /// <summary>(byte index, bit) of a SPI_SET* user preference inside UserPreferencesMask.</summary>
    public static (int Byte, byte Bit) MaskBit(uint spiSetAction)
    {
        int index = (int)(spiSetAction - 0x1001) / 2;
        return (index / 8, (byte)(1 << (index % 8)));
    }

    /// <summary>Mask bytes when the value is a usable REG_BINARY (≥ 4 bytes); the Windows default when absent; null when malformed.</summary>
    public static byte[]? MaskBytes(RegValue? value)
    {
        if (value == null) return (byte[])DefaultMask.Clone();
        if (value.Kind != RegValueKind.Binary) return null;
        try
        {
            var bytes = Convert.FromHexString(value.Data);
            return bytes.Length >= 4 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static bool IsBitSet(byte[] mask, uint spiSetAction)
    {
        var (b, bit) = MaskBit(spiSetAction);
        return b < mask.Length && (mask[b] & bit) != 0;
    }

    /// <summary>Copy of <paramref name="mask"/> with every <see cref="TurnedOff"/> bit cleared (length and other bits unchanged).</summary>
    public static byte[] PerformanceMask(byte[] mask)
    {
        var copy = (byte[])mask.Clone();
        foreach (var spi in TurnedOff)
        {
            var (b, bit) = MaskBit(spi);
            if (b < copy.Length) copy[b] &= (byte)~bit;
        }
        return copy;
    }

    /// <summary>MinAnimate is REG_SZ "0" / "1" (a DWORD is tolerated); absent = on (Windows default); unparsable = null.</summary>
    public static bool? MinAnimateOn(RegValue? value)
    {
        if (value == null) return true;
        if (value.Kind == RegValueKind.Dword) return value.AsDword != 0;
        if (value.Kind != RegValueKind.String) return null;
        return int.TryParse(value.Data.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n != 0 : null;
    }

    /// <summary>DWORD flag where absent means on (TaskbarAnimations, EnableAeroPeek).</summary>
    public static bool DwordFlagOn(RegValue? value) => (value?.AsDword ?? 1) != 0;

    /// <summary>
    /// Names of the effects that are still on (empty = already at "best performance, readable"). An unreadable
    /// mask is skipped rather than guessed.
    /// </summary>
    public static IReadOnlyList<string> EffectsOn(RegistryValueSet set)
    {
        var on = new List<string>();
        var mask = MaskBytes(set.Get(PreferencesMask));
        if (mask != null)
        {
            foreach (var spi in TurnedOff)
                if (IsBitSet(mask, spi)) on.Add(EffectName(spi));
        }
        if (MinAnimateOn(set.Get(MinAnimate)) != false) on.Add("minAnimate");
        if (DwordFlagOn(set.Get(TaskbarAnimations))) on.Add("taskbarAnimations");
        if (DwordFlagOn(set.Get(AeroPeek))) on.Add("peek");
        return on;
    }

    public static string EffectName(uint spi) => spi switch
    {
        SPI_SETMENUANIMATION => "menuAnimation",
        SPI_SETCOMBOBOXANIMATION => "comboBoxAnimation",
        SPI_SETLISTBOXSMOOTHSCROLLING => "listBoxSmoothScrolling",
        SPI_SETMENUFADE => "menuFade",
        SPI_SETSELECTIONFADE => "selectionFade",
        SPI_SETTOOLTIPANIMATION => "toolTipAnimation",
        SPI_SETTOOLTIPFADE => "toolTipFade",
        SPI_SETCURSORSHADOW => "cursorShadow",
        SPI_SETDROPSHADOW => "dropShadow",
        _ => "0x" + spi.ToString("X4", CultureInfo.InvariantCulture)
    };

    // ---------------------------------------------------------------- RegistrySafeActionTarget

    /// <summary>VisualFXSetting is bookkeeping for the Windows dialog only — it does not decide "satisfied".</summary>
    protected override bool Satisfies(RegistryValueSet current) => EffectsOn(current).Count == 0;

    protected override RegistryValueSet BuildTarget(RegistryValueSet current)
    {
        var rawMask = current.Get(PreferencesMask);
        var mask = MaskBytes(rawMask);
        // A malformed mask is never rewritten: keep it byte for byte, only the other values change.
        var maskValue = mask == null ? rawMask : RegValue.Binary(PerformanceMask(mask));
        return new RegistryValueSet()
            .Add(PreferencesMask, maskValue)
            .Add(MinAnimate, RegValue.String("0"))
            .Add(TaskbarAnimations, RegValue.Dword(0))
            .Add(AeroPeek, RegValue.Dword(0))
            .Add(VisualFxSetting, RegValue.Dword(VisualFxCustom));
    }

    /// <summary>Applies whatever is in the registry now (target on apply, the exact previous values on undo).</summary>
    protected override void AfterWrite(RegistryValueSet written)
    {
        try
        {
            var mask = MaskBytes(written.Get(PreferencesMask));
            if (mask != null)
            {
                foreach (var spi in TurnedOff)
                    _live.SetPreference(spi, IsBitSet(mask, spi));
            }
            _live.SetMinimizeAnimation(MinAnimateOn(written.Get(MinAnimate)) != false);
            _live.BroadcastSettingChange();
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Visual effects live apply");
        }
    }

    /// <summary>Status for the UI: true = some effect is still on, false = at target, null = unreadable.</summary>
    public static bool? ReadEffectsOn(IOptimizeRegistry? registry = null)
    {
        var set = RegistryValueSet.Read(registry ?? HkcuOptimizeRegistry.Instance, All, "Visual effects");
        return set == null ? null : EffectsOn(set).Count > 0;
    }

    /// <summary>Effect names still on (empty when at target, null when unreadable) — evidence chips.</summary>
    public static IReadOnlyList<string>? ReadEffectsOnList(IOptimizeRegistry? registry = null)
    {
        var set = RegistryValueSet.Read(registry ?? HkcuOptimizeRegistry.Instance, All, "Visual effects");
        return set == null ? null : EffectsOn(set);
    }
}

/// <summary>Real <see cref="IVisualEffectsLive"/>: user32 SystemParametersInfo + a WM_SETTINGCHANGE broadcast.</summary>
public sealed class SpiVisualEffectsLive : IVisualEffectsLive
{
    public static readonly SpiVisualEffectsLive Instance = new();

    private const uint SPI_SETANIMATION = 0x0049;
    private const uint SPIF_SENDCHANGE = 0x0002;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct ANIMATIONINFO
    {
        public uint cbSize;
        public int iMinAnimate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfoAnimation(uint uiAction, uint uiParam, ref ANIMATIONINFO pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string? lParam, uint flags, uint timeout, out UIntPtr result);

    public void SetPreference(uint spiSetAction, bool value)
    {
        // For the user-preference SPIs the BOOL travels in pvParam itself.
        if (!SystemParametersInfo(spiSetAction, 0, value ? new IntPtr(1) : IntPtr.Zero, SPIF_SENDCHANGE))
            AppLog.Warn(new Win32Exception(Marshal.GetLastWin32Error()),
                "SystemParametersInfo " + VisualEffectsSafeActionTarget.EffectName(spiSetAction));
    }

    public void SetMinimizeAnimation(bool on)
    {
        var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(), iMinAnimate = on ? 1 : 0 };
        if (!SystemParametersInfoAnimation(SPI_SETANIMATION, info.cbSize, ref info, SPIF_SENDCHANGE))
            AppLog.Warn(new Win32Exception(Marshal.GetLastWin32Error()), "SPI_SETANIMATION");
    }

    public void BroadcastSettingChange()
    {
        // "TraySettings" makes Explorer re-read the taskbar values; the generic broadcast covers the rest.
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, "TraySettings", SMTO_ABORTIFHUNG, 1000, out _);
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, null, SMTO_ABORTIFHUNG, 1000, out _);
    }
}
