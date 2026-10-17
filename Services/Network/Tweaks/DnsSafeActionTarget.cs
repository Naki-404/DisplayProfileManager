using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Visor.Services.Elevation;
using Visor.Services.Optimize;

namespace Visor.Services.NetworkTweaks;

/// <summary>
/// IPv4 DNS configuration of one adapter: automatic (DHCP / router) or a static ordered list.
/// Journal form: <c>dhcp</c> or <c>static:1.1.1.1,1.0.0.1</c>.
/// </summary>
public sealed record DnsServerState(bool IsDhcp, IReadOnlyList<string> Servers)
{
    public const string DhcpToken = "dhcp";
    public const string StaticPrefix = "static:";

    public static readonly DnsServerState Dhcp = new(true, Array.Empty<string>());

    public static DnsServerState Static(IEnumerable<string> servers) => new(false, servers.ToArray());

    public string Serialize() => IsDhcp ? DhcpToken : StaticPrefix + string.Join(",", Servers);

    public static bool TryParse(string? text, out DnsServerState state)
    {
        state = Dhcp;
        if (string.IsNullOrEmpty(text)) return false;
        if (text == DhcpToken) return true;
        if (!text.StartsWith(StaticPrefix, StringComparison.Ordinal)) return false;
        if (!ElevationAllowlist.TryParseDnsServers(text[StaticPrefix.Length..], out var list)) return false;
        state = Static(list.Select(a => a.ToString()));
        return true;
    }

    /// <summary>
    /// The adapter's <c>NameServer</c> registry value (Tcpip\Parameters\Interfaces\{GUID}): empty / absent =
    /// DNS comes from DHCP; otherwise the static list (comma or space separated). Null when the value holds
    /// something the helper could not restore exactly (non-IPv4 token, more than 4 servers).
    /// </summary>
    public static DnsServerState? FromNameServer(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Dhcp;
        var tokens = value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > ElevationAllowlist.MaxDnsServers) return null;
        var list = new List<string>(tokens.Length);
        foreach (var t in tokens)
        {
            if (!ElevationAllowlist.IsCanonicalIPv4(t.Trim(), out var ip)) return null;
            string s = ip!.ToString();
            if (list.Contains(s)) return null;
            list.Add(s);
        }
        return Static(list);
    }

    public bool SameAs(DnsServerState other) =>
        other != null && IsDhcp == other.IsDhcp && Servers.SequenceEqual(other.Servers, StringComparer.Ordinal);
}

/// <summary>
/// "Use these DNS servers on this adapter" (<c>dns.set|{GUID}|a.b.c.d,e.f.g.h</c>). Previous state is read
/// without admin from the adapter's <c>NameServer</c> value (empty = DHCP); apply and undo go through the
/// elevated helper (WMI SetDNSServerSearchOrder: a list, or null to return to DHCP) and are verified by
/// re-reading the value. All server choices for one adapter share the undo slot <c>dns|{GUID}</c>, and
/// <see cref="SafeActionService.ApplyAsync"/> keeps the pre-Visor state when you switch twice.
/// </summary>
public sealed class DnsSafeActionTarget : ISafeActionTarget
{
    public const string IdPrefix = "dns.set|";
    public const string JournalPrefix = "dns|";
    public const string NameServerValue = "NameServer";

    private readonly IElevationBroker _broker;
    private readonly IMachineRegistryReader _registry;
    private readonly Func<bool> _flushCache;
    private readonly DnsServerState _target;

    public DnsSafeActionTarget(string interfaceGuid, IReadOnlyList<IPAddress> servers, IElevationBroker broker,
        IMachineRegistryReader? registry = null, Func<bool>? flushCache = null)
    {
        if (!ElevationAllowlist.IsBracedGuid(interfaceGuid)) throw new ArgumentException("{GUID} expected", nameof(interfaceGuid));
        ArgumentNullException.ThrowIfNull(servers);
        string csv = string.Join(",", servers.Select(s => s.ToString()));
        if (!ElevationAllowlist.TryParseDnsServers(csv, out var parsed)) throw new ArgumentException("1..4 IPv4 servers expected", nameof(servers));
        InterfaceGuid = interfaceGuid.ToUpperInvariant();
        _target = DnsServerState.Static(parsed.Select(a => a.ToString()));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _registry = registry ?? MachineRegistryReader.Instance;
        _flushCache = flushCache ?? DnsCache.Flush;
    }

