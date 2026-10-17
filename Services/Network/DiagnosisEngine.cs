using System.Net;

namespace Visor.Services;

public enum DiagnosisSeverity
{
    Info = 0,
    Warning = 1,
    Problem = 2
}

/// <summary>A measured number behind a finding: localizable key (net.ev.*) + format args.</summary>
public sealed record DiagnosisEvidence(string Key, params object[] Args);

/// <summary>
/// One hypothesis with the numbers that support it. <see cref="Key"/> is a Loc key (net.diag.*),
/// <see cref="Suggestions"/> are Loc keys (net.sug.*) for safe actions; <see cref="DiagnosisEngine.SuggestionSubSection"/>
/// says which ones open a Visor section (e.g. the DNS test) instead of being plain advice.
/// </summary>
public sealed class DiagnosisFinding
{
    public required DiagnosisSeverity Severity { get; init; }
    public required string Key { get; init; }
    public object[] Args { get; init; } = Array.Empty<object>();
    public required IReadOnlyList<DiagnosisEvidence> Evidence { get; init; }
    public IReadOnlyList<string> Suggestions { get; init; } = Array.Empty<string>();
}

/// <summary>Everything the engine looks at. All members optional; rules only fire on data they have.</summary>
public sealed class DiagnosisInput
{
    public NetworkAdapterInfo? Adapter { get; init; }
    /// <summary>From <see cref="AdapterDetailsService.QueryFullDuplex"/>; null = unknown.</summary>
    public bool? FullDuplex { get; init; }
    public WlanConnectionInfo? Wlan { get; init; }
    public TracerouteResult? Trace { get; init; }
    public string? PublicPingHost { get; init; }
    public PingSeriesStats? PublicPing { get; init; }
    public PingSeriesStats? GatewayPing { get; init; }
    public IReadOnlyList<DnsResolveResult>? Dns { get; init; }
}

/// <summary>Tunable rule thresholds (defaults are conservative so the engine under-reports rather than invents).</summary>
public sealed record DiagnosisThresholds
{
    public int WeakWifiQuality { get; init; } = 50;
    /// <summary>Jitter on the gateway hop / gateway ping that counts as "local instability" (ms).</summary>
    public double LanJitterMs { get; init; } = 10;
    public double IspJitterMs { get; init; } = 20;
    /// <summary>Ping-series loss that is no longer noise (%).</summary>
    public double LossPercent { get; init; } = 2;
    public double DnsSlowMs { get; init; } = 300;
    /// <summary>"Ping fine" for the DNS rules: avg below this and loss below <see cref="LossPercent"/>.</summary>
    public double PingOkAvgMs { get; init; } = 100;
    public double HighLatencyP50Ms { get; init; } = 150;
    public int MinMtu { get; init; } = 1400;
    public int SlowLinkMbps { get; init; } = 100;
    /// <summary>Minimum ping samples before loss / jitter rules trust the series.</summary>
    public int MinPingSamples { get; init; } = 5;
}

/// <summary>
/// Turns traceroute + ping series + adapter facts into ordered hypotheses. Pure and synchronous — no I/O.
/// Every finding carries the numbers it is based on; nothing is reported without evidence.
/// </summary>
public static class DiagnosisEngine
{
    // Finding keys
    public const string KeyApipa = "net.diag.apipa";
    public const string KeyNoGateway = "net.diag.noGateway";
    public const string KeyWifiSignal = "net.diag.wifiSignal";
    public const string KeyWifiWeak = "net.diag.wifiWeak";
    public const string KeyLan = "net.diag.lan";
    public const string KeyIsp = "net.diag.isp";
    public const string KeyDnsSlow = "net.diag.dnsSlow";
    public const string KeyDnsFail = "net.diag.dnsFail";
    public const string KeyMtu = "net.diag.mtu";
    public const string KeyHalfDuplex = "net.diag.halfDuplex";
    public const string KeyLinkSlow = "net.diag.linkSlow";
    public const string KeyInconclusive = "net.diag.inconclusive";
    public const string KeyPathDrop = "net.diag.pathDrop";
    public const string KeyHighLatency = "net.diag.highLatency";
    public const string KeyLossGeneric = "net.diag.lossGeneric";
    public const string KeyOk = "net.diag.ok";

