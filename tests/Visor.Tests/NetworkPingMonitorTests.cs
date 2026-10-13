using System.Net;
using System.Net.NetworkInformation;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class NetworkPingMonitorTests
{
    private static readonly IPAddress Host = IPAddress.Parse("1.1.1.1");

    private static PingMonitor Make(FakePinger p, int capacity = 300, bool owns = false) =>
        new(p, Host, "cf", capacity, intervalMs: 1, timeoutMs: 100, ownsPinger: owns);

    [Fact]
    public void RingBuffer_overwrites_oldest_and_counts_total()
    {
        var ring = new RingBuffer<int>(3);
        Assert.Equal(0, ring.Count);
        Assert.Equal(default, ring.Last);
        ring.Add(1); ring.Add(2); ring.Add(3);
        Assert.Equal(new[] { 1, 2, 3 }, ring.ToArray());
        ring.Add(4); ring.Add(5);
        Assert.Equal(3, ring.Count);
        Assert.Equal(5, ring.TotalAdded);
        Assert.Equal(new[] { 3, 4, 5 }, ring.ToArray());
        Assert.Equal(5, ring.Last);
        ring.Clear();
        Assert.Equal(0, ring.Count);
        Assert.Empty(ring.ToArray());
    }

    [Fact]
    public void RingBuffer_rejects_zero_capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBuffer<int>(0));
    }

    [Fact]
    public void Percentile_nearest_rank()
    {
        var v = Enumerable.Range(1, 20).Select(i => (double)i).ToList();
        Assert.Equal(19, NetworkStats.PercentileSorted(v, 95));
        Assert.Equal(10, NetworkStats.PercentileSorted(v, 50));
        Assert.Equal(1, NetworkStats.PercentileSorted(v, 0));
        Assert.Equal(20, NetworkStats.PercentileSorted(v, 100));
        Assert.Null(NetworkStats.PercentileSorted(Array.Empty<double>(), 95));
        Assert.Equal(7, NetworkStats.Percentile(new[] { 9.0, 7, 3 }, 50));
    }

    [Fact]
    public void Stats_p50_p95_loss_jitter_recent()
    {
        var rtts = new List<double?>();
        for (int i = 1; i <= 19; i++) rtts.Add(i);   // 1..19
        rtts.Add(null);                               // one loss at the end
        var s = PingSeriesStats.Compute(rtts);
        Assert.Equal(20, s.Sent);
        Assert.Equal(19, s.Received);
        Assert.Equal(1, s.Lost);
        Assert.Equal(5, s.LossPercent);
        Assert.Equal(10, s.P50Ms);   // ceil(0.5*19)=10 → 10th value
        Assert.Equal(19, s.P95Ms);   // ceil(0.95*19)=19 → 19th value
        Assert.Equal(19, s.MaxMs);
        Assert.Equal(1, s.MinMs);
        Assert.Equal(1, s.JitterMs); // consecutive |Δ| = 1
        Assert.Null(s.LastMs);
        Assert.Equal(1, s.RecentLost);
    }

    [Fact]
    public void Stats_empty_and_all_lost()
    {
        Assert.Same(PingSeriesStats.Empty, PingSeriesStats.Compute(Array.Empty<double?>()));
        var s = PingSeriesStats.Compute(new double?[] { null, null });
        Assert.Equal(100, s.LossPercent);
        Assert.Null(s.P95Ms);
        Assert.Equal(2, s.RecentLost);
    }

    [Fact]
    public async Task ProbeOnce_appends_success_and_loss_samples_with_sequence()
    {
        var p = new FakePinger().Then(
            PingReplyInfo.Success("1.1.1.1", 12),
            PingReplyInfo.Timeout(),
            new PingReplyInfo(IPStatus.DestinationHostUnreachable, 0, null),
            PingReplyInfo.Success("1.1.1.1", 14));
        using var m = Make(p);
        var seen = new List<PingSeriesSample>();
        m.SampleAdded += seen.Add;

        for (int i = 0; i < 4; i++) await m.ProbeOnceAsync(default);

        Assert.Equal(4, m.Count);
        Assert.Equal(4, m.TotalSent);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, seen.Select(s => s.Sequence));
        Assert.Equal(new double?[] { 12, null, null, 14 }, m.RttWindow());
        Assert.Equal(ProbeErrorCode.Timeout, seen[1].ErrorCode);
        Assert.Equal(ProbeErrorCode.Unreachable, seen[2].ErrorCode);
        Assert.True(seen[1].IsLost);
        var st = m.Stats;
        Assert.Equal(50, st.LossPercent);
        Assert.Equal(14, st.LastMs);
        Assert.Equal(2, st.JitterMs);
        Assert.Equal("cf", m.TargetLabel);
        Assert.Equal(0, p.TtlsSeen.Distinct().Single()); // OS default TTL
    }

    [Fact]
    public async Task Ring_wraps_after_capacity_and_stats_use_window_only()
    {
        var p = new FakePinger();
        for (int i = 1; i <= 8; i++) p.Then(PingReplyInfo.Success("1.1.1.1", i * 10));
        using var m = Make(p, capacity: 5);
        for (int i = 0; i < 8; i++) await m.ProbeOnceAsync(default);
        Assert.Equal(5, m.Count);
        Assert.Equal(8, m.TotalSent);
        Assert.Equal(new double?[] { 40, 50, 60, 70, 80 }, m.RttWindow());
        Assert.Equal(40, m.Stats.MinMs);
        Assert.Equal(5, m.Stats.Sent);
        Assert.Equal(8, m.Snapshot()[^1].Sequence);
    }

    [Fact]
    public async Task Start_runs_loop_until_stopped_and_dispose_releases_owned_pinger()
    {
        var p = new FakePinger();
        for (int i = 0; i < 1000; i++) p.Then(PingReplyInfo.Success("1.1.1.1", 5));
        var m = Make(p, owns: true);
        Assert.False(m.IsRunning);
        m.Start();
        Assert.True(m.IsRunning);
        m.Start(); // idempotent

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (m.Count < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(5);
        Assert.True(m.Count >= 3, "loop produced samples");

        await m.Stop();
        Assert.False(m.IsRunning);
        long after = m.TotalSent;
        await Task.Delay(30);
        Assert.Equal(after, m.TotalSent); // no more samples after stop

        m.Dispose();
        deadline = DateTime.UtcNow.AddSeconds(2);
        while (!p.Disposed && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(p.Disposed);
        Assert.Throws<ObjectDisposedException>(() => m.Start());
    }

    [Fact]
    public async Task Pinger_exception_becomes_lost_sample_not_crash()
    {
        var m = new PingMonitor(new ThrowingPinger(), Host, null, 10, 1, 100);
        var s = await m.ProbeOnceAsync(default);
        Assert.True(s.IsLost);
        Assert.Equal(ProbeErrorCode.NoNetwork, s.ErrorCode);
        Assert.Equal(Host.ToString(), m.TargetLabel);
    }

    [Fact]
    public void Defaults_are_1hz_300_samples()
    {
        Assert.Equal(1000, PingMonitor.DefaultIntervalMs);
        Assert.Equal(300, PingMonitor.DefaultCapacity);
        var m = new PingMonitor(new FakePinger(), Host);
        Assert.Equal(300, m.Capacity);
        Assert.Equal(0, m.Count);
        Assert.Same(PingSeriesStats.Empty, m.Stats);
    }

    [Fact]
    public async Task Stop_before_start_is_noop()
    {
        using var m = Make(new FakePinger());
        await m.Stop();
        Assert.False(m.IsRunning);
    }

    private sealed class ThrowingPinger : IPinger
    {
        public Task<PingReplyInfo> SendAsync(IPAddress target, int timeoutMs, int ttl, CancellationToken ct) =>
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.NetworkDown);
        public void Dispose() { }
    }
}

