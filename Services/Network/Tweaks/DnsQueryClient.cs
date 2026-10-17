using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Visor.Services.NetworkTweaks;

/// <summary>One timed query. <see cref="RttMs"/> is set only when the server answered (<see cref="DnsParseResult.Answered"/>).</summary>
public sealed record DnsQueryResult(DnsResponseStatus Status, double? RttMs, IReadOnlyList<IPAddress> Addresses)
{
    public bool Answered => RttMs != null;
}

/// <summary>Seam: sends one A query straight to a DNS server (bypassing the Windows resolver cache).</summary>
public interface IDnsQueryClient
{
    Task<DnsQueryResult> QueryAsync(IPAddress server, string name, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Plain UDP DNS client (port 53 by default): one packet out, wait for the response with our random id,
/// ignore strays, give up after the timeout. No admin rights, no third-party packages.
/// </summary>
public sealed class UdpDnsQueryClient : IDnsQueryClient
{
    private readonly int _port;

    public UdpDnsQueryClient(int port = 53)
    {
        if (port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _port = port;
    }

    public async Task<DnsQueryResult> QueryAsync(IPAddress server, string name, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        var none = Array.Empty<IPAddress>();
        ushort id = (ushort)RandomNumberGenerator.GetInt32(0, 65536);
        byte[] query = DnsWire.BuildAQuery(id, name);

        using var udp = new UdpClient(server.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            udp.Connect(server, _port);
            await udp.SendAsync(query, cts.Token).ConfigureAwait(false);
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                var parsed = DnsWire.Parse(r.Buffer, id);
                if (parsed.Status == DnsResponseStatus.IdMismatch) continue;   // stray / spoofed — keep waiting
                double ms = sw.Elapsed.TotalMilliseconds;
                return new DnsQueryResult(parsed.Status, parsed.Answered ? ms : null, parsed.Addresses);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DnsQueryResult(DnsResponseStatus.Timeout, null, none);
        }
        catch (SocketException)
        {
            return new DnsQueryResult(DnsResponseStatus.NetworkError, null, none);
        }
    }
}

/// <summary>Windows DNS client cache (no admin rights needed).</summary>
public static class DnsCache
{
    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DnsFlushResolverCacheNative();

    /// <summary>Same as <c>ipconfig /flushdns</c>. Plain action — nothing to undo (the cache refills itself).</summary>
    public static bool Flush()
    {
        try { return DnsFlushResolverCacheNative(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            AppLog.Warn(ex, "DnsFlushResolverCache");
            return false;
        }
    }
}
