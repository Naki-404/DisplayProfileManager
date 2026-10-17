using System.Globalization;
using System.Runtime.InteropServices;
using Visor.Services.Elevation;

namespace Visor.Services.Optimize.Pc;

/// <summary>Per-adapter GPU scheduling caps as the kernel reports them (D3DKMT, no admin).</summary>
/// <param name="WddmVersion">D3DKMT_DRIVERVERSION, e.g. 2700 = WDDM 2.7, 3200 = WDDM 3.2; 0 when unknown.</param>
/// <param name="Supported">HwSchSupported: the driver can run hardware scheduling (WDDM ≥ 2.7 only).</param>
/// <param name="Enabled">HwSchEnabled: hardware scheduling is running now (what was applied at boot).</param>
public sealed record GpuSchedulingCaps(string AdapterLuid, int WddmVersion, bool Supported, bool Enabled, bool EnabledByDefault);

/// <summary>Seam over the kernel query. Null = the API / query is unavailable (older Windows) → callers skip.</summary>
public interface IGpuSchedulingProbe
{
    IReadOnlyList<GpuSchedulingCaps>? Query();
}

/// <summary>
/// Reads <c>KMTQAITYPE_WDDM_2_7_CAPS</c> (70) and <c>KMTQAITYPE_DRIVERVERSION</c> (13) for every adapter via
/// gdi32 D3DKMT (read-only, no admin). Verified on Windows 11 / WDDM 3.2: NVIDIA reports 0xB
/// (supported | enabled | independent VidPn vsync) with HwSchMode absent from the registry.
/// </summary>
public sealed class D3dkmtGpuSchedulingProbe : IGpuSchedulingProbe
{
    public static readonly D3dkmtGpuSchedulingProbe Instance = new();

    private const int KMTQAITYPE_DRIVERVERSION = 13;
    private const int KMTQAITYPE_WDDM_2_7_CAPS = 70;

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public uint LuidLow;
        public int LuidHigh;
        public uint NumOfSources;
        public int bPrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2
    {
        public uint NumAdapters;
        public IntPtr pAdapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 e);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO q);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER c);

    public IReadOnlyList<GpuSchedulingCaps>? Query()
    {
        IntPtr buf = IntPtr.Zero;
        try
        {
            var e = new D3DKMT_ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0 || e.NumAdapters > 64) return null;
            int sz = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
            uint capacity = e.NumAdapters;
            buf = Marshal.AllocHGlobal(sz * (int)capacity);
            e.pAdapters = buf;
            if (D3DKMTEnumAdapters2(ref e) != 0) return null;

            var list = new List<GpuSchedulingCaps>();
            bool anyCapsQuery = false;
            for (int i = 0; i < Math.Min(e.NumAdapters, capacity); i++)
            {
                var a = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(buf + i * sz);
                try
                {
                    int? ver = QueryInt(a.hAdapter, KMTQAITYPE_DRIVERVERSION);
                    int? caps = QueryInt(a.hAdapter, KMTQAITYPE_WDDM_2_7_CAPS);
                    if (caps.HasValue) anyCapsQuery = true;
                    int c = caps ?? 0;
                    list.Add(new GpuSchedulingCaps(
                        ((long)a.LuidHigh << 32 | a.LuidLow).ToString("X", CultureInfo.InvariantCulture),
                        ver ?? 0,
                        Supported: (c & 0x1) != 0,
                        Enabled: (c & 0x2) != 0,
                        EnabledByDefault: (c & 0x4) != 0));
                }
                finally
                {
                    var close = new D3DKMT_CLOSEADAPTER { hAdapter = a.hAdapter };
                    D3DKMTCloseAdapter(ref close);
                }
            }
            // Pre-2004 Windows has no WDDM 2.7 caps query: "unknown", not "unsupported".
            return anyCapsQuery ? list : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "GPU scheduling caps");
            return null;
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
    }

    private static int? QueryInt(uint adapter, int type)
    {
        IntPtr data = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(data, 0);
            var q = new D3DKMT_QUERYADAPTERINFO { hAdapter = adapter, Type = type, pPrivateDriverData = data, PrivateDriverDataSize = 4 };
            return D3DKMTQueryAdapterInfo(ref q) == 0 ? Marshal.ReadInt32(data) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }
}

/// <summary>
/// Hardware-accelerated GPU scheduling, combined view: the HKLM <c>HwSchMode</c> value (what applies at the
/// next boot) and the kernel caps (what runs now). Null members = not readable.
/// </summary>
public sealed record HagsStatus
{
    /// <summary>Raw HwSchMode DWORD (2 = on, 1 = off), null when absent / unreadable.</summary>
    public int? RegistryValue { get; init; }
    public bool RegistryReadable { get; init; }
    /// <summary>Any adapter reports HwSchSupported (needs WDDM ≥ 2.7); null when the caps query is unavailable.</summary>
    public bool? Supported { get; init; }
    /// <summary>Any adapter reports HwSchEnabled — the state applied at boot; null when unknown.</summary>
    public bool? RunningOn { get; init; }
    /// <summary>Highest WDDM version among adapters (2700 = 2.7), 0 when unknown.</summary>
    public int MaxWddmVersion { get; init; }

