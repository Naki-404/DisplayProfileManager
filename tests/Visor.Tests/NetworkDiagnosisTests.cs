using System.Net;
using System.Net.NetworkInformation;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class NetworkDiagnosisTests
{
    // --- builders ------------------------------------------------------------------------------

    internal static TracerouteHop Hop(int ttl, string? addr, bool gateway = false, bool firstPublic = false, bool destination = false, params double?[] rtts)
    {
        var s = NetworkStats.Summarize(rtts);
        bool answered = s.Received > 0;
        var ip = addr == null ? null : IPAddress.Parse(addr);
        return new TracerouteHop
        {
            Ttl = ttl,
            Address = ip,
            Rtts = rtts,
            Received = s.Received,
            LossPercent = s.LossPercent,
            MinMs = s.MinMs, AvgMs = s.AvgMs, MaxMs = s.MaxMs, JitterMs = s.JitterMs,
            State = answered ? HopState.Answered : HopState.Silent,
            IsGateway = gateway,
            IsFirstPublic = firstPublic,
            IsPublic = ip != null && !IPAddressClass.IsPrivate(ip),
            IsDestination = destination
        };
    }

    internal static TracerouteResult Trace(TracerouteTermination term, params TracerouteHop[] hops)
    {
        int? gw = null, pub = null;
        for (int i = 0; i < hops.Length; i++)
        {
            if (gw == null && hops[i].IsGateway) gw = i;
            if (pub == null && hops[i].IsFirstPublic) pub = i;
        }
        int last = -1;
        for (int i = hops.Length - 1; i >= 0; i--) if (hops[i].Received > 0) { last = i; break; }
        // Silent hops followed by an answering one are "filtered", as the service would classify them.
        var classified = hops.Select((h, i) => h.Received == 0 && i < last ? WithState(h, HopState.Filtered) : h).ToArray();
        return new TracerouteResult
        {
            TargetHost = "1.1.1.1",
            Target = IPAddress.Parse("1.1.1.1"),
            Hops = classified,
            Termination = term,
            GatewayHopIndex = gw,
            FirstPublicHopIndex = pub,
            LastAnsweringTtl = last >= 0 && last < hops.Length - 1 ? hops[last].Ttl : (last < 0 ? 0 : (int?)null)
        };
    }

    private static TracerouteHop WithState(TracerouteHop h, HopState state) => new()
    {
        Ttl = h.Ttl, Address = h.Address, Rtts = h.Rtts, Received = h.Received, LossPercent = h.LossPercent,
        MinMs = h.MinMs, AvgMs = h.AvgMs, MaxMs = h.MaxMs, JitterMs = h.JitterMs, State = state,
        IsGateway = h.IsGateway, IsFirstPublic = h.IsFirstPublic, IsPublic = h.IsPublic, IsDestination = h.IsDestination
    };

    internal static TracerouteResult HealthyTrace() => Trace(TracerouteTermination.Reached,
        Hop(1, "192.168.1.1", gateway: true, rtts: new double?[] { 1, 1, 2 }),
        Hop(2, "100.64.0.1", rtts: new double?[] { 5, 6, 5 }),
        Hop(3, "203.0.113.1", firstPublic: true, rtts: new double?[] { 12, 13, 12 }),
        Hop(4, "1.1.1.1", destination: true, rtts: new double?[] { 14, 14, 15 }));

    internal static PingSeriesStats Series(params double?[] rtts) => PingSeriesStats.Compute(rtts);

    internal static PingSeriesStats GoodSeries() => Series(Enumerable.Repeat<double?>(15, 30).ToArray());

    internal static NetworkAdapterInfo Adapter(string type = "ethernet", string? gw = "192.168.1.1", string ip = "192.168.1.20",
        long? speed = 1_000_000_000, int? mtu = 1500) => new()
    {
        Name = "Ethernet", TypeCode = type, Status = OperationalStatus.Up, IPv4 = ip, Gateway = gw, SpeedBps = speed, Mtu = mtu
    };

    internal static WlanConnectionInfo Wlan(int quality) => new()
    {
        InterfaceDescription = "Intel Wi-Fi", PhyCode = "802.11ax", SignalQuality = quality, EstimatedDbm = WlanInfoService.EstimateDbm(quality)
    };

    private static DiagnosisFinding? Find(IReadOnlyList<DiagnosisFinding> f, string key) => f.FirstOrDefault(x => x.Key == key);

    // --- invariants ----------------------------------------------------------------------------

    [Fact]
    public void Every_finding_has_evidence_and_is_sorted_by_severity()
    {
        var all = DiagnosisEngine.Evaluate(new DiagnosisInput
        {
            Adapter = Adapter(speed: 100_000_000, mtu: 1300),
            FullDuplex = false,
            Wlan = Wlan(30),
            Trace = Trace(TracerouteTermination.ConsecutiveTimeouts,
                Hop(1, "192.168.1.1", gateway: true, rtts: new double?[] { 1, null, 30 }),
                Hop(2, null, rtts: new double?[] { null, null, null })),
            PublicPingHost = "1.1.1.1",
            PublicPing = Series(10, null, 12, null, 400, 12, 13, null),
            Dns = new[] { new DnsResolveResult { Host = "dns.google", Ms = 900 } }
        });
        Assert.NotEmpty(all);
        Assert.All(all, f => Assert.NotEmpty(f.Evidence));
        Assert.All(all, f => Assert.StartsWith("net.diag.", f.Key));
        Assert.All(all.SelectMany(f => f.Suggestions), s => Assert.StartsWith("net.sug.", s));
        Assert.All(all.SelectMany(f => f.Evidence), e => Assert.StartsWith("net.ev.", e.Key));
        for (int i = 1; i < all.Count; i++)
            Assert.True(all[i - 1].Severity >= all[i].Severity, "sorted by severity");
        Assert.Null(Find(all, DiagnosisEngine.KeyOk));
    }

    [Fact]
    public void No_data_yields_nothing()
    {
        Assert.Empty(DiagnosisEngine.Evaluate(new DiagnosisInput()));
    }

    [Fact]
    public void Healthy_everything_yields_ok_with_numbers()
    {
        var f = DiagnosisEngine.Evaluate(new DiagnosisInput
        {
            Adapter = Adapter(), FullDuplex = true, Trace = HealthyTrace(), PublicPing = GoodSeries(), PublicPingHost = "1.1.1.1",
            Dns = new[] { new DnsResolveResult { Host = "dns.google", Ms = 20, Address = "8.8.8.8" } }
        });
        var ok = Assert.Single(f);
        Assert.Equal(DiagnosisEngine.KeyOk, ok.Key);
        Assert.Equal(DiagnosisSeverity.Info, ok.Severity);
        Assert.Contains(ok.Evidence, e => e.Key == DiagnosisEngine.EvPingP50 && (double)e.Args[1] == 15);
        Assert.Contains(ok.Evidence, e => e.Key == DiagnosisEngine.EvPingLoss);
    }

    // --- wifi ----------------------------------------------------------------------------------

    [Fact]
    public void WifiSignal_fires_on_weak_signal_plus_hop1_jitter()
    {
        var trace = Trace(TracerouteTermination.Reached,
            Hop(1, "192.168.1.1", gateway: true, rtts: new double?[] { 2, 40, 3 }),
            Hop(2, "1.1.1.1", firstPublic: true, rtts: new double?[] { 20, 21, 20 }));
        var f = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter("wifi"), Wlan = Wlan(35), Trace = trace });
        var w = Find(f, DiagnosisEngine.KeyWifiSignal);
        Assert.NotNull(w);
        Assert.Equal(DiagnosisSeverity.Warning, w!.Severity);
        Assert.Contains(w.Evidence, e => e.Key == DiagnosisEngine.EvWifiSignal && (int)e.Args[0] == 35 && (int)e.Args[1] == -83);
        Assert.Contains(w.Evidence, e => e.Key == DiagnosisEngine.EvHopJitter && (int)e.Args[0] == 1);
        Assert.Contains(DiagnosisEngine.SugEthernet, w.Suggestions);
        Assert.Contains(DiagnosisEngine.SugMoveCloser, w.Suggestions);
        Assert.Null(Find(f, DiagnosisEngine.KeyLan));   // not double-reported
        Assert.Null(Find(f, DiagnosisEngine.KeyWifiWeak));
    }

    [Fact]
    public void WifiSignal_does_not_fire_with_strong_signal_or_without_jitter()
    {
        var jittery = Trace(TracerouteTermination.Reached,
            Hop(1, "192.168.1.1", gateway: true, rtts: new double?[] { 2, 40, 3 }),
            Hop(2, "1.1.1.1", firstPublic: true, rtts: new double?[] { 20, 21, 20 }));
        var strong = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter("wifi"), Wlan = Wlan(90), Trace = jittery });
        Assert.Null(Find(strong, DiagnosisEngine.KeyWifiSignal));
        Assert.NotNull(Find(strong, DiagnosisEngine.KeyLan)); // the jitter is still reported, as LAN

        var weakButCalm = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter("wifi"), Wlan = Wlan(35), Trace = HealthyTrace() });
        Assert.Null(Find(weakButCalm, DiagnosisEngine.KeyWifiSignal));
        var weak = Find(weakButCalm, DiagnosisEngine.KeyWifiWeak);
        Assert.NotNull(weak);
        Assert.Equal(DiagnosisSeverity.Info, weak!.Severity);
    }

    // --- lan -----------------------------------------------------------------------------------

    [Fact]
    public void Lan_fires_on_loss_at_gateway_hop()
    {
        var trace = Trace(TracerouteTermination.Reached,
            Hop(1, "192.168.1.1", gateway: true, rtts: new double?[] { 1, null, 1 }),
            Hop(2, "1.1.1.1", firstPublic: true, rtts: new double?[] { 20, 21, 20 }));
        var f = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter(), Trace = trace });
        var lan = Find(f, DiagnosisEngine.KeyLan);
        Assert.NotNull(lan);
        Assert.Equal(DiagnosisSeverity.Problem, lan!.Severity);
        var ev = Assert.Single(lan.Evidence);
        Assert.Equal(DiagnosisEngine.EvHopLoss, ev.Key);
        Assert.Equal(1, ev.Args[0]);
        Assert.Equal(33.3, (double)ev.Args[1]);
        Assert.Contains(DiagnosisEngine.SugCheckCable, lan.Suggestions);
        Assert.DoesNotContain(DiagnosisEngine.SugEthernet, lan.Suggestions);
        Assert.Null(Find(f, DiagnosisEngine.KeyIsp));
    }

    [Fact]
    public void Lan_fires_on_gateway_ping_series_loss_and_suggests_ethernet_on_wifi()
    {
        var gw = Series(1, 1, null, 1, 1, null, 1, 1, 1, 1); // 20 % loss
        var f = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter("wifi"), GatewayPing = gw });
        var lan = Find(f, DiagnosisEngine.KeyLan);
        Assert.NotNull(lan);
        Assert.Contains(lan!.Evidence, e => e.Key == DiagnosisEngine.EvPingLoss && (double)e.Args[1] == 20);
        Assert.Contains(DiagnosisEngine.SugEthernet, lan.Suggestions);
    }

    [Fact]
    public void Lan_does_not_fire_on_clean_gateway()
    {
        var f = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter(), Trace = HealthyTrace(), GatewayPing = Series(1, 1, 2, 1, 1, 2) });
        Assert.Null(Find(f, DiagnosisEngine.KeyLan));
    }

    [Fact]
    public void Short_ping_series_is_ignored()
    {
        var f = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter(), GatewayPing = Series(1, null, 1) });
        Assert.Null(Find(f, DiagnosisEngine.KeyLan));
    }

    // --- isp -----------------------------------------------------------------------------------

    [Fact]
    public void Isp_fires_when_gateway_clean_and_first_public_hop_lossy()
    {
        var trace = Trace(TracerouteTermination.Reached,
            Hop(1, "192.168.1.1", gateway: true, rtts: new double?[] { 1, 1, 1 }),
            Hop(2, "203.0.113.1", firstPublic: true, rtts: new double?[] { 12, null, 13 }),
            Hop(3, "1.1.1.1", rtts: new double?[] { 15, 15, 16 }));
        var pub = Series(15, 15, null, 15, 15, 15, 15, 15, 15, 15); // 10 %
        var f = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter(), Trace = trace, PublicPing = pub, PublicPingHost = "1.1.1.1" });
        var isp = Find(f, DiagnosisEngine.KeyIsp);
        Assert.NotNull(isp);
        Assert.Equal(DiagnosisSeverity.Problem, isp!.Severity);
        Assert.Contains(isp.Evidence, e => e.Key == DiagnosisEngine.EvHopLoss && (int)e.Args[0] == 2);
        Assert.Contains(isp.Evidence, e => e.Key == DiagnosisEngine.EvPingLoss && (string)e.Args[0] == "1.1.1.1");
        Assert.Contains(DiagnosisEngine.SugContactIsp, isp.Suggestions);
        Assert.Null(Find(f, DiagnosisEngine.KeyLan));
        Assert.Null(Find(f, DiagnosisEngine.KeyLossGeneric));
    }

    [Fact]
    public void Isp_fires_on_jitter_at_isp_hop_as_warning()
    {
        var trace = Trace(TracerouteTermination.Reached,
            Hop(1, "192.168.1.1", gateway: true, rtts: new double?[] { 1, 1, 1 }),
            Hop(2, "100.64.0.1", rtts: new double?[] { 5, 60, 6 }),              // ISP CGNAT hop, jitter ~55
            Hop(3, "203.0.113.1", firstPublic: true, rtts: new double?[] { 12, 12, 13 }));
        var f = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter(), Trace = trace });
        var isp = Find(f, DiagnosisEngine.KeyIsp);
        Assert.NotNull(isp);
        Assert.Equal(DiagnosisSeverity.Warning, isp!.Severity);
        Assert.Contains(isp.Evidence, e => e.Key == DiagnosisEngine.EvHopJitter && (int)e.Args[0] == 2);
    }

    [Fact]
    public void Isp_does_not_fire_when_gateway_is_also_bad_or_path_is_clean()
    {
        var both = Trace(TracerouteTermination.Reached,
            Hop(1, "192.168.1.1", gateway: true, rtts: new double?[] { 1, null, 1 }),
            Hop(2, "203.0.113.1", firstPublic: true, rtts: new double?[] { 12, null, 13 }));
        var f = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter(), Trace = both });
        Assert.NotNull(Find(f, DiagnosisEngine.KeyLan));
        Assert.Null(Find(f, DiagnosisEngine.KeyIsp));

        var clean = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter(), Trace = HealthyTrace() });
        Assert.Null(Find(clean, DiagnosisEngine.KeyIsp));
    }

    // --- dns -----------------------------------------------------------------------------------

    [Fact]
    public void DnsSlow_fires_when_resolve_slow_but_ping_fine()
    {
        var f = DiagnosisEngine.Evaluate(new DiagnosisInput
        {
            PublicPing = GoodSeries(), PublicPingHost = "1.1.1.1",
            Dns = new[]
            {
                new DnsResolveResult { Host = "dns.google", Ms = 450, Address = "8.8.8.8" },
                new DnsResolveResult { Host = "cloudflare-dns.com", Ms = 25, Address = "104.16.1.1" }
            }
        });
        var d = Find(f, DiagnosisEngine.KeyDnsSlow);
        Assert.NotNull(d);
        Assert.Equal(DiagnosisSeverity.Warning, d!.Severity);
        Assert.Equal(450.0, d.Args[0]);
        Assert.Contains(d.Evidence, e => e.Key == DiagnosisEngine.EvDnsSlow && (string)e.Args[0] == "dns.google");
        Assert.DoesNotContain(d.Evidence, e => e.Key == DiagnosisEngine.EvDnsSlow && (string)e.Args[0] == "cloudflare-dns.com");
        Assert.Contains(d.Evidence, e => e.Key == DiagnosisEngine.EvPingP50);
        Assert.Equal(new[] { DiagnosisEngine.SugDnsManual }, d.Suggestions);
    }

    [Fact]
    public void DnsSlow_does_not_fire_when_fast_or_when_ping_is_bad()
    {
        var fast = DiagnosisEngine.Evaluate(new DiagnosisInput
        {
            PublicPing = GoodSeries(), Dns = new[] { new DnsResolveResult { Host = "dns.google", Ms = 120, Address = "8.8.8.8" } }
        });
        Assert.Null(Find(fast, DiagnosisEngine.KeyDnsSlow));

        var badPing = DiagnosisEngine.Evaluate(new DiagnosisInput
        {
            PublicPing = Series(300, null, 300, null, 300, null, 300, 300),
            Dns = new[] { new DnsResolveResult { Host = "dns.google", Ms = 900, Address = "8.8.8.8" } }
        });
        Assert.Null(Find(badPing, DiagnosisEngine.KeyDnsSlow)); // slow DNS is a symptom of the loss, not a finding
        Assert.NotNull(Find(badPing, DiagnosisEngine.KeyLossGeneric));
    }

    [Fact]
    public void DnsSlow_without_ping_data_is_info()
    {
        var f = DiagnosisEngine.Evaluate(new DiagnosisInput
        {
            Dns = new[] { new DnsResolveResult { Host = "dns.google", Ms = 900, Address = "8.8.8.8" } }
        });
        var d = Find(f, DiagnosisEngine.KeyDnsSlow);
        Assert.NotNull(d);
        Assert.Equal(DiagnosisSeverity.Info, d!.Severity);
    }

    [Fact]
    public void DnsFail_with_working_ping_is_problem()
    {
        var f = DiagnosisEngine.Evaluate(new DiagnosisInput
        {
            PublicPing = GoodSeries(),
            Dns = new[] { new DnsResolveResult { Host = "dns.google", Ms = 2000, ErrorCode = ProbeErrorCode.Timeout } }
        });
        var d = Find(f, DiagnosisEngine.KeyDnsFail);
        Assert.NotNull(d);
        Assert.Equal(DiagnosisSeverity.Problem, d!.Severity);
        Assert.Contains(d.Evidence, e => e.Key == DiagnosisEngine.EvDnsFail && (string)e.Args[1] == "timeout");
        Assert.Contains(DiagnosisEngine.SugDnsManual, d.Suggestions);
        Assert.Null(Find(f, DiagnosisEngine.KeyDnsSlow));
    }

    // --- link layer ----------------------------------------------------------------------------

    [Fact]
    public void Mtu_duplex_and_slow_link_rules()
    {
        var f = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter(speed: 100_000_000, mtu: 1280), FullDuplex = false });
        var mtu = Find(f, DiagnosisEngine.KeyMtu);
        Assert.NotNull(mtu);
        Assert.Equal(1280, mtu!.Args[0]);
        Assert.Contains(mtu.Evidence, e => e.Key == DiagnosisEngine.EvMtu && (int)e.Args[0] == 1280);
        var dup = Find(f, DiagnosisEngine.KeyHalfDuplex);
        Assert.NotNull(dup);
        Assert.Contains(dup!.Evidence, e => e.Key == DiagnosisEngine.EvDuplex);
        Assert.Contains(dup.Evidence, e => e.Key == DiagnosisEngine.EvLinkSpeed && (int)e.Args[0] == 100);
        Assert.Contains(DiagnosisEngine.SugDuplexAuto, dup.Suggestions);
        var slow = Find(f, DiagnosisEngine.KeyLinkSlow);
        Assert.NotNull(slow);
        Assert.Equal(DiagnosisSeverity.Info, slow!.Severity);

        var ten = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter(speed: 10_000_000) });
        Assert.Equal(DiagnosisSeverity.Warning, Find(ten, DiagnosisEngine.KeyLinkSlow)!.Severity);
    }

    [Fact]
    public void Link_rules_do_not_fire_on_healthy_gigabit_or_wifi()
    {
        var eth = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter(), FullDuplex = true });
        Assert.Null(Find(eth, DiagnosisEngine.KeyMtu));
        Assert.Null(Find(eth, DiagnosisEngine.KeyHalfDuplex));
        Assert.Null(Find(eth, DiagnosisEngine.KeyLinkSlow));

        // Wi-Fi reports no duplex and a low "speed" is normal — never flagged.
        var wifi = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter("wifi", speed: 72_000_000), FullDuplex = false });
        Assert.Null(Find(wifi, DiagnosisEngine.KeyHalfDuplex));
        Assert.Null(Find(wifi, DiagnosisEngine.KeyLinkSlow));
    }

    // --- adapter blockers ----------------------------------------------------------------------

    [Fact]
    public void Apipa_and_missing_gateway_are_problems()
    {
        var apipa = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter(gw: null, ip: "169.254.7.7") });
        var a = Find(apipa, DiagnosisEngine.KeyApipa);
        Assert.NotNull(a);
        Assert.Equal(DiagnosisSeverity.Problem, a!.Severity);
        Assert.Equal("169.254.7.7", a.Evidence[0].Args[0]);
        Assert.Null(Find(apipa, DiagnosisEngine.KeyNoGateway)); // one root cause, not two

        var nogw = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter(gw: null) });
        Assert.NotNull(Find(nogw, DiagnosisEngine.KeyNoGateway));
        Assert.Null(Find(nogw, DiagnosisEngine.KeyApipa));

        var fine = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter() });
        Assert.Null(Find(fine, DiagnosisEngine.KeyNoGateway));
        Assert.Null(Find(fine, DiagnosisEngine.KeyApipa));
    }

    // --- trace reachability --------------------------------------------------------------------

    [Fact]
    public void Inconclusive_when_no_public_hop_answered()
    {
        var trace = Trace(TracerouteTermination.ConsecutiveTimeouts,
            Hop(1, "192.168.1.1", gateway: true, rtts: new double?[] { 1, 1, 1 }),
            Hop(2, null, rtts: new double?[] { null, null, null }),
            Hop(3, null, rtts: new double?[] { null, null, null }));
        var f = DiagnosisEngine.Evaluate(new DiagnosisInput { Adapter = Adapter(), Trace = trace });
        var inc = Find(f, DiagnosisEngine.KeyInconclusive);
        Assert.NotNull(inc);
        Assert.Equal(DiagnosisSeverity.Info, inc!.Severity);
        Assert.Contains(inc.Evidence, e => e.Key == DiagnosisEngine.EvNoPublicHop && (int)e.Args[0] == 3);
        Assert.Contains(inc.Evidence, e => e.Key == DiagnosisEngine.EvLastAnswer && (int)e.Args[0] == 1);
        Assert.Equal(new[] { DiagnosisEngine.SugIcmpFiltered }, inc.Suggestions);
        Assert.Null(Find(f, DiagnosisEngine.KeyPathDrop));
        Assert.Null(Find(f, DiagnosisEngine.KeyIsp));
    }

    [Fact]
    public void PathDrop_when_public_hops_answered_then_silence()
    {
        var trace = Trace(TracerouteTermination.ConsecutiveTimeouts,
            Hop(1, "192.168.1.1", gateway: true, rtts: new double?[] { 1, 1, 1 }),
            Hop(2, "203.0.113.1", firstPublic: true, rtts: new double?[] { 10, 10, 11 }),
            Hop(3, null, rtts: new double?[] { null, null, null }));
        var f = DiagnosisEngine.Evaluate(new DiagnosisInput { Trace = trace });
        var drop = Find(f, DiagnosisEngine.KeyPathDrop);
        Assert.NotNull(drop);
        Assert.Equal(2, drop!.Args[0]);
        Assert.Null(Find(f, DiagnosisEngine.KeyInconclusive));
    }

    [Fact]
    public void Reached_or_cancelled_trace_gives_no_reachability_finding()
    {
        var reached = DiagnosisEngine.Evaluate(new DiagnosisInput { Trace = HealthyTrace() });
        Assert.Null(Find(reached, DiagnosisEngine.KeyInconclusive));
        Assert.Null(Find(reached, DiagnosisEngine.KeyPathDrop));
        var cancelled = DiagnosisEngine.Evaluate(new DiagnosisInput
        {
            Trace = Trace(TracerouteTermination.Cancelled, Hop(1, "192.168.1.1", gateway: true, rtts: new double?[] { 1 }))
        });
        Assert.Null(Find(cancelled, DiagnosisEngine.KeyInconclusive));
    }

    // --- public ping ---------------------------------------------------------------------------

    [Fact]
    public void HighLatency_and_generic_loss()
    {
        var slow = DiagnosisEngine.Evaluate(new DiagnosisInput { PublicPing = Series(200, 210, 190, 205, 200, 220), PublicPingHost = "1.1.1.1" });
        var hl = Find(slow, DiagnosisEngine.KeyHighLatency);
        Assert.NotNull(hl);
        Assert.Contains(hl!.Evidence, e => e.Key == DiagnosisEngine.EvPingP50 && (string)e.Args[0] == "1.1.1.1");
        Assert.Null(Find(slow, DiagnosisEngine.KeyLossGeneric));

        var lossy = DiagnosisEngine.Evaluate(new DiagnosisInput { PublicPing = Series(20, null, 20, 20, null, 20) });
        var lg = Find(lossy, DiagnosisEngine.KeyLossGeneric);
        Assert.NotNull(lg);
        Assert.Equal(DiagnosisSeverity.Warning, lg!.Severity);
        Assert.Null(Find(lossy, DiagnosisEngine.KeyOk));

        var fine = DiagnosisEngine.Evaluate(new DiagnosisInput { PublicPing = GoodSeries() });
        Assert.Null(Find(fine, DiagnosisEngine.KeyHighLatency));
        Assert.Null(Find(fine, DiagnosisEngine.KeyLossGeneric));
        Assert.NotNull(Find(fine, DiagnosisEngine.KeyOk));
    }

    [Fact]
    public void Thresholds_are_honoured()
    {
        var t = new DiagnosisThresholds { DnsSlowMs = 50 };
        var f = DiagnosisEngine.Evaluate(new DiagnosisInput
        {
            PublicPing = GoodSeries(), Dns = new[] { new DnsResolveResult { Host = "x", Ms = 80, Address = "1.2.3.4" } }
        }, t);
        Assert.NotNull(Find(f, DiagnosisEngine.KeyDnsSlow));
    }
}
