using System.Net;
using Visor.Services.NetworkTweaks;
using Xunit;

namespace Visor.Tests;

public class NetTweakBenchmarkTests
{
    private static DnsCandidate C(string id, string ip, bool current = false) =>
        new(id, id, new[] { IPAddress.Parse(ip) }, IsCurrent: current);

    // ---------------- stats ----------------

    [Fact]
    public void Stats_median_p95_and_failures()
    {
        var s = DnsBenchmarkStats.Compute(new double?[] { 10, 30, 20, null, 40 });
        Assert.Equal(5, s.Queries);
        Assert.Equal(4, s.Answered);
        Assert.Equal(1, s.Failures);
        Assert.Equal(20, s.FailurePercent, 6);
        Assert.Equal(10, s.MinMs);
        Assert.Equal(25, s.MedianMs);   // even count → mean of 20 and 30
        Assert.Equal(40, s.P95Ms);
    }

    [Fact]
    public void Stats_nearest_rank_p95_on_twenty_samples_is_the_19th()
    {
        var s = DnsBenchmarkStats.Compute(Enumerable.Range(1, 20).Select(i => (double?)i));
        Assert.Equal(19, s.P95Ms);
        Assert.Equal(10.5, s.MedianMs);
        Assert.Equal(0, s.FailurePercent);
    }

    [Fact]
    public void Stats_all_failed_and_empty()
    {
        var dead = DnsBenchmarkStats.Compute(new double?[] { null, null });
        Assert.Equal(100, dead.FailurePercent);
        Assert.Null(dead.MedianMs);
        Assert.Null(dead.P95Ms);
        var empty = DnsBenchmarkStats.Compute(Array.Empty<double?>());
        Assert.Equal(0, empty.Queries);
        Assert.Null(empty.MedianMs);
    }

    // ---------------- ranking ----------------

    private static DnsBenchmarkStats S(double median, double p95, double failPct = 0) =>
        new(100, (int)(100 - failPct), failPct, median, median, p95);

