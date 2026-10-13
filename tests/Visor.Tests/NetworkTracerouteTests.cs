using System.Net;
using System.Net.NetworkInformation;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

/// <summary>Scripted pinger: replies per TTL (cycled per probe), no network I/O.</summary>
internal sealed class FakePinger : IPinger
{
    private readonly Dictionary<int, PingReplyInfo[]> _byTtl = new();
    private readonly Dictionary<int, int> _cursor = new();
    private readonly Queue<PingReplyInfo> _sequence = new();
    public int Calls { get; private set; }
    public List<int> TtlsSeen { get; } = new();
    public bool Disposed { get; private set; }
    /// <summary>Optional hook before each reply (used to cancel mid-run).</summary>
    public Action<int>? BeforeReply { get; set; }

    public FakePinger Hop(int ttl, params PingReplyInfo[] replies)
    {
        _byTtl[ttl] = replies;
        return this;
    }

    public FakePinger Expired(int ttl, string address, double rtt) => Hop(ttl, PingReplyInfo.Expired(address, rtt));
    public FakePinger Silent(int ttl) => Hop(ttl, PingReplyInfo.Timeout());
    public FakePinger Reached(int ttl, string address, double rtt) => Hop(ttl, PingReplyInfo.Success(address, rtt));

    /// <summary>TTL-agnostic ordered replies (ping monitor).</summary>
    public FakePinger Then(params PingReplyInfo[] replies)
    {
        foreach (var r in replies) _sequence.Enqueue(r);
        return this;
    }

    public Task<PingReplyInfo> SendAsync(IPAddress target, int timeoutMs, int ttl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls++;
        TtlsSeen.Add(ttl);
        BeforeReply?.Invoke(ttl);
        ct.ThrowIfCancellationRequested(); // a real pinger cancels the in-flight echo via WaitAsync
        if (_sequence.Count > 0)
            return Task.FromResult(_sequence.Dequeue());
        if (!_byTtl.TryGetValue(ttl, out var replies) || replies.Length == 0)
            return Task.FromResult(PingReplyInfo.Timeout());
        int i = _cursor.GetValueOrDefault(ttl);
        _cursor[ttl] = i + 1;
        return Task.FromResult(replies[i % replies.Length]);
    }

    public void Dispose() => Disposed = true;
}

public class NetworkTracerouteTests
{
    private static readonly IPAddress Target = IPAddress.Parse("1.1.1.1");
    private static readonly TracerouteOptions Fast = new() { InterProbeDelayMs = 0, MaxHops = 30, ProbesPerHop = 3 };

    private static Task<TracerouteResult> Run(FakePinger p, TracerouteOptions? o = null, IProgress<TracerouteHop>? progress = null, CancellationToken ct = default)
        => TracerouteService.RunAsync(p, Target, "1.1.1.1", o ?? Fast, ct, progress);

    [Fact]
    public async Task Marks_gateway_first_public_and_destination()
    {
        var p = new FakePinger()
            .Expired(1, "192.168.1.1", 1)
            .Expired(2, "100.64.0.1", 5)        // CGNAT — still private
            .Expired(3, "203.0.113.1", 12)      // first public
            .Expired(4, "203.0.113.9", 14)
            .Reached(5, "1.1.1.1", 15);
        var r = await Run(p);

        Assert.True(r.ReachedDestination);
        Assert.Equal(TracerouteTermination.Reached, r.Termination);
        Assert.Equal(5, r.Hops.Count);
        Assert.Equal(0, r.GatewayHopIndex);
        Assert.True(r.Hops[0].IsGateway);
        Assert.Equal(2, r.FirstPublicHopIndex);
        Assert.True(r.Hops[2].IsFirstPublic);
        Assert.False(r.Hops[3].IsFirstPublic);
        Assert.True(r.Hops[3].IsPublic);
        Assert.False(r.Hops[1].IsPublic);
        Assert.True(r.Hops[4].IsDestination);
        Assert.Null(r.LastAnsweringTtl);
        Assert.All(r.Hops, h => Assert.Equal(HopState.Answered, h.State));
        Assert.Equal(15, p.Calls); // 5 hops × 3 probes
    }