    /// <summary>What the registry asks for: 2 → on, 1 → off, absent / other → null (driver default).</summary>
    public bool? RegistryOn => RegistryValue switch { 2 => true, 1 => false, _ => null };

    /// <summary>Best current answer: the kernel state, else the registry request.</summary>
    public bool? EffectiveOn => RunningOn ?? RegistryOn;

    /// <summary>Registry asks for a state the kernel does not run yet → a restart is pending.</summary>
    public bool PendingRestart => RegistryOn.HasValue && RunningOn.HasValue && RegistryOn.Value != RunningOn.Value;

    public static string WddmText(int version) =>
        version <= 0 ? "?" : (version / 1000).ToString(CultureInfo.InvariantCulture) + "." + (version % 1000 / 100).ToString(CultureInfo.InvariantCulture);

    public string[] Evidence()
    {
        var e = new List<string>
        {
            "HwSchMode=" + (RegistryValue?.ToString(CultureInfo.InvariantCulture) ?? (RegistryReadable ? "absent" : "unreadable")),
            "running=" + (RunningOn switch { true => "on", false => "off", null => "?" }),
            "supported=" + (Supported switch { true => "yes", false => "no", null => "?" }),
            "wddm=" + WddmText(MaxWddmVersion)
        };
        if (PendingRestart) e.Add("pendingRestart");
        return e.ToArray();
    }
}

public static class HagsStatusReader
{
    public static HagsStatus Read(IMachineRegistryReader? registry = null, IGpuSchedulingProbe? probe = null)
    {
        int? raw = null;
        bool readable = true;
        try { raw = (registry ?? MachineRegistryReader.Instance).GetValue(HagsTarget.KeyPath, HagsTarget.ValueName)?.AsDword; }
        catch (Exception ex)
        {
            readable = false;
            AppLog.Warn(ex, "HAGS registry read");
        }

        IReadOnlyList<GpuSchedulingCaps>? caps = null;
        try { caps = (probe ?? D3dkmtGpuSchedulingProbe.Instance).Query(); }
        catch (Exception ex) { AppLog.Warn(ex, "HAGS caps"); }

        bool? supported = null, running = null;
        int maxWddm = 0;
        if (caps != null)
        {
            maxWddm = caps.Count == 0 ? 0 : caps.Max(c => c.WddmVersion);
            var capable = caps.Where(c => c.WddmVersion >= 2700).ToList();
            supported = capable.Any(c => c.Supported);
            running = capable.Any(c => c.Supported && c.Enabled);
            if (supported == false) running = null; // "off" is meaningless on hardware that cannot do it
        }
        return new HagsStatus
        {
            RegistryValue = raw,
            RegistryReadable = readable,
            Supported = supported,
            RunningOn = running,
            MaxWddmVersion = maxWddm
        };
    }
}

/// <summary>
/// HAGS on / off: HKLM <c>SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode</c> (2 = on, 1 = off)
/// written through the elevated helper. Takes effect after a restart. Undo restores the exact previous
/// value or deletes it when it was absent (driver default). "Already satisfied" also looks at the kernel
/// state, so an absent value on a GPU that already runs HAGS is a no-op for <c>hags.on</c>.
/// </summary>
public sealed class HagsTarget : ElevatedRegistrySafeActionTarget
{
    public const string OnId = "hags.on";
    public const string OffId = "hags.off";
    public const string Key = "hagsMode";
    public const string KeyPath = ElevationAllowlist.GraphicsDriversKey;
    public const string ValueName = "HwSchMode";

    public static readonly RegAddress Address = new(KeyPath, ValueName);
    private static readonly RegAddress[] All = { Address };

    private readonly IGpuSchedulingProbe? _probe;

    public HagsTarget(bool enable, IElevationBroker broker, IMachineRegistryReader? registry = null,
        IGpuSchedulingProbe? probe = null) : base(broker, registry)
    {
        Enable = enable;
        _probe = probe;
    }

    public bool Enable { get; }
    public override string ActionId => Enable ? OnId : OffId;
    public override string JournalKey => Key;
    public override string LocPrefix => "optimize." + Key;
    protected override IReadOnlyList<RegAddress> Addresses => All;
    protected override string Label => "HAGS";

    protected override bool Satisfies(RegistryValueSet current)
    {
        int? v = current.Get(Address)?.AsDword;
        if (v is 2 or 1) return (v == 2) == Enable;
        // Absent / unknown value: the driver default applies — trust the running state when known.
        bool? running = RunningState();
        return running.HasValue && running.Value == Enable;
    }

    protected override RegistryValueSet BuildTarget(RegistryValueSet current) =>
        new RegistryValueSet().Add(Address, RegValue.Dword(Enable ? 2 : 1));

    private bool? RunningState()
    {
        try
        {
            var caps = (_probe ?? D3dkmtGpuSchedulingProbe.Instance).Query();
            if (caps == null) return null;
            var capable = caps.Where(c => c.WddmVersion >= 2700 && c.Supported).ToList();
            return capable.Count == 0 ? null : capable.Any(c => c.Enabled);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
