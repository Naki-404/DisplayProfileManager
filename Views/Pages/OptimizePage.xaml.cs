using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Visor.Controls;
using Visor.Models;
using Visor.Services;
using Visor.Services.Optimize;
using Visor.Services.Optimize.Pc;
using WpfButton = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace Visor;

/// <summary>Sub-sections of the Optimize page, in strip order.</summary>
public enum OptimizeSection
{
    Overview,
    System,
    Graphics,
    Input,
    Network,
    Startup,
    Cleanup
}

/// <summary>
/// Optimize section, split into sub-sections (segmented strip, Left / Right moves): Overview (summary,
/// suggestions, undo journal), System (Game Mode, power plans, HAGS, Game Bar, managed info), Graphics (per-game
/// fullscreen optimizations, the HAGS switch, visual effects), Input (mouse acceleration), Network (shared
/// <see cref="NetTweakPanel"/>), Startup (startup apps) and Cleanup (cache cleanup). Every apply / undo goes
/// through <see cref="SafeActionUi"/> (async, never blocks the UI); every read runs off the UI thread and the
/// page re-reads after each action.
/// </summary>
public partial class OptimizePage : ShellPageBase
{
    private bool _stale = true;
    private int _refreshGen;
    private PageSnapshot _snap = new();
    private OptimizeSection _section = OptimizeSection.Overview;
    private NetTweakPanel? _netPanel;
    /// <summary>Before/after bookkeeping per journal key; fed by <see cref="RecordMetric"/> (Monitor stage).</summary>
    private readonly Dictionary<string, OptimizationExperiment> _experiments = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Everything the page renders, captured in one background pass.</summary>
    private sealed class PageSnapshot
    {
        public IReadOnlyList<Recommendation> Recs = Array.Empty<Recommendation>();
        public int DismissedCount;
        public bool? GameMode;
        public string? PowerPlan;
        public bool? GameBarOn;
        public bool? MouseAccelOn;
        public IReadOnlyList<GameFsoStatus> Fso = Array.Empty<GameFsoStatus>();
        public OptimizeInfoItem? PowerPerGame;
        public IReadOnlyList<SafeActionJournalEntry> Journal = Array.Empty<SafeActionJournalEntry>();
        public IReadOnlyList<SafeActionJournalEntry> PerGameJournal = Array.Empty<SafeActionJournalEntry>();
        public bool SessionActive;
        public HagsStatus? Hags;
        public string? UltimateGuid;
        public bool? UltimateActive;
        /// <summary>The Ultimate scheme Visor created, when still installed (only that one can be removed in-app).</summary>
        public string? UltimateCreatedGuid;
        public IReadOnlyList<StartupEntry>? Startup;
        public CleanupScanResult? CleanupScan;
        public DeliveryOptimizationStatus? DoCache;
        /// <summary>Visual effects: true = some effect still on, false = performance set, null = unreadable.</summary>
        public bool? VisualFxOn;
        public IReadOnlyList<string>? VisualFxEffects;
        public bool Loaded;
    }

    public OptimizePage(IShellHost host) : base(host)
    {
        InitializeComponent();
        SafeActionUi.Completed += OnSafeActionCompleted;
    }

    public override ShellSection Section => ShellSection.Optimize;
    public override string TitleKey => "nav.optimize";

    private static SafeActionService Safe => App.Services.SafeActions;