    [Fact]
    public async Task Per_hop_stats_min_avg_max_jitter_loss()
    {
        var p = new FakePinger()
            .Hop(1, PingReplyInfo.Expired("10.0.0.1", 10), PingReplyInfo.Timeout(), PingReplyInfo.Expired("10.0.0.1", 30))
            .Reached(2, "1.1.1.1", 20);
        var r = await Run(p);
        var h = r.Hops[0];
        Assert.Equal(3, h.Sent);
        Assert.Equal(2, h.Received);
        Assert.Equal(10, h.MinMs);
        Assert.Equal(30, h.MaxMs);
        Assert.Equal(20, h.AvgMs);
        Assert.Equal(20, h.JitterMs);          // |30-10| / 1
        Assert.Equal(100.0 / 3.0, h.LossPercent, 6);
        Assert.Equal(new double?[] { 10, null, 30 }, h.Rtts);
    }

    [Fact]
    public async Task Filtered_hop_is_distinguished_from_trailing_silence()
    {
        var p = new FakePinger()
            .Expired(1, "192.168.0.1", 1)
            .Silent(2)                            // router drops ICMP for itself
            .Expired(3, "198.51.100.1", 9)
            .Silent(4).Silent(5).Silent(6).Silent(7).Silent(8).Silent(9);
        var r = await Run(p);

        Assert.Equal(TracerouteTermination.ConsecutiveTimeouts, r.Termination);
        Assert.False(r.ReachedDestination);
        Assert.Equal(HopState.Filtered, r.Hops[1].State);
        Assert.Null(r.Hops[1].Address);
        Assert.Equal(HopState.Answered, r.Hops[2].State);
        Assert.Equal(3, r.LastAnsweringTtl);
        // 5 consecutive silent hops stop the trace: hops 4..8 → 8 hops total.
        Assert.Equal(8, r.Hops.Count);
        Assert.All(r.Hops.Skip(3), h => Assert.Equal(HopState.Silent, h.State));
        Assert.Equal(2, r.FirstPublicHopIndex);
    }

    [Fact]
    public async Task Everything_silent_reports_no_gateway_and_last_answer_zero()
    {
        var p = new FakePinger(); // every TTL times out
        var r = await Run(p, new TracerouteOptions { InterProbeDelayMs = 0, MaxConsecutiveSilentHops = 3, ProbesPerHop = 1 });
        Assert.Equal(TracerouteTermination.ConsecutiveTimeouts, r.Termination);
        Assert.Equal(3, r.Hops.Count);
        Assert.Null(r.GatewayHopIndex);
        Assert.Null(r.FirstPublicHopIndex);
        Assert.Equal(0, r.LastAnsweringTtl);
        Assert.All(r.Hops, h => Assert.Equal(HopState.Silent, h.State));
    }

    [Fact]
    public async Task Max_hops_terminates_without_destination()
    {
        var p = new FakePinger();
        for (int ttl = 1; ttl <= 10; ttl++) p.Expired(ttl, "203.0.113." + ttl, ttl);
        var r = await Run(p, new TracerouteOptions { InterProbeDelayMs = 0, MaxHops = 4, ProbesPerHop = 1 });
        Assert.Equal(TracerouteTermination.MaxHops, r.Termination);
        Assert.Equal(4, r.Hops.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, p.TtlsSeen);
    }

    [Fact]
    public async Task Unreachable_reply_terminates_trace_and_keeps_node()
    {
        var p = new FakePinger()
            .Expired(1, "192.168.1.1", 1)
            .Hop(2, new PingReplyInfo(IPStatus.DestinationHostUnreachable, 3, IPAddress.Parse("192.168.1.1")));
        var r = await Run(p);
        Assert.Equal(TracerouteTermination.Unreachable, r.Termination);
        Assert.Equal(2, r.Hops.Count);
        Assert.Equal(IPStatus.DestinationHostUnreachable, r.Hops[1].ErrorStatus);
        Assert.Equal(HopState.Answered, r.Hops[1].State);
        Assert.Equal(0, r.Hops[1].Received);
    }

