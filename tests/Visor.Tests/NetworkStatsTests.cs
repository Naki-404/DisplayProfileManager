using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class NetworkStatsTests
{
    [Fact]
    public void Summarize_empty_is_zero()
    {
        var s = NetworkStats.Summarize(Array.Empty<double?>());
        Assert.Equal(0, s.Sent);
        Assert.Equal(0, s.Received);
        Assert.Null(s.MinMs);
        Assert.Null(s.JitterMs);
    }

    [Fact]
    public void Summarize_all_lost()
    {
        var s = NetworkStats.Summarize(new double?[] { null, null, null });
        Assert.Equal(3, s.Sent);
        Assert.Equal(0, s.Received);
        Assert.Equal(3, s.Lost);
        Assert.Equal(100, s.LossPercent);
        Assert.Null(s.AvgMs);
    }

    [Fact]
    public void Summarize_computes_min_max_avg_median_loss()
    {
        // 10, 20, 30, lost, 40
        var s = NetworkStats.Summarize(new double?[] { 10, 20, 30, null, 40 });
        Assert.Equal(5, s.Sent);
        Assert.Equal(4, s.Received);
        Assert.Equal(1, s.Lost);
        Assert.Equal(20, s.LossPercent);
        Assert.Equal(10, s.MinMs);
        Assert.Equal(40, s.MaxMs);
        Assert.Equal(25, s.AvgMs);
        Assert.Equal(25, s.MedianMs); // 10,20,30,40 → (20+30)/2
    }

    [Fact]
    public void Jitter_mean_absolute_consecutive_delta()
    {
        // |20-10| + |30-20| + |50-30| = 10+10+20 = 40 / 3
        var j = NetworkStats.Jitter(new[] { 10.0, 20, 30, 50 });
        Assert.NotNull(j);
        Assert.Equal(40.0 / 3.0, j!.Value, 6);
    }

    [Fact]
    public void Jitter_null_when_fewer_than_two()
    {
        Assert.Null(NetworkStats.Jitter(Array.Empty<double>()));
        Assert.Null(NetworkStats.Jitter(new[] { 5.0 }));
    }

    [Fact]
    public void Summarize_jitter_uses_success_order_not_sorted()
    {
        // Success order 40, 10, 20 → |10-40|+|20-10| = 30+10 = 40/2 = 20
        var s = NetworkStats.Summarize(new double?[] { 40, null, 10, 20 });
        Assert.Equal(20, s.JitterMs);
    }

    [Fact]
    public void LinkSpeedParts_scales()
    {
        Assert.Null(NetworkInfoService.LinkSpeedParts(null));
        var mb = NetworkInfoService.LinkSpeedParts(100_000_000);
        Assert.NotNull(mb);
        Assert.False(mb!.Value.Gigabits);
        Assert.Equal(100, mb.Value.Value);
        var gb = NetworkInfoService.LinkSpeedParts(1_000_000_000);
        Assert.NotNull(gb);
        Assert.True(gb!.Value.Gigabits);
        Assert.Equal(1, gb.Value.Value);
    }

    [Fact]
    public void FormatLinkSpeed_dead_helper_removed()
    {
        Assert.Null(typeof(NetworkInfoService).GetMethod("FormatLinkSpeed"));
    }

    [Fact]
    public void IsLinkLocal_detects_apipa()
    {
        Assert.True(NetworkInfoService.IsLinkLocal(IPAddress.Parse("169.254.1.2")));
        Assert.False(NetworkInfoService.IsLinkLocal(IPAddress.Parse("192.168.1.1")));
    }

    [Fact]
    public void IsUsableGateway_rejects_unspecified_loopback_apipa()
    {
        Assert.False(NetworkInfoService.IsUsableGateway(IPAddress.Any));
        Assert.False(NetworkInfoService.IsUsableGateway(IPAddress.Loopback));
        Assert.False(NetworkInfoService.IsUsableGateway(IPAddress.Parse("169.254.10.1")));
        Assert.True(NetworkInfoService.IsUsableGateway(IPAddress.Parse("192.168.1.1")));
    }

    [Fact]
    public void IsUsableGateway_accepts_ipv6_including_link_local_router()
    {
        Assert.True(NetworkInfoService.IsUsableGateway(IPAddress.Parse("fe80::1")));
        Assert.True(NetworkInfoService.IsUsableGateway(IPAddress.Parse("2001:db8::1")));
        Assert.False(NetworkInfoService.IsUsableGateway(IPAddress.IPv6Any));
        Assert.False(NetworkInfoService.IsUsableGateway(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void RankScore_prefers_gateway_wifi_over_virtual_ethernet()
    {
        int wifiGw = NetworkInfoService.RankScore(
            hasGateway: true, hasRealIp: true, onlyLinkLocal: false,
            likelyVirtual: false, ethernet: false, wifi: true, speedMbps: 300);
        int virtEth = NetworkInfoService.RankScore(
            hasGateway: false, hasRealIp: true, onlyLinkLocal: false,
            likelyVirtual: true, ethernet: true, wifi: false, speedMbps: 10000);
        Assert.True(wifiGw > virtEth);
    }

    [Fact]
    public void RankScore_penalizes_link_local_only()
    {
        int good = NetworkInfoService.RankScore(true, true, false, false, true, false, 1000);
        int apipa = NetworkInfoService.RankScore(false, false, true, false, true, false, 1000);
        Assert.True(good > apipa);
    }

    [Fact]
    public void IsLikelyVirtual_matches_host_switches_but_not_vpn_or_tap_names()
    {
        Assert.True(NetworkInfoService.IsLikelyVirtual("vEthernet (WSL)"));
        Assert.True(NetworkInfoService.IsLikelyVirtual("Hyper-V Virtual Ethernet Adapter"));
        Assert.False(NetworkInfoService.IsLikelyVirtual("Wi-Fi"));
        Assert.False(NetworkInfoService.IsLikelyVirtual("Ethernet"));
        Assert.False(NetworkInfoService.IsLikelyVirtual("TAP-Windows Adapter V9"));
        Assert.False(NetworkInfoService.IsLikelyVirtual("Corporate VPN"));
        Assert.False(NetworkInfoService.IsLikelyVirtual(""));
    }

    [Fact]
    public void TypeCodeOf_maps_common_types()
    {
        Assert.Equal("ethernet", NetworkInfoService.TypeCodeOf(NetworkInterfaceType.Ethernet));
        Assert.Equal("wifi", NetworkInfoService.TypeCodeOf(NetworkInterfaceType.Wireless80211));
        Assert.Equal("other", NetworkInfoService.TypeCodeOf(NetworkInterfaceType.Ppp));
    }

    [SkippableFact]
    public void CapturePrimary_is_consistent_and_cached()
    {
        // Read-only adapter enumeration (no packets); skipped on hosts without any Up adapter.
        var info = NetworkInfoService.CapturePrimary(forceRefresh: true);
        Skip.If(info == null, "no usable network adapter on this host");
        Assert.False(string.IsNullOrWhiteSpace(info!.Name));
        Assert.False(string.IsNullOrWhiteSpace(info.TypeCode));
        Assert.Same(info, NetworkInfoService.CapturePrimary());
    }

    [Fact]
    public void NoGatewayError_constant_stable()
    {
        Assert.Equal("no_gateway", NetworkProbeService.NoGatewayError);
    }

    [Theory]
    [InlineData(IPStatus.Success, ProbeErrorCode.None)]
    [InlineData(IPStatus.TimedOut, ProbeErrorCode.Timeout)]
    [InlineData(IPStatus.DestinationHostUnreachable, ProbeErrorCode.Unreachable)]
    [InlineData(IPStatus.DestinationNetworkUnreachable, ProbeErrorCode.Unreachable)]
    [InlineData(IPStatus.TtlExpired, ProbeErrorCode.TtlExpired)]
    [InlineData(IPStatus.BadDestination, ProbeErrorCode.BadDestination)]
    [InlineData(IPStatus.Unknown, ProbeErrorCode.Unknown)]
    public void MapStatus_maps_ipstatus_to_codes(IPStatus status, ProbeErrorCode expected)
    {
        Assert.Equal(expected, NetworkProbeService.MapStatus(status));
    }

    [Fact]
    public void MapException_maps_exception_types_to_codes()
    {
        Assert.Equal(ProbeErrorCode.Timeout, NetworkProbeService.MapException(new TimeoutException()));
        Assert.Equal(ProbeErrorCode.Cancelled, NetworkProbeService.MapException(new OperationCanceledException()));
        Assert.Equal(ProbeErrorCode.HostNotFound,
            NetworkProbeService.MapException(new PingException("x", new SocketException((int)SocketError.HostNotFound))));
        Assert.Equal(ProbeErrorCode.NoNetwork,
            NetworkProbeService.MapException(new SocketException((int)SocketError.NetworkUnreachable)));
        Assert.Equal(ProbeErrorCode.PermissionDenied, NetworkProbeService.MapException(new UnauthorizedAccessException()));
        Assert.Equal(ProbeErrorCode.Unknown, NetworkProbeService.MapException(new InvalidOperationException()));
    }

    [Fact]
    public void Error_tokens_and_loc_keys_are_stable()
    {
        Assert.Equal("timeout", NetworkProbeService.Token(ProbeErrorCode.Timeout));
        Assert.Equal("hostNotFound", NetworkProbeService.Token(ProbeErrorCode.HostNotFound));
        Assert.Equal("net.err.unreachable", NetworkProbeService.ErrorLocKey(ProbeErrorCode.Unreachable));
        var sample = new ProbeSample { Index = 0, ErrorCode = ProbeErrorCode.Timeout };
        Assert.Equal("timeout", sample.Error);
        Assert.Null(new ProbeSample { Index = 1, RttMs = 3 }.Error);
        Assert.Null(typeof(DnsResolveResult).GetProperty("ResolverLabel"));
        Assert.Equal(TimeSpan.FromSeconds(2), NetworkProbeService.DnsTimeout);
    }
}
