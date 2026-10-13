using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Visor.Services;

/// <summary>How a hop behaved relative to the rest of the path.</summary>
public enum HopState
{
    /// <summary>At least one probe answered.</summary>
    Answered,
    /// <summary>No probe answered, but a later hop did — the router drops ICMP for itself (filtered), not a fault.</summary>
    Filtered,
    /// <summary>No probe answered and nothing after it answered either (trailing silence).</summary>
    Silent
}

public enum TracerouteTermination
{
    /// <summary>The destination replied (Success).</summary>
    Reached,
    /// <summary>Hit the hop limit without reaching the destination.</summary>
    MaxHops,
    /// <summary>Stopped after N consecutive silent hops.</summary>
    ConsecutiveTimeouts,
    /// <summary>A hop answered "destination unreachable" / similar.</summary>
    Unreachable,
    Cancelled
}

public sealed class TracerouteHop
{
    public required int Ttl { get; init; }
    public IPAddress? Address { get; init; }
    public string? HostName { get; init; }
    /// <summary>Per-probe RTT in send order; null = no reply.</summary>
    public required IReadOnlyList<double?> Rtts { get; init; }
    public int Sent => Rtts.Count;
    public int Received { get; init; }
    public double LossPercent { get; init; }
    public double? MinMs { get; init; }
    public double? AvgMs { get; init; }
    public double? MaxMs { get; init; }
    /// <summary>Mean |Δ| between consecutive successful probes; null when fewer than 2 replies.</summary>
    public double? JitterMs { get; init; }
    public HopState State { get; init; }
    /// <summary>First hop that answered (TTL 1 normally) — the default gateway.</summary>
    public bool IsGateway { get; init; }
    /// <summary>First hop with a non-private address — the ISP edge.</summary>
    public bool IsFirstPublic { get; init; }
    public bool IsPublic { get; init; }
    public bool IsDestination { get; init; }
    /// <summary>Non-success / non-TTL-expired ICMP status that terminated the trace (e.g. unreachable).</summary>
    public IPStatus? ErrorStatus { get; init; }
}

public sealed class TracerouteResult
{
    public required string TargetHost { get; init; }
    public required IPAddress Target { get; init; }
    public required IReadOnlyList<TracerouteHop> Hops { get; init; }
    public required TracerouteTermination Termination { get; init; }
    public bool ReachedDestination => Termination == TracerouteTermination.Reached;
    /// <summary>Index in <see cref="Hops"/> of the gateway hop, or null if TTL 1 never answered.</summary>
    public int? GatewayHopIndex { get; init; }
    /// <summary>Index in <see cref="Hops"/> of the first public (non-private) answering hop, or null.</summary>
    public int? FirstPublicHopIndex { get; init; }
    /// <summary>TTL of the last answering hop; everything after it timed out (null when the last hop answered).</summary>
    public int? LastAnsweringTtl { get; init; }
    public TracerouteHop? GatewayHop => GatewayHopIndex is int g ? Hops[g] : null;
    public TracerouteHop? FirstPublicHop => FirstPublicHopIndex is int p ? Hops[p] : null;
}

public sealed class TracerouteOptions
{
    public int MaxHops { get; init; } = 30;
    public int ProbesPerHop { get; init; } = 3;
    public int TimeoutMs { get; init; } = 1500;
    /// <summary>Stop when this many hops in a row give no reply and the destination is still unreached.</summary>
    public int MaxConsecutiveSilentHops { get; init; } = 5;
    public bool ReverseDns { get; init; }
    public TimeSpan ReverseDnsTimeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Pause between probes to the same hop (ms). 0 in tests.</summary>
    public int InterProbeDelayMs { get; init; } = 30;
    /// <summary>Known default gateway; when set, the gateway flag requires the TTL-1 hop to match it (else TTL 1 is assumed).</summary>
    public IPAddress? KnownGateway { get; init; }
}

/// <summary>
/// ICMP traceroute with increasing TTL (same mechanism as <c>tracert</c>, which runs as a standard user).
/// Only sends probes when invoked by the UI; nothing leaves the machine except the echoes themselves.
/// </summary>
public static class TracerouteService
{
    public const int MaxHopsLimit = 64;
    public const int MaxProbesPerHop = 10;

