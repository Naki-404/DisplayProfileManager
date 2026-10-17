using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Visor.Controls;
using Visor.Services;

namespace Visor;

/// <summary>
/// Network section: adapter overview, live throughput sparkline, Wi-Fi association, continuous ping series,
/// traceroute, one-click diagnosis (DiagnosisEngine) with report export, plus the 10-echo probe and DNS timing.
/// Nothing probes on its own: every network action starts from a click; the only timer reads adapter byte
/// counters while the page is live. Everything stops in <see cref="CancelProbes"/> (deactivate / close).
/// </summary>
public partial class NetworkPage : ShellPageBase
{
    private const int SeriesCapacity = 120;
    private const int DiagnosePingSeconds = 20;
    private const int DiagnoseMinLiveSamples = 20;

    private CancellationTokenSource? _netPingCts;
    private CancellationTokenSource? _netDnsCts;
    private CancellationTokenSource? _traceCts;
    private CancellationTokenSource? _diagCts;
    private readonly List<double?> _netPingPartial = new();
    /// <summary>Set when a refresh was skipped because the page was hidden; honored by RefreshIfStale.</summary>
    private bool _stale = true;
    /// <summary>Gateway presence from the last adapter capture (keeps the host combo right without re-capturing).</summary>
    private bool _netHasGateway;

    // Live throughput (1 Hz counter deltas; the timer runs only while the page is live).
    private AdapterThroughputMonitor? _throughput;
    private string? _throughputAdapterId;
    private readonly DispatcherTimer _throughputTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // Continuous ping series (started / stopped by the user; disposed on deactivate).
    private PingMonitor? _pingMonitor;
    private string? _pingMonitorHost;

    // Last collected pieces — what Diagnose evaluates and Export renders.
    private NetworkAdapterInfo? _lastAdapter;
    private WlanQueryResult? _lastWlan;
    private int _wlanGen;
    private bool? _lastDuplex;
    private readonly Dictionary<string, PingSeriesStats> _lastSeries = new(StringComparer.OrdinalIgnoreCase);
    private TracerouteResult? _lastTrace;
    private IReadOnlyList<DnsResolveResult>? _lastDns;
    private IReadOnlyList<DiagnosisFinding> _lastFindings = Array.Empty<DiagnosisFinding>();

    public NetworkPage(IShellHost host) : base(host)
    {
        InitializeComponent();
        _throughputTimer.Tick += ThroughputTimer_Tick;
    }

    public override ShellSection Section => ShellSection.Network;
    public override string TitleKey => "nav.network";

    /// <summary>Host panel of the diagnostics cards (kept for callers that place extra cards there).</summary>
    public System.Windows.Controls.Panel DiagnosticsPanel => DiagnosticsHost;

    private bool WindowAlive => Host.Window.IsLoaded;

    public override void ApplyLocalization()
    {
        LblNetOverview.Text = Loc.T("net.overview");
        BtnNetRefresh.Content = Loc.T("system.refresh");
        TxtNetEmpty.Text = Loc.T("net.empty");
        LblNetThroughput.Text = Loc.T("net.ui.throughput");
        TxtNetThroughputHint.Text = Loc.T("net.ui.throughput.hint");
        NetThroughputChart.Label = Loc.T("net.ui.throughput");
        LblNetWlan.Text = Loc.T("net.ui.wlan");
        TxtNetWlanHint.Text = Loc.T("net.ui.wlan.hint");
        BarNetWlan.Label = Loc.T("net.report.signal");
        LblNetHost.Text = Loc.T("net.host");
        TxtNetHostHint.Text = Loc.T("net.ui.host.hint");
        LblNetSeries.Text = Loc.T("net.ui.series");
        TxtNetSeriesHint.Text = Loc.Tf("net.ui.series.hint", SeriesCapacity);
        BtnNetSeriesStart.Content = Loc.T("net.ping.start");
        BtnNetSeriesStop.Content = Loc.T("net.ping.stop");
        NetPingChart.Label = Loc.T("net.ui.series");
        LblNetTrace.Text = Loc.T("net.ui.trace");
        TxtNetTraceHint.Text = Loc.T("net.ui.trace.hint");
        BtnNetTraceStart.Content = Loc.T("net.ping.start");
        BtnNetTraceStop.Content = Loc.T("net.ping.stop");
        LblNetDiag.Text = Loc.T("net.ui.diagnose");
        TxtNetDiagHint.Text = Loc.Tf("net.ui.diagnose.hint", DiagnosePingSeconds);
        BtnNetDiagnose.Content = Loc.T("net.ui.diagnose.run");
        BtnNetDiagnoseCancel.Content = Loc.T("btn.cancel");
        BtnNetExport.Content = Loc.T("net.ui.export");
        ChkNetExportIds.Content = Loc.T("net.ui.export.ids");
        TxtNetDiagEmpty.Text = Loc.T("net.ui.diagnose.empty");
        LblNetLatency.Text = Loc.T("net.latency");
        TxtNetLatencyHint.Text = Loc.T("net.latency.hint");
        BtnNetPingStart.Content = Loc.T("net.ping.start");
        BtnNetPingStop.Content = Loc.T("net.ping.stop");
        TxtNetJitterNote.Text = Loc.T("net.jitter.note");
        LblNetDns.Text = Loc.T("net.dns");
        TxtNetDnsHint.Text = Loc.T("net.dns.hint");
        BtnNetDns.Content = Loc.T("net.dns.run");
        BtnNetDnsStop.Content = Loc.T("net.ping.stop");
        TxtNetDnsEmpty.Text = Loc.T("net.dns.empty");
        DotNetAdapter.Label = Loc.T("net.overview");
        if (_lastWlan != null) RenderWlan(_lastWlan);
        if (_lastTrace != null) RenderTrace(_lastTrace);
        RenderFindings(_lastFindings, _lastFindings.Count == 0 && _diagCts == null);
        if (IsLive)
            RefreshNetworkPage();
        else
        {
            RebuildNetHosts(_netHasGateway);
            _stale = true;
        }
    }

