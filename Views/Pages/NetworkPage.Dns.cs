using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Visor.Controls;
using Visor.Services;
using Visor.Services.NetworkTweaks;
using WpfButton = System.Windows.Controls.Button;

namespace Visor;

/// <summary>
/// Network → DNS: what the primary adapter uses now (DHCP vs static, read without admin), the DNS speed test
/// (<see cref="DnsBenchmarkService"/>, no admin, cancellable), "Use X" through the elevated helper
/// (<see cref="SafeActionUi.ApplyAsync"/>, one UAC prompt, journaled), Undo and the resolver-cache flush.
/// </summary>
public partial class NetworkPage
{
    private CancellationTokenSource? _benchCts;
    private DnsBenchmarkReport? _benchReport;
    private bool _benchStale;
    private int _dnsGen;
    private string? _dnsGuid;

    private sealed record DnsCurrentInfo(string? Guid, string? Adapter, DnsServerState? State,
        IReadOnlyList<IPAddress> Effective, SafeActionJournalEntry? Journal);

    private static SafeActionService Safe => App.Services.SafeActions;

    private void ApplyDnsLocalization()
    {
        LblDnsCurrent.Text = Loc.T("net.dns.section");
        BtnDnsFlush.Content = Loc.T("net.dns.flush");
        BtnDnsUndo.Content = Loc.T("system.rollback");
        BtnDnsUndo.ToolTip = Loc.T("net.tweak.dns.undo") + " " + Loc.T("net.tweak.status.uacHint") + ".";
        TxtDnsScope.Text = Loc.T("net.dns.scope");
        DotDnsCurrent.Label = Loc.T("net.dns.section");
        LblDnsBench.Text = Loc.T("net.dns.bench.title");
        TxtDnsBenchHint.Text = Loc.T("net.dns.bench.hint");
        TxtDnsHonest.Text = Loc.T("net.dns.bench.honest");
        LblDnsCustom.Text = Loc.T("net.dns.bench.custom");
        BtnDnsBenchRun.Content = Loc.T("net.dns.bench.run");
        BtnDnsBenchStop.Content = Loc.T("net.dns.bench.cancel");
        BarDnsBench.Label = Loc.T("net.dns.bench.title");
        TxtDnsUac.Text = Loc.T("net.tweak.status.uacHint");
        DnsDetails.SetTexts(Loc.T("net.tweak.dns.why"), Loc.T("net.tweak.dns.risk"), Loc.T("net.tweak.dns.undo"));
        DnsDetails.Relabel();
        ValidateCustomDns();
        if (_benchReport != null) RenderBench(_benchReport);
    }

    // -- Current DNS ---------------------------------------------------------------------------------------