public class NetworkThroughputTests
{
    private static AdapterCounters C(long rx, long tx, int sec) => new(rx, tx, new DateTime(2026, 1, 1, 0, 0, sec, DateTimeKind.Utc));

    [Fact]
    public void Delta_computes_bytes_per_second()
    {
        var s = AdapterThroughputMonitor.Delta(C(1000, 500, 0), C(3000, 1500, 2));
        Assert.NotNull(s);
        Assert.Equal(1000, s!.RxBytesPerSec);
        Assert.Equal(500, s.TxBytesPerSec);
        Assert.Equal(0.008, s.RxMbps, 6);
    }

    [Fact]
    public void Delta_null_on_first_read_wrap_or_zero_time()
    {
        Assert.Null(AdapterThroughputMonitor.Delta(null, C(1, 1, 1)));
        Assert.Null(AdapterThroughputMonitor.Delta(C(5000, 10, 0), C(100, 20, 1)));   // rx went backwards (reset/wrap)
        Assert.Null(AdapterThroughputMonitor.Delta(C(10, 5000, 0), C(20, 100, 1)));   // tx went backwards
        Assert.Null(AdapterThroughputMonitor.Delta(C(10, 10, 1), C(20, 20, 1)));      // Δt = 0
    }

    [Fact]
    public void Tick_skips_first_and_wrapped_reads_and_fills_ring()
    {
        var reads = new Queue<AdapterCounters?>(new AdapterCounters?[]
        {
            C(0, 0, 0), C(100, 50, 1), C(300, 100, 2), C(10, 10, 3) /* wrap */, C(20, 20, 4), null, C(500, 500, 6), C(600, 650, 7)
        });
        var m = new AdapterThroughputMonitor(() => reads.Dequeue(), capacity: 3);
        Assert.Null(m.Tick());                                  // first read
        Assert.Equal(100, m.Tick()!.RxBytesPerSec);
        Assert.Equal(200, m.Tick()!.RxBytesPerSec);
        Assert.Null(m.Tick());                                  // counters went backwards
        Assert.Equal(10, m.Tick()!.RxBytesPerSec);              // resumes from the new baseline
        Assert.Null(m.Tick());                                  // source lost the adapter
        Assert.Null(m.Tick());                                  // first read after loss = new baseline
        var last = m.Tick();
        Assert.Equal(150, last!.TxBytesPerSec);
        Assert.Equal(3, m.Count);
        Assert.Same(last, m.Last);
        Assert.Equal(new[] { 200.0, 10, 100 }, m.Snapshot().Select(s => s.RxBytesPerSec));
        m.Reset();
        Assert.Equal(0, m.Count);
    }

    [Fact]
    public void Tick_tolerates_throwing_source()
    {
        var m = new AdapterThroughputMonitor(() => throw new InvalidOperationException("x"));
        Assert.Null(m.Tick());
        Assert.Equal(0, m.Count);
    }

    [Fact]
    public void ReadCounters_null_for_unknown_adapter()
    {
        // Enumerates adapters read-only; no packets. Unknown Id must yield null, never throw.
        Assert.Null(AdapterThroughputMonitor.ReadCounters(""));
        Assert.Null(AdapterThroughputMonitor.ReadCounters("{00000000-0000-0000-0000-000000000000}"));
    }

    [Fact]
    public void QueryFullDuplex_null_for_invalid_index()
    {
        Assert.Null(AdapterDetailsService.QueryFullDuplex(null));
        Assert.Null(AdapterDetailsService.QueryFullDuplex(0));
    }
}
