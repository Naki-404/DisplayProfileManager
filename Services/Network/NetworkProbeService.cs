using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Visor.Services;

/// <summary>Localizable probe failure classes. UI: <c>Loc.T(NetworkProbeService.ErrorLocKey(code))</c>.</summary>
public enum ProbeErrorCode
{
    None,
    Timeout,
    HostNotFound,
    NoNetwork,
    Unreachable,
    TtlExpired,
    BadDestination,
    PermissionDenied,
    Cancelled,
    Unknown
}

public sealed class ProbeSample
{
    public required int Index { get; init; }
    public double? RttMs { get; init; }
    public ProbeErrorCode ErrorCode { get; init; }
    /// <summary>Error token (camelCase <see cref="ErrorCode"/>) or null when the sample succeeded. Prefer <see cref="ErrorCode"/>.</summary>
    public string? Error => ErrorCode == ProbeErrorCode.None ? null : NetworkProbeService.Token(ErrorCode);
}

public sealed class DnsResolveResult
{
    public required string Host { get; init; }
    /// <summary>First resolve (cold — unless the OS resolver already had it cached).</summary>
    public double? Ms { get; init; }
    /// <summary>Immediate second resolve (served from the resolver cache); null when not measured or the first failed.</summary>
    public double? RepeatMs { get; init; }
    public string? Address { get; init; }
    public ProbeErrorCode ErrorCode { get; init; }
    /// <summary>Error token (camelCase <see cref="ErrorCode"/>) or null on success. Prefer <see cref="ErrorCode"/>.</summary>
    public string? Error => ErrorCode == ProbeErrorCode.None ? null : NetworkProbeService.Token(ErrorCode);
}

/// <summary>
/// ICMP latency probes and DNS resolve timing. ICMP may be blocked — that is reported, never
/// claimed as "no internet". Does not change system DNS. All probes are click-initiated by the UI.
/// </summary>
public static class NetworkProbeService
{
    public const string NoGatewayError = "no_gateway";
    public static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(2);

    public static readonly string[] DefaultHosts =
    {
        "1.1.1.1",
        "8.8.8.8",
        "gateway"
    };

    public static readonly string[] DefaultDnsHosts =
    {
        "dns.google",
        "cloudflare-dns.com",
        "www.microsoft.com"
    };

    /// <summary>Loc key for a failure class: net.err.timeout, net.err.hostNotFound, …</summary>
    public static string ErrorLocKey(ProbeErrorCode code) => "net.err." + Token(code);

    /// <summary>camelCase token of the code ("timeout", "hostNotFound", …).</summary>
    public static string Token(ProbeErrorCode code)
    {
        string s = code.ToString();
        return s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
    }

    public static ProbeErrorCode MapStatus(IPStatus status) => status switch
    {
        IPStatus.Success => ProbeErrorCode.None,
        IPStatus.TimedOut or IPStatus.TimeExceeded => ProbeErrorCode.Timeout,
        IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable
            or IPStatus.DestinationUnreachable or IPStatus.DestinationProhibited
            or IPStatus.DestinationProtocolUnreachable or IPStatus.DestinationPortUnreachable
            or IPStatus.DestinationScopeMismatch => ProbeErrorCode.Unreachable,
        IPStatus.TtlExpired => ProbeErrorCode.TtlExpired,
        IPStatus.BadDestination or IPStatus.BadRoute or IPStatus.BadHeader
            or IPStatus.BadOption or IPStatus.UnrecognizedNextHeader => ProbeErrorCode.BadDestination,
        _ => ProbeErrorCode.Unknown
    };

