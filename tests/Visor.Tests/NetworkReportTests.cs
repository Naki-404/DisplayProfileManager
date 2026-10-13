using System.Net.NetworkInformation;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class NetworkReportTests
{
    /// <summary>Pass-through localizer: "key(arg1,arg2)" so tests assert on keys, not on Loc tables.</summary>
    private static string Passthrough(string key, object[] args) =>
        args.Length == 0 ? key : key + "(" + string.Join(",", args.Select(a => Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture))) + ")";

    private static NetworkReport Full() => new()
    {
        GeneratedUtc = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc),
        AppVersion = "2.1.0",
        Adapter = new NetworkAdapterInfo
        {
            Name = "Wi-Fi", Description = "Intel(R) Wi-Fi 6 AX201", TypeCode = "wifi", Status = OperationalStatus.Up,
            SpeedBps = 1_200_000_000, IPv4 = "192.168.1.20", Gateway = "192.168.1.1",
            DnsServers = new[] { "192.168.1.1", "1.1.1.1" }, Mtu = 1500, IsDhcpEnabled = true, DhcpServer = "192.168.1.1"
        },
        Throughput = new ThroughputSample(DateTime.UtcNow, 125_000, 12_500),
        Wlan = new WlanConnectionInfo
        {
            InterfaceDescription = "Intel(R) Wi-Fi 6 AX201", Ssid = "SecretHomeNet", BssidOui = "A4:2B:B0", PhyCode = "802.11ax",
            SignalQuality = 72, EstimatedDbm = -64, RxRateMbps = 573.5, TxRateMbps = 573.5, Channel = 36, BandCode = "5"
        },
        PingSeries = new[]
        {
            new PingSeriesReport("1.1.1.1", PingSeriesStats.Compute(new double?[] { 10, 12, null, 11, 13 })),
            new PingSeriesReport("gateway", PingSeriesStats.Compute(new double?[] { 1, 1, 2 }))
        },
        Trace = NetworkDiagnosisTests.Trace(TracerouteTermination.Reached,
            NetworkDiagnosisTests.Hop(1, "192.168.1.1", gateway: true, rtts: new double?[] { 1, 1, 2 }),
            NetworkDiagnosisTests.Hop(2, null, rtts: new double?[] { null, null, null }),
            NetworkDiagnosisTests.Hop(3, "203.0.113.1", firstPublic: true, rtts: new double?[] { 12, 13, 12 }),
            NetworkDiagnosisTests.Hop(4, "1.1.1.1", destination: true, rtts: new double?[] { 14, 14, 15 })),
        Dns = new[]
        {
            new DnsResolveResult { Host = "dns.google", Ms = 23.4, RepeatMs = 0.3, Address = "8.8.8.8" },
            new DnsResolveResult { Host = "nope.invalid", Ms = 2000, ErrorCode = ProbeErrorCode.HostNotFound }
        },
        Findings = new[]
        {
            new DiagnosisFinding
            {
                Severity = DiagnosisSeverity.Warning, Key = DiagnosisEngine.KeyDnsSlow, Args = new object[] { 450.0 },
                Evidence = new[] { new DiagnosisEvidence(DiagnosisEngine.EvDnsSlow, "dns.google", 450.0) },
                Suggestions = new[] { DiagnosisEngine.SugDnsManual }
            }
        }
    };

    [Fact]
    public void Default_report_hides_ssid_and_bssid()
    {
        string md = NetworkReportExporter.Render(Full(), new NetworkReportOptions { Localizer = Passthrough });
        Assert.DoesNotContain("SecretHomeNet", md);
        Assert.DoesNotContain("A4:2B:B0", md);
        Assert.Contains("SSID: " + NetworkReportExporter.RedactedMarker, md);
        Assert.Contains("802.11ax", md);
        Assert.Contains("72 % (≈ -64 dBm, estimate)", md);
        Assert.Contains("36 (5 GHz)", md);
    }

    [Fact]
    public void Opt_in_includes_identifiers()
    {
        string md = NetworkReportExporter.Render(Full(), new NetworkReportOptions { Localizer = Passthrough, IncludeIdentifiers = true });
        Assert.Contains("SSID: SecretHomeNet", md);
        Assert.Contains("BSSID (OUI): A4:2B:B0", md);
    }

    [Fact]
    public void Markdown_contains_all_sections_and_tables()
    {
        string md = NetworkReportExporter.Render(Full(), new NetworkReportOptions { Localizer = Passthrough });
        Assert.StartsWith("# net.report.title", md);
        Assert.Contains("Visor 2.1.0", md);
        Assert.Contains("## net.report.findings", md);
        Assert.Contains("**net.sev.warning** — net.diag.dnsSlow(450)", md);
        Assert.Contains("  - net.ev.dnsSlow(dns.google,450)", md);
        Assert.Contains("    - net.sug.dnsManual", md);
        Assert.Contains("## net.report.adapter", md);
        Assert.Contains("Wi-Fi (Intel(R) Wi-Fi 6 AX201)", md);
        Assert.Contains("1.2 Gbps", md);
        Assert.Contains("DHCP: yes (192.168.1.1)", md);
        Assert.Contains("DNS: 192.168.1.1, 1.1.1.1", md);
        Assert.Contains("1 Mbps ↓ / 0.1 Mbps ↑", md);
        Assert.Contains("## net.report.ping", md);
        Assert.Contains("| 1.1.1.1 | 5 | 1 | 20 | 10 | 11 | 13 | 13 | 1.7 |", md);
        Assert.Contains("## net.report.trace(1.1.1.1,1.1.1.1)", md);
        Assert.Contains("| 1 | 192.168.1.1 |", md);
        Assert.Contains("net.report.markGateway", md);
        Assert.Contains("| 2 | * | — | — | — | — | 100 | net.report.markFiltered |", md);
        Assert.Contains("net.report.markPublic", md);
        Assert.Contains("net.report.markDestination", md);
        Assert.Contains("net.report.termination(net.trace.end.reached)", md);
        Assert.Contains("## DNS", md);
        Assert.Contains("| dns.google | 8.8.8.8 | 23.4 | 0.3 |", md);
        Assert.Contains("| nope.invalid | net.err.hostNotFound | 2000 | — |", md);
        Assert.EndsWith("net.report.footer" + Environment.NewLine, md);
    }

    [Fact]
    public void Plain_text_has_no_markdown_markup()
    {
        string txt = NetworkReportExporter.Render(Full(), new NetworkReportOptions { Localizer = Passthrough, Markdown = false });
        Assert.DoesNotContain("## ", txt);
        Assert.DoesNotContain("**", txt);
        Assert.DoesNotContain("|", txt);
        Assert.Contains("[net.sev.warning] net.diag.dnsSlow(450)", txt);
        Assert.Contains("net.report.title" + Environment.NewLine + "================", txt);
        Assert.Contains("1.1.1.1", txt);
    }

    [Fact]
    public void Empty_report_renders_without_throwing()
    {
        string md = NetworkReportExporter.Render(new NetworkReport(), new NetworkReportOptions { Localizer = Passthrough });
        Assert.Contains("net.report.noFindings", md);
        Assert.Contains("net.report.noAdapter", md);
        Assert.DoesNotContain("## Wi-Fi", md);
        Assert.DoesNotContain("## net.report.ping", md);
        Assert.DoesNotContain("## DNS", md);
    }

    [Fact]
    public void Wlan_unavailable_reason_is_rendered()
    {
        string md = NetworkReportExporter.Render(new NetworkReport { WlanReason = WlanUnavailableReason.NoAdapter },
            new NetworkReportOptions { Localizer = Passthrough });
        Assert.Contains("## Wi-Fi", md);
        Assert.Contains("net.wlan.noAdapter", md);
    }

    [Fact]
    public void Default_localizer_uses_loc_strings()
    {
        // The default localizer goes through Loc: the report shows translated text, never raw keys.
        string md = NetworkReportExporter.Render(new NetworkReport());
        Assert.Contains(Loc.T("net.report.title"), md);
        Assert.Contains(Loc.T("net.report.noFindings"), md);
        Assert.DoesNotContain("net.report.title", md);
    }

    [Fact]
    public void Markdown_pipes_in_values_are_escaped()
    {
        var r = new NetworkReport
        {
            Dns = new[] { new DnsResolveResult { Host = "a|b", Ms = 1, Address = "1.2.3.4" } }
        };
        string md = NetworkReportExporter.Render(r, new NetworkReportOptions { Localizer = Passthrough });
        Assert.Contains("| a\\|b |", md);
    }
}