    public override void ApplyLocalization()
    {
        RbOptOverview.Content = Loc.T("optimize.sec.overview");
        RbOptSystem.Content = Loc.T("optimize.sec.system");
        RbOptGraphics.Content = Loc.T("optimize.sec.graphics");
        RbOptInput.Content = Loc.T("optimize.sec.input");
        RbOptNetwork.Content = Loc.T("optimize.sec.network");
        RbOptStartup.Content = Loc.T("optimize.sec.startup");
        RbOptCleanup.Content = Loc.T("optimize.sec.cleanup");

        LblOptSummary.Text = Loc.T("optimize.ui.summary");
        TxtOptSummaryHint.Text = Loc.T("optimize.ui.summary.hint");
        BtnOptimizeRefresh.Content = Loc.T("system.refresh");
        LblSumSuggestions.Text = Loc.T("rec.header");
        LblSumApplied.Text = Loc.T("optimize.ui.summary.applied");
        LblSumLast.Text = Loc.T("optimize.ui.summary.last");
        TxtOptLoading.Text = Loc.T("system.loading");
        LblRec.Text = Loc.T("rec.header");
        TxtRecHint.Text = Loc.T("rec.hint");
        TxtRecEmpty.Text = Loc.T("rec.empty");
        BtnRecReset.Content = Loc.T("rec.reset");
        LblJournal.Text = Loc.T("optimize.ui.journal");
        TxtJournalHint.Text = Loc.T("optimize.ui.journal.hint");
        TxtJournalEmpty.Text = Loc.T("optimize.ui.journal.empty");

        LblSystem.Text = Loc.T("system.safe");
        TxtSystemHint.Text = Loc.T("system.safe.hint");
        LblSystemGpu.Text = Loc.T("optimize.ui.system.admin");
        TxtSystemGpuHint.Text = Loc.T("optimize.ui.system.admin.hint");
        LblInfo.Text = Loc.T("optimize.ui.info");
        TxtInfoHint.Text = Loc.T("optimize.ui.info.hint");

        LblPerGame.Text = Loc.T("optimize.ui.perGame");
        TxtPerGameHint.Text = Loc.T("optimize.ui.perGame.hint");
        TxtPerGameEmpty.Text = Loc.T("optimize.ui.perGame.empty");
        LblGfxHags.Text = Loc.T("optimize.hagsMode.title");
        LblGfxInfo.Text = Loc.T("optimize.ui.gfx.desktop");

        LblInput.Text = Loc.T("optimize.ui.input");
        TxtInputHint.Text = Loc.T("optimize.ui.input.hint");
        TxtInputMore.Text = Loc.T("optimize.ui.input.more");

        LblNetDnsLink.Text = Loc.T("optimize.ui.net.dns");
        TxtNetDnsLink.Text = Loc.T("optimize.ui.net.dns.hint");
        BtnOpenDns.Content = Loc.T("optimize.ui.net.dns.open");
        _netPanel?.Relabel();

        LblStartup.Text = Loc.T("optimize.startup.title");
        BtnStartupRefresh.Content = Loc.T("system.refresh");
        TxtStartupWhy.Text = Loc.T("optimize.startup.why");
        TxtStartupNote.Text = Loc.T("optimize.startup.impactNote");
        StartupDetails.SetTexts(null, Loc.T("optimize.startup.risk"), Loc.T("optimize.startup.undo"));
        StartupDetails.Relabel();
        RbStartupAll.Content = Loc.T("optimize.startup.filter.all");
        RbStartupEnabled.Content = Loc.T("optimize.startup.filter.enabled");
        RbStartupDisabled.Content = Loc.T("optimize.startup.filter.disabled");

        LblCleanup.Text = Loc.T("optimize.cleanup.title");
        BtnCleanupScan.Content = Loc.T("optimize.cleanup.scan");
        TxtCleanupWhy.Text = Loc.T("optimize.cleanup.why");
        CleanupDetails.SetTexts(null, Loc.T("optimize.cleanup.risk"), Loc.T("optimize.cleanup.undo"));
        CleanupDetails.Relabel();

        if (_snap.Loaded) RenderAll();
        Refresh();
    }

    public override void OnActivated() => Refresh();

    public override void OnDeactivated() => CancelCleanupScan();

    public override void RefreshIfStale()
    {
        if (_stale) Refresh();
    }

    public override void ShowSubSection(string key)
    {
        if (Enum.TryParse<OptimizeSection>(key, ignoreCase: true, out var s))
            ShowSection(s);
    }

    /// <summary>Selects a sub-section (strip + panel); renders it and refreshes when the page is live.</summary>
    public void ShowSection(OptimizeSection section)
    {
        var rb = RadioFor(section);
        if (rb.IsChecked != true)
            rb.IsChecked = true; // → OptSection_Checked
        else
            ApplySection(section);
    }