    // Suggestion keys (manual, safe actions only)
    public const string SugEthernet = "net.sug.ethernet";
    public const string SugMoveCloser = "net.sug.moveCloser";
    public const string SugBand5 = "net.sug.band5";
    public const string SugRestartRouter = "net.sug.restartRouter";
    public const string SugCheckCable = "net.sug.checkCable";
    /// <summary>Points at Network → DNS (resolver test + one-click switch with undo); the UI renders it as a link.</summary>
    public const string SugDnsInApp = "net.sug.dnsInApp";
    public const string SugContactIsp = "net.sug.contactIsp";
    public const string SugDuplexAuto = "net.sug.duplexAuto";
    public const string SugMtuCheck = "net.sug.mtuCheck";
    public const string SugIcmpFiltered = "net.sug.icmpFiltered";
    public const string SugRunLonger = "net.sug.runLonger";
    public const string SugDhcpRenew = "net.sug.dhcpRenew";

    /// <summary>Network sub-section a suggestion links to ("dns"), or null when it is plain advice.</summary>
    public static string? SuggestionSubSection(string suggestionKey) =>
        string.Equals(suggestionKey, SugDnsInApp, StringComparison.OrdinalIgnoreCase) ? "dns" : null;

    // Evidence keys
    public const string EvWifiSignal = "net.ev.wifiSignal";   // {0} quality %, {1} dBm
    public const string EvHopJitter = "net.ev.hopJitter";     // {0} hop, {1} ms
    public const string EvHopLoss = "net.ev.hopLoss";         // {0} hop, {1} loss %, {2} sent
    public const string EvHopAvg = "net.ev.hopAvg";           // {0} hop, {1} avg ms
    public const string EvPingLoss = "net.ev.pingLoss";       // {0} host, {1} loss %, {2} sent
    public const string EvPingJitter = "net.ev.pingJitter";   // {0} host, {1} ms
    public const string EvPingP50 = "net.ev.pingP50";         // {0} host, {1} p50 ms, {2} p95 ms
    public const string EvDnsSlow = "net.ev.dnsSlow";         // {0} host, {1} ms
    public const string EvDnsFail = "net.ev.dnsFail";         // {0} host, {1} error token
    public const string EvMtu = "net.ev.mtu";                 // {0} bytes
    public const string EvDuplex = "net.ev.halfDuplex";       // (no args)
    public const string EvLinkSpeed = "net.ev.linkSpeed";     // {0} Mbps
    public const string EvNoPublicHop = "net.ev.noPublicHop"; // {0} hops probed
    public const string EvLastAnswer = "net.ev.lastAnswer";   // {0} last answering hop
    public const string EvIpv4 = "net.ev.ipv4";               // {0} address
    public const string EvNoGateway = "net.ev.noGateway";     // (no args)