    [Fact]
    public async Task Known_gateway_marks_router_answering_on_later_ttl()
    {
        var gw = IPAddress.Parse("192.168.1.1");
        var p = new FakePinger().Silent(1).Expired(2, "192.168.1.1", 2).Reached(3, "1.1.1.1", 10);
        var r = await Run(p, new TracerouteOptions { InterProbeDelayMs = 0, KnownGateway = gw, ProbesPerHop = 1 });
        Assert.Equal(1, r.GatewayHopIndex);
        Assert.True(r.Hops[1].IsGateway);
        Assert.Equal(HopState.Filtered, r.Hops[0].State);
    }

    [Fact]
    public async Task Progress_reports_each_hop_in_order()
    {
        var p = new FakePinger().Expired(1, "10.0.0.1", 1).Expired(2, "10.0.0.2", 2).Reached(3, "1.1.1.1", 3);
        var seen = new List<int>();
        var progress = new SyncProgress<TracerouteHop>(h => seen.Add(h.Ttl));
        await Run(p, new TracerouteOptions { InterProbeDelayMs = 0, ProbesPerHop = 1 }, progress);
        Assert.Equal(new[] { 1, 2, 3 }, seen);
    }

    [Fact]
    public async Task Cancellation_returns_partial_result()
    {
        using var cts = new CancellationTokenSource();
        var p = new FakePinger().Expired(1, "10.0.0.1", 1).Expired(2, "10.0.0.2", 2).Reached(3, "1.1.1.1", 3);
        p.BeforeReply = ttl => { if (ttl == 2) cts.Cancel(); };
        var r = await Run(p, new TracerouteOptions { InterProbeDelayMs = 0, ProbesPerHop = 1 }, ct: cts.Token);
        Assert.Equal(TracerouteTermination.Cancelled, r.Termination);
        Assert.Single(r.Hops);
        Assert.True(r.Hops[0].IsGateway);
    }

    [Fact]
    public async Task Reverse_dns_only_when_enabled_and_uses_resolver()
    {
        var p = new FakePinger().Expired(1, "10.0.0.1", 1).Reached(2, "1.1.1.1", 3);
        var resolver = new FakeReverse();
        var off = await TracerouteService.RunAsync(p, Target, "1.1.1.1",
            new TracerouteOptions { InterProbeDelayMs = 0, ProbesPerHop = 1, ReverseDns = false }, default, null, resolver);
        Assert.Equal(0, resolver.Calls);
        Assert.All(off.Hops, h => Assert.Null(h.HostName));

        var p2 = new FakePinger().Expired(1, "10.0.0.1", 1).Reached(2, "1.1.1.1", 3);
        var on = await TracerouteService.RunAsync(p2, Target, "1.1.1.1",
            new TracerouteOptions { InterProbeDelayMs = 0, ProbesPerHop = 1, ReverseDns = true }, default, null, resolver);
        Assert.Equal(2, resolver.Calls);
        Assert.Equal("one.one.one.one", on.Hops[1].HostName);
        Assert.Equal(TimeSpan.FromSeconds(2), resolver.LastTimeout);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.100.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("169.254.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("2606:4700::1111", false)]
    public void IsPrivate_classifies(string addr, bool expected)
    {
        Assert.Equal(expected, IPAddressClass.IsPrivate(IPAddress.Parse(addr)));
    }

    [Fact]
    public void Options_defaults_match_spec()
    {
        var o = new TracerouteOptions();
        Assert.Equal(30, o.MaxHops);
        Assert.Equal(3, o.ProbesPerHop);
        Assert.False(o.ReverseDns);
        Assert.Equal(TimeSpan.FromSeconds(2), o.ReverseDnsTimeout);
    }

    private sealed class FakeReverse : IReverseResolver
    {
        public int Calls;
        public TimeSpan LastTimeout;
        public Task<string?> ReverseAsync(IPAddress address, TimeSpan timeout, CancellationToken ct)
        {
            Calls++;
            LastTimeout = timeout;
            return Task.FromResult<string?>(address.ToString() == "1.1.1.1" ? "one.one.one.one" : null);
        }
    }
}

/// <summary>IProgress that invokes synchronously (Progress&lt;T&gt; posts to a sync context and would race the asserts).</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    public SyncProgress(Action<T> handler) => _handler = handler;
    public void Report(T value) => _handler(value);
}
