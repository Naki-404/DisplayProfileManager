using System.Windows;
using System.Windows.Controls;
using Visor.Controls;
using Visor.Services;
using Visor.Services.NetworkTweaks;

namespace Visor;

/// <summary>
/// Network tweaks shared by Optimize → Network and Network → TCP: the TCP info card (auto-tuning / ECN / RSS
/// from <c>netsh int tcp show global</c>) and one <see cref="TweakRow"/> per target — Nagle on the primary
/// adapter, multimedia network throttling, TCP auto-tuning restore. Both pages host their own instance;
/// state is never cached across them — every <see cref="RefreshAsync"/> re-reads registry / netsh off the
/// UI thread, and every apply / undo re-reads afterwards, so both places always show the real state.
/// </summary>
internal sealed class NetTweakPanel : StackPanel
{
    private readonly IShellHost _host;
    private readonly StackPanel _rows = new();
    private readonly StackPanel _tcpLines = new();
    private readonly TextBlock _rowsHeader;
    private readonly TextBlock _rowsHint;
    private readonly TextBlock _tcpHeader;
    private int _gen;
    private Snapshot? _last;

    private sealed class Snapshot
    {
        public string? Guid;
        public string? Adapter;
        public bool NagleReadable;
        public bool NagleOff;
        public bool NagleUndo;
        public bool MmcssReadable;
        public bool MmcssOff;
        public bool MmcssUndo;
        public TcpGlobalInfo? Tcp;
        public bool AutotuningUndo;
    }