    private System.Windows.Controls.RadioButton RadioFor(OptimizeSection s) => s switch
    {
        OptimizeSection.System => RbOptSystem,
        OptimizeSection.Graphics => RbOptGraphics,
        OptimizeSection.Input => RbOptInput,
        OptimizeSection.Network => RbOptNetwork,
        OptimizeSection.Startup => RbOptStartup,
        OptimizeSection.Cleanup => RbOptCleanup,
        _ => RbOptOverview
    };

    private void OptSection_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelOverview == null || sender is not System.Windows.Controls.RadioButton rb) return;
        var section = rb == RbOptSystem ? OptimizeSection.System
            : rb == RbOptGraphics ? OptimizeSection.Graphics
            : rb == RbOptInput ? OptimizeSection.Input
            : rb == RbOptNetwork ? OptimizeSection.Network
            : rb == RbOptStartup ? OptimizeSection.Startup
            : rb == RbOptCleanup ? OptimizeSection.Cleanup
            : OptimizeSection.Overview;
        ApplySection(section);
    }

    private void ApplySection(OptimizeSection section)
    {
        bool changed = _section != section;
        _section = section;
        PanelOverview.Visibility = Vis(section == OptimizeSection.Overview);
        PanelSystem.Visibility = Vis(section == OptimizeSection.System);
        PanelGraphics.Visibility = Vis(section == OptimizeSection.Graphics);
        PanelInput.Visibility = Vis(section == OptimizeSection.Input);
        PanelNetwork.Visibility = Vis(section == OptimizeSection.Network);
        PanelStartup.Visibility = Vis(section == OptimizeSection.Startup);
        PanelCleanup.Visibility = Vis(section == OptimizeSection.Cleanup);
        if (changed) OptScroll.ScrollToTop();

        if (section == OptimizeSection.Network && IsLive)
        {
            EnsureNetPanel();
            _ = _netPanel!.RefreshAsync();
        }
        if (section == OptimizeSection.Cleanup && IsLive && _snap.Loaded && _cleanupScan == null && _cleanupCts == null)
            _ = ScanCleanupAsync();
    }

    private static Visibility Vis(bool v) => v ? Visibility.Visible : Visibility.Collapsed;

    private void EnsureNetPanel()
    {
        if (_netPanel != null) return;
        _netPanel = new NetTweakPanel(Host);
        NetTweakHost.Children.Add(_netPanel);
    }

    private void OpenDns_Click(object sender, RoutedEventArgs e) => Host.Navigate(ShellSection.Network, "dns");

    /// <summary>Monitoring / config changed elsewhere — rebuild when visible, else mark stale.</summary>
    public void Refresh()
    {
        if (TxtOptLoading == null) return;
        if (!IsLive)
        {
            _stale = true;
            return;
        }
        _stale = false;
        _ = RefreshAsync();
        if (_section == OptimizeSection.Network)
        {
            EnsureNetPanel();
            _ = _netPanel!.RefreshAsync();
        }
    }

    private void OptimizeRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>A SafeAction finished anywhere (this page, Network page, a toast's Undo): experiments + staleness.</summary>
    private void OnSafeActionCompleted(string journalKey, bool applied)
    {
        if (applied) MarkExperimentApplied(journalKey);
        else _experiments.Remove(journalKey);
        if (!IsLive) _stale = true;
    }

    // -- Snapshot (background) ---------------------------------------------------------------------------

    /// <summary>Re-reads everything off the UI thread and re-renders (newest call wins).</summary>
    private async Task RefreshAsync()
    {
        int gen = ++_refreshGen;
        var profiles = Host.Profiles.ToList();
        var dismissed = (UiStateStore.Current.DismissedRecommendations ?? new List<string>()).ToList();
        var cleanup = _cleanupScan;
        var doCache = _doStatus;
        TxtOptLoading.Visibility = Visibility.Visible;
        BtnOptimizeRefresh.IsEnabled = false;
        BtnStartupRefresh.IsEnabled = false;

        PageSnapshot snap;
        try
        {
            snap = await Task.Run(() => CaptureSnapshot(profiles, dismissed, cleanup, doCache));
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Optimize refresh");
            snap = new PageSnapshot { CleanupScan = cleanup, DoCache = doCache, Loaded = true };
        }
        if (gen != _refreshGen || !Host.Window.IsLoaded) return; // a newer refresh is in flight
        TxtOptLoading.Visibility = Visibility.Collapsed;
        BtnOptimizeRefresh.IsEnabled = true;
        BtnStartupRefresh.IsEnabled = true;
        _snap = snap;
        _cleanupScan ??= snap.CleanupScan;
        _doStatus ??= snap.DoCache;
        try
        {
            RenderAll();
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Optimize render");
        }
    }

    /// <summary>Refresh now and wait for it (used after an action so the row shows the verified state).</summary>
    private Task RefreshNowAsync()
    {
        if (!IsLive)
        {
            _stale = true;
            return Task.CompletedTask;
        }
        _stale = false;
        return RefreshAsync();
    }

    private static PageSnapshot CaptureSnapshot(List<GameProfile> profiles, List<string> dismissed, CleanupScanResult? cleanup,
        DeliveryOptimizationStatus? doCache)
    {
        var safe = Safe;
        var pc = App.Services.Pc;
        var s = new PageSnapshot
        {
            Loaded = true,
            SessionActive = App.Services.Monitor.SnapshotActive,
            GameMode = GameModeSetting.Read(),
            PowerPlan = PowerPlanSafeAction.ReadAliasOrGuid(),
            GameBarOn = GameBarSafeActionTarget.ReadBackgroundOn(),
            MouseAccelOn = MouseAccelerationSafeActionTarget.ReadAccelerationOn(),
            Fso = OptimizeStatus.FsoStatuses(profiles),
            PowerPerGame = OptimizeStatus.PowerPerGameItem(profiles),
            DismissedCount = RecommendationDismissals.Parse(dismissed, DateTime.UtcNow).Count,
            Hags = HagsStatusReader.Read(),
            UltimateGuid = pc.Ultimate.FindInstalled(),
            CleanupScan = cleanup,
            DoCache = doCache,
            VisualFxEffects = VisualEffectsSafeActionTarget.ReadEffectsOnList()
        };
        s.VisualFxOn = s.VisualFxEffects == null ? null : s.VisualFxEffects.Count > 0;
        s.UltimateActive = s.UltimateGuid == null ? false : pc.Ultimate.IsActive();
        s.UltimateCreatedGuid = pc.Ultimate.CreatedInstalledGuid();
        try
        {
            var apps = pc.Startup.List();
            pc.Startup.Register(safe, apps); // per-entry enable / disable targets (no system change)
            s.Startup = apps;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Startup apps list");
        }
        // The cache scan only measures. First visit measures once; later scans run on the Scan click / after a clean.
        if (s.CleanupScan == null)
        {
            try { s.CleanupScan = pc.Cleanup.Scan(); }
            catch (Exception ex) { AppLog.Warn(ex, "Cleanup scan"); }
        }
        s.DoCache ??= pc.DeliveryOptimization.Read(); // WMI, no admin; measured with the first scan, then on Scan / after a clean

        var ctx = BuildContext(profiles, dismissed, s.GameMode, s.GameBarOn, s.MouseAccelOn, s.Fso,
            s.Startup, s.CleanupScan, pc.Cleanup.PrimaryDrive(), s.Hags);
        s.Recs = RecommendationEngine.Evaluate(ctx);
        s.Journal = safe.Journal.Entries.OrderByDescending(e => e.Utc).ToList();
        s.PerGameJournal = safe.PerGameEntries();
        return s;
    }

    private void RenderAll()
    {
        RenderSummary();
        RenderRecommendations();
        RenderJournal();
        RenderSystem();
        RenderGraphics();
        RenderInput();
        RenderStartup();
        RenderCleanup();
    }

    // -- Recommendations: shared evaluation ------------------------------------------------------------------

    /// <summary>Pure evaluation shared with the Home "Suggestions" card (synchronous: a few HKCU reads + cached adapter).</summary>
    public static IReadOnlyList<Recommendation> Evaluate(IEnumerable<GameProfile> profiles)
    {
        var list = profiles.ToList();
        var dismissed = UiStateStore.Current.DismissedRecommendations ?? new List<string>();
        var ctx = BuildContext(list, dismissed,
            GameModeSetting.Read(),
            GameBarSafeActionTarget.ReadBackgroundOn(),
            MouseAccelerationSafeActionTarget.ReadAccelerationOn(),
            OptimizeStatus.FsoStatuses(list));
        return RecommendationEngine.Evaluate(ctx);
    }

    /// <summary>Measured context: Visor state, network, HKCU facts, hardware services (null = not measured yet).</summary>
    private static RecommendationContext BuildContext(IReadOnlyList<GameProfile> profiles, IEnumerable<string> dismissed,
        bool? gameMode, bool? gameBarOn, bool? mouseAccelOn, IReadOnlyList<GameFsoStatus> fso,
        IReadOnlyList<StartupEntry>? startupApps = null, CleanupScanResult? cleanupScan = null, string? cleanupDrive = null,
        HagsStatus? hags = null)
    {
        var net = NetworkInfoService.CapturePrimary();
        var services = App.Services;
        return new RecommendationContext
        {
            MonitoringPaused = services.Monitor.IsPaused,
            ProfileCount = profiles.Count,
            GameModeEnabled = gameMode,
            HasUsableNetworkAdapter = net != null,
            HasUsableGateway = !string.IsNullOrEmpty(net?.Gateway),
            DismissedIds = new HashSet<string>(dismissed, StringComparer.OrdinalIgnoreCase),
            Inventory = HardwareInventoryService.Cached,
            Sensors = services.Sensors.Latest,
            Health = services.Health,
            Crashes = services.CrashSummary,
            GameRunning = services.Monitor.SnapshotActive,
            GameBarBackgroundOn = gameBarOn,
            MouseAccelerationOn = mouseAccelOn,
            FsoStatuses = fso,
            StartupApps = startupApps,
            CleanupScan = cleanupScan,
            CleanupDrive = cleanupDrive,
            Hags = hags
        };
    }

    /// <summary>Startup housekeeping: drop expired / legacy dismissal tokens (saved only when something changed).</summary>
    public static void PruneDismissedRecommendations()
    {
        try
        {
            var st = UiStateStore.Current;
            st.DismissedRecommendations ??= new List<string>();
            if (RecommendationDismissals.Prune(st.DismissedRecommendations, DateTime.UtcNow))
                UiStateStore.Save();
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Prune dismissed recommendations");
        }
    }

    public static string FormatRecommendationBody(Recommendation rec)
    {
        if (rec.Id == RecommendationEngine.IdConflicts && rec.BodyArgs.Length >= 2)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(rec.BodyArgs[0]))
                parts.Add(Loc.Tf("rec.conflicts.apps", rec.BodyArgs[0]));
            if (rec.BodyArgs[1] == "1")
                parts.Add(Loc.T("rec.conflicts.nightLight"));
            return parts.Count > 0
                ? Loc.Tf("rec.conflicts.body.detail", string.Join(" · ", parts))
                : Loc.T("rec.conflicts.body");
        }
        return rec.BodyArgs.Length > 0
            ? Loc.Tf(rec.BodyKey, rec.BodyArgs.Cast<object>().ToArray())
            : Loc.T(rec.BodyKey);
    }

    // -- Summary ---------------------------------------------------------------------------------------------

    private void RenderSummary()
    {
        TxtSumSuggestions.Text = _snap.Recs.Count == 0 ? Loc.T("rec.empty") : Loc.Plural("unit.suggestions", _snap.Recs.Count);
        TxtSumApplied.Text = _snap.Journal.Count.ToString(CultureInfo.CurrentCulture);
        var last = _snap.Journal.FirstOrDefault();
        TxtSumLast.Text = last == null
            ? Loc.T("optimize.ui.summary.none")
            : last.Utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }

    // -- Suggestions -----------------------------------------------------------------------------------------

    private void RenderRecommendations()
    {
        RecList.Children.Clear();
        TxtRecEmpty.Visibility = _snap.Recs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var rec in _snap.Recs)
            RecList.Children.Add(BuildRecommendationRow(rec));
        BtnRecReset.Visibility = _snap.DismissedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RecReset_Click(object sender, RoutedEventArgs e)
    {
        var st = UiStateStore.Current;
        st.DismissedRecommendations ??= new List<string>();
        RecommendationEngine.ResetDismissed(st.DismissedRecommendations);
        UiStateStore.Save();
        Refresh();
    }

    private FrameworkElement BuildRecommendationRow(Recommendation rec)
    {
        var card = NewRowCard();
        var root = new DockPanel();

        var actions = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        DockPanel.SetDock(actions, Dock.Right);

        var dismiss = new WpfButton
        {
            Content = Loc.T("rec.dismiss"),
            Tag = rec,
            Margin = new Thickness(4, 0, 0, 0)
        };
        dismiss.SetResourceReference(StyleProperty, "CompactGhostButton");
        dismiss.Click += RecDismiss_Click;
        actions.Children.Add(dismiss);

        bool needsAdmin = rec.SafeActionId != null && SafeActionUi.NeedsElevation(rec.SafeActionId);
        if (rec.Action != RecommendationAction.None && !string.IsNullOrEmpty(rec.ActionKey))
        {
            var apply = new WpfButton { Content = Loc.T(rec.ActionKey), Tag = rec };
            apply.SetResourceReference(StyleProperty, "CompactAccentButton");
            if (rec.SafeActionId != null && IsBlocked(rec.SafeActionId))
            {
                apply.IsEnabled = false;
                apply.ToolTip = Loc.T("safeAction.code.sessionActive");
                ToolTipService.SetShowOnDisabled(apply, true);
            }
            apply.Click += RecApply_Click;
            actions.Children.Insert(0, apply);
        }
        root.Children.Add(actions);

        var textCol = new StackPanel();
        // DockPanel, not a horizontal StackPanel: a StackPanel measures the title with infinite width, so it never
        // wrapped and long (Russian) titles ran under the action buttons. Badge pinned to the first text line.
        var titleRow = new DockPanel();
        var badge = TweakRow.Badge(Loc.T(rec.SeverityKey),
            rec.Severity == RecommendationSeverity.Warning ? "WarningBrush" : "BorderBrush",
            rec.Severity == RecommendationSeverity.Warning ? "WarningBrush" : "MutedBrush");
        badge.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(badge, Dock.Left);
        titleRow.Children.Add(badge);
        var title = new TextBlock
        {
            Text = Loc.T(rec.TitleKey),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        titleRow.Children.Add(title);
        textCol.Children.Add(titleRow);
        textCol.Children.Add(TweakRow.Hint(FormatRecommendationBody(rec), new Thickness(0, 4, 0, 0)));
        if (needsAdmin)
            textCol.Children.Add(TweakRow.Hint(Loc.T("net.tweak.status.uacHint"), new Thickness(0, 4, 0, 0)));
        if (rec.Evidence.Length > 0)
            textCol.Children.Add(NewChips(rec.Evidence));
        if (rec.WhyKey != null || rec.RiskKey != null || rec.UndoKey != null)
        {
            textCol.Children.Add(NewDetails(
                rec.WhyKey == null ? null : Loc.T(rec.WhyKey),
                rec.RiskKey == null ? null : Loc.T(rec.RiskKey),
                rec.UndoKey == null ? null : Loc.T(rec.UndoKey)));
        }
        root.Children.Add(textCol);
        card.Child = root;
        return card;
    }

    private void RecDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: Recommendation rec }) return;
        var st = UiStateStore.Current;
        st.DismissedRecommendations ??= new List<string>();
        // Dismissal is per occurrence (id + context hash) and expires — see RecommendationDismissals.
        RecommendationDismissals.Add(st.DismissedRecommendations, rec, DateTime.UtcNow);
        UiStateStore.Save();
        Refresh();
    }

    private async void RecApply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: Recommendation rec } button) return;
        button.IsEnabled = false;
        try
        {
            switch (rec.Action)
            {
                case RecommendationAction.ResumeMonitoring:
                    if (App.Services.Monitor.IsPaused)
                        Host.TogglePauseMonitoring();
                    Refresh();
                    break;
                case RecommendationAction.SafeActionEnableGameMode:
                    await EnableGameModeFromRecommendationAsync();
                    break;
                case RecommendationAction.SafeActionHighPerfPower:
                    await PowerHighPerfAsync();
                    break;
                case RecommendationAction.SafeActionGameBarOff:
                case RecommendationAction.SafeActionMouseAccelOff:
                case RecommendationAction.SafeActionFsoForGame:
                    if (rec.SafeActionId != null)
                        await ApplyAndRefreshAsync(rec.SafeActionId, new SafeApplyOptions { WhyKey = rec.WhyKey, RiskKey = rec.RiskKey });
                    break;
                case RecommendationAction.SafeActionHagsOn:
                    await ApplyAndRefreshAsync(rec.SafeActionId ?? HagsTarget.OnId, new SafeApplyOptions
                    {
                        WhyKey = rec.WhyKey ?? "optimize.hagsMode.why",
                        RiskKey = rec.RiskKey ?? "optimize.hagsMode.risk",
                        AfterNote = Loc.T("optimize.hagsMode.pendingRestart")
                    });
                    break;
                case RecommendationAction.OpenStartupApps:
                    ShowSection(OptimizeSection.Startup);
                    break;
                case RecommendationAction.OpenCleanup:
                    ShowSection(OptimizeSection.Cleanup);
                    break;
                case RecommendationAction.OpenSystem:
                    Host.Navigate(ShellSection.MyPc);
                    break;
                case RecommendationAction.OpenNetwork:
                    Host.Navigate(ShellSection.Network);
                    break;
                case RecommendationAction.OpenGames:
                    Host.Navigate(ShellSection.Games);
                    break;
            }
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>True when the target refuses to apply right now (a game session owns the setting).</summary>
    private bool IsBlocked(string actionId) =>
        _snap.SessionActive && Safe.FindTarget(actionId)?.BlockedDuringGameSession == true;

    // -- Journal ---------------------------------------------------------------------------------------------

    private void RenderJournal()
    {
        JournalList.Children.Clear();
        foreach (var e in _snap.Journal)
        {
            var card = NewRowCard();
            var root = new DockPanel();
            string key = e.Key;
            string title = SafeActionUi.TitleFor(e.ActionId);
            bool admin = SafeActionUi.NeedsElevation(e.ActionId);
            var undoBtn = AsyncButton(Loc.T("system.rollback"), "CompactGhostButton", title,
                () => UndoAndRefreshAsync(key));
            undoBtn.Margin = new Thickness(8, 0, 0, 0);
            undoBtn.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(undoBtn, Dock.Right);
            root.Children.Add(undoBtn);

            var text = new StackPanel();
            var titleRow = new DockPanel();
            if (admin)
            {
                var badge = TweakRow.Badge(Loc.T("optimize.ui.adminBadge"), "WarningBrush", "WarningBrush");
                badge.ToolTip = Loc.T("net.tweak.status.uacHint");
                badge.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(badge, Dock.Left);
                titleRow.Children.Add(badge);
            }
            var titleTb = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap };
            titleTb.SetResourceReference(StyleProperty, "CardTitle");
            titleRow.Children.Add(titleTb);
            text.Children.Add(titleRow);
            text.Children.Add(TweakRow.Hint(e.Utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), new Thickness(0, 2, 0, 0)));
            var exp = ExperimentLine(e.Key);
            if (exp != null) text.Children.Add(TweakRow.Hint(exp, new Thickness(0, 2, 0, 0)));
            root.Children.Add(text);
            card.Child = root;
            JournalList.Children.Add(card);
        }
        TxtJournalEmpty.Visibility = _snap.Journal.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // -- Experiments (before / after) ------------------------------------------------------------------------

    /// <summary>
    /// Feeds a measured metric ("fps", "frametimeMs") into every open experiment. Intended for the Monitor
    /// stage; until it calls this, journal rows simply show no before/after line.
    /// </summary>
    public void RecordMetric(string name, double value)
    {
        var utc = DateTime.UtcNow;
        foreach (var e in _experiments.Values)
            e.Record(name, value, utc);
    }

    private void MarkExperimentApplied(string journalKey)
    {
        if (!_experiments.TryGetValue(journalKey, out var exp) || exp.Phase == ExperimentPhase.After)
            _experiments[journalKey] = exp = new OptimizationExperiment(journalKey);
        exp.MarkApplied(DateTime.UtcNow);
    }

    private string? ExperimentLine(string journalKey)
    {
        if (!_experiments.TryGetValue(journalKey, out var exp)) return null;
        if (exp.BeforeCount + exp.AfterCount == 0) return null;
        var report = exp.Compute();
        if (report.InsufficientData)
            return Loc.Tf("optimize.ui.exp.insufficient", exp.BeforeCount, exp.AfterCount, exp.MinSamples);
        var parts = report.Metrics.Where(m => !m.InsufficientData && m.Delta != null)
            .Select(m => Loc.Tf("optimize.ui.exp.delta", m.Name,
                m.BeforeMean!.Value.ToString("0.#", CultureInfo.CurrentCulture),
                m.AfterMean!.Value.ToString("0.#", CultureInfo.CurrentCulture),
                m.DeltaPercent?.ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture) ?? "—"));
        return string.Join(" · ", parts);
    }

    // -- Apply / undo glue -----------------------------------------------------------------------------------

    /// <summary>Apply through the shared flow, then re-read the page (the busy row stays busy until then).</summary>
    private async Task ApplyAndRefreshAsync(string actionId, SafeApplyOptions? options = null)
    {
        await SafeActionUi.ApplyAsync(Host, actionId, options);
        await RefreshNowAsync();
    }

    private async Task UndoAndRefreshAsync(string actionIdOrKey, string? historyDetail = null)
    {
        await SafeActionUi.UndoAsync(Host, actionIdOrKey, historyDetail);
        await RefreshNowAsync();
    }

    // -- Small element factories -----------------------------------------------------------------------------

    /// <summary>Button whose async click disables it until the work (and the re-read) is done.</summary>
    private static WpfButton AsyncButton(string text, string styleKey, string? subject, Func<Task> run, bool enabled = true)
    {
        var b = new WpfButton { Content = text, IsEnabled = enabled };
        b.SetResourceReference(StyleProperty, styleKey);
        if (!string.IsNullOrEmpty(subject))
            System.Windows.Automation.AutomationProperties.SetName(b, text + " · " + subject);
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await run(); }
            catch (Exception ex) { AppLog.Warn(ex, "Optimize action"); }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    private static Border NewRowCard()
    {
        var card = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(8)
        };
        card.SetResourceReference(Border.BackgroundProperty, "FieldBrush");
        return card;
    }

    /// <summary>Evidence chips: raw measured facts, never localized, never invented.</summary>
    private static WrapPanel NewChips(IEnumerable<string> items)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var item in items)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 6, 4),
                BorderThickness = new Thickness(1)
            };
            chip.SetResourceReference(Border.BackgroundProperty, "PillBgBrush");
            chip.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            var tb = new TextBlock { Text = item, FontSize = 10 };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            chip.Child = tb;
            wrap.Children.Add(chip);
        }
        return wrap;
    }

    private static OptDetailsPanel NewDetails(string? why, string? risk, string? undo)
    {
        var p = new OptDetailsPanel();
        p.SetTexts(why, risk, undo);
        return p;
    }
}
