using System.Net;
using System.Net.Sockets;
using Visor.Services.Elevation;

namespace Visor.Services.NetworkTweaks;

/// <summary>A DNS server pair to benchmark. Only <see cref="Servers"/>[0] (primary) is timed — Windows asks it first.</summary>
public sealed record DnsCandidate(string Id, string Name, IReadOnlyList<IPAddress> Servers, bool IsCurrent = false, bool IsCustom = false)
{
    public IPAddress Primary => Servers[0];

    /// <summary>"1.1.1.1,1.0.0.1" — the form used in action ids and helper ops.</summary>
    public string ServersCsv => string.Join(",", Servers.Select(s => s.ToString()));
}

/// <summary>Well-known public resolvers (IPv4 pairs).</summary>
public static class DnsProviders
{
    public const string CurrentId = "current";
    public const string CustomId = "custom";

    public static readonly IReadOnlyList<DnsCandidate> Public = new[]
    {
        Make("cloudflare", "Cloudflare", "1.1.1.1", "1.0.0.1"),
        Make("google", "Google", "8.8.8.8", "8.8.4.4"),
        Make("quad9", "Quad9", "9.9.9.9", "149.112.112.112"),
        Make("adguard", "AdGuard", "94.140.14.14", "94.140.15.15"),
        Make("opendns", "OpenDNS", "208.67.222.222", "208.67.220.220"),
    };

    private static DnsCandidate Make(string id, string name, params string[] servers) =>
        new(id, name, servers.Select(IPAddress.Parse).ToArray());

    /// <summary>
    /// Current DNS (as Windows uses it now), the public resolvers and an optional custom pair. When the
    /// current servers ARE one of the public pairs, that pair is flagged current instead of listed twice.
    /// </summary>
    public static IReadOnlyList<DnsCandidate> BuildCandidates(IReadOnlyList<IPAddress>? currentDns, IReadOnlyList<IPAddress>? custom)
    {
        var current = (currentDns ?? Array.Empty<IPAddress>())
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Distinct().Take(ElevationAllowlist.MaxDnsServers).ToArray();
        var list = new List<DnsCandidate>();
        bool currentMatched = false;
        foreach (var p in Public)
        {
            bool isCurrent = current.Length > 0 && current[0].Equals(p.Primary);
            currentMatched |= isCurrent;
            list.Add(isCurrent ? p with { IsCurrent = true } : p);
        }
        if (current.Length > 0 && !currentMatched)
            list.Insert(0, new DnsCandidate(CurrentId, "Current", current, IsCurrent: true));

        var customList = (custom ?? Array.Empty<IPAddress>())
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Distinct().Take(ElevationAllowlist.MaxDnsServers).ToArray();
        if (customList.Length > 0 && !list.Any(c => c.Primary.Equals(customList[0])))
            list.Add(new DnsCandidate(CustomId, "Custom", customList, IsCustom: true));
        return list;
    }

    /// <summary>Parses user input "1.2.3.4, 5.6.7.8" (1..4 canonical IPv4, as the helper requires). Null when invalid.</summary>
    public static IReadOnlyList<IPAddress>? ParseCustom(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string csv = string.Join(",", text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        return ElevationAllowlist.TryParseDnsServers(csv, out var list) ? list : null;
    }
}

/// <summary>Latency statistics of one candidate (nulls in the input = failed queries).</summary>
public sealed record DnsBenchmarkStats(int Queries, int Answered, double FailurePercent, double? MinMs, double? MedianMs, double? P95Ms)
{
    public int Failures => Queries - Answered;

    public static DnsBenchmarkStats Compute(IEnumerable<double?> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var all = samples.ToList();
        var ok = all.Where(s => s.HasValue).Select(s => s!.Value).OrderBy(v => v).ToArray();
        double failPct = all.Count == 0 ? 0 : 100.0 * (all.Count - ok.Length) / all.Count;
        if (ok.Length == 0) return new DnsBenchmarkStats(all.Count, 0, all.Count == 0 ? 0 : 100, null, null, null);
        return new DnsBenchmarkStats(all.Count, ok.Length, failPct, ok[0], Median(ok), NearestRank(ok, 0.95));
    }

    /// <summary>Median of a sorted array (mean of the two middle values for an even count).</summary>
    public static double Median(double[] sorted)
    {
        int n = sorted.Length;
        if (n == 0) throw new ArgumentException("empty", nameof(sorted));
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    /// <summary>Nearest-rank percentile of a sorted array (p95 of 20 samples = the 19th).</summary>
    public static double NearestRank(double[] sorted, double p)
    {
        int n = sorted.Length;
        if (n == 0) throw new ArgumentException("empty", nameof(sorted));
        int rank = (int)Math.Ceiling(p * n);
        return sorted[Math.Clamp(rank, 1, n) - 1];
    }
}

public sealed record DnsBenchmarkRow(DnsCandidate Candidate, DnsBenchmarkStats Stats, int Rank);

public sealed record DnsBenchmarkReport(IReadOnlyList<DnsBenchmarkRow> Rows, DateTime Utc, int Rounds, IReadOnlyList<string> Domains)
{
    public DnsBenchmarkRow? Best => Rows.FirstOrDefault(r => r.Stats.Answered > 0);
    public DnsBenchmarkRow? Current => Rows.FirstOrDefault(r => r.Candidate.IsCurrent);