    public NetTweakPanel(IShellHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));

        var tcpCard = Card();
        var tcpStack = new StackPanel();
        _tcpHeader = Header();
        tcpStack.Children.Add(_tcpHeader);
        tcpStack.Children.Add(_tcpLines);
        tcpCard.Child = tcpStack;
        Children.Add(tcpCard);

        var rowsCard = Card();
        var rowsStack = new StackPanel();
        _rowsHeader = Header();
        rowsStack.Children.Add(_rowsHeader);
        _rowsHint = TweakRow.Hint("", new Thickness(0, 0, 0, 10));
        rowsStack.Children.Add(_rowsHint);
        rowsStack.Children.Add(_rows);
        rowsCard.Child = rowsStack;
        Children.Add(rowsCard);
        Relabel();
    }

    public void Relabel()
    {
        _tcpHeader.Text = Loc.T("net.tcp.title");
        _rowsHeader.Text = Loc.T("net.tweak.header");
        _rowsHint.Text = Loc.T("net.tweak.header.hint");
        if (_last != null) Render(_last);
    }

    /// <summary>Re-reads every state off the UI thread and rebuilds the rows (newest call wins).</summary>
    public async Task RefreshAsync()
    {
        int gen = ++_gen;
        if (_last == null)
        {
            _rows.Children.Clear();
            _rows.Children.Add(TweakRow.Hint(Loc.T("system.loading"), new Thickness(0)));
        }
        Snapshot snap;
        try
        {
            snap = await Task.Run(() => Capture());
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Network tweaks read");
            snap = new Snapshot();
        }
        if (gen != _gen || !_host.Window.IsLoaded) return;
        _last = snap;
        Render(snap);
    }

    private static Snapshot Capture()
    {
        var safe = App.Services.SafeActions;
        var s = new Snapshot
        {
            Adapter = NetworkInfoService.CapturePrimary()?.Name,
            Guid = NetworkTweakCatalog.PrimaryInterfaceGuid()
        };
        if (s.Guid != null)
        {
            string id = NetworkTweakCatalog.NagleActionId(s.Guid);
            s.NagleReadable = safe.ReadState(id) != null;
            s.NagleOff = s.NagleReadable && safe.IsAtTarget(id);
            s.NagleUndo = safe.CanUndo(id);
        }
        s.MmcssReadable = safe.ReadState(NetworkTweakCatalog.ThrottlingId) != null;
        s.MmcssOff = s.MmcssReadable && safe.IsAtTarget(NetworkTweakCatalog.ThrottlingId);
        s.MmcssUndo = safe.CanUndo(NetworkTweakCatalog.ThrottlingId);
        s.Tcp = new NetshTcpGlobalReader().Read();
        s.AutotuningUndo = safe.CanUndo(NetworkTweakCatalog.AutotuningId);
        return s;
    }

    private void Render(Snapshot s)
    {
        RenderTcpInfo(s.Tcp);
        _rows.Children.Clear();
        _rows.Children.Add(NagleRow(s));
        _rows.Children.Add(MmcssRow(s));
        _rows.Children.Add(AutotuningRow(s));
    }

    // -- TCP info card -----------------------------------------------------------------------------------

    private void RenderTcpInfo(TcpGlobalInfo? tcp)
    {
        _tcpLines.Children.Clear();
        if (tcp == null || !tcp.IsParsed)
        {
            _tcpLines.Children.Add(TweakRow.Hint(Loc.T("net.tcp.unreadable"), new Thickness(0)));
            return;
        }
        _tcpLines.Children.Add(InfoLine(
            Loc.Tf("net.tcp.autotuning", TcpValue(tcp.AutoTuningLevel)),
            AutotuningVerdict(tcp),
            AutotuningKind(tcp)));
        _tcpLines.Children.Add(InfoLine(Loc.Tf("net.tcp.rss", TcpValue(tcp.Rss)), Loc.T("net.tcp.rss.info"),
            tcp.Rss switch { "enabled" => StatusKind.Ok, "disabled" => StatusKind.Warn, _ => StatusKind.Unknown }));
        _tcpLines.Children.Add(InfoLine(Loc.Tf("net.tcp.ecn", TcpValue(tcp.Ecn)), Loc.T("net.tcp.ecn.info"), StatusKind.Unknown));
        if (tcp.CongestionProvider != TcpGlobalInfo.Unknown)
            _tcpLines.Children.Add(InfoLine(Loc.Tf("net.tcp.congestion", tcp.CongestionProvider), null, StatusKind.Unknown));
    }

    private static string AutotuningVerdict(TcpGlobalInfo tcp) =>
        tcp.AutoTuningDegraded ? Loc.T("net.tcp.autotuning.degraded")
        : tcp.AutoTuningLevel == TcpAutotuningSafeActionTarget.Normal ? Loc.T("net.tcp.autotuning.ok")
        : Loc.T("net.tcp.autotuning.other");

    private static StatusKind AutotuningKind(TcpGlobalInfo tcp) =>
        tcp.AutoTuningDegraded ? StatusKind.Warn
        : tcp.AutoTuningLevel == TcpAutotuningSafeActionTarget.Normal ? StatusKind.Ok
        : StatusKind.Unknown;

    /// <summary>Normalized netsh token → localized word (unknown tokens are shown as they are).</summary>
    public static string TcpValue(string? token)
    {
        if (string.IsNullOrEmpty(token) || token == TcpGlobalInfo.Unknown) return Loc.T("net.tcp.unknown");
        string key = "net.tcp.val." + token;
        return Loc.Has(key) ? Loc.T(key) : token;
    }

    private static FrameworkElement InfoLine(string title, string? hint, StatusKind kind)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var dot = new StatusDot { Status = kind, Label = title, Margin = new Thickness(0, 5, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(dot, Dock.Left);
        dock.Children.Add(dot);
        var col = new StackPanel();
        var t = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        col.Children.Add(t);
        if (!string.IsNullOrEmpty(hint)) col.Children.Add(TweakRow.Hint(hint, new Thickness(0, 2, 0, 0)));
        dock.Children.Add(col);
        return dock;
    }

    // -- Rows --------------------------------------------------------------------------------------------

    private TweakRow NagleRow(Snapshot s)
    {
        var row = new TweakRow(Loc.T("net.tweak.nagle.title"))
            .SetNeedsAdmin(true)
            .SetDetails(Loc.T("net.tweak.nagle.why"), Loc.T("net.tweak.nagle.risk"), Loc.T("net.tweak.nagle.undo"));
        string adapter = string.IsNullOrEmpty(s.Adapter) ? "?" : s.Adapter!;
        if (s.Guid == null)
            row.SetStatus(Loc.T("net.tweak.noAdapter"), StatusKind.Unknown);
        else if (!s.NagleReadable)
            row.SetStatus(Loc.T("system.unavailable"), StatusKind.Unknown);
        else
            row.SetStatus(Loc.Tf(s.NagleOff ? "net.tweak.nagle.status.off" : "net.tweak.nagle.status.default", adapter),
                s.NagleOff ? StatusKind.Ok : StatusKind.Unknown);

        string? id = s.Guid == null ? null : NetworkTweakCatalog.NagleActionId(s.Guid);
        row.AddAction(Loc.T("btn.apply"), accent: true,
            run: () => ApplyAsync(id!, Loc.T("net.tweak.status.reconnect")),
            enabled: id != null && s.NagleReadable && !s.NagleOff,
            busyText: Loc.T("optimize.ui.busy.uac"));
        row.AddAction(Loc.T("system.rollback"), accent: false,
            run: () => UndoAsync(id!),
            enabled: id != null && s.NagleUndo,
            busyText: Loc.T("optimize.ui.busy.uac"));
        return row;
    }

    private TweakRow MmcssRow(Snapshot s)
    {
        var row = new TweakRow(Loc.T("net.tweak.mmcss.title"))
            .SetNeedsAdmin(true)
            .SetDetails(Loc.T("net.tweak.mmcss.why"), Loc.T("net.tweak.mmcss.risk"), Loc.T("net.tweak.mmcss.undo"));
        if (!s.MmcssReadable)
            row.SetStatus(Loc.T("system.unavailable"), StatusKind.Unknown);
        else
            row.SetStatus(Loc.T(s.MmcssOff ? "net.tweak.mmcss.status.off" : "net.tweak.mmcss.status.default"),
                s.MmcssOff ? StatusKind.Ok : StatusKind.Unknown);
        row.AddAction(Loc.T("btn.apply"), accent: true,
            run: () => ApplyAsync(NetworkTweakCatalog.ThrottlingId, Loc.T("net.tweak.status.reboot")),
            enabled: s.MmcssReadable && !s.MmcssOff,
            busyText: Loc.T("optimize.ui.busy.uac"));
        row.AddAction(Loc.T("system.rollback"), accent: false,
            run: () => UndoAsync(NetworkTweakCatalog.ThrottlingId),
            enabled: s.MmcssUndo,
            busyText: Loc.T("optimize.ui.busy.uac"));
        return row;
    }

    private TweakRow AutotuningRow(Snapshot s)
    {
        var row = new TweakRow(Loc.T("net.tweak.autotuning.title"))
            .SetNeedsAdmin(true)
            .SetDetails(Loc.T("net.tweak.autotuning.why"), Loc.T("net.tweak.autotuning.risk"), Loc.T("net.tweak.autotuning.undo"));
        var tcp = s.Tcp;
        if (tcp == null || tcp.AutoTuningLevel == TcpGlobalInfo.Unknown)
            row.SetStatus(Loc.Tf("net.tcp.autotuning", Loc.T("net.tcp.unknown")), StatusKind.Unknown);
        else
            row.SetStatus(Loc.Tf("net.tcp.autotuning", TcpValue(tcp.AutoTuningLevel)) + " · " + AutotuningVerdict(tcp), AutotuningKind(tcp));
        bool degraded = tcp?.AutoTuningDegraded == true;
        row.AddAction(Loc.T("btn.apply"), accent: true,
            run: () => ApplyAsync(NetworkTweakCatalog.AutotuningId, null),
            enabled: degraded,
            tooltip: degraded ? null : Loc.T("net.tcp.autotuning.ok"),
            busyText: Loc.T("optimize.ui.busy.uac"));
        row.AddAction(Loc.T("system.rollback"), accent: false,
            run: () => UndoAsync(NetworkTweakCatalog.AutotuningId),
            enabled: s.AutotuningUndo,
            busyText: Loc.T("optimize.ui.busy.uac"));
        return row;
    }

    private async Task ApplyAsync(string actionId, string? afterNote)
    {
        await SafeActionUi.ApplyAsync(_host, actionId, new SafeApplyOptions { AfterNote = afterNote });
        await RefreshAsync();
    }

    private async Task UndoAsync(string actionId)
    {
        await SafeActionUi.UndoAsync(_host, actionId);
        await RefreshAsync();
    }

    // -- Factories ---------------------------------------------------------------------------------------

    private static Border Card()
    {
        var b = new Border { Margin = new Thickness(0, 0, 0, 12) };
        b.SetResourceReference(StyleProperty, "CardSurface");
        return b;
    }

    private static TextBlock Header()
    {
        var t = new TextBlock { Margin = new Thickness(0, 0, 0, 4) };
        t.SetResourceReference(StyleProperty, "SubHeader");
        return t;
    }
}