    public override void OnActivated() => RefreshNetworkPage();

    public override void OnDeactivated() => CancelProbes();

    public override void RefreshIfStale()
    {
        if (_stale) RefreshNetworkPage();
    }

    /// <summary>Stops every probe, the ping series and the throughput timer (page left, window hidden, exit).</summary>
    public void CancelProbes()
    {
        try { _netPingCts?.Cancel(); } catch { /* ignore */ }
        try { _netDnsCts?.Cancel(); } catch { /* ignore */ }
        try { _traceCts?.Cancel(); } catch { /* ignore */ }
        try { _diagCts?.Cancel(); } catch { /* ignore */ }
        StopThroughput();
        StopPingSeries();
    }

    private void RebuildNetHosts(bool hasGateway)
    {
        if (CmbNetHost == null) return;
        string? prev = (CmbNetHost.SelectedItem as ComboBoxItem)?.Tag as string
                       ?? CmbNetHost.Text?.Trim();
        CmbNetHost.Items.Clear();
        foreach (var h in NetworkProbeService.DefaultHosts)
        {
            bool isGw = string.Equals(h, "gateway", StringComparison.OrdinalIgnoreCase);
            string label = isGw ? Loc.T("net.host.gateway") : h;
            CmbNetHost.Items.Add(new ComboBoxItem
            {
                Content = label,
                Tag = h,
                IsEnabled = !isGw || hasGateway
            });
        }
        int idx = 0;
        if (!string.IsNullOrEmpty(prev))
        {
            for (int i = 0; i < CmbNetHost.Items.Count; i++)
            {
                if (CmbNetHost.Items[i] is ComboBoxItem c
                    && string.Equals(c.Tag as string, prev, StringComparison.OrdinalIgnoreCase)
                    && c.IsEnabled)
                {
                    idx = i;
                    break;
                }
            }
        }
        CmbNetHost.SelectedIndex = idx;
    }

    private string SelectedHost() =>
        ((CmbNetHost.SelectedItem as ComboBoxItem)?.Tag as string ?? CmbNetHost.Text?.Trim() ?? "").Trim();

    /// <summary>One adapter capture per refresh feeds the host combo, the overview card and the throughput monitor.</summary>
    private void RefreshNetworkPage(bool forceRefresh = false)
    {
        if (TxtNetAdapter == null) return;
        if (!IsLive)
        {
            _stale = true;
            return;
        }
        _stale = false;
        var info = NetworkInfoService.CapturePrimary(forceRefresh);
        _lastAdapter = info;
        _netHasGateway = !string.IsNullOrEmpty(info?.Gateway);
        RebuildNetHosts(_netHasGateway);
        StartThroughput(info);
        _ = RefreshWlanAsync();
        if (info == null)
        {
            TxtNetAdapter.Text = "";
            TxtNetDetails.Text = "";
            TxtNetDns.Text = "";
            TxtNetEmpty.Visibility = Visibility.Visible;
            DotNetAdapter.Status = StatusKind.Bad;
            return;
        }
        TxtNetEmpty.Visibility = Visibility.Collapsed;
        DotNetAdapter.Status = info.Status == System.Net.NetworkInformation.OperationalStatus.Up
            ? (_netHasGateway ? StatusKind.Ok : StatusKind.Warn)
            : StatusKind.Bad;
        TxtNetAdapter.Text = Loc.Tf("net.adapter.line", info.Name, LocalizeNetType(info.TypeCode));
        var bits = new List<string>();
        if (!string.IsNullOrEmpty(info.IPv4))
            bits.Add(Loc.Tf("net.ip", info.IPv4));
        if (!string.IsNullOrEmpty(info.IPv6))
            bits.Add(Loc.Tf("net.ip6", info.IPv6));
        if (!string.IsNullOrEmpty(info.Gateway))
            bits.Add(Loc.Tf("net.gateway", info.Gateway));
        var speedParts = NetworkInfoService.LinkSpeedParts(info.SpeedBps);
        if (speedParts is { } sp)
        {
            bits.Add(sp.Gigabits
                ? Loc.Tf("net.speed.gbps", sp.Value.ToString("0.#"))
                : Loc.Tf("net.speed.mbps", sp.Value.ToString("0.#")));
        }
        if (info.Mtu is int mtu)
            bits.Add(Loc.Tf("net.ev.mtu", mtu));
        bits.Add(LocalizeNetStatus(info.Status));
        TxtNetDetails.Text = string.Join(" · ", bits);
        TxtNetDns.Text = info.DnsServers.Count == 0
            ? Loc.T("net.dns.none")
            : Loc.Tf("net.dns.list", string.Join(", ", info.DnsServers));
    }

    public static string LocalizeNetType(string code) => code switch
    {
        "ethernet" => Loc.T("net.type.ethernet"),
        "wifi" => Loc.T("net.type.wifi"),
        _ => Loc.T("net.type.other")
    };

