using Visor.Services.Elevation;
using Visor.Services.Optimize;

namespace Visor.Services.NetworkTweaks;

/// <summary>
/// "Nagle off" for one adapter (<c>tcp.nagle.off|{GUID}</c>): <c>TcpAckFrequency=1</c> and <c>TCPNoDelay=1</c>
/// under Tcpip\Parameters\Interfaces\{GUID}. Only affects TCP traffic of that adapter (MMOs such as WoW,
/// some launchers / chat) — most shooters use UDP and see no difference. Undo restores both values
/// exactly (deletes them when they were absent). Takes effect for new connections / after a reconnect.
/// </summary>
public sealed class NagleSafeActionTarget : ElevatedRegistrySafeActionTarget
{
    public const string IdPrefix = "tcp.nagle.off|";
    public const string TcpAckFrequency = "TcpAckFrequency";
    public const string TcpNoDelay = "TCPNoDelay";

    public NagleSafeActionTarget(string interfaceGuid, IElevationBroker broker, IMachineRegistryReader? registry = null)
        : base(broker, registry)
    {
        if (!ElevationAllowlist.IsBracedGuid(interfaceGuid)) throw new ArgumentException("{GUID} expected", nameof(interfaceGuid));
        InterfaceGuid = interfaceGuid.ToUpperInvariant();
        InterfaceKey = ElevationAllowlist.TcpipInterfacesKey + "\\" + InterfaceGuid;
        _addresses = new[] { new RegAddress(InterfaceKey, TcpAckFrequency), new RegAddress(InterfaceKey, TcpNoDelay) };
    }

    private readonly RegAddress[] _addresses;

    public string InterfaceGuid { get; }
    public string InterfaceKey { get; }
    public override string ActionId => IdPrefix + InterfaceGuid;
    public override string LocPrefix => "net.tweak.nagle";
    protected override IReadOnlyList<RegAddress> Addresses => _addresses;
    protected override string? RequiredKey => InterfaceKey;
    protected override string Label => "Nagle";

    public static bool TryParseActionId(string? actionId, out string interfaceGuid)
    {
        interfaceGuid = "";
        if (actionId == null || !actionId.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        string g = actionId[IdPrefix.Length..];
        if (!ElevationAllowlist.IsBracedGuid(g)) return false;
        interfaceGuid = g.ToUpperInvariant();
        return true;
    }

    protected override bool Satisfies(RegistryValueSet current) =>
        current.Get(_addresses[0])?.AsDword == 1 && current.Get(_addresses[1])?.AsDword == 1;

    protected override RegistryValueSet BuildTarget(RegistryValueSet current) =>
        new RegistryValueSet().Add(_addresses[0], RegValue.Dword(1)).Add(_addresses[1], RegValue.Dword(1));
}

/// <summary>
/// MMCSS network throttling (<c>mmcss.net</c>): <c>NetworkThrottlingIndex=0xFFFFFFFF</c> (off; default 10)
/// and <c>SystemResponsiveness=10</c> (default 20) under Multimedia\SystemProfile. Honest scope: Windows
/// throttles non-multimedia network processing only while MMCSS-registered audio / video plays, so the
/// effect is limited to "game + music / stream / voice in the background" — and often not measurable.
/// Undo restores both values exactly. Needs a reboot (or MMCSS restart) to take full effect.
/// </summary>
public sealed class NetworkThrottlingSafeActionTarget : ElevatedRegistrySafeActionTarget
{
    public const string Id = "mmcss.net";
    public const string NetworkThrottlingIndex = "NetworkThrottlingIndex";
    public const string SystemResponsiveness = "SystemResponsiveness";
    /// <summary>0xFFFFFFFF as the signed journal form.</summary>
    public const int ThrottlingOff = -1;
    public const int TargetResponsiveness = 10;

    public static readonly RegAddress ThrottlingAddress = new(ElevationAllowlist.MultimediaSystemProfileKey, NetworkThrottlingIndex);
    public static readonly RegAddress ResponsivenessAddress = new(ElevationAllowlist.MultimediaSystemProfileKey, SystemResponsiveness);
    private static readonly RegAddress[] AddressList = { ThrottlingAddress, ResponsivenessAddress };

    public NetworkThrottlingSafeActionTarget(IElevationBroker broker, IMachineRegistryReader? registry = null)
        : base(broker, registry) { }

    public override string ActionId => Id;
    public override string LocPrefix => "net.tweak.mmcss";
    protected override IReadOnlyList<RegAddress> Addresses => AddressList;
    protected override string? RequiredKey => ElevationAllowlist.MultimediaSystemProfileKey;
    protected override string Label => "MMCSS network";

    /// <summary>Windows treats 0..10 as 10 (it rounds up to a multiple of 10 with a floor of 10).</summary>
    private static bool ResponsivenessAtTarget(RegValue? v) =>
        v?.AsDword is int sr && sr >= 0 && sr <= TargetResponsiveness;

    protected override bool Satisfies(RegistryValueSet current) =>
        current.Get(ThrottlingAddress)?.AsDword == ThrottlingOff && ResponsivenessAtTarget(current.Get(ResponsivenessAddress));

    protected override RegistryValueSet BuildTarget(RegistryValueSet current)
    {
        var sr = current.Get(ResponsivenessAddress);
        return new RegistryValueSet()
            .Add(ThrottlingAddress, RegValue.Dword(ThrottlingOff))
            .Add(ResponsivenessAddress, ResponsivenessAtTarget(sr) ? sr : RegValue.Dword(TargetResponsiveness));
    }
}