    public string InterfaceGuid { get; }
    public IReadOnlyList<string> TargetServers => _target.Servers;
    public string ActionId => ActionIdFor(InterfaceGuid, _target.Servers);
    public string JournalKey => JournalPrefix + InterfaceGuid;
    public bool BlockedDuringGameSession => false;
    public string LocPrefix => "net.tweak.dns";
    public bool RequiresElevation => true;

    public string InterfaceKey => ElevationAllowlist.TcpipInterfacesKey + "\\" + InterfaceGuid;

    public static string ActionIdFor(string interfaceGuid, IEnumerable<string> servers) =>
        IdPrefix + interfaceGuid.ToUpperInvariant() + "|" + string.Join(",", servers);

    /// <summary>Parses <c>dns.set|{GUID}|a,b</c>. False for anything else (including invalid servers).</summary>
    public static bool TryParseActionId(string? actionId, out string interfaceGuid, out IReadOnlyList<IPAddress> servers)
    {
        interfaceGuid = "";
        servers = Array.Empty<IPAddress>();
        if (actionId == null || !actionId.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var parts = actionId[IdPrefix.Length..].Split('|');
        if (parts.Length != 2 || !ElevationAllowlist.IsBracedGuid(parts[0])) return false;
        if (!ElevationAllowlist.TryParseDnsServers(parts[1], out servers)) return false;
        interfaceGuid = parts[0].ToUpperInvariant();
        return true;
    }

    public DnsServerState? ReadDnsState()
    {
        try
        {
            if (!_registry.KeyExists(InterfaceKey)) return null;
            var v = _registry.GetValue(InterfaceKey, NameServerValue);
            if (v == null) return DnsServerState.Dhcp;
            if (v.Kind != RegValueKind.String) return null;
            return DnsServerState.FromNameServer(v.Data);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "DNS state read");
            return null;
        }
    }

    public string? ReadState() => ReadDnsState()?.Serialize();

    public string? ResolveTargetState() => ReadDnsState() == null ? null : _target.Serialize();

    public bool SatisfiesTarget(string state, string targetState) =>
        DnsServerState.TryParse(state, out var s) && DnsServerState.TryParse(targetState, out var t) && s.SameAs(t);

    public bool StatesEqual(string a, string b) => SatisfiesTarget(a, b);

    public bool WriteState(string state) => false;

    public async Task<SafeActionWriteResult> WriteStateAsync(string state, CancellationToken ct)
    {
        if (!DnsServerState.TryParse(state, out var s))
            return SafeActionWriteResult.Fail(SafeActionCodes.BadBackup);
        var op = s.IsDhcp
            ? new ElevatedOp(ElevatedOpKind.DnsSetDhcp, InterfaceGuid)
            : new ElevatedOp(ElevatedOpKind.DnsSetStatic, InterfaceGuid, null, string.Join(",", s.Servers));
        var r = await ElevatedWrites.RunAsync(_broker, new[] { op }, ct, "DNS").ConfigureAwait(false);
        if (r.Written)
        {
            // Old answers from the previous servers would otherwise linger for their TTL.
            try { _flushCache(); }
            catch (Exception ex) { AppLog.Warn(ex, "DNS flush after change"); }
        }
        return r;
    }

    /// <summary>IPv4 DNS servers Windows currently uses on the adapter (DHCP or static) — for display / the benchmark baseline.</summary>
    public static IReadOnlyList<IPAddress> EffectiveDnsServers(string interfaceGuid)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Id, interfaceGuid, StringComparison.OrdinalIgnoreCase));
            if (nic == null) return Array.Empty<IPAddress>();
            return nic.GetIPProperties().DnsAddresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .ToArray();
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException)
        {
            AppLog.Warn(ex, "Effective DNS");
            return Array.Empty<IPAddress>();
        }
    }
}