    /// <summary>Resolves <paramref name="host"/> ("gateway" = primary adapter gateway) and traces to it with the real pinger.</summary>
    public static async Task<TracerouteResult> RunAsync(
        string host,
        TracerouteOptions? options,
        CancellationToken ct,
        IProgress<TracerouteHop>? progress = null)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0) throw new ArgumentException("host");
        options ??= new TracerouteOptions();

        IPAddress? target;
        if (string.Equals(host, "gateway", StringComparison.OrdinalIgnoreCase))
        {
            string? gw = NetworkInfoService.CapturePrimary()?.Gateway;
            if (string.IsNullOrEmpty(gw) || !IPAddress.TryParse(gw, out target))
                throw new InvalidOperationException(NetworkProbeService.NoGatewayError);
        }
        else if (!IPAddress.TryParse(host, out target))
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct)
                .WaitAsync(NetworkProbeService.DnsTimeout, ct).ConfigureAwait(false);
            target = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
            if (target == null)
                throw new SocketException((int)SocketError.HostNotFound);
        }

        IPAddress? known = options.KnownGateway;
        if (known == null)
        {
            string? gw = NetworkInfoService.CapturePrimary()?.Gateway;
            if (!string.IsNullOrEmpty(gw) && IPAddress.TryParse(gw, out var parsed))
                known = parsed;
        }
        var effective = new TracerouteOptions
        {
            MaxHops = options.MaxHops,
            ProbesPerHop = options.ProbesPerHop,
            TimeoutMs = options.TimeoutMs,
            MaxConsecutiveSilentHops = options.MaxConsecutiveSilentHops,
            ReverseDns = options.ReverseDns,
            ReverseDnsTimeout = options.ReverseDnsTimeout,
            InterProbeDelayMs = options.InterProbeDelayMs,
            KnownGateway = known
        };

        using var pinger = new SystemPinger();
        return await RunAsync(pinger, target!, host, effective, ct, progress,
            options.ReverseDns ? SystemReverseResolver.Instance : null).ConfigureAwait(false);
    }

    /// <summary>Core loop with an injected pinger (tests pass fakes; no network I/O happens here otherwise).</summary>
    public static async Task<TracerouteResult> RunAsync(
        IPinger pinger,
        IPAddress target,
        string targetHost,
        TracerouteOptions? options,
        CancellationToken ct,
        IProgress<TracerouteHop>? progress = null,
        IReverseResolver? reverse = null)
    {
        ArgumentNullException.ThrowIfNull(pinger);
        ArgumentNullException.ThrowIfNull(target);
        options ??= new TracerouteOptions();
        int maxHops = Math.Clamp(options.MaxHops, 1, MaxHopsLimit);
        int probes = Math.Clamp(options.ProbesPerHop, 1, MaxProbesPerHop);
        int timeout = Math.Clamp(options.TimeoutMs, 100, 10_000);
        int silentLimit = Math.Max(1, options.MaxConsecutiveSilentHops);

        var raw = new List<RawHop>(maxHops);
        var termination = TracerouteTermination.MaxHops;
        int consecutiveSilent = 0;
        bool cancelled = false;

        try
        {
            for (int ttl = 1; ttl <= maxHops; ttl++)
            {
                ct.ThrowIfCancellationRequested();
                var rtts = new double?[probes];
                IPAddress? addr = null;
                bool reached = false;
                IPStatus? error = null;

                for (int p = 0; p < probes; p++)
                {
                    ct.ThrowIfCancellationRequested();
                    var reply = await pinger.SendAsync(target, timeout, ttl, ct).ConfigureAwait(false);
                    if (reply.IsSuccess)
                    {
                        rtts[p] = Math.Max(0, reply.RttMs);
                        addr ??= reply.Address ?? target;
                        reached = true;
                    }
                    else if (reply.IsTtlExpired)
                    {
                        rtts[p] = Math.Max(0, reply.RttMs);
                        addr ??= reply.Address;
                    }
                    else if (reply.IsTimeout || reply.Status == IPStatus.Unknown)
                    {
                        rtts[p] = null;
                    }
                    else
                    {
                        // Unreachable & co: the replying node is part of the path, but the trace ends here.
                        addr ??= reply.Address;
                        error = reply.Status;
                        rtts[p] = null;
                    }
                    if (p + 1 < probes && options.InterProbeDelayMs > 0)
                        await Task.Delay(options.InterProbeDelayMs, ct).ConfigureAwait(false);
                }

                string? name = null;
                if (addr != null && options.ReverseDns && reverse != null)
                    name = await reverse.ReverseAsync(addr, options.ReverseDnsTimeout, ct).ConfigureAwait(false);

                var hop = new RawHop(ttl, addr, name, rtts, reached, error);
                raw.Add(hop);
                progress?.Report(Materialize(hop, HopState.Answered, false, false, false));

                if (reached) { termination = TracerouteTermination.Reached; break; }
                if (error != null) { termination = TracerouteTermination.Unreachable; break; }

                bool silent = rtts.All(r => r == null);
                consecutiveSilent = silent ? consecutiveSilent + 1 : 0;
                if (consecutiveSilent >= silentLimit)
                {
                    termination = TracerouteTermination.ConsecutiveTimeouts;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        if (cancelled) termination = TracerouteTermination.Cancelled;
        return Build(raw, target, targetHost, termination, options.KnownGateway);
    }

    private sealed record RawHop(int Ttl, IPAddress? Address, string? Name, double?[] Rtts, bool Reached, IPStatus? Error);

    private static TracerouteResult Build(
        List<RawHop> raw, IPAddress target, string targetHost, TracerouteTermination termination, IPAddress? knownGateway)
    {
        int lastAnswering = -1;
        for (int i = raw.Count - 1; i >= 0; i--)
        {
            if (raw[i].Rtts.Any(r => r != null) || raw[i].Error != null) { lastAnswering = i; break; }
        }

        int? gatewayIdx = null;
        int? firstPublicIdx = null;
        var hops = new List<TracerouteHop>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            bool answered = r.Rtts.Any(x => x != null) || r.Error != null;
            HopState state = answered ? HopState.Answered : (i < lastAnswering ? HopState.Filtered : HopState.Silent);

            bool isGateway = false;
            if (gatewayIdx == null && answered && r.Address != null)
            {
                // TTL 1 is the default gateway by construction; a known gateway address also qualifies
                // when TTL 1 was filtered and the router answers on a later TTL (rare, but seen on some CPE).
                isGateway = r.Ttl == 1 || (knownGateway != null && r.Address.Equals(knownGateway));
                if (isGateway) gatewayIdx = i;
            }

            bool isPublic = answered && r.Address != null && !IPAddressClass.IsPrivate(r.Address);
            bool firstPublic = false;
            if (isPublic && firstPublicIdx == null)
            {
                firstPublic = true;
                firstPublicIdx = i;
            }
            hops.Add(Materialize(r, state, isGateway, firstPublic, isPublic));
        }

        int? lastAnsweringTtl = null;
        if (lastAnswering >= 0 && lastAnswering < raw.Count - 1)
            lastAnsweringTtl = raw[lastAnswering].Ttl;
        else if (lastAnswering < 0 && raw.Count > 0)
            lastAnsweringTtl = 0;

        return new TracerouteResult
        {
            TargetHost = targetHost,
            Target = target,
            Hops = hops,
            Termination = termination,
            GatewayHopIndex = gatewayIdx,
            FirstPublicHopIndex = firstPublicIdx,
            LastAnsweringTtl = lastAnsweringTtl
        };
    }

    private static TracerouteHop Materialize(RawHop r, HopState state, bool isGateway, bool firstPublic, bool isPublic)
    {
        var s = NetworkStats.Summarize(r.Rtts);
        return new TracerouteHop
        {
            Ttl = r.Ttl,
            Address = r.Address,
            HostName = r.Name,
            Rtts = r.Rtts,
            Received = s.Received,
            LossPercent = s.LossPercent,
            MinMs = s.MinMs,
            AvgMs = s.AvgMs,
            MaxMs = s.MaxMs,
            JitterMs = s.JitterMs,
            State = state,
            IsGateway = isGateway,
            IsFirstPublic = firstPublic,
            IsPublic = isPublic,
            IsDestination = r.Reached,
            ErrorStatus = r.Error
        };
    }
}