    private static string LocalizeNetStatus(System.Net.NetworkInformation.OperationalStatus s) => s switch
    {
        System.Net.NetworkInformation.OperationalStatus.Up => Loc.T("net.status.up"),
        System.Net.NetworkInformation.OperationalStatus.Down => Loc.T("net.status.down"),
        _ => Loc.Tf("net.status.other", s.ToString())
    };

    /// <summary>The explicit button bypasses the adapter cache; page selection uses the cached capture.</summary>
    private void NetRefresh_Click(object sender, RoutedEventArgs e) => RefreshNetworkPage(forceRefresh: true);

    private static string Mbps(double v) => Loc.Tf("net.speed.mbps", v.ToString("0.##", CultureInfo.CurrentCulture));

    private static string Ms(double? v) => v is double d ? d.ToString("0.#", CultureInfo.CurrentCulture) : "—";

    // -- Live throughput ---------------------------------------------------------------------------------

    private void StartThroughput(NetworkAdapterInfo? info)
    {
        string? id = info?.Id;
        if (string.IsNullOrEmpty(id))
        {
            StopThroughput();
            _throughput = null;
            _throughputAdapterId = null;
            NetThroughputChart.Series = null;
            NetThroughputChart.SecondarySeries = null;
            TxtNetThroughputRates.Text = "";
            return;
        }
        if (_throughput == null || !string.Equals(_throughputAdapterId, id, StringComparison.OrdinalIgnoreCase))
        {
            _throughput = AdapterThroughputMonitor.ForAdapter(id, SeriesCapacity);
            _throughputAdapterId = id;
            NetThroughputChart.Series = null;
            NetThroughputChart.SecondarySeries = null;
            TxtNetThroughputRates.Text = Loc.T("net.ui.throughput.idle");
        }
        if (!_throughputTimer.IsEnabled)
        {
            // Prime the counters now so the first visible sample arrives on the next tick.
            _throughput.Tick();
            _throughputTimer.Start();
        }
    }

    private void StopThroughput()
    {
        _throughputTimer.Stop();
        _throughput?.Reset();
    }