    public static ProbeErrorCode MapException(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            switch (e)
            {
                case OperationCanceledException:
                    return ProbeErrorCode.Cancelled;
                case TimeoutException:
                    return ProbeErrorCode.Timeout;
                case UnauthorizedAccessException:
                    return ProbeErrorCode.PermissionDenied;
                case SocketException se:
                    return se.SocketErrorCode switch
                    {
                        SocketError.HostNotFound or SocketError.NoData or SocketError.NoRecovery
                            or SocketError.TryAgain => ProbeErrorCode.HostNotFound,
                        SocketError.NetworkDown or SocketError.NetworkUnreachable
                            or SocketError.HostDown or SocketError.NotInitialized => ProbeErrorCode.NoNetwork,
                        SocketError.HostUnreachable => ProbeErrorCode.Unreachable,
                        SocketError.TimedOut => ProbeErrorCode.Timeout,
                        SocketError.AccessDenied => ProbeErrorCode.PermissionDenied,
                        _ => ProbeErrorCode.Unknown
                    };
            }
        }
        return ProbeErrorCode.Unknown;
    }

    /// <summary>
    /// Runs <paramref name="count"/> ICMP echoes to <paramref name="host"/>.
    /// Special host "gateway" resolves to the primary adapter gateway (IPv4, else IPv6).
    /// </summary>
    public static async Task<IReadOnlyList<ProbeSample>> PingAsync(
        string host,
        int count,
        int timeoutMs,
        CancellationToken ct,
        IProgress<ProbeSample>? progress = null)
    {
        count = Math.Clamp(count, 1, 50);
        timeoutMs = Math.Clamp(timeoutMs, 200, 10_000);
        host = (host ?? "").Trim();
        if (host.Length == 0) throw new ArgumentException("host");

        if (string.Equals(host, "gateway", StringComparison.OrdinalIgnoreCase))
        {
            string? gw = NetworkInfoService.CapturePrimary()?.Gateway;
            if (string.IsNullOrEmpty(gw))
                throw new InvalidOperationException(NoGatewayError);
            host = gw;
        }

        var list = new List<ProbeSample>(count);
        using var ping = new Ping();
        // Cancel via WaitAsync only — Ping.SendAsyncCancel blocks the cancelling thread
        // (often the UI) until the in-flight echo finishes.
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            ProbeSample sample;
            try
            {
                var reply = await ping.SendPingAsync(host, timeoutMs).WaitAsync(ct).ConfigureAwait(false);
                sample = reply.Status == IPStatus.Success
                    ? new ProbeSample { Index = i, RttMs = reply.RoundtripTime }
                    : new ProbeSample { Index = i, ErrorCode = MapStatus(reply.Status) };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sample = new ProbeSample { Index = i, ErrorCode = MapException(ex) };
            }
            list.Add(sample);
            progress?.Report(sample);
            if (i + 1 < count)
                await Task.Delay(40, ct).ConfigureAwait(false);
        }
        return list;
    }

    /// <summary>
    /// System DNS resolve timing for each host (does not change DNS settings). Each lookup is bounded by
    /// <see cref="DnsTimeout"/>; the first resolve is reported as <see cref="DnsResolveResult.Ms"/> and an
    /// immediate repeat (resolver cache) as <see cref="DnsResolveResult.RepeatMs"/>.
    /// </summary>
    public static async Task<IReadOnlyList<DnsResolveResult>> ResolveDnsAsync(
        IEnumerable<string> hosts,
        CancellationToken ct,
        IProgress<DnsResolveResult>? progress = null,
        bool measureRepeat = true)
    {
        var results = new List<DnsResolveResult>();
        foreach (var raw in hosts)
        {
            ct.ThrowIfCancellationRequested();
            string host = (raw ?? "").Trim();
            if (host.Length == 0) continue;

            var (ms, addrs, code) = await ResolveOnceAsync(host, ct).ConfigureAwait(false);
            DnsResolveResult row;
            if (code == ProbeErrorCode.None)
            {
                double? repeat = null;
                if (measureRepeat)
                {
                    var (ms2, _, code2) = await ResolveOnceAsync(host, ct).ConfigureAwait(false);
                    if (code2 == ProbeErrorCode.None) repeat = ms2;
                }
                var resolved = addrs ?? Array.Empty<IPAddress>();
                var first = resolved.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                            ?? resolved.FirstOrDefault();
                row = new DnsResolveResult
                {
                    Host = host,
                    Ms = ms,
                    RepeatMs = repeat,
                    Address = first?.ToString()
                };
            }
            else
            {
                row = new DnsResolveResult { Host = host, Ms = ms, ErrorCode = code };
            }
            results.Add(row);
            progress?.Report(row);
        }
        return results;
    }

    private static async Task<(double Ms, IPAddress[]? Addresses, ProbeErrorCode Code)> ResolveOnceAsync(
        string host, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct).WaitAsync(DnsTimeout, ct).ConfigureAwait(false);
            sw.Stop();
            return (sw.Elapsed.TotalMilliseconds, addrs, ProbeErrorCode.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            return (sw.Elapsed.TotalMilliseconds, null, MapException(ex));
        }
    }
}
