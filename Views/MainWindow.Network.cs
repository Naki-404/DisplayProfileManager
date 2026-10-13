using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Visor.Models;
using Visor.Services;

namespace Visor;

/// <summary>Network page: adapter overview, latency probe, DNS resolve.</summary>
public partial class MainWindow : Window
{
    // -- Network -------------------------------------------------------------------------------------

    private CancellationTokenSource? _netPingCts;
    private CancellationTokenSource? _netDnsCts;
    private readonly List<double?> _netPingPartial = new();
    /// <summary>Set when a refresh was skipped because the page was hidden; honored by RefreshStalePages.</summary>
    private bool _networkStale = true;
    /// <summary>Gateway presence from the last adapter capture (keeps the host combo right without re-capturing).</summary>
    private bool _netHasGateway;

    private bool NetworkPageIsLive => IsVisible && ReferenceEquals(MainTabs.SelectedItem, TabNetwork);

    private void CancelNetworkProbes()
    {
        try { _netPingCts?.Cancel(); } catch { /* ignore */ }
        try { _netDnsCts?.Cancel(); } catch { /* ignore */ }
    }

    private void RebuildNetHosts(bool hasGateway)
    {
        if (CmbNetHost == null) return;
        string? prev = (CmbNetHost.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string
                       ?? CmbNetHost.Text?.Trim();
        CmbNetHost.Items.Clear();
        foreach (var h in NetworkProbeService.DefaultHosts)
        {
            bool isGw = string.Equals(h, "gateway", StringComparison.OrdinalIgnoreCase);
            string label = isGw ? Loc.T("net.host.gateway") : h;
            var item = new System.Windows.Controls.ComboBoxItem
            {
                Content = label,
                Tag = h,
                IsEnabled = !isGw || hasGateway
            };
            CmbNetHost.Items.Add(item);
        }
        int idx = 0;
        if (!string.IsNullOrEmpty(prev))
        {
            for (int i = 0; i < CmbNetHost.Items.Count; i++)
            {
                if (CmbNetHost.Items[i] is System.Windows.Controls.ComboBoxItem c
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

    /// <summary>One adapter capture per refresh feeds both the host combo and the overview card.</summary>
    private void RefreshNetworkPage(bool forceRefresh = false)
    {
        if (TxtNetAdapter == null) return;
        if (!NetworkPageIsLive)
        {
            _networkStale = true;
            return;
        }
        _networkStale = false;
        var info = NetworkInfoService.CapturePrimary(forceRefresh);
        _netHasGateway = !string.IsNullOrEmpty(info?.Gateway);
        RebuildNetHosts(_netHasGateway);
        if (info == null)
        {
            TxtNetAdapter.Text = "";
            TxtNetDetails.Text = "";
            TxtNetDns.Text = "";
            TxtNetEmpty.Visibility = Visibility.Visible;
            return;
        }
        TxtNetEmpty.Visibility = Visibility.Collapsed;
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
        bits.Add(LocalizeNetStatus(info.Status));
        TxtNetDetails.Text = string.Join(" · ", bits);
        TxtNetDns.Text = info.DnsServers.Count == 0
            ? Loc.T("net.dns.none")
            : Loc.Tf("net.dns.list", string.Join(", ", info.DnsServers));
    }

    private static string LocalizeNetType(string code) => code switch
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

    private async void NetPingStart_Click(object sender, RoutedEventArgs e)
    {
        if (_netPingCts != null) return;
        string host = (CmbNetHost.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string
                      ?? CmbNetHost.Text?.Trim()
                      ?? "";
        if (string.IsNullOrWhiteSpace(host))
        {
            ShowToast(Loc.T("net.host.missing"));
            return;
        }
        if (string.Equals(host, "gateway", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(NetworkInfoService.CapturePrimary()?.Gateway))
        {
            ShowToast(Loc.T("net.gateway.missing"));
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
            if (!IsLoaded) return;
            var summary = NetworkStats.Summarize(samples.Select(s => s.RttMs).ToList());
            TxtNetPingSummary.Text = FormatPingSummary(summary);
            if (summary.Received == 0)
                TxtNetPingDetail.Text = (TxtNetPingDetail.Text + "\n" + Loc.T("net.ping.icmp.note")).Trim();
        }
        catch (OperationCanceledException)
        {
            if (!IsLoaded) return;
            var summary = NetworkStats.Summarize(_netPingPartial);
            TxtNetPingSummary.Text = summary.Sent == 0
                ? Loc.T("net.ping.stopped")
                : Loc.Tf("net.ping.stopped.summary", FormatPingSummary(summary));
        }
        catch (Exception ex)
        {
            if (!IsLoaded) return;
            string msg = string.Equals(ex.Message, NetworkProbeService.NoGatewayError, StringComparison.Ordinal)
                ? Loc.T("net.gateway.missing")
                : Loc.Tf("net.ping.error", ex.Message);
            TxtNetPingSummary.Text = msg;
        }
        finally
        {
            _netPingCts?.Dispose();
            _netPingCts = null;
            if (IsLoaded)
            {
                BtnNetPingStart.IsEnabled = true;
                BtnNetPingStop.IsEnabled = false;
            }
        }
    }

    private void NetPingStop_Click(object sender, RoutedEventArgs e) =>
        _netPingCts?.Cancel();

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
            await NetworkProbeService.ResolveDnsAsync(
                NetworkProbeService.DefaultDnsHosts, _netDnsCts.Token, progress).ConfigureAwait(true);
            if (!IsLoaded) return;
            TxtNetDnsStatus.Text = "";
            if (rows == 0)
                TxtNetDnsEmpty.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            if (IsLoaded)
                TxtNetDnsStatus.Text = rows == 0
                    ? Loc.T("net.dns.stopped")
                    : Loc.Tf("net.dns.stopped.partial", rows);
        }
        catch (Exception ex)
        {
            if (IsLoaded)
            {
                TxtNetDnsStatus.Text = "";
                ShowToast(Loc.T(NetworkProbeService.ErrorLocKey(NetworkProbeService.MapException(ex))));
            }
        }
        finally
        {
            _netDnsCts?.Dispose();
            _netDnsCts = null;
            if (IsLoaded)
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
        NetDnsResults.Children.Add(new TextBlock
        {
            Text = line,
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap
        });
    }

    private void NetDnsStop_Click(object sender, RoutedEventArgs e) =>
        _netDnsCts?.Cancel();
}
