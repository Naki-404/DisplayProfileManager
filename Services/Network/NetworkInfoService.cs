using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Visor.Services;

public sealed class NetworkAdapterInfo
{
    public required string Name { get; init; }
    /// <summary>Stable type code: ethernet | wifi | other.</summary>
    public required string TypeCode { get; init; }
    public OperationalStatus Status { get; init; }
    public long? SpeedBps { get; init; }
    public string? IPv4 { get; init; }
    public string? IPv6 { get; init; }
    /// <summary>Preferred gateway for probes: IPv4 when present, else the IPv6 gateway.</summary>
    public string? Gateway { get; init; }
    public string? GatewayV6 { get; init; }
    public IReadOnlyList<string> DnsServers { get; init; } = Array.Empty<string>();
    /// <summary>True when the OS routing table named this adapter for the default route.</summary>
    public bool OwnsDefaultRoute { get; init; }
    /// <summary>OS adapter Id (GUID string) — used to re-find the NIC for byte counters.</summary>
    public string? Id { get; init; }
    public int? InterfaceIndex { get; init; }
    public string? Description { get; init; }
    public int? Mtu { get; init; }
    /// <summary>IPv4 address assigned by DHCP (null = unknown, false = static).</summary>
    public bool? IsDhcpEnabled { get; init; }
    public string? DhcpServer { get; init; }
    public bool IPv6Enabled { get; init; }
}

