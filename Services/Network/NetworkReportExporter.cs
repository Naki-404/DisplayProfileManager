using System.Globalization;
using System.Text;

namespace Visor.Services;

public sealed record PingSeriesReport(string Host, PingSeriesStats Stats);

/// <summary>Everything the last diagnostics run collected; the UI assembles it and hands it to the exporter.</summary>
public sealed class NetworkReport
{
    public DateTime GeneratedUtc { get; init; } = DateTime.UtcNow;
    public string? AppVersion { get; init; }
    public NetworkAdapterInfo? Adapter { get; init; }
    public bool? FullDuplex { get; init; }
    public ThroughputSample? Throughput { get; init; }
    public WlanConnectionInfo? Wlan { get; init; }
    public WlanUnavailableReason WlanReason { get; init; }
    public IReadOnlyList<PingSeriesReport> PingSeries { get; init; } = Array.Empty<PingSeriesReport>();
    public TracerouteResult? Trace { get; init; }
    public IReadOnlyList<DnsResolveResult>? Dns { get; init; }
    public IReadOnlyList<DiagnosisFinding> Findings { get; init; } = Array.Empty<DiagnosisFinding>();
}

public sealed class NetworkReportOptions
{
    /// <summary>Include SSID and the access-point vendor OUI. Off by default — the report is meant to be shared.</summary>
    public bool IncludeIdentifiers { get; init; }
    /// <summary>Markdown (tables, headings) vs plain text (ASCII columns).</summary>
    public bool Markdown { get; init; } = true;
    /// <summary>Key + args → text. Defaults to <see cref="Loc.Tf"/>; tests inject a pass-through.</summary>
    public Func<string, object[], string>? Localizer { get; init; }
}

/// <summary>
/// Renders the last diagnostics as text. Pure: the UI picks the path and writes the string.
/// Identifiers (SSID, BSSID OUI) are omitted unless <see cref="NetworkReportOptions.IncludeIdentifiers"/>.
/// </summary>
public static class NetworkReportExporter
{
    public const string RedactedMarker = "[hidden]";