    /// <summary>
    /// True only when switching is worth a UAC prompt: the best candidate is not the current one and is
    /// clearly faster (≥ 5 ms AND ≥ 20 % on the median) or the current one drops queries (&gt; 5 %) while the best does not.
    /// </summary>
    public bool SwitchRecommended => DnsBenchmarkRanking.IsMeaningfulGain(Current?.Stats, Best);
}

/// <summary>Pure ranking rules (unit-tested).</summary>
public static class DnsBenchmarkRanking
{
    /// <summary>Failure share above which a candidate is ranked behind every reliable one.</summary>
    public const double UnreliableFailurePercent = 5.0;
    public const double MinGainMs = 5.0;
    public const double MinGainFraction = 0.20;

    /// <summary>
    /// Reliable (≤ 5 % failures) before unreliable before "never answered"; within a group by median,
    /// then p95, then failure share; ties keep the current DNS first (no switch for nothing).
    /// </summary>
    public static IReadOnlyList<DnsBenchmarkRow> Rank(IEnumerable<(DnsCandidate Candidate, DnsBenchmarkStats Stats)> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var ordered = results
            .OrderBy(r => Group(r.Stats))
            .ThenBy(r => r.Stats.MedianMs ?? double.MaxValue)
            .ThenBy(r => r.Stats.P95Ms ?? double.MaxValue)
            .ThenBy(r => r.Stats.FailurePercent)
            .ThenBy(r => r.Candidate.IsCurrent ? 0 : 1)
            .ToList();
        return ordered.Select((r, i) => new DnsBenchmarkRow(r.Candidate, r.Stats, i + 1)).ToList();
    }

    private static int Group(DnsBenchmarkStats s) =>
        s.Answered == 0 ? 2 : s.FailurePercent > UnreliableFailurePercent ? 1 : 0;

    public static bool IsMeaningfulGain(DnsBenchmarkStats? current, DnsBenchmarkRow? best)
    {
        if (best == null || best.Candidate.IsCurrent || best.Stats.MedianMs == null) return false;
        if (current == null || current.Answered == 0) return true;   // current DNS unknown / dead
        bool currentUnreliable = current.FailurePercent > UnreliableFailurePercent;
        if (currentUnreliable && best.Stats.FailurePercent <= 1.0) return true;
        if (current.MedianMs == null) return true;
        double gain = current.MedianMs.Value - best.Stats.MedianMs.Value;
        return gain >= MinGainMs && gain >= current.MedianMs.Value * MinGainFraction;
    }
}

/// <summary>
/// Times A lookups of popular game / CDN / platform names straight against each candidate's primary
/// server over UDP (no admin, the Windows cache is bypassed). One warm-up round is discarded so the
/// numbers show what you get for popular names in practice (resolver cache hits); then
/// <see cref="Rounds"/> measured rounds. Within a (round, domain) step all candidates are queried in
/// parallel so they share the same network conditions.
/// </summary>
public sealed class DnsBenchmarkService
{
    public static readonly IReadOnlyList<string> DefaultDomains = new[]
    {
        "store.steampowered.com", "steamcdn-a.akamaihd.net", "www.epicgames.com", "www.riotgames.com",
        "eu.battle.net", "www.ea.com", "www.ubisoft.com", "www.xbox.com", "discord.com", "www.twitch.tv",
        "www.youtube.com", "www.google.com", "www.cloudflare.com", "www.microsoft.com",
    };

    private readonly IDnsQueryClient _client;
    private readonly TimeSpan _timeout;

    public DnsBenchmarkService(IDnsQueryClient? client = null, TimeSpan? timeout = null, int rounds = 3,
        IReadOnlyList<string>? domains = null, bool warmUp = true)
    {
        _client = client ?? new UdpDnsQueryClient();
        _timeout = timeout ?? TimeSpan.FromMilliseconds(1500);
        Rounds = Math.Clamp(rounds, 1, 20);
        Domains = domains is { Count: > 0 } ? domains : DefaultDomains;
        WarmUp = warmUp;
    }

    public int Rounds { get; }
    public IReadOnlyList<string> Domains { get; }
    public bool WarmUp { get; }

    /// <param name="progress">0..1 as steps complete.</param>
    public async Task<DnsBenchmarkReport> RunAsync(IReadOnlyList<DnsCandidate> candidates, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var samples = candidates.Select(_ => new List<double?>()).ToArray();
        int totalRounds = Rounds + (WarmUp ? 1 : 0);
        int totalSteps = Math.Max(1, totalRounds * Domains.Count);
        int step = 0;
        for (int round = 0; round < totalRounds; round++)
        {
            bool measured = !WarmUp || round > 0;
            foreach (var domain in Domains)
            {
                ct.ThrowIfCancellationRequested();
                var tasks = candidates.Select(c => SafeQuery(c.Primary, domain, ct)).ToArray();
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                if (measured)
                    for (int i = 0; i < results.Length; i++)
                        samples[i].Add(results[i].RttMs);
                progress?.Report(++step / (double)totalSteps);
            }
        }
        var rows = DnsBenchmarkRanking.Rank(candidates.Select((c, i) => (c, DnsBenchmarkStats.Compute(samples[i]))));
        return new DnsBenchmarkReport(rows, DateTime.UtcNow, Rounds, Domains);
    }

    private async Task<DnsQueryResult> SafeQuery(IPAddress server, string domain, CancellationToken ct)
    {
        try { return await _client.QueryAsync(server, domain, _timeout, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "DNS benchmark query");
            return new DnsQueryResult(DnsResponseStatus.NetworkError, null, Array.Empty<IPAddress>());
        }
    }
}
