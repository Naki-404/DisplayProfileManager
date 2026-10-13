using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace Visor.Services;

/// <summary>One ICMP echo reply as seen by <see cref="IPinger"/> (status + wall-clock RTT + replying node).</summary>
public sealed record PingReplyInfo(IPStatus Status, double RttMs, IPAddress? Address)
{
    public bool IsSuccess => Status == IPStatus.Success;
    public bool IsTtlExpired => Status == IPStatus.TtlExpired;
    public bool IsTimeout => Status is IPStatus.TimedOut or IPStatus.TimeExceeded;

    public static PingReplyInfo Timeout(double rttMs = 0) => new(IPStatus.TimedOut, rttMs, null);
    public static PingReplyInfo Success(string address, double rttMs) => new(IPStatus.Success, rttMs, IPAddress.Parse(address));
    public static PingReplyInfo Expired(string address, double rttMs) => new(IPStatus.TtlExpired, rttMs, IPAddress.Parse(address));
}

/// <summary>
/// Seam for ICMP echo so traceroute / ping monitor can run against fake replies in tests.
/// Implementations must never throw on network errors — map them to a non-success status instead
/// (cancellation is the only exception that propagates).
/// </summary>
public interface IPinger : IDisposable
{
    /// <param name="ttl">IP TTL; 0 or less = OS default.</param>
    Task<PingReplyInfo> SendAsync(IPAddress target, int timeoutMs, int ttl, CancellationToken ct);
}

/// <summary>Reverse DNS seam for traceroute (off by default; bounded by a timeout).</summary>
public interface IReverseResolver
{
    /// <summary>Host name for an address, or null on failure / timeout. Never throws except on cancellation.</summary>
    Task<string?> ReverseAsync(IPAddress address, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Real <see cref="Ping"/> wrapper. Not thread-safe: one in-flight echo per instance.</summary>
public sealed class SystemPinger : IPinger
{
    private readonly Ping _ping = new();
    private readonly byte[] _buffer = new byte[32];

    public async Task<PingReplyInfo> SendAsync(IPAddress target, int timeoutMs, int ttl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        timeoutMs = Math.Clamp(timeoutMs, 100, 10_000);
        var options = ttl > 0 ? new PingOptions(ttl, dontFragment: false) : null;
        var sw = Stopwatch.StartNew();
        try
        {
            // Cancel via WaitAsync only — Ping.SendAsyncCancel blocks the cancelling thread.
            var reply = await _ping.SendPingAsync(target, timeoutMs, _buffer, options).WaitAsync(ct).ConfigureAwait(false);
            sw.Stop();
            // The OS reports RoundtripTime only for Success; for TTL-expired replies fall back to wall-clock.
            double rtt = reply.Status == IPStatus.Success && reply.RoundtripTime > 0
                ? reply.RoundtripTime
                : sw.Elapsed.TotalMilliseconds;
            IPAddress? addr = reply.Address;
            if (addr != null && (addr.Equals(IPAddress.Any) || addr.Equals(IPAddress.IPv6Any)))
                addr = null;
            return new PingReplyInfo(reply.Status, rtt, addr);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            return new PingReplyInfo(StatusFromException(ex), sw.Elapsed.TotalMilliseconds, null);
        }
    }

    private static IPStatus StatusFromException(Exception ex) =>
        NetworkProbeService.MapException(ex) switch
        {
            ProbeErrorCode.Timeout => IPStatus.TimedOut,
            ProbeErrorCode.Unreachable => IPStatus.DestinationHostUnreachable,
            ProbeErrorCode.NoNetwork => IPStatus.DestinationNetworkUnreachable,
            ProbeErrorCode.BadDestination => IPStatus.BadDestination,
            _ => IPStatus.Unknown
        };

    public void Dispose() => _ping.Dispose();
}

/// <summary>System resolver reverse lookup bounded by a timeout.</summary>
public sealed class SystemReverseResolver : IReverseResolver
{
    public static readonly SystemReverseResolver Instance = new();

    public async Task<string?> ReverseAsync(IPAddress address, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), ct).WaitAsync(timeout, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return null;
        }
    }
}

/// <summary>Address-class helpers shared by traceroute classification and diagnosis.</summary>
public static class IPAddressClass
{
    /// <summary>
    /// True for addresses that cannot be an Internet hop: RFC 1918, CGNAT 100.64/10, APIPA, loopback,
    /// IPv6 link-local / unique-local / loopback. Everything else counts as "public" for hop marking.
    /// </summary>
    public static bool IsPrivate(IPAddress? a)
    {
        if (a == null) return true;
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            if (b.Length != 4) return true;
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;   // CGNAT
            if (b[0] == 169 && b[1] == 254) return true;                   // APIPA
            if (b[0] == 0) return true;
            return false;
        }
        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal) return true;
            if (a.Equals(IPAddress.IPv6Any)) return true;
            if (a.IsIPv4MappedToIPv6) return IsPrivate(a.MapToIPv4());
            return false;
        }
        return true;
    }
}