    public static string Render(NetworkReport report, NetworkReportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        options ??= new NetworkReportOptions();
        var loc = options.Localizer ?? ((k, a) => Loc.Tf(k, a));
        var sb = new StringBuilder();
        bool md = options.Markdown;
        var inv = CultureInfo.InvariantCulture;

        H1(sb, md, L(loc, "net.report.title"));
        sb.Append(L(loc, "net.report.generated", report.GeneratedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", inv)));
        if (!string.IsNullOrEmpty(report.AppVersion)) sb.Append(" · Visor ").Append(report.AppVersion);
        sb.AppendLine().AppendLine();

        // --- Findings first: that is what the reader wants -------------------------------------
        H2(sb, md, L(loc, "net.report.findings"));
        if (report.Findings.Count == 0)
        {
            sb.AppendLine(L(loc, "net.report.noFindings")).AppendLine();
        }
        else
        {
            foreach (var f in report.Findings)
            {
                string sev = L(loc, "net.sev." + f.Severity.ToString().ToLowerInvariant());
                sb.Append(md ? "- **" : "* [").Append(sev).Append(md ? "** — " : "] ").AppendLine(loc(f.Key, f.Args));
                foreach (var e in f.Evidence)
                    sb.Append("  - ").AppendLine(loc(e.Key, e.Args));
                if (f.Suggestions.Count > 0)
                {
                    sb.Append("  - ").AppendLine(L(loc, "net.report.try"));
                    foreach (var s in f.Suggestions)
                        sb.Append("    - ").AppendLine(loc(s, Array.Empty<object>()));
                }
            }
            sb.AppendLine();
        }

        // --- Adapter -----------------------------------------------------------------------------
        H2(sb, md, L(loc, "net.report.adapter"));
        if (report.Adapter is { } a)
        {
            KV(sb, L(loc, "net.report.name"), a.Name + (string.IsNullOrEmpty(a.Description) ? "" : " (" + a.Description + ")"));
            KV(sb, L(loc, "net.report.type"), a.TypeCode);
            KV(sb, L(loc, "net.report.status"), a.Status.ToString());
            KV(sb, L(loc, "net.report.linkSpeed"), a.SpeedBps is long bps ? FormatMbps(bps / 1_000_000.0, inv) : "—");
            KV(sb, L(loc, "net.report.duplex"), report.FullDuplex switch { true => "full", false => "half", null => "—" });
            KV(sb, L(loc, "net.report.mtu"), a.Mtu?.ToString(inv) ?? "—");
            KV(sb, "IPv4", a.IPv4 ?? "—");
            KV(sb, "IPv6", a.IPv6 ?? "—");
            KV(sb, L(loc, "net.report.gateway"), a.Gateway ?? "—");
            KV(sb, "DNS", a.DnsServers.Count == 0 ? "—" : string.Join(", ", a.DnsServers));
            KV(sb, "DHCP", a.IsDhcpEnabled switch
            {
                true => "yes" + (string.IsNullOrEmpty(a.DhcpServer) ? "" : " (" + a.DhcpServer + ")"),
                false => "no (static)",
                null => "—"
            });
            if (report.Throughput is { } th)
                KV(sb, L(loc, "net.report.throughput"),
                    FormatMbps(th.RxMbps, inv) + " ↓ / " + FormatMbps(th.TxMbps, inv) + " ↑");
        }
        else
        {
            sb.AppendLine(L(loc, "net.report.noAdapter"));
        }
        sb.AppendLine();

        // --- Wi-Fi -------------------------------------------------------------------------------
        if (report.Wlan is { } w)
        {
            H2(sb, md, "Wi-Fi");
            KV(sb, "SSID", options.IncludeIdentifiers ? (w.Ssid ?? "—") : RedactedMarker);
            KV(sb, "BSSID (OUI)", options.IncludeIdentifiers ? (w.BssidOui ?? "—") : RedactedMarker);
            KV(sb, "PHY", w.PhyCode);
            KV(sb, L(loc, "net.report.signal"), w.SignalQuality.ToString(inv) + " % (≈ " + w.Dbm.ToString(inv) + " dBm"
                + (w.RssiDbm != null ? ", driver" : ", estimate") + ")");
            KV(sb, L(loc, "net.report.rate"), FormatMbps(w.RxRateMbps, inv) + " ↓ / " + FormatMbps(w.TxRateMbps, inv) + " ↑");
            KV(sb, L(loc, "net.report.channel"), (w.Channel?.ToString(inv) ?? "—") + " (" + w.BandCode + " GHz)");
            sb.AppendLine();
        }
        else if (report.WlanReason != WlanUnavailableReason.None)
        {
            H2(sb, md, "Wi-Fi");
            sb.AppendLine(L(loc, "net.wlan." + Camel(report.WlanReason.ToString()))).AppendLine();
        }

        // --- Ping series -------------------------------------------------------------------------
        if (report.PingSeries.Count > 0)
        {
            H2(sb, md, L(loc, "net.report.ping"));
            var header = new[] { L(loc, "net.report.host"), "sent", "lost", "loss %", "min", "p50", "p95", "max", "jitter" };
            var rows = report.PingSeries.Select(p => new[]
            {
                p.Host,
                p.Stats.Sent.ToString(inv),
                p.Stats.Lost.ToString(inv),
                N(p.Stats.LossPercent, inv),
                N(p.Stats.MinMs, inv),
                N(p.Stats.P50Ms, inv),
                N(p.Stats.P95Ms, inv),
                N(p.Stats.MaxMs, inv),
                N(p.Stats.JitterMs, inv)
            }).ToList();
            Table(sb, md, header, rows);
            sb.AppendLine();
        }

        // --- Traceroute --------------------------------------------------------------------------
        if (report.Trace is { } tr)
        {
            H2(sb, md, L(loc, "net.report.trace", tr.TargetHost, tr.Target.ToString()));
            var header = new[] { "#", L(loc, "net.report.hop"), "min", "avg", "max", "jitter", "loss %", L(loc, "net.report.mark") };
            var rows = new List<string[]>();
            foreach (var h in tr.Hops)
            {
                var marks = new List<string>();
                if (h.IsGateway) marks.Add(L(loc, "net.report.markGateway"));
                if (h.IsFirstPublic) marks.Add(L(loc, "net.report.markPublic"));
                if (h.IsDestination) marks.Add(L(loc, "net.report.markDestination"));
                if (h.State == HopState.Filtered) marks.Add(L(loc, "net.report.markFiltered"));
                if (h.ErrorStatus is { } es) marks.Add(es.ToString());
                string node = h.Address == null ? "*" : h.Address.ToString();
                if (!string.IsNullOrEmpty(h.HostName)) node = h.HostName + " [" + node + "]";
                rows.Add(new[]
                {
                    h.Ttl.ToString(inv), node, N(h.MinMs, inv), N(h.AvgMs, inv), N(h.MaxMs, inv), N(h.JitterMs, inv),
                    N(h.LossPercent, inv), string.Join(", ", marks)
                });
            }
            Table(sb, md, header, rows);
            sb.AppendLine(L(loc, "net.report.termination", L(loc, "net.trace.end." + Camel(tr.Termination.ToString()))));
            sb.AppendLine();
        }

        // --- DNS ---------------------------------------------------------------------------------
        if (report.Dns is { Count: > 0 } dns)
        {
            H2(sb, md, "DNS");
            var header = new[] { L(loc, "net.report.host"), L(loc, "net.report.result"), "ms", L(loc, "net.report.repeatMs") };
            var rows = dns.Select(d => new[]
            {
                d.Host,
                d.ErrorCode == ProbeErrorCode.None ? (d.Address ?? "ok") : L(loc, NetworkProbeService.ErrorLocKey(d.ErrorCode)),
                N(d.Ms, inv),
                N(d.RepeatMs, inv)
            }).ToList();
            Table(sb, md, header, rows);
            sb.AppendLine();
        }

        sb.AppendLine(L(loc, "net.report.footer"));
        return sb.ToString();
    }

    // --- formatting helpers -----------------------------------------------------------------------

    private static string L(Func<string, object[], string> loc, string key, params object[] args) => loc(key, args);

    private static string Camel(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    private static string N(double? v, CultureInfo c) => v is double d ? d.ToString("0.#", c) : "—";

    private static string FormatMbps(double mbps, CultureInfo c) =>
        mbps >= 1000 ? (mbps / 1000).ToString("0.##", c) + " Gbps" : mbps.ToString("0.##", c) + " Mbps";

    private static void H1(StringBuilder sb, bool md, string text)
    {
        if (md) sb.Append("# ").AppendLine(text);
        else sb.AppendLine(text).AppendLine(new string('=', text.Length));
        if (!md) sb.AppendLine();
    }

    private static void H2(StringBuilder sb, bool md, string text)
    {
        if (md) sb.Append("## ").AppendLine(text);
        else sb.AppendLine(text).AppendLine(new string('-', text.Length));
    }

    private static void KV(StringBuilder sb, string key, string value) =>
        sb.Append("- ").Append(key).Append(": ").AppendLine(value);

    private static void Table(StringBuilder sb, bool md, string[] header, IReadOnlyList<string[]> rows)
    {
        if (md)
        {
            sb.Append("| ").Append(string.Join(" | ", header.Select(Esc))).AppendLine(" |");
            sb.Append("|").Append(string.Join("", header.Select(_ => "---|"))).AppendLine();
            foreach (var r in rows)
                sb.Append("| ").Append(string.Join(" | ", r.Select(Esc))).AppendLine(" |");
            return;
        }
        int cols = header.Length;
        var widths = new int[cols];
        for (int i = 0; i < cols; i++)
        {
            widths[i] = header[i].Length;
            foreach (var r in rows)
                if (i < r.Length) widths[i] = Math.Max(widths[i], r[i].Length);
        }
        sb.AppendLine(string.Join("  ", header.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        foreach (var r in rows)
            sb.AppendLine(string.Join("  ", r.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
    }

    private static string Esc(string s) => s.Replace("|", "\\|");
}
