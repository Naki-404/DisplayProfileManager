using System.Net;
using Visor.Services.Elevation;

namespace Visor.Services.NetworkTweaks;

/// <summary>
/// Entry point for the UI: builds the network SafeAction targets and resolves the per-adapter ids
/// (<c>dns.set|{GUID}|servers</c>, <c>tcp.nagle.off|{GUID}</c>) so journaled entries stay undoable after a
/// restart. Every target here <see cref="ISafeActionTarget.RequiresElevation"/> — use
/// <see cref="SafeActionService.ApplyAsync"/> / <see cref="SafeActionService.UndoAsync"/>.
/// </summary>
public static class NetworkTweakCatalog
{
    public const string ThrottlingId = NetworkThrottlingSafeActionTarget.Id;
    public const string AutotuningId = TcpAutotuningSafeActionTarget.Id;

    /// <summary>Targets with fixed ids (MMCSS network throttling, TCP auto-tuning restore).</summary>
    public static IReadOnlyList<ISafeActionTarget> CreateStatic(IElevationBroker broker, IMachineRegistryReader? registry = null,
        ITcpGlobalReader? tcpReader = null)
    {
        ArgumentNullException.ThrowIfNull(broker);
        return new ISafeActionTarget[]
        {
            new NetworkThrottlingSafeActionTarget(broker, registry),
            new TcpAutotuningSafeActionTarget(broker, tcpReader),
        };
    }

    /// <summary>Per-adapter targets from their action id; null for ids this catalog does not own.</summary>
    public static ISafeActionTarget? Resolve(string? actionId, IElevationBroker broker, IMachineRegistryReader? registry = null,
        Func<bool>? flushCache = null)
    {
        ArgumentNullException.ThrowIfNull(broker);
        if (DnsSafeActionTarget.TryParseActionId(actionId, out var dnsIf, out var servers))
            return new DnsSafeActionTarget(dnsIf, servers, broker, registry, flushCache);
        if (NagleSafeActionTarget.TryParseActionId(actionId, out var nagleIf))
            return new NagleSafeActionTarget(nagleIf, broker, registry);
        return null;
    }

    public static string DnsActionId(string interfaceGuid, IEnumerable<IPAddress> servers) =>
        DnsSafeActionTarget.ActionIdFor(interfaceGuid, servers.Select(s => s.ToString()));

    public static string NagleActionId(string interfaceGuid) => NagleSafeActionTarget.IdPrefix + interfaceGuid.ToUpperInvariant();

    /// <summary>Interface GUID ("{...}") of the adapter that owns the default route, or null.</summary>
    public static string? PrimaryInterfaceGuid()
    {
        var id = NetworkInfoService.CapturePrimary()?.Id;
        return ElevationAllowlist.IsBracedGuid(id) ? id!.ToUpperInvariant() : null;
    }
}