    private async Task RefreshDnsCurrentAsync()
    {
        int gen = ++_dnsGen;
        DnsCurrentInfo info;
        try
        {
            info = await Task.Run(() => CaptureDnsCurrent());
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "DNS current read");
            info = new DnsCurrentInfo(null, null, null, Array.Empty<IPAddress>(), null);
        }
        if (gen != _dnsGen || !WindowAlive) return;
        _dnsGuid = info.Guid;
        RenderDnsCurrent(info);
    }

    private static DnsCurrentInfo CaptureDnsCurrent()
    {
        string? guid = NetworkTweakCatalog.PrimaryInterfaceGuid();
        string? adapter = NetworkInfoService.CapturePrimary()?.Name;
        if (guid == null) return new DnsCurrentInfo(null, adapter, null, Array.Empty<IPAddress>(), null);
        // The registry NameServer value (no admin) tells DHCP from static; any dns.set id of this adapter reads it.
        string? raw = Safe.ReadState(NetworkTweakCatalog.DnsActionId(guid, DnsProviders.Public[0].Servers));
        DnsServerState? state = DnsServerState.TryParse(raw, out var s) ? s : null;
        var effective = DnsSafeActionTarget.EffectiveDnsServers(guid);
        var journal = Safe.Journal.Get(DnsSafeActionTarget.JournalPrefix + guid);
        return new DnsCurrentInfo(guid, adapter, state, effective, journal);
    }

    private static string DnsStateText(DnsServerState state) =>
        state.IsDhcp ? Loc.T("net.dns.prev.dhcp") : string.Join(", ", state.Servers);

    private void RenderDnsCurrent(DnsCurrentInfo info)
    {
        string effective = info.Effective.Count == 0 ? "—" : string.Join(", ", info.Effective.Select(a => a.ToString()));
        if (info.Guid == null)
        {
            TxtDnsCurrent.Text = Loc.T("net.tweak.noAdapter");
            DotDnsCurrent.Status = StatusKind.Unknown;
        }
        else
        {
            TxtDnsCurrent.Text = info.State is { IsDhcp: false } st
                ? Loc.Tf("net.dns.current", string.Join(", ", st.Servers))
                : info.State is { IsDhcp: true }
                    ? Loc.Tf("net.dns.current.dhcp", effective)
                    : Loc.Tf("net.dns.current", effective);
            DotDnsCurrent.Status = info.Effective.Count > 0 ? StatusKind.Ok : StatusKind.Warn;
        }
        TxtDnsCurrentAdapter.Text = string.IsNullOrEmpty(info.Adapter) ? "" : Loc.Tf("net.dns.adapter", info.Adapter);
        TxtDnsCurrentAdapter.Visibility = string.IsNullOrEmpty(info.Adapter) ? Visibility.Collapsed : Visibility.Visible;

        if (info.Journal != null)
        {
            string previous = DnsServerState.TryParse(info.Journal.PreviousState, out var prev) ? DnsStateText(prev) : "?";
            TxtDnsChanged.Text = Loc.Tf("net.dns.changedByVisor", previous);
            TxtDnsChanged.Visibility = Visibility.Visible;
            BtnDnsUndo.Visibility = Visibility.Visible;
        }
        else
        {
            TxtDnsChanged.Visibility = Visibility.Collapsed;
            BtnDnsUndo.Visibility = Visibility.Collapsed;
        }
    }

    private async void DnsUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_dnsGuid == null) return;
        BtnDnsUndo.IsEnabled = false;
        try
        {
            if (await SafeActionUi.UndoAsync(Host, DnsSafeActionTarget.JournalPrefix + _dnsGuid))
                MarkBenchStale();
        }
        finally
        {
            if (WindowAlive) BtnDnsUndo.IsEnabled = true;
        }
        await RefreshDnsCurrentAsync();
    }

    private async void DnsFlush_Click(object sender, RoutedEventArgs e)
    {
        BtnDnsFlush.IsEnabled = false;
        bool ok;
        try { ok = await Task.Run(() => DnsCache.Flush()); }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "DNS flush");
            ok = false;
        }
        if (!WindowAlive) return;
        BtnDnsFlush.IsEnabled = true;
        Host.ShowToast(Loc.T(ok ? "net.dns.flush.ok" : "net.dns.flush.fail"));
    }

    // -- Benchmark -----------------------------------------------------------------------------------------

    private void DnsCustom_TextChanged(object sender, TextChangedEventArgs e) => ValidateCustomDns();

    /// <summary>Live validation of the custom pair: 1–4 canonical IPv4 addresses (what the helper accepts).</summary>
    private bool ValidateCustomDns()
    {
        if (TxtDnsCustomHint == null) return true;
        string text = TxtDnsCustom.Text?.Trim() ?? "";
        bool ok = text.Length == 0 || DnsProviders.ParseCustom(text) != null;
        TxtDnsCustomHint.Text = Loc.T(ok ? "net.dns.bench.customHint" : "net.dns.bench.customInvalid");
        TxtDnsCustomHint.SetResourceReference(TextBlock.ForegroundProperty, ok ? "MutedBrush" : "WarningBrush");
        return ok;
    }

    private async void DnsBenchRun_Click(object sender, RoutedEventArgs e)
    {
        if (_benchCts != null) return;
        if (!ValidateCustomDns())
        {
            TxtDnsCustom.Focus();
            return;
        }
        string customText = TxtDnsCustom.Text?.Trim() ?? "";
        var custom = customText.Length == 0 ? null : DnsProviders.ParseCustom(customText);

        var cts = new CancellationTokenSource();
        _benchCts = cts;
        SetBenchBusy(true);
        TxtDnsBenchStatus.Text = Loc.Tf("net.dns.bench.running", 0);
        try
        {
            var current = await Task.Run(() =>
            {
                string? guid = NetworkTweakCatalog.PrimaryInterfaceGuid();
                return guid == null ? (IReadOnlyList<IPAddress>)Array.Empty<IPAddress>() : DnsSafeActionTarget.EffectiveDnsServers(guid);
            }, cts.Token);
            var candidates = DnsProviders.BuildCandidates(current, custom);
            var progress = new Progress<double>(p =>
            {
                if (!ReferenceEquals(_benchCts, cts)) return;
                BarDnsBench.Value = p * 100;
                TxtDnsBenchStatus.Text = Loc.Tf("net.dns.bench.running", (int)Math.Round(p * 100));
            });
            var report = await new DnsBenchmarkService().RunAsync(candidates, progress, cts.Token);
            if (!WindowAlive) return;
            _benchReport = report;
            _benchStale = false;
            RenderBench(report);
            TxtDnsBenchStatus.Text = Loc.Tf("net.dns.bench.done",
                report.Utc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture), report.Rounds, report.Domains.Count);
        }
        catch (OperationCanceledException)
        {
            if (WindowAlive) TxtDnsBenchStatus.Text = Loc.T("net.dns.bench.stopped");
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "DNS benchmark");
            if (WindowAlive) TxtDnsBenchStatus.Text = Loc.Tf("net.ping.error", ErrorText(ex));
        }
        finally
        {
            _benchCts = null;
            cts.Dispose();
            if (WindowAlive) SetBenchBusy(false);
        }
    }

    private void DnsBenchStop_Click(object sender, RoutedEventArgs e)
    {
        try { _benchCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void SetBenchBusy(bool busy)
    {
        BtnDnsBenchRun.IsEnabled = !busy;
        BtnDnsBenchStop.IsEnabled = busy;
        TxtDnsCustom.IsEnabled = !busy;
        BarDnsBench.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BarDnsBench.Value = 0;
        BtnDnsUse.IsEnabled = !busy;
    }

    private static string CandidateName(DnsCandidate c) => c.Id switch
    {
        DnsProviders.CurrentId => Loc.T("net.dns.bench.current"),
        DnsProviders.CustomId => Loc.T("net.dns.bench.custom"),
        _ => c.Name
    };

    private static string MsText(double? ms) =>
        ms is double v ? Loc.Tf("net.dns.bench.ms", v.ToString("0.#", CultureInfo.CurrentCulture)) : "—";

    private static readonly double[] BenchColumns = { 0, 76, 76, 64, 0 };

    private Grid NewBenchGrid()
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var w in BenchColumns)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions[4].Width = GridLength.Auto;
        return g;
    }

    private void RenderBench(DnsBenchmarkReport report)
    {
        DnsBenchRows.Children.Clear();
        var header = NewBenchGrid();
        var cols = new[] { Loc.T("net.dns.bench.col.server"), Loc.T("net.dns.bench.col.median"), Loc.T("net.dns.bench.col.p95"), Loc.T("net.dns.bench.col.fail") };
        for (int i = 0; i < cols.Length; i++)
        {
            var tb = new TextBlock { Text = cols[i], FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Grid.SetColumn(tb, i);
            header.Children.Add(tb);
        }
        DnsBenchRows.Children.Add(header);

        var best = report.Best;
        foreach (var row in report.Rows)
            DnsBenchRows.Children.Add(BuildBenchRow(row, ReferenceEquals(row, best)));

        // Verdict: switch only when the gain is real (DnsBenchmarkRanking), else "keep".
        string verdict;
        if (best == null)
            verdict = Loc.T("net.dns.bench.noAnswer");
        else if (report.SwitchRecommended)
        {
            var cur = report.Current?.Stats.MedianMs;
            verdict = cur is double c && best.Stats.MedianMs is double b && c > b
                ? Loc.Tf("net.dns.bench.switch", CandidateName(best.Candidate), Math.Round(c - b).ToString("0", CultureInfo.CurrentCulture))
                : Loc.Tf("net.dns.bench.best", CandidateName(best.Candidate));
        }
        else
            verdict = Loc.T("net.dns.bench.keep");
        if (_benchStale) verdict = Loc.T("net.dns.bench.rerun");
        TxtDnsVerdict.Text = verdict;
        TxtDnsVerdict.Visibility = Visibility.Visible;

        bool offerUse = !_benchStale && best != null && !best.Candidate.IsCurrent;
        DnsUseRow.Visibility = offerUse ? Visibility.Visible : Visibility.Collapsed;
        if (offerUse)
        {
            BtnDnsUse.Content = Loc.Tf("net.dns.bench.apply", CandidateName(best!.Candidate));
            BtnDnsUse.Tag = best.Candidate;
            BtnDnsUse.SetResourceReference(StyleProperty, report.SwitchRecommended ? "CompactAccentButton" : "CompactGhostButton");
            AutomationProperties.SetName(BtnDnsUse, (string)BtnDnsUse.Content);
        }
    }

    private FrameworkElement BuildBenchRow(DnsBenchmarkRow row, bool isBest)
    {
        var g = NewBenchGrid();
        var c = row.Candidate;
        var s = row.Stats;

        var nameCol = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        var nameLine = new DockPanel();
        if (c.IsCurrent)
        {
            var badge = TweakRow.Badge(Loc.T("net.dns.bench.current"), "AccentBrush", "AccentBrush");
            DockPanel.SetDock(badge, Dock.Left);
            nameLine.Children.Add(badge);
        }
        var name = new TextBlock { Text = CandidateName(c), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        if (isBest) name.FontWeight = FontWeights.SemiBold;
        nameLine.Children.Add(name);
        nameCol.Children.Add(nameLine);
        var servers = TweakRow.Hint(c.ServersCsv.Replace(",", ", "), new Thickness(0, 1, 0, 0));
        servers.TextWrapping = TextWrapping.NoWrap;
        servers.TextTrimming = TextTrimming.CharacterEllipsis;
        nameCol.Children.Add(servers);
        g.Children.Add(nameCol);

        bool dead = s.Answered == 0;
        AddCell(g, 1, MsText(s.MedianMs), dead ? "MutedBrush" : isBest ? "AccentBrush" : "TextBrush");
        AddCell(g, 2, MsText(s.P95Ms), dead ? "MutedBrush" : "TextBrush");
        AddCell(g, 3, s.FailurePercent.ToString("0.#", CultureInfo.CurrentCulture) + " %",
            s.FailurePercent > DnsBenchmarkRanking.UnreliableFailurePercent ? "WarningBrush" : "MutedBrush");

        if (!c.IsCurrent && !dead && !_benchStale)
        {
            var use = new WpfButton { Content = Loc.T("net.dns.bench.use"), Tag = c, VerticalAlignment = VerticalAlignment.Top };
            use.SetResourceReference(StyleProperty, "LinkButton");
            AutomationProperties.SetName(use, Loc.Tf("net.dns.bench.apply", CandidateName(c)));
            use.ToolTip = Loc.T("net.tweak.status.uacHint");
            use.Click += DnsUse_Click;
            Grid.SetColumn(use, 4);
            g.Children.Add(use);
        }
        return g;
    }

    private static void AddCell(Grid g, int col, string text, string brushKey)
    {
        var tb = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Top };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        Grid.SetColumn(tb, col);
        g.Children.Add(tb);
    }

    private async void DnsUse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: DnsCandidate c } button) return;
        button.IsEnabled = false;
        BtnDnsUse.IsEnabled = false;
        try
        {
            string? guid = await Task.Run(() => NetworkTweakCatalog.PrimaryInterfaceGuid());
            if (guid == null)
            {
                Host.ShowToast(Loc.T("net.tweak.noAdapter"));
                return;
            }
            string id = NetworkTweakCatalog.DnsActionId(guid, c.Servers);
            var r = await SafeActionUi.ApplyAsync(Host, id, new SafeApplyOptions
            {
                Title = Loc.Tf("net.dns.bench.apply", CandidateName(c)),
                WhyKey = "net.tweak.dns.why",
                RiskKey = "net.tweak.dns.risk",
                OkToast = Loc.Tf("net.dns.applied", CandidateName(c)),
                HistoryDetail = id
            });
            if (r?.Result == SafeActionResult.Applied) MarkBenchStale();
        }
        finally
        {
            if (WindowAlive)
            {
                button.IsEnabled = true;
                BtnDnsUse.IsEnabled = _benchCts == null;
            }
        }
        await RefreshDnsCurrentAsync();
    }

    /// <summary>DNS changed since the test ran: the "current" flags are outdated — ask for a new run.</summary>
    private void MarkBenchStale()
    {
        if (_benchReport == null) return;
        _benchStale = true;
        RenderBench(_benchReport);
    }
}