/// <summary>Read-only view of the active (preferred) network interface. Never exposes Wi-Fi passwords.</summary>
public static class NetworkInfoService
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

    private static readonly object CacheGate = new();
    private static NetworkAdapterInfo? _cached;
    private static DateTime _cachedUtc = DateTime.MinValue;
    private static bool _cacheValid;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    /// <summary>Primary adapter; cached for <see cref="CacheTtl"/> (Home refresh calls this on every monitor event).</summary>
    public static NetworkAdapterInfo? CapturePrimary(bool forceRefresh = false)
    {
        lock (CacheGate)
        {
            if (!forceRefresh && _cacheValid && DateTime.UtcNow - _cachedUtc < CacheTtl)
                return _cached;
        }
        var fresh = CaptureUncached();
        lock (CacheGate)
        {
            _cached = fresh;
            _cachedUtc = DateTime.UtcNow;
            _cacheValid = true;
        }
        return fresh;
    }

    private static NetworkAdapterInfo? CaptureUncached()
    {
        try
        {
            var all = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .ToList();
            if (all.Count == 0) return null;

            int? bestIndex = TryGetDefaultRouteIndex();
            NetworkInterface? nic = null;
            if (bestIndex is int idx)
                nic = all.FirstOrDefault(n => InterfaceIndexV4(n) == idx || InterfaceIndexV6(n) == idx);
            nic ??= all
                .Where(n => n.GetIPProperties().UnicastAddresses.Any(a =>
                    a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                .OrderByDescending(Rank)
                .FirstOrDefault();
            if (nic == null) return null;

            var props = nic.GetIPProperties();
            string? ipv4 = props.UnicastAddresses
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocal(a))
                ?.ToString()
                ?? props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    ?.Address.ToString();
            string? ipv6 = props.UnicastAddresses
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal)
                ?.ToString();
            string? gw4 = props.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsUsableGateway(a))
                ?.ToString();
            string? gw6 = props.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && IsUsableGateway(a))
                ?.ToString();
            var dns = props.DnsAddresses
                .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Select(a => a.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            long? speed = null;
            try
            {
                if (nic.Speed > 0 && nic.Speed < long.MaxValue)
                    speed = nic.Speed;
            }
            catch { /* some adapters throw */ }

            int? mtu = null;
            bool? dhcp = null;
            int? ifIndex = InterfaceIndexV4(nic) ?? InterfaceIndexV6(nic);
            try
            {
                var v4 = nic.Supports(NetworkInterfaceComponent.IPv4) ? props.GetIPv4Properties() : null;
                if (v4 != null)
                {
                    mtu = v4.Mtu > 0 ? v4.Mtu : null;
                    dhcp = v4.IsDhcpEnabled;
                }
            }
            catch { /* unsupported on some adapters */ }
            string? dhcpServer = null;
            try
            {
                dhcpServer = props.DhcpServerAddresses
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
            }
            catch { /* ignore */ }

            return new NetworkAdapterInfo
            {
                Name = nic.Name,
                TypeCode = TypeCodeOf(nic.NetworkInterfaceType),
                Status = nic.OperationalStatus,
                SpeedBps = speed,
                IPv4 = ipv4,
                IPv6 = ipv6,
                Gateway = gw4 ?? gw6,
                GatewayV6 = gw6,
                DnsServers = dns,
                OwnsDefaultRoute = bestIndex is int b && (InterfaceIndexV4(nic) == b || InterfaceIndexV6(nic) == b),
                Id = nic.Id,
                InterfaceIndex = ifIndex,
                Description = nic.Description,
                Mtu = mtu,
                IsDhcpEnabled = dhcp,
                DhcpServer = dhcpServer,
                IPv6Enabled = ipv6 != null
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Network adapter capture");
            return null;
        }
    }

    /// <summary>Interface index the OS would use to reach the public Internet (IPv4), or null.</summary>
    private static int? TryGetDefaultRouteIndex()
    {
        try
        {
            // 1.1.1.1 in network byte order; no packet is sent — this is a routing-table lookup.
            uint dest = BitConverter.ToUInt32(new byte[] { 1, 1, 1, 1 }, 0);
            if (GetBestInterface(dest, out uint index) == 0 && index != 0)
                return (int)index;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "GetBestInterface");
        }
        return null;
    }

    private static int? InterfaceIndexV4(NetworkInterface n)
    {
        try { return n.Supports(NetworkInterfaceComponent.IPv4) ? n.GetIPProperties().GetIPv4Properties()?.Index : null; }
        catch { return null; }
    }

    private static int? InterfaceIndexV6(NetworkInterface n)
    {
        try { return n.Supports(NetworkInterfaceComponent.IPv6) ? n.GetIPProperties().GetIPv6Properties()?.Index : null; }
        catch { return null; }
    }

    /// <summary>Mbps numeric part for localized formatting; null if N/A. Gbps when &gt;= 1000 Mbps.</summary>
    public static (double Value, bool Gigabits)? LinkSpeedParts(long? bps)
    {
        if (bps is null or <= 0) return null;
        double mbps = bps.Value / 1_000_000.0;
        if (mbps >= 1000)
            return (mbps / 1000.0, true);
        return (mbps, false);
    }

    public static bool IsLinkLocal(IPAddress a) =>
        a.AddressFamily == AddressFamily.InterNetwork
        && a.GetAddressBytes() is { Length: 4 } b
        && b[0] == 169 && b[1] == 254;

    /// <summary>
    /// Gateway usable for probes. IPv4: not loopback, unspecified, or APIPA.
    /// IPv6: not loopback / unspecified (link-local fe80:: routers are normal and allowed).
    /// </summary>
    public static bool IsUsableGateway(IPAddress a)
    {
        if (a == null) return false;
        if (IPAddress.IsLoopback(a)) return false;
        if (a.AddressFamily == AddressFamily.InterNetwork)
            return !a.Equals(IPAddress.Any) && !IsLinkLocal(a);
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return !a.Equals(IPAddress.IPv6Any);
        return false;
    }

    /// <summary>
    /// Pure ranking inputs (for tests). Higher = preferred.
    /// Mirrors <see cref="Rank(NetworkInterface)"/> weights without needing a live NIC.
    /// </summary>
    public static int RankScore(bool hasGateway, bool hasRealIp, bool onlyLinkLocal,
        bool likelyVirtual, bool ethernet, bool wifi, int speedMbps)
    {
        int score = 0;
        if (hasGateway) score += 10_000;
        if (hasRealIp) score += 5_000;
        if (onlyLinkLocal) score -= 5_000;
        if (likelyVirtual) score -= 8_000;
        if (ethernet) score += 50;
        else if (wifi) score += 40;
        if (speedMbps > 0)
            score += Math.Min(speedMbps, 2_000);
        return score;
    }

    /// <summary>
    /// Host-side virtual switches (Hyper-V / WSL / VirtualBox / VMware / Docker). VPN and TAP adapters are
    /// deliberately NOT matched by name — they are ordinary <see cref="NetworkInterfaceType"/>s and may own the default route.
    /// </summary>
    public static bool IsLikelyVirtual(string name) =>
        !string.IsNullOrEmpty(name)
        && (name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || name.Contains("WSL", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Docker", StringComparison.OrdinalIgnoreCase));

    /// <summary>Fallback when the routing table gives no answer. Higher = preferred. Gateway &gt; real IP &gt; physical &gt; speed.</summary>
    public static int Rank(NetworkInterface n)
    {
        try
        {
            var props = n.GetIPProperties();
            bool hasGateway = props.GatewayAddresses.Any(g => IsUsableGateway(g.Address));
            bool hasRealIp = props.UnicastAddresses.Any(a =>
                (a.Address.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocal(a.Address))
                || (a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal));
            bool onlyLinkLocal = !hasRealIp && props.UnicastAddresses.Any(a =>
                a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);
            bool ethernet = n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx
                or NetworkInterfaceType.Ethernet3Megabit;
            bool wifi = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            bool virt = IsLikelyVirtual(n.Name) || IsLikelyVirtual(n.Description);
            int mbps = 0;
            try
            {
                long spd = n.Speed;
                if (spd > 0 && spd < long.MaxValue)
                    mbps = (int)Math.Min(spd / 1_000_000, int.MaxValue);
            }
            catch { /* ignore */ }

            return RankScore(hasGateway, hasRealIp, onlyLinkLocal, virt, ethernet, wifi, mbps);
        }
        catch
        {
            return 0;
        }
    }

    public static string TypeCodeOf(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.GigabitEthernet => "ethernet",
        NetworkInterfaceType.Wireless80211 => "wifi",
        _ => "other"
    };
}