    private void ThroughputTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsLive || _throughput == null)
        {
            StopThroughput();
            return;
        }
        var sample = _throughput.Tick();
        if (sample == null) return;
        var snap = _throughput.Snapshot();
        NetThroughputChart.Series = snap.Select(s => (double?)s.RxMbps).ToList();
        NetThroughputChart.SecondarySeries = snap.Select(s => (double?)s.TxMbps).ToList();
        TxtNetThroughputRates.Text = Loc.Tf("net.ui.throughput.rates", Mbps(sample.RxMbps), Mbps(sample.TxMbps));
    }

    // -- Wi-Fi -------------------------------------------------------------------------------------------

    private async Task RefreshWlanAsync()
    {
        int gen = ++_wlanGen;
        WlanQueryResult result;
        try
        {
            result = await Task.Run(WlanInfoService.Query).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "WLAN refresh");
            result = WlanQueryResult.Unavailable(WlanUnavailableReason.Error);
        }
        if (gen != _wlanGen || !WindowAlive) return;
        _lastWlan = result;
        RenderWlan(result);
    }

    /// <summary>SSID is rendered on-page only — never logged, never exported unless the user opts in.</summary>
    private void RenderWlan(WlanQueryResult r)
    {
        var w = r.Connection;
        if (w == null)
        {
            TxtNetWlanSsid.Text = "";
            TxtNetWlanLink.Text = "";
            WlanSignalRow.Visibility = Visibility.Collapsed;
            TxtNetWlanLink.Visibility = Visibility.Collapsed;
            TxtNetWlanUnavailable.Visibility = Visibility.Visible;
            TxtNetWlanUnavailable.Text = r.Reason == WlanUnavailableReason.None
                ? Loc.T("net.wlan.notConnected")
                : Loc.T("net.wlan." + Camel(r.Reason.ToString()));
            return;
        }
        TxtNetWlanUnavailable.Visibility = Visibility.Collapsed;
        WlanSignalRow.Visibility = Visibility.Visible;
        TxtNetWlanLink.Visibility = Visibility.Visible;
        TxtNetWlanSsid.Text = string.IsNullOrEmpty(w.Ssid)
            ? Loc.T("net.ui.wlan.ssidHidden")
            : Loc.Tf("net.ui.wlan.ssid", w.Ssid);
        BarNetWlan.Value = w.SignalQuality;
        BarNetWlan.SetResourceReference(ThinBar.FillProperty,
            w.SignalQuality >= 50 ? "SuccessBrush" : w.SignalQuality >= 30 ? "WarningBrush" : "DangerBrush");
        TxtNetWlanSignal.Text = Loc.Tf("net.ui.wlan.signal", w.SignalQuality, w.Dbm,
            Loc.T(w.RssiDbm != null ? "net.ui.wlan.driver" : "net.ui.wlan.estimate"));
        var bits = new List<string>
        {
            w.InterfaceDescription,
            w.PhyCode,
            Loc.Tf("net.ui.wlan.rates", w.RxRateMbps.ToString("0.#", CultureInfo.CurrentCulture), w.TxRateMbps.ToString("0.#", CultureInfo.CurrentCulture)),
            w.Channel is int ch
                ? Loc.Tf("net.ui.wlan.channel", ch, w.BandCode == "unknown" ? "?" : w.BandCode)
                : Loc.T("net.ui.wlan.channel.unknown")
        };
        if (!string.IsNullOrEmpty(w.BssidOui))
            bits.Add(Loc.Tf("net.ui.wlan.oui", w.BssidOui));
        TxtNetWlanLink.Text = string.Join(" · ", bits);
    }

    private static string Camel(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    // -- Host resolution ---------------------------------------------------------------------------------

    /// <summary>"gateway" → the primary gateway; literal IPs as-is; names through the system resolver (IPv4 preferred).</summary>
    private static async Task<IPAddress> ResolveHostAsync(string host, CancellationToken ct)
    {
        if (string.Equals(host, "gateway", StringComparison.OrdinalIgnoreCase))
        {
            string? gw = NetworkInfoService.CapturePrimary()?.Gateway;
            if (string.IsNullOrEmpty(gw) || !IPAddress.TryParse(gw, out var gwIp))
                throw new InvalidOperationException(NetworkProbeService.NoGatewayError);
            return gwIp;
        }
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var addrs = await Dns.GetHostAddressesAsync(host, ct).WaitAsync(NetworkProbeService.DnsTimeout, ct).ConfigureAwait(false);
        return addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addrs.FirstOrDefault()
               ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    private string ErrorText(Exception ex) =>
        string.Equals(ex.Message, NetworkProbeService.NoGatewayError, StringComparison.Ordinal)
            ? Loc.T("net.gateway.missing")
            : Loc.T(NetworkProbeService.ErrorLocKey(NetworkProbeService.MapException(ex)));

    // -- Ping series (continuous) ------------------------------------------------------------------------

    private async void NetSeriesStart_Click(object sender, RoutedEventArgs e)
    {
        if (_pingMonitor != null) return;
        string host = SelectedHost();
        if (string.IsNullOrWhiteSpace(host))
        {
            Host.ShowToast(Loc.T("net.host.missing"));
            return;
        }
        BtnNetSeriesStart.IsEnabled = false;
        IPAddress ip;
        try
        {
            ip = await ResolveHostAsync(host, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (!WindowAlive) return;
            TxtNetSeriesStatus.Text = Loc.Tf("net.ui.series.resolveFail", host, ErrorText(ex));
            BtnNetSeriesStart.IsEnabled = true;
            return;
        }
        if (!WindowAlive || !IsLive || _pingMonitor != null)
        {
            BtnNetSeriesStart.IsEnabled = true;
            return;
        }
        var mon = PingMonitor.Create(ip, host, SeriesCapacity);
        mon.SampleAdded += _ => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ReferenceEquals(_pingMonitor, mon)) UpdateSeriesUi(mon);
        }));
        _pingMonitor = mon;
        _pingMonitorHost = host;
        NetPingChart.Series = null;
        TxtNetSeriesStats.Text = Loc.T("net.ui.series.stats.nodata");
        TxtNetSeriesStatus.Text = Loc.Tf("net.ui.series.running", host + " (" + ip + ")");
        BtnNetSeriesStop.IsEnabled = true;
        mon.Start();
    }

    private void NetSeriesStop_Click(object sender, RoutedEventArgs e) => StopPingSeries();

    private void StopPingSeries()
    {
        var mon = _pingMonitor;
        if (mon == null) return;
        _pingMonitor = null;
        try
        {
            var stats = mon.Stats;
            if (_pingMonitorHost != null && stats.Sent > 0)
                _lastSeries[_pingMonitorHost] = stats;
            _ = mon.Stop();
            mon.Dispose();
            if (BtnNetSeriesStart != null)
            {
                TxtNetSeriesStatus.Text = stats.Sent == 0
                    ? Loc.T("net.ping.stopped")
                    : Loc.Tf("net.ui.series.stopped", _pingMonitorHost ?? mon.TargetLabel);
                TxtNetSeriesStats.Text = FormatSeriesStats(stats);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Ping series stop");
        }
        finally
        {
            if (BtnNetSeriesStart != null)
            {
                BtnNetSeriesStart.IsEnabled = true;
                BtnNetSeriesStop.IsEnabled = false;
            }
        }
    }

    private void UpdateSeriesUi(PingMonitor mon)
    {
        var window = mon.RttWindow();
        NetPingChart.Series = window;
        var stats = PingSeriesStats.Compute(window);
        TxtNetSeriesStats.Text = FormatSeriesStats(stats);
        string last = window.Count > 0 && window[^1] is double v
            ? Loc.Tf("net.ui.series.last", Ms(v))
            : Loc.T("net.ui.series.lost");
        TxtNetSeriesStatus.Text = Loc.Tf("net.ui.series.running", mon.TargetLabel + " (" + mon.Target + ")") + " · " + last;
    }

    private static string FormatSeriesStats(PingSeriesStats s)
    {
        if (s.Sent == 0) return Loc.T("net.ping.none");
        if (s.Received == 0) return Loc.Tf("net.ping.summary.lost", s.Sent, s.LossPercent.ToString("0.#", CultureInfo.CurrentCulture));
        return Loc.Tf("net.ui.series.stats", s.Sent, s.LossPercent.ToString("0.#", CultureInfo.CurrentCulture),
            Ms(s.P50Ms), Ms(s.P95Ms), Ms(s.MaxMs), Ms(s.JitterMs));
    }

    /// <summary>Fixed-length series to one address (Diagnose). Disposed on cancel; stats are what the ring holds.</summary>
    private static async Task<PingSeriesStats> RunSeriesAsync(IPAddress ip, string label, int seconds, CancellationToken ct, Action<int>? secondsLeft)
    {
        using var mon = PingMonitor.Create(ip, label, seconds + 5);
        mon.Start();
        for (int s = seconds; s > 0; s--)
        {
            secondsLeft?.Invoke(s);
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        await mon.Stop().ConfigureAwait(false);
        return mon.Stats;
    }

    // -- Traceroute --------------------------------------------------------------------------------------

    private async void NetTraceStart_Click(object sender, RoutedEventArgs e)
    {
        if (_traceCts != null || _diagCts != null) return;
        string host = SelectedHost();
        if (string.IsNullOrWhiteSpace(host))
        {
            Host.ShowToast(Loc.T("net.host.missing"));
            return;
        }
        _traceCts = new CancellationTokenSource();
        BtnNetTraceStart.IsEnabled = false;
        BtnNetTraceStop.IsEnabled = true;
        try
        {
            await RunTraceAsync(host, _traceCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            if (WindowAlive) TxtNetTraceStatus.Text = Loc.T("net.trace.end.cancelled");
        }
        catch (Exception ex)
        {
            if (WindowAlive) TxtNetTraceStatus.Text = Loc.Tf("net.ping.error", ErrorText(ex));
        }
        finally
        {
            _traceCts?.Dispose();
            _traceCts = null;
            if (WindowAlive)
            {
                BtnNetTraceStart.IsEnabled = true;
                BtnNetTraceStop.IsEnabled = false;
            }
        }
    }

    private void NetTraceStop_Click(object sender, RoutedEventArgs e) => _traceCts?.Cancel();

    /// <summary>Runs the trace with live hop rows; the final result (with marks) replaces them.</summary>
    private async Task<TracerouteResult> RunTraceAsync(string host, CancellationToken ct)
    {
        NetTraceRows.Children.Clear();
        NetTraceRows.Children.Add(BuildTraceHeader());
        TxtNetTraceStatus.Text = Loc.Tf("net.ui.trace.running", host, 1);
        var progress = new Progress<TracerouteHop>(h =>
        {
            NetTraceRows.Children.Add(BuildTraceRow(h));
            TxtNetTraceStatus.Text = Loc.Tf("net.ui.trace.running", host, h.Ttl + 1);
        });
        var result = await TracerouteService.RunAsync(host, new TracerouteOptions(), ct, progress).ConfigureAwait(true);
        if (!WindowAlive) return result;
        _lastTrace = result;
        RenderTrace(result);
        return result;
    }

    private void RenderTrace(TracerouteResult result)
    {
        NetTraceRows.Children.Clear();
        NetTraceRows.Children.Add(BuildTraceHeader());
        foreach (var h in result.Hops)
            NetTraceRows.Children.Add(BuildTraceRow(h));
        string end = Loc.T("net.trace.end." + Camel(result.Termination.ToString()));
        TxtNetTraceStatus.Text = Loc.Tf("net.ui.trace.done", result.TargetHost + " (" + result.Target + ")", end);
    }

    private static readonly double[] TraceColumnWidths = { 26, 0, 46, 46, 46, 50, 52, 0 };

    private Grid NewTraceGrid()
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        foreach (var w in TraceColumnWidths)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions[7].Width = GridLength.Auto;
        return g;
    }

    private FrameworkElement BuildTraceHeader()
    {
        var g = NewTraceGrid();
        var cells = new[] { "#", Loc.T("net.report.hop"), "min", "avg", "max", "jitter", "loss %", Loc.T("net.report.mark") };
        for (int i = 0; i < cells.Length; i++)
        {
            var tb = new TextBlock { Text = cells[i], FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Grid.SetColumn(tb, i);
            g.Children.Add(tb);
        }
        return g;
    }

    private FrameworkElement BuildTraceRow(TracerouteHop h)
    {
        var g = NewTraceGrid();
        string node = h.Address == null ? "*" : h.Address.ToString();
        if (!string.IsNullOrEmpty(h.HostName)) node = h.HostName + " [" + node + "]";
        var marks = new List<string>();
        if (h.IsGateway) marks.Add(Loc.T("net.report.markGateway"));
        if (h.IsFirstPublic) marks.Add(Loc.T("net.report.markPublic"));
        if (h.IsDestination) marks.Add(Loc.T("net.report.markDestination"));
        if (h.State == HopState.Filtered) marks.Add(Loc.T("net.report.markFiltered"));
        if (h.ErrorStatus is { } es) marks.Add(Loc.T(NetworkProbeService.ErrorLocKey(NetworkProbeService.MapStatus(es))));
        var cells = new[]
        {
            h.Ttl.ToString(CultureInfo.CurrentCulture), node, Ms(h.MinMs), Ms(h.AvgMs), Ms(h.MaxMs), Ms(h.JitterMs),
            h.Received == 0 && h.Sent == 0 ? "—" : h.LossPercent.ToString("0", CultureInfo.CurrentCulture),
            string.Join(", ", marks)
        };
        bool muted = h.State != HopState.Answered;
        for (int i = 0; i < cells.Length; i++)
        {
            var tb = new TextBlock
            {
                Text = cells[i],
                FontSize = 11,
                Margin = new Thickness(0, 0, 6, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (i == 1) tb.ToolTip = node;
            tb.SetResourceReference(TextBlock.ForegroundProperty,
                muted ? "MutedBrush" : i == 7 && (h.IsGateway || h.IsFirstPublic || h.IsDestination) ? "AccentBrush" : "TextBrush");
            if (i == 6 && h.LossPercent > 0 && !muted)
                tb.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            Grid.SetColumn(tb, i);
            g.Children.Add(tb);
        }
        return g;
    }

    // -- Diagnose ----------------------------------------------------------------------------------------

    private async void NetDiagnose_Click(object sender, RoutedEventArgs e)
    {
        if (_diagCts != null || _traceCts != null) return;
        string host = SelectedHost();
        if (string.IsNullOrWhiteSpace(host))
        {
            Host.ShowToast(Loc.T("net.host.missing"));
            return;
        }
        // The engine wants a public host for latency / loss; the gateway gets its own series.
        string publicHost = string.Equals(host, "gateway", StringComparison.OrdinalIgnoreCase) ? NetworkProbeService.DefaultHosts[0] : host;

        _diagCts = new CancellationTokenSource();
        var ct = _diagCts.Token;
        SetDiagnoseBusy(true);
        NetFindings.Children.Clear();
        TxtNetDiagEmpty.Visibility = Visibility.Collapsed;
        try
        {
            TxtNetDiagStatus.Text = Loc.T("net.ui.diagnose.step.adapter");
            var adapter = NetworkInfoService.CapturePrimary(forceRefresh: true);
            _lastAdapter = adapter;
            var wlanTask = Task.Run(WlanInfoService.Query, ct);
            int? ifIndex = adapter?.InterfaceIndex;
            var duplexTask = Task.Run(() => AdapterDetailsService.QueryFullDuplex(ifIndex), ct);

            // Ping series: reuse the live one when it already has enough samples to the same public host.
            PingSeriesStats? pub = null;
            if (_pingMonitor is { } live && live.Count >= DiagnoseMinLiveSamples
                && string.Equals(_pingMonitorHost, publicHost, StringComparison.OrdinalIgnoreCase))
                pub = live.Stats;

            IPAddress pubIp = await ResolveHostAsync(publicHost, ct).ConfigureAwait(true);
            IPAddress? gwIp = !string.IsNullOrEmpty(adapter?.Gateway) && IPAddress.TryParse(adapter.Gateway, out var g) ? g : null;
            var pubTask = pub != null
                ? Task.FromResult(pub)
                : RunSeriesAsync(pubIp, publicHost, DiagnosePingSeconds, ct,
                    left => Dispatcher.BeginInvoke(new Action(() => TxtNetDiagStatus.Text = Loc.Tf("net.ui.diagnose.step.ping", left))));
            var gwTask = gwIp == null
                ? Task.FromResult<PingSeriesStats?>(null)
                : RunSeriesAsync(gwIp, "gateway", DiagnosePingSeconds, ct, null).ContinueWith(t => (PingSeriesStats?)t.Result, ct,
                    TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            if (pub == null) TxtNetDiagStatus.Text = Loc.Tf("net.ui.diagnose.step.ping", DiagnosePingSeconds);
            pub = await pubTask.ConfigureAwait(true);
            var gw = await gwTask.ConfigureAwait(true);
            if (!WindowAlive) return;
            _lastSeries[publicHost] = pub;
            if (gw != null) _lastSeries["gateway"] = gw;

            TxtNetDiagStatus.Text = Loc.T("net.ui.diagnose.step.trace");
            BtnNetTraceStart.IsEnabled = false;
            var trace = await RunTraceAsync(publicHost, ct).ConfigureAwait(true);
            if (!WindowAlive) return;

            TxtNetDiagStatus.Text = Loc.T("net.ui.diagnose.step.dns");
            NetDnsResults.Children.Clear();
            TxtNetDnsEmpty.Visibility = Visibility.Collapsed;
            var dnsProgress = new Progress<DnsResolveResult>(AppendDnsRow);
            var dns = await NetworkProbeService.ResolveDnsAsync(NetworkProbeService.DefaultDnsHosts, ct, dnsProgress).ConfigureAwait(true);
            if (!WindowAlive) return;
            _lastDns = dns;

            var wlan = await wlanTask.ConfigureAwait(true);
            var duplex = await duplexTask.ConfigureAwait(true);
            if (!WindowAlive) return;
            _lastWlan = wlan;
            _lastDuplex = duplex;
            RenderWlan(wlan);

            var findings = DiagnosisEngine.Evaluate(new DiagnosisInput
            {
                Adapter = adapter,
                FullDuplex = duplex,
                Wlan = wlan.Connection,
                Trace = trace,
                PublicPingHost = publicHost,
                PublicPing = pub,
                GatewayPing = gw,
                Dns = dns
            });
            _lastFindings = findings;
            RenderFindings(findings, showEmptyHint: false);
            TxtNetDiagStatus.Text = Loc.Tf("net.ui.diagnose.done", findings.Count);
        }
        catch (OperationCanceledException)
        {
            if (WindowAlive) TxtNetDiagStatus.Text = Loc.T("net.ui.diagnose.stopped");
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Network diagnose");
            if (WindowAlive) TxtNetDiagStatus.Text = Loc.Tf("net.ping.error", ErrorText(ex));
        }
        finally
        {
            _diagCts?.Dispose();
            _diagCts = null;
            if (WindowAlive)
            {
                SetDiagnoseBusy(false);
                BtnNetTraceStart.IsEnabled = _traceCts == null;
                if (NetFindings.Children.Count == 0) TxtNetDiagEmpty.Visibility = Visibility.Visible;
            }
        }
    }

    private void NetDiagnoseCancel_Click(object sender, RoutedEventArgs e) => _diagCts?.Cancel();

    private void SetDiagnoseBusy(bool busy)
    {
        BtnNetDiagnose.IsEnabled = !busy;
        BtnNetDiagnoseCancel.IsEnabled = busy;
        BtnNetExport.IsEnabled = !busy;
        BtnNetTraceStart.IsEnabled = !busy;
        BtnNetTraceStop.IsEnabled = false;
    }

    private void RenderFindings(IReadOnlyList<DiagnosisFinding> findings, bool showEmptyHint)
    {
        NetFindings.Children.Clear();
        foreach (var f in findings)
            NetFindings.Children.Add(BuildFindingRow(f));
        TxtNetDiagEmpty.Visibility = findings.Count == 0 && showEmptyHint ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement BuildFindingRow(DiagnosisFinding f)
    {
        var card = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(8)
        };
        card.SetResourceReference(Border.BackgroundProperty, "FieldBrush");
        var col = new StackPanel();

        string brushKey = f.Severity switch
        {
            DiagnosisSeverity.Problem => "DangerBrush",
            DiagnosisSeverity.Warning => "WarningBrush",
            _ => "MutedBrush"
        };
        // DockPanel (badge left, title fills): a horizontal StackPanel gave the title infinite width, so it never
        // wrapped and long findings were clipped at the card edge.
        var titleRow = new DockPanel();
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(1)
        };
        badge.SetResourceReference(Border.BorderBrushProperty, f.Severity == DiagnosisSeverity.Info ? "BorderBrush" : brushKey);
        badge.SetResourceReference(Border.BackgroundProperty, "PillBgBrush");
        var badgeText = new TextBlock { Text = Loc.T("net.sev." + Camel(f.Severity.ToString())), FontSize = 10, FontWeight = FontWeights.SemiBold };
        badgeText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        badge.Child = badgeText;
        titleRow.Children.Add(badge);
        var title = new TextBlock { Text = Loc.Tf(f.Key, f.Args), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        titleRow.Children.Add(title);
        col.Children.Add(titleRow);

        foreach (var ev in f.Evidence)
            col.Children.Add(Hint("• " + Loc.Tf(ev.Key, ev.Args), new Thickness(8, 4, 0, 0)));
        if (f.Suggestions.Count > 0)
        {
            col.Children.Add(Hint(Loc.T("net.report.try"), new Thickness(0, 6, 0, 0)));
            foreach (var s in f.Suggestions)
                col.Children.Add(Hint("– " + Loc.T(s), new Thickness(8, 2, 0, 0)));
        }
        card.Child = col;
        return card;
    }

    private TextBlock Hint(string text, Thickness margin)
    {
        var tb = new TextBlock { Text = text, Margin = margin, TextWrapping = TextWrapping.Wrap };
        tb.SetResourceReference(StyleProperty, "Hint");
        return tb;
    }

    // -- Export ------------------------------------------------------------------------------------------

    private void NetExport_Click(object sender, RoutedEventArgs e)
    {
        bool anything = _lastSeries.Count > 0 || _lastTrace != null || _lastDns != null || _lastFindings.Count > 0;
        if (!anything)
        {
            Host.ShowToast(Loc.T("net.ui.export.none"));
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
            DefaultExt = ".md",
            AddExtension = true,
            FileName = "visor-network-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".md"
        };
        if (dlg.ShowDialog(Host.Window) != true) return;
        bool markdown = !dlg.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        try
        {
            var report = new NetworkReport
            {
                AppVersion = AppVersion(),
                Adapter = _lastAdapter,
                FullDuplex = _lastDuplex,
                Throughput = _throughput?.Last,
                Wlan = _lastWlan?.Connection,
                WlanReason = _lastWlan?.Reason ?? WlanUnavailableReason.None,
                PingSeries = _lastSeries.Select(kv => new PingSeriesReport(kv.Key, kv.Value)).ToList(),
                Trace = _lastTrace,
                Dns = _lastDns,
                Findings = _lastFindings
            };
            string text = NetworkReportExporter.Render(report, new NetworkReportOptions
            {
                IncludeIdentifiers = ChkNetExportIds.IsChecked == true,
                Markdown = markdown
            });
            File.WriteAllText(dlg.FileName, text);
            Host.ShowToast(Loc.Tf("net.ui.export.done", Path.GetFileName(dlg.FileName)));
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Network report export");
            Host.ShowToast(Loc.Tf("net.ui.export.fail", ex.Message));
        }
    }

    private static string? AppVersion()
    {
        try
        {
            var asm = typeof(NetworkPage).Assembly;
            var info = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
                asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            return info?.InformationalVersion ?? asm.GetName().Version?.ToString(3);
        }
        catch
        {
            return null;
        }
    }

    // -- 10-echo latency probe (unchanged) ---------------------------------------------------------------

    private async void NetPingStart_Click(object sender, RoutedEventArgs e)
    {
        if (_netPingCts != null) return;
        string host = SelectedHost();
        if (string.IsNullOrWhiteSpace(host))
        {
            Host.ShowToast(Loc.T("net.host.missing"));
            return;
        }
        if (string.Equals(host, "gateway", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(NetworkInfoService.CapturePrimary()?.Gateway))
        {
            Host.ShowToast(Loc.T("net.gateway.missing"));
            return;
        }

        _netPingCts = new CancellationTokenSource();
        _netPingPartial.Clear();
        BtnNetPingStart.IsEnabled = false;
        BtnNetPingStop.IsEnabled = true;
        TxtNetPingSummary.Text = Loc.T("net.ping.running");
        TxtNetPingDetail.Text = "";
        try
        {
            var progress = new Progress<ProbeSample>(s =>
            {
                _netPingPartial.Add(s.RttMs);
                string line = s.RttMs != null
                    ? Loc.Tf("net.ping.sample.ok", s.Index + 1, s.RttMs.Value.ToString("0.#"))
                    : Loc.Tf("net.ping.sample.fail", s.Index + 1, Loc.T(NetworkProbeService.ErrorLocKey(s.ErrorCode)));
                TxtNetPingDetail.Text = string.IsNullOrEmpty(TxtNetPingDetail.Text)
                    ? line
                    : TxtNetPingDetail.Text + "\n" + line;
            });
            var samples = await NetworkProbeService.PingAsync(host, count: 10, timeoutMs: 1500,
                _netPingCts.Token, progress).ConfigureAwait(true);
            if (!WindowAlive) return;
            var summary = NetworkStats.Summarize(samples.Select(s => s.RttMs).ToList());
            TxtNetPingSummary.Text = FormatPingSummary(summary);
            if (summary.Received == 0)
                TxtNetPingDetail.Text = (TxtNetPingDetail.Text + "\n" + Loc.T("net.ping.icmp.note")).Trim();
        }
        catch (OperationCanceledException)
        {
            if (!WindowAlive) return;
            var summary = NetworkStats.Summarize(_netPingPartial);
            TxtNetPingSummary.Text = summary.Sent == 0
                ? Loc.T("net.ping.stopped")
                : Loc.Tf("net.ping.stopped.summary", FormatPingSummary(summary));
        }
        catch (Exception ex)
        {
            if (!WindowAlive) return;
            string msg = string.Equals(ex.Message, NetworkProbeService.NoGatewayError, StringComparison.Ordinal)
                ? Loc.T("net.gateway.missing")
                : Loc.Tf("net.ping.error", ex.Message);
            TxtNetPingSummary.Text = msg;
        }
        finally
        {
            _netPingCts?.Dispose();
            _netPingCts = null;
            if (WindowAlive)
            {
                BtnNetPingStart.IsEnabled = true;
                BtnNetPingStop.IsEnabled = false;
            }
        }
    }

    private void NetPingStop_Click(object sender, RoutedEventArgs e) => _netPingCts?.Cancel();

    private static string FormatPingSummary(NetworkStats.Summary s)
    {
        if (s.Sent == 0) return Loc.T("net.ping.none");
        if (s.Received == 0)
            return Loc.Tf("net.ping.summary.lost", s.Sent, s.LossPercent.ToString("0.#"));
        string min = s.MinMs!.Value.ToString("0.#");
        string med = s.MedianMs!.Value.ToString("0.#");
        string max = s.MaxMs!.Value.ToString("0.#");
        string avg = s.AvgMs!.Value.ToString("0.#");
        string loss = s.LossPercent.ToString("0.#");
        if (s.JitterMs is null)
            return Loc.Tf("net.ping.summary.nojitter", s.Received, s.Sent, min, med, max, avg, loss);
        return Loc.Tf("net.ping.summary",
            s.Received, s.Sent, min, med, max, avg, loss, s.JitterMs.Value.ToString("0.#"));
    }

    // -- DNS (unchanged) ---------------------------------------------------------------------------------

    private async void NetDns_Click(object sender, RoutedEventArgs e)
    {
        if (_netDnsCts != null) return;
        _netDnsCts = new CancellationTokenSource();
        BtnNetDns.IsEnabled = false;
        BtnNetDnsStop.IsEnabled = true;
        NetDnsResults.Children.Clear();
        TxtNetDnsEmpty.Visibility = Visibility.Collapsed;
        TxtNetDnsStatus.Text = Loc.T("net.dns.running");
        int rows = 0;
        try
        {
            var progress = new Progress<DnsResolveResult>(r =>
            {
                rows++;
                AppendDnsRow(r);
            });
            var results = await NetworkProbeService.ResolveDnsAsync(
                NetworkProbeService.DefaultDnsHosts, _netDnsCts.Token, progress).ConfigureAwait(true);
            if (!WindowAlive) return;
            _lastDns = results;
            TxtNetDnsStatus.Text = "";
            if (rows == 0)
                TxtNetDnsEmpty.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            if (WindowAlive)
                TxtNetDnsStatus.Text = rows == 0
                    ? Loc.T("net.dns.stopped")
                    : Loc.Tf("net.dns.stopped.partial", rows);
        }
        catch (Exception ex)
        {
            if (WindowAlive)
            {
                TxtNetDnsStatus.Text = "";
                Host.ShowToast(Loc.T(NetworkProbeService.ErrorLocKey(NetworkProbeService.MapException(ex))));
            }
        }
        finally
        {
            _netDnsCts?.Dispose();
            _netDnsCts = null;
            if (WindowAlive)
            {
                BtnNetDns.IsEnabled = true;
                BtnNetDnsStop.IsEnabled = false;
            }
        }
    }

    private void AppendDnsRow(DnsResolveResult r)
    {
        // "first / repeat": the second lookup usually hits the resolver cache, but that is not guaranteed.
        string line = r.ErrorCode != ProbeErrorCode.None
            ? Loc.Tf("net.dns.row.fail", r.Host, Loc.T(NetworkProbeService.ErrorLocKey(r.ErrorCode)), r.Ms?.ToString("0.#") ?? "—")
            : r.RepeatMs is { } repeat
                ? Loc.Tf("net.dns.row.ok2", r.Host, r.Address ?? "?", r.Ms?.ToString("0.#") ?? "—", repeat.ToString("0.#"))
                : Loc.Tf("net.dns.row.ok", r.Host, r.Address ?? "?", r.Ms?.ToString("0.#") ?? "—");
        NetDnsResults.Children.Add(Hint(line, new Thickness(0, 0, 0, 4)));
    }

    private void NetDnsStop_Click(object sender, RoutedEventArgs e) => _netDnsCts?.Cancel();
}