    [Fact]
    public void Ranking_reliable_first_then_median_then_p95()
    {
        var rows = DnsBenchmarkRanking.Rank(new[]
        {
            (C("flaky", "1.1.1.1"), S(5, 6, failPct: 20)),
            (C("slow", "2.2.2.2"), S(40, 60)),
            (C("fast-jittery", "3.3.3.3"), S(10, 90)),
            (C("fast-steady", "4.4.4.4"), S(10, 12)),
            (C("dead", "5.5.5.5"), DnsBenchmarkStats.Compute(new double?[] { null })),
        });
        Assert.Equal(new[] { "fast-steady", "fast-jittery", "slow", "flaky", "dead" }, rows.Select(r => r.Candidate.Id));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, rows.Select(r => r.Rank));
    }

    [Fact]
    public void Ranking_tie_keeps_current_first()
    {
        var rows = DnsBenchmarkRanking.Rank(new[]
        {
            (C("other", "1.1.1.1"), S(10, 12)),
            (C("mine", "2.2.2.2", current: true), S(10, 12)),
        });
        Assert.Equal("mine", rows[0].Candidate.Id);
    }

    [Fact]
    public void Switch_is_recommended_only_for_a_meaningful_gain()
    {
        var cur = S(30, 40);
        DnsBenchmarkRow Best(double median, bool current = false, double fail = 0) => new(C("b", "9.9.9.9", current), S(median, median + 2, fail), 1);

        Assert.True(DnsBenchmarkRanking.IsMeaningfulGain(cur, Best(12)));       // 18 ms and 60 %
        Assert.False(DnsBenchmarkRanking.IsMeaningfulGain(cur, Best(27)));      // 3 ms — noise
        Assert.False(DnsBenchmarkRanking.IsMeaningfulGain(S(100, 120), Best(94)));   // 6 ms but only 6 %
        Assert.False(DnsBenchmarkRanking.IsMeaningfulGain(cur, Best(5, current: true)));
        Assert.True(DnsBenchmarkRanking.IsMeaningfulGain(S(20, 30, failPct: 15), Best(21)));   // current drops queries
        Assert.True(DnsBenchmarkRanking.IsMeaningfulGain(null, Best(21)));
        Assert.False(DnsBenchmarkRanking.IsMeaningfulGain(cur, null));
    }

    // ---------------- candidates ----------------

    [Fact]
    public void Candidates_include_current_once_and_custom()
    {
        var list = DnsProviders.BuildCandidates(new[] { IPAddress.Parse("192.168.1.1") }, new[] { IPAddress.Parse("76.76.2.0") });
        Assert.Equal(DnsProviders.CurrentId, list[0].Id);
        Assert.True(list[0].IsCurrent);
        Assert.Equal(DnsProviders.CustomId, list[^1].Id);
        Assert.Equal(new[] { "cloudflare", "google", "quad9", "adguard", "opendns" },
            list.Where(c => !c.IsCurrent && !c.IsCustom).Select(c => c.Id));

        // Already on Cloudflare: no duplicate "current" row, Cloudflare carries the flag.
        var onCf = DnsProviders.BuildCandidates(new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("1.0.0.1") }, null);
        Assert.DoesNotContain(onCf, c => c.Id == DnsProviders.CurrentId);
        Assert.True(onCf.Single(c => c.Id == "cloudflare").IsCurrent);

        // No IPv4 DNS known → no current row.
        Assert.DoesNotContain(DnsProviders.BuildCandidates(new[] { IPAddress.IPv6Loopback }, null), c => c.IsCurrent);
    }

    [Fact]
    public void Custom_input_parsing()
    {
        Assert.Equal(new[] { IPAddress.Parse("1.2.3.4"), IPAddress.Parse("5.6.7.8") }, DnsProviders.ParseCustom(" 1.2.3.4, 5.6.7.8 "));
        Assert.Equal(new[] { IPAddress.Parse("1.2.3.4") }, DnsProviders.ParseCustom("1.2.3.4;"));
        Assert.Null(DnsProviders.ParseCustom("1.2.3"));
        Assert.Null(DnsProviders.ParseCustom("dns.google"));
        Assert.Null(DnsProviders.ParseCustom(""));
    }

    // ---------------- service with a fake client ----------------

    private sealed class FakeDns : IDnsQueryClient
    {
        public readonly Dictionary<string, Func<int, double?>> ByServer = new();
        public readonly Dictionary<string, int> Calls = new();
        public readonly object Gate = new();

        public Task<DnsQueryResult> QueryAsync(IPAddress server, string name, TimeSpan timeout, CancellationToken ct)
        {
            int n;
            lock (Gate)
            {
                Calls.TryGetValue(server.ToString(), out n);
                Calls[server.ToString()] = n + 1;
            }
            double? rtt = ByServer[server.ToString()](n);
            return Task.FromResult(rtt == null
                ? new DnsQueryResult(DnsResponseStatus.Timeout, null, Array.Empty<IPAddress>())
                : new DnsQueryResult(DnsResponseStatus.Ok, rtt, new[] { IPAddress.Parse("10.0.0.1") }));
        }
    }

    [Fact]
    public async Task Service_discards_warmup_and_ranks_by_measured_rounds()
    {
        var fake = new FakeDns();
        // "a": very slow on the first (warm-up) round only, then 8 ms. "b": constant 15 ms. "c": every 3rd query lost.
        fake.ByServer["1.1.1.1"] = n => n < 3 ? 900 : 8;
        fake.ByServer["2.2.2.2"] = _ => 15;
        fake.ByServer["3.3.3.3"] = n => n % 3 == 0 ? null : 5;
        var svc = new DnsBenchmarkService(fake, rounds: 2, domains: new[] { "x.com", "y.com", "z.com" });
        var progress = new List<double>();
        var report = await svc.RunAsync(new[] { C("a", "1.1.1.1"), C("b", "2.2.2.2", current: true), C("c", "3.3.3.3") },
            new SyncProgress(progress));

        Assert.Equal(9, fake.Calls["1.1.1.1"]);                     // 1 warm-up + 2 rounds × 3 domains
        Assert.Equal(6, report.Rows.Single(r => r.Candidate.Id == "a").Stats.Queries);
        Assert.Equal(8, report.Rows.Single(r => r.Candidate.Id == "a").Stats.MedianMs);
        Assert.Equal(new[] { "a", "b", "c" }, report.Rows.Select(r => r.Candidate.Id));   // c is unreliable
        Assert.Equal("a", report.Best!.Candidate.Id);
        Assert.Equal("b", report.Current!.Candidate.Id);
        Assert.True(report.SwitchRecommended);                     // 15 → 8 ms: 7 ms and 47 % faster
    }

    private sealed class SyncProgress : IProgress<double>
    {
        private readonly List<double> _sink;
        public SyncProgress(List<double> sink) => _sink = sink;
        public void Report(double value) { lock (_sink) _sink.Add(value); }
    }

    [Fact]
    public async Task Service_reports_progress_to_one_and_honours_cancellation()
    {
        var fake = new FakeDns();
        fake.ByServer["1.1.1.1"] = _ => 5;
        var progress = new List<double>();
        var svc = new DnsBenchmarkService(fake, rounds: 1, domains: new[] { "x.com", "y.com" }, warmUp: false);
        await svc.RunAsync(new[] { C("a", "1.1.1.1") }, new SyncProgress(progress));
        Assert.Equal(new[] { 0.5, 1.0 }, progress);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.RunAsync(new[] { C("a", "1.1.1.1") }, null, cts.Token));
    }
}