    public static IReadOnlyList<DiagnosisFinding> Evaluate(DiagnosisInput input, DiagnosisThresholds? t = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        t ??= new DiagnosisThresholds();
        var findings = new List<DiagnosisFinding>();

        bool isWifi = input.Wlan != null || input.Adapter?.TypeCode == "wifi";
        bool isEthernet = input.Adapter?.TypeCode == "ethernet";
        bool apipa = input.Adapter?.IPv4 is string ip && IPAddress.TryParse(ip, out var a4) && NetworkInfoService.IsLinkLocal(a4);

        // --- Adapter-level blockers --------------------------------------------------------------
        if (apipa)
        {
            findings.Add(new DiagnosisFinding
            {
                Severity = DiagnosisSeverity.Problem,
                Key = KeyApipa,
                Evidence = new[] { new DiagnosisEvidence(EvIpv4, input.Adapter!.IPv4!) },
                Suggestions = new[] { SugDhcpRenew, SugRestartRouter, SugCheckCable }
            });
        }
        else if (input.Adapter != null && string.IsNullOrEmpty(input.Adapter.Gateway))
        {
            findings.Add(new DiagnosisFinding
            {
                Severity = DiagnosisSeverity.Problem,
                Key = KeyNoGateway,
                Evidence = new[] { new DiagnosisEvidence(EvNoGateway) },
                Suggestions = new[] { SugDhcpRenew, SugRestartRouter, SugCheckCable }
            });
        }

        // --- Local segment: gateway hop / gateway ping / Wi-Fi ------------------------------------
        var gwHop = input.Trace?.GatewayHop;
        var gwPing = Trusted(input.GatewayPing, t);
        var localEvidence = new List<DiagnosisEvidence>();
        bool localLoss = false, localJitter = false;
        if (gwHop != null)
        {
            if (gwHop.LossPercent > 0)
            {
                localLoss = true;
                localEvidence.Add(new DiagnosisEvidence(EvHopLoss, gwHop.Ttl, R(gwHop.LossPercent), gwHop.Sent));
            }
            if (gwHop.JitterMs is double gj && gj > t.LanJitterMs)
            {
                localJitter = true;
                localEvidence.Add(new DiagnosisEvidence(EvHopJitter, gwHop.Ttl, R(gj)));
            }
        }
        if (gwPing != null)
        {
            if (gwPing.LossPercent >= t.LossPercent)
            {
                localLoss = true;
                localEvidence.Add(new DiagnosisEvidence(EvPingLoss, "gateway", R(gwPing.LossPercent), gwPing.Sent));
            }
            if (gwPing.JitterMs is double pj && pj > t.LanJitterMs)
            {
                localJitter = true;
                localEvidence.Add(new DiagnosisEvidence(EvPingJitter, "gateway", R(pj)));
            }
        }

        bool wifiWeak = input.Wlan != null && input.Wlan.SignalQuality < t.WeakWifiQuality;
        bool localProblem = localLoss || localJitter;
        if (wifiWeak && localProblem)
        {
            var ev = new List<DiagnosisEvidence>
            {
                new(EvWifiSignal, input.Wlan!.SignalQuality, input.Wlan.Dbm)
            };
            ev.AddRange(localEvidence);
            findings.Add(new DiagnosisFinding
            {
                Severity = localLoss ? DiagnosisSeverity.Problem : DiagnosisSeverity.Warning,
                Key = KeyWifiSignal,
                Args = new object[] { input.Wlan.SignalQuality },
                Evidence = ev,
                Suggestions = new[] { SugMoveCloser, SugBand5, SugEthernet }
            });
        }
        else if (wifiWeak)
        {
            findings.Add(new DiagnosisFinding
            {
                Severity = DiagnosisSeverity.Info,
                Key = KeyWifiWeak,
                Args = new object[] { input.Wlan!.SignalQuality },
                Evidence = new[] { new DiagnosisEvidence(EvWifiSignal, input.Wlan.SignalQuality, input.Wlan.Dbm) },
                Suggestions = new[] { SugMoveCloser, SugBand5 }
            });
        }
        else if (localProblem)
        {
            var sug = new List<string>();
            if (isWifi) { sug.Add(SugEthernet); sug.Add(SugBand5); }
            else sug.Add(SugCheckCable);
            sug.Add(SugRestartRouter);
            findings.Add(new DiagnosisFinding
            {
                Severity = localLoss ? DiagnosisSeverity.Problem : DiagnosisSeverity.Warning,
                Key = KeyLan,
                Evidence = localEvidence,
                Suggestions = sug
            });
        }

        // --- ISP segment: hops after the gateway up to and including the first public hop ------------
        bool ispFired = false;
        if (input.Trace != null && !localProblem)
        {
            var hops = input.Trace.Hops;
            int start = input.Trace.GatewayHopIndex is int g ? g + 1 : 0;
            int end = input.Trace.FirstPublicHopIndex ?? -1;
            var ev = new List<DiagnosisEvidence>();
            if (end >= start)
            {
                for (int i = start; i <= end; i++)
                {
                    var h = hops[i];
                    if (h.State != HopState.Answered) continue;
                    if (h.LossPercent > 0)
                        ev.Add(new DiagnosisEvidence(EvHopLoss, h.Ttl, R(h.LossPercent), h.Sent));
                    if (h.JitterMs is double j && j > t.IspJitterMs)
                        ev.Add(new DiagnosisEvidence(EvHopJitter, h.Ttl, R(j)));
                }
            }
            var pub = Trusted(input.PublicPing, t);
            if (ev.Count > 0 && pub != null && pub.LossPercent >= t.LossPercent)
                ev.Add(new DiagnosisEvidence(EvPingLoss, input.PublicPingHost ?? "public", R(pub.LossPercent), pub.Sent));
            if (ev.Count > 0)
            {
                ispFired = true;
                findings.Add(new DiagnosisFinding
                {
                    Severity = ev.Any(e => e.Key is EvHopLoss or EvPingLoss) ? DiagnosisSeverity.Problem : DiagnosisSeverity.Warning,
                    Key = KeyIsp,
                    Evidence = ev,
                    Suggestions = new[] { SugRunLonger, SugRestartRouter, SugContactIsp }
                });
            }
        }

        // --- Public ping series ------------------------------------------------------------------
        var pubPing = Trusted(input.PublicPing, t);
        string pubHost = input.PublicPingHost ?? "public";
        bool pingFine = pubPing != null && pubPing.LossPercent < t.LossPercent
                        && (pubPing.AvgMs ?? double.MaxValue) < t.PingOkAvgMs;
        if (pubPing != null && pubPing.LossPercent >= t.LossPercent && !localProblem && !ispFired)
        {
            findings.Add(new DiagnosisFinding
            {
                Severity = DiagnosisSeverity.Warning,
                Key = KeyLossGeneric,
                Evidence = new[] { new DiagnosisEvidence(EvPingLoss, pubHost, R(pubPing.LossPercent), pubPing.Sent) },
                Suggestions = new[] { SugRunLonger, SugRestartRouter }
            });
        }
        if (pubPing is { P50Ms: double p50 } && p50 > t.HighLatencyP50Ms && pubPing.LossPercent < t.LossPercent)
        {
            findings.Add(new DiagnosisFinding
            {
                Severity = DiagnosisSeverity.Info,
                Key = KeyHighLatency,
                Args = new object[] { R(p50) },
                Evidence = new[] { new DiagnosisEvidence(EvPingP50, pubHost, R(p50), R(pubPing.P95Ms ?? p50)) },
                Suggestions = isWifi ? new[] { SugEthernet, SugRunLonger } : new[] { SugRunLonger }
            });
        }

        // --- DNS ---------------------------------------------------------------------------------
        if (input.Dns is { Count: > 0 } dns)
        {
            var slow = dns.Where(d => d.ErrorCode == ProbeErrorCode.None && d.Ms is double ms && ms > t.DnsSlowMs).ToList();
            var failed = dns.Where(d => d.ErrorCode != ProbeErrorCode.None).ToList();
            bool pingKnown = pubPing != null;
            if (failed.Count > 0 && (pingFine || !pingKnown))
            {
                var ev = failed.Select(d => new DiagnosisEvidence(EvDnsFail, d.Host, NetworkProbeService.Token(d.ErrorCode))).ToList();
                if (pingFine) ev.Add(new DiagnosisEvidence(EvPingLoss, pubHost, R(pubPing!.LossPercent), pubPing.Sent));
                findings.Add(new DiagnosisFinding
                {
                    Severity = pingFine ? DiagnosisSeverity.Problem : DiagnosisSeverity.Warning,
                    Key = KeyDnsFail,
                    Evidence = ev,
                    Suggestions = new[] { SugDnsInApp, SugRestartRouter }
                });
            }
            else if (slow.Count > 0 && (pingFine || !pingKnown))
            {
                var ev = slow.Select(d => new DiagnosisEvidence(EvDnsSlow, d.Host, R(d.Ms!.Value))).ToList();
                if (pingFine) ev.Add(new DiagnosisEvidence(EvPingP50, pubHost, R(pubPing!.P50Ms ?? 0), R(pubPing.P95Ms ?? 0)));
                findings.Add(new DiagnosisFinding
                {
                    Severity = pingFine ? DiagnosisSeverity.Warning : DiagnosisSeverity.Info,
                    Key = KeyDnsSlow,
                    Args = new object[] { R(slow.Max(d => d.Ms!.Value)) },
                    Evidence = ev,
                    Suggestions = new[] { SugDnsInApp }
                });
            }
        }

        // --- Link-layer anomalies ----------------------------------------------------------------
        if (input.Adapter?.Mtu is int mtu && mtu < t.MinMtu)
        {
            findings.Add(new DiagnosisFinding
            {
                Severity = DiagnosisSeverity.Warning,
                Key = KeyMtu,
                Args = new object[] { mtu },
                Evidence = new[] { new DiagnosisEvidence(EvMtu, mtu) },
                Suggestions = new[] { SugMtuCheck }
            });
        }
        int? linkMbps = input.Adapter?.SpeedBps is long bps && bps > 0 ? (int)Math.Min(bps / 1_000_000, int.MaxValue) : null;
        if (isEthernet && input.FullDuplex == false)
        {
            var ev = new List<DiagnosisEvidence> { new(EvDuplex) };
            if (linkMbps is int lm) ev.Add(new DiagnosisEvidence(EvLinkSpeed, lm));
            findings.Add(new DiagnosisFinding
            {
                Severity = DiagnosisSeverity.Warning,
                Key = KeyHalfDuplex,
                Evidence = ev,
                Suggestions = new[] { SugDuplexAuto, SugCheckCable }
            });
        }
        if (isEthernet && linkMbps is int speed && speed <= t.SlowLinkMbps)
        {
            findings.Add(new DiagnosisFinding
            {
                Severity = speed <= 10 ? DiagnosisSeverity.Warning : DiagnosisSeverity.Info,
                Key = KeyLinkSlow,
                Args = new object[] { speed },
                Evidence = new[] { new DiagnosisEvidence(EvLinkSpeed, speed) },
                Suggestions = new[] { SugCheckCable }
            });
        }

        // --- Trace reachability ------------------------------------------------------------------
        if (input.Trace is { ReachedDestination: false } tr && tr.Termination != TracerouteTermination.Cancelled)
        {
            int lastTtl = tr.LastAnsweringTtl ?? (tr.Hops.Count > 0 ? tr.Hops[^1].Ttl : 0);
            if (tr.FirstPublicHopIndex == null)
            {
                findings.Add(new DiagnosisFinding
                {
                    Severity = DiagnosisSeverity.Info,
                    Key = KeyInconclusive,
                    Evidence = new[]
                    {
                        new DiagnosisEvidence(EvNoPublicHop, tr.Hops.Count),
                        new DiagnosisEvidence(EvLastAnswer, lastTtl)
                    },
                    Suggestions = new[] { SugIcmpFiltered }
                });
            }
            else
            {
                findings.Add(new DiagnosisFinding
                {
                    Severity = DiagnosisSeverity.Info,
                    Key = KeyPathDrop,
                    Args = new object[] { lastTtl },
                    Evidence = new[] { new DiagnosisEvidence(EvLastAnswer, lastTtl) },
                    Suggestions = new[] { SugIcmpFiltered }
                });
            }
        }

        // --- Nothing alarming: say so, with the numbers ------------------------------------------
        if (!findings.Any(f => f.Severity >= DiagnosisSeverity.Warning))
        {
            var ev = new List<DiagnosisEvidence>();
            if (pubPing is { Received: > 0 })
            {
                ev.Add(new DiagnosisEvidence(EvPingP50, pubHost, R(pubPing.P50Ms ?? 0), R(pubPing.P95Ms ?? 0)));
                ev.Add(new DiagnosisEvidence(EvPingLoss, pubHost, R(pubPing.LossPercent), pubPing.Sent));
            }
            else if (input.Trace?.Hops.LastOrDefault(h => h.AvgMs != null) is { } last)
            {
                ev.Add(new DiagnosisEvidence(EvHopAvg, last.Ttl, R(last.AvgMs!.Value)));
            }
            if (ev.Count > 0)
            {
                findings.Add(new DiagnosisFinding
                {
                    Severity = DiagnosisSeverity.Info,
                    Key = KeyOk,
                    Evidence = ev
                });
            }
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ToList(); // OrderBy is stable: rule order kept within a severity
    }

    /// <summary>Series only count once they have enough samples; otherwise treated as absent.</summary>
    private static PingSeriesStats? Trusted(PingSeriesStats? s, DiagnosisThresholds t) =>
        s != null && s.Sent >= t.MinPingSamples ? s : null;

    private static double R(double v) => Math.Round(v, 1);
}
