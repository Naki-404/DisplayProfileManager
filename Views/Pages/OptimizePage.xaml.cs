using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Visor.Controls;
using Visor.Models;
using Visor.Services;
using Visor.Services.Optimize;
using WpfButton = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace Visor;

/// <summary>
/// Optimize section: suggestions (RecommendationEngine over the full measured context), SafeAction cards
/// (Game Mode, power plan, Game Bar background, mouse acceleration), per-game fullscreen-optimization
/// rows, read-only info cards and the undo journal. Registry / WMI reads run off the UI thread.
/// </summary>
public partial class OptimizePage : ShellPageBase
{
    private SafeActionService? _safeActions;
    private bool _stale = true;
    private int _refreshGen;
    private PageSnapshot _snap = new();
    private readonly List<OptDetailsPanel> _detailPanels = new();
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
        public IReadOnlyList<OptimizeInfoItem> Info = Array.Empty<OptimizeInfoItem>();
        public IReadOnlyList<SafeActionJournalEntry> Journal = Array.Empty<SafeActionJournalEntry>();
        public IReadOnlyList<SafeActionJournalEntry> PerGameJournal = Array.Empty<SafeActionJournalEntry>();
        public bool SessionActive;
    }

    public OptimizePage(IShellHost host) : base(host)
    {
        InitializeComponent();
    }

    public override ShellSection Section => ShellSection.Optimize;
    public override string TitleKey => "nav.optimize";

    /// <summary>Journaled, reversible Windows tweaks. Created on first use (UI thread).</summary>
    private SafeActionService? SafeActions
    {
        get
        {
            if (_safeActions != null) return _safeActions;
            try
            {
                _safeActions = SafeActionService.CreateDefault(
                    App.Services.Config.ConfigDirectory,
                    p => App.Services.Display.SetPowerPlan(p),
                    () => App.Services.Monitor.SnapshotActive);
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "SafeAction service");
            }
            return _safeActions;
        }
    }

    public override void ApplyLocalization()
    {
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
        LblActions.Text = Loc.T("system.safe");
        TxtActionsHint.Text = Loc.T("system.safe.hint");
        LblPerGame.Text = Loc.T("optimize.ui.perGame");
        TxtPerGameHint.Text = Loc.T("optimize.ui.perGame.hint");
        TxtPerGameEmpty.Text = Loc.T("optimize.ui.perGame.empty");
        LblInfo.Text = Loc.T("optimize.ui.info");
        TxtInfoHint.Text = Loc.T("optimize.ui.info.hint");
        LblJournal.Text = Loc.T("optimize.ui.journal");
        TxtJournalHint.Text = Loc.T("optimize.ui.journal.hint");
        TxtJournalEmpty.Text = Loc.T("optimize.ui.journal.empty");
        Refresh();
    }

    public override void OnActivated() => Refresh();

    public override void RefreshIfStale()
    {
        if (_stale) Refresh();
    }

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
    }

    private void OptimizeRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private bool WindowAlive => Host.Window.IsLoaded;

    // -- Snapshot (background) ---------------------------------------------------------------------------

    private async Task RefreshAsync()
    {
        int gen = ++_refreshGen;
        var safe = SafeActions;
        var profiles = Host.Profiles.ToList();
        var dismissed = (UiStateStore.Current.DismissedRecommendations ?? new List<string>()).ToList();
        TxtOptLoading.Visibility = Visibility.Visible;
        BtnOptimizeRefresh.IsEnabled = false;

        PageSnapshot snap;
        try
        {
            snap = await Task.Run(() => CaptureSnapshot(profiles, dismissed, safe)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Optimize refresh");
            snap = new PageSnapshot();
        }
        if (gen != _refreshGen) return; // a newer refresh is in flight
        TxtOptLoading.Visibility = Visibility.Collapsed;
        BtnOptimizeRefresh.IsEnabled = true;
        _snap = snap;
        try
        {
            RenderAll();
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Optimize render");
        }
    }

    private static PageSnapshot CaptureSnapshot(List<GameProfile> profiles, List<string> dismissed, SafeActionService? safe)
    {
        var s = new PageSnapshot
        {
            SessionActive = App.Services.Monitor.SnapshotActive,
            GameMode = GameModeSetting.Read(),
            PowerPlan = PowerPlanSafeAction.ReadAliasOrGuid(),
            GameBarOn = GameBarSafeActionTarget.ReadBackgroundOn(),
            MouseAccelOn = MouseAccelerationSafeActionTarget.ReadAccelerationOn(),
            Fso = OptimizeStatus.FsoStatuses(profiles),
            Info = OptimizeStatus.InfoItems(profiles),
            DismissedCount = RecommendationDismissals.Parse(dismissed, DateTime.UtcNow).Count
        };
        s.Recs = RecommendationEngine.Evaluate(BuildContext(profiles, dismissed, s.GameMode, s.GameBarOn, s.MouseAccelOn, s.Fso));
        if (safe != null)
        {
            s.Journal = safe.Journal.Entries.OrderByDescending(e => e.Utc).ToList();
            s.PerGameJournal = safe.PerGameEntries();
        }
        return s;
    }

    private void RenderAll()
    {
        _detailPanels.Clear();
        RenderSummary();
        RenderRecommendations();
        RenderActions();
        RenderPerGame();
        RenderInfo();
        RenderJournal();
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

    /// <summary>Full measured context: Visor state, network, HKCU facts, hardware singletons when present.</summary>
    private static RecommendationContext BuildContext(IReadOnlyList<GameProfile> profiles, IEnumerable<string> dismissed,
        bool? gameMode, bool? gameBarOn, bool? mouseAccelOn, IReadOnlyList<GameFsoStatus> fso)
    {
        var net = NetworkInfoService.CapturePrimary();
        // The hardware singletons (App.Services.Sensors / CrashSummary / Health) are added by the My PC stage;
        // they are looked up by name so this page builds before and after that lands and never invents data.
        var sensors = ServiceMember<SensorSnapshot>("Sensors");
        return new RecommendationContext
        {
            MonitoringPaused = App.Services.Monitor.IsPaused,
            ProfileCount = profiles.Count,
            GameModeEnabled = gameMode,
            HasUsableNetworkAdapter = net != null,
            HasUsableGateway = !string.IsNullOrEmpty(net?.Gateway),
            DismissedIds = new HashSet<string>(dismissed, StringComparer.OrdinalIgnoreCase),
            Inventory = HardwareInventoryService.Cached,
            Sensors = sensors,
            Health = ServiceMember<HealthReport>("Health"),
            Crashes = ServiceMember<CrashEventSummary>("CrashSummary") ?? ServiceMember<CrashEventSummary>("Crashes"),
            GameRunning = App.Services.Monitor.SnapshotActive,
            GameBarBackgroundOn = gameBarOn,
            MouseAccelerationOn = mouseAccelOn,
            FsoStatuses = fso
        };
    }

    /// <summary>
    /// Null-tolerant lookup of an <see cref="AppServices"/> member by name: the value itself, or its
    /// Latest / Cached / Current / Report / Summary property when the member is a service wrapper.
    /// Returns null when the member does not exist yet — never throws, never fabricates.
    /// </summary>
    private static T? ServiceMember<T>(string name) where T : class
    {
        try
        {
            var services = App.Services;
            if (services == null) return null;
            var prop = services.GetType().GetProperty(name);
            var value = prop?.GetValue(services);
            if (value == null) return null;
            if (value is T direct) return direct;
            foreach (var inner in new[] { "Latest", "Cached", "Current", "Report", "Summary" })
            {
                var p = value.GetType().GetProperty(inner);
                if (p != null && typeof(T).IsAssignableFrom(p.PropertyType) && p.GetValue(value) is T found)
                    return found;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Recommendation context " + name);
        }
        return null;
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
            Style = (Style)FindResource("CompactGhostButton"),
            Tag = rec,
            Margin = new Thickness(4, 0, 0, 0)
        };
        dismiss.Click += RecDismiss_Click;
        actions.Children.Add(dismiss);

        if (rec.Action != RecommendationAction.None && !string.IsNullOrEmpty(rec.ActionKey))
        {
            var apply = new WpfButton
            {
                Content = Loc.T(rec.ActionKey),
                Style = (Style)FindResource("CompactAccentButton"),
                Tag = rec
            };
            if (rec.SafeActionId != null && IsBlocked(rec.SafeActionId))
            {
                apply.IsEnabled = false;
                apply.ToolTip = Loc.T("safeAction.code.sessionActive");
            }
            apply.Click += RecApply_Click;
            actions.Children.Insert(0, apply);
        }
        root.Children.Add(actions);

        var textCol = new StackPanel();
        var titleRow = new StackPanel { Orientation = WpfOrientation.Horizontal };
        titleRow.Children.Add(NewBadge(Loc.T(rec.SeverityKey),
            rec.Severity == RecommendationSeverity.Warning ? "WarningBrush" : "BorderBrush",
            rec.Severity == RecommendationSeverity.Warning ? "WarningBrush" : "MutedBrush"));
        var title = new TextBlock
        {
            Text = Loc.T(rec.TitleKey),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        titleRow.Children.Add(title);
        textCol.Children.Add(titleRow);
        textCol.Children.Add(NewHint(FormatRecommendationBody(rec), new Thickness(0, 4, 0, 0)));
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

    private void RecApply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: Recommendation rec }) return;
        switch (rec.Action)
        {
            case RecommendationAction.ResumeMonitoring:
                if (App.Services.Monitor.IsPaused)
                    Host.TogglePauseMonitoring();
                break;
            case RecommendationAction.SafeActionEnableGameMode:
                EnableGameModeFromRecommendation();
                return; // the toggle path refreshes itself
            case RecommendationAction.SafeActionHighPerfPower:
                PowerHighPerf_Click(sender, e);
                return;
            case RecommendationAction.SafeActionGameBarOff:
            case RecommendationAction.SafeActionMouseAccelOff:
            case RecommendationAction.SafeActionFsoForGame:
                if (rec.SafeActionId != null)
                    ConfirmAndApply(rec.SafeActionId, rec.WhyKey, rec.RiskKey);
                return;
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
        Refresh();
    }

    // -- Actions (global SafeAction targets) -----------------------------------------------------------------

    private void RenderActions()
    {
        ActionList.Children.Clear();
        var safe = SafeActions;
        bool session = _snap.SessionActive;

        // Game Mode: on/off toggle sharing one undo slot.
        var gm = _snap.GameMode;
        ActionList.Children.Add(BuildActionCard(
            title: Loc.T("optimize.gameMode.title"),
            status: gm == null ? Loc.T("system.unavailable") : Loc.T(gm.Value ? "system.gameMode.on" : "system.gameMode.off"),
            dot: StatusDot.FromBool(gm),
            applyText: Loc.T(gm == true ? "system.gameMode.disable" : "system.gameMode.enable"),
            applyEnabled: gm != null && safe != null,
            applyTooltip: null,
            onApply: GameModeToggle_Click,
            undoEnabled: safe?.CanUndo(GameModeSetting.ActionId) == true,
            onUndo: GameModeRollback_Click,
            why: Loc.T("optimize.gameMode.why"), risk: Loc.T("optimize.gameMode.risk"), undo: Loc.T("optimize.gameMode.undo")));

        // Power plan (desktop): blocked while a game session owns the plan.
        var plan = _snap.PowerPlan;
        ActionList.Children.Add(BuildActionCard(
            title: Loc.T("optimize.powerHighPerf.title"),
            status: plan == null ? Loc.T("system.unavailable") : Loc.Tf("system.power.current", ComboUi.PowerPlanStateLabel(plan)),
            dot: plan == null ? StatusKind.Unknown : RecommendationEngine.IsPerformanceClassPlan(plan) ? StatusKind.Ok : StatusKind.Warn,
            applyText: Loc.T("system.power.highPerf"),
            applyEnabled: !session && safe != null,
            applyTooltip: session ? Loc.T("safeAction.code.sessionActive") : null,
            onApply: PowerHighPerf_Click,
            undoEnabled: safe?.CanUndo(PowerPlanSafeAction.ActionId) == true,
            onUndo: PowerRollback_Click,
            why: Loc.T("optimize.powerHighPerf.why"), risk: Loc.T("optimize.powerHighPerf.risk"), undo: Loc.T("optimize.powerHighPerf.undo")));

        // Xbox Game Bar background features.
        ActionList.Children.Add(BuildGenericActionCard(GameBarSafeActionTarget.Id, _snap.GameBarOn,
            onText: Loc.T("optimize.ui.gameBar.status.on"), offText: Loc.T("optimize.ui.gameBar.status.off"),
            applyText: Loc.T("rec.gamebar.off.action")));

        // Mouse acceleration.
        ActionList.Children.Add(BuildGenericActionCard(MouseAccelerationSafeActionTarget.Id, _snap.MouseAccelOn,
            onText: Loc.T("optimize.ui.mouseAccel.status.on"), offText: Loc.T("optimize.ui.mouseAccel.status.off"),
            applyText: Loc.T("rec.mouse.accel.action")));
    }

    /// <summary>Card for a registry target whose "apply" means "turn the feature off" (true = still on).</summary>
    private FrameworkElement BuildGenericActionCard(string actionId, bool? featureOn, string onText, string offText, string applyText)
    {
        var safe = SafeActions;
        var target = safe?.FindTarget(actionId);
        bool blocked = IsBlocked(actionId);
        return BuildActionCard(
            title: Loc.T(target?.TitleKey ?? "optimize." + actionId + ".title"),
            status: featureOn == null ? Loc.T("system.unavailable") : featureOn.Value ? onText : offText,
            dot: featureOn == null ? StatusKind.Unknown : featureOn.Value ? StatusKind.Warn : StatusKind.Ok,
            applyText: applyText,
            applyEnabled: featureOn == true && safe != null && !blocked,
            applyTooltip: blocked ? Loc.T("safeAction.code.sessionActive") : null,
            onApply: (s, _) => ConfirmAndApply(actionId, target?.WhyKey, target?.RiskKey),
            undoEnabled: safe?.CanUndo(actionId) == true,
            onUndo: (s, _) => UndoSafeAction(actionId, historyDetail: actionId + ".undo"),
            why: target == null ? null : Loc.T(target.WhyKey),
            risk: target == null ? null : Loc.T(target.RiskKey),
            undo: target == null ? null : Loc.T(target.UndoKey));
    }

    private FrameworkElement BuildActionCard(string title, string status, StatusKind dot, string applyText, bool applyEnabled,
        string? applyTooltip, RoutedEventHandler onApply, bool undoEnabled, RoutedEventHandler onUndo,
        string? why, string? risk, string? undo)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dotCtl = new StatusDot { Status = dot, Label = title, Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        grid.Children.Add(dotCtl);

        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var titleTb = new TextBlock { Text = title };
        titleTb.SetResourceReference(StyleProperty, "CardTitle");
        text.Children.Add(titleTb);
        text.Children.Add(NewHint(status, new Thickness(0, 2, 0, 0)));
        text.Children.Add(NewDetails(why, risk, undo));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var buttons = new StackPanel { Orientation = WpfOrientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var apply = new WpfButton
        {
            Content = applyText,
            Style = (Style)FindResource("CompactAccentButton"),
            Margin = new Thickness(0, 0, 6, 0),
            IsEnabled = applyEnabled,
            ToolTip = applyTooltip
        };
        ToolTipService.SetShowOnDisabled(apply, true);
        apply.Click += onApply;
        buttons.Children.Add(apply);
        var undoBtn = new WpfButton
        {
            Content = Loc.T("system.rollback"),
            Style = (Style)FindResource("CompactGhostButton"),
            IsEnabled = undoEnabled
        };
        undoBtn.Click += onUndo;
        buttons.Children.Add(undoBtn);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        return grid;
    }

    /// <summary>True when the target refuses to apply right now (a game session owns the setting).</summary>
    private bool IsBlocked(string actionId) =>
        _snap.SessionActive && SafeActions?.FindTarget(actionId)?.BlockedDuringGameSession == true;

    // -- Per game (fullscreen optimizations) ---------------------------------------------------------------

    private void RenderPerGame()
    {
        PerGameList.Children.Clear();
        var safe = SafeActions;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in _snap.Fso)
        {
            seen.Add(s.ExePath);
            string actionId = FullscreenOptimizationSafeActionTarget.ActionIdFor(s.ExePath);
            string name = string.IsNullOrWhiteSpace(s.ProfileName) ? Path.GetFileName(s.ExePath) : s.ProfileName;
            string status = s.FsoDisabled switch
            {
                true => Loc.T("optimize.ui.fso.status.off"),
                false => Loc.T("optimize.ui.fso.status.on"),
                null => Loc.T("system.unavailable")
            };
            PerGameList.Children.Add(BuildPerGameRow(actionId, name, s.ExePath, status,
                s.FsoDisabled == null ? StatusKind.Unknown : s.FsoDisabled.Value ? StatusKind.Ok : StatusKind.Warn,
                applyEnabled: s.FsoDisabled == false && safe != null,
                undoEnabled: safe?.CanUndo(actionId) == true));
        }

        // Journaled per-game changes whose profile is gone / disabled: still undoable.
        foreach (var e in _snap.PerGameJournal)
        {
            var exe = FullscreenOptimizationSafeActionTarget.ExePathFromActionId(e.ActionId);
            if (exe == null || !seen.Add(exe)) continue;
            PerGameList.Children.Add(BuildPerGameRow(e.ActionId, Path.GetFileName(exe), exe,
                Loc.T("optimize.ui.fso.status.off"), StatusKind.Ok, applyEnabled: false, undoEnabled: true));
        }

        TxtPerGameEmpty.Visibility = PerGameList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement BuildPerGameRow(string actionId, string name, string exePath, string status, StatusKind dot,
        bool applyEnabled, bool undoEnabled)
    {
        var card = BuildActionCard(
            title: name,
            status: status,
            dot: dot,
            applyText: Loc.T("rec.fso.game.action"),
            applyEnabled: applyEnabled,
            applyTooltip: null,
            onApply: (_, _) => ConfirmAndApply(actionId, "optimize.fso.why", "optimize.fso.risk"),
            undoEnabled: undoEnabled,
            onUndo: (_, _) => UndoSafeAction(actionId, historyDetail: "fso.undo"),
            why: Loc.T("optimize.fso.why"), risk: Loc.T("optimize.fso.risk"), undo: Loc.T("optimize.fso.undo"));
        // Second hint line: the exe the flag is keyed by (the registry value name).
        if (card is Grid g && g.Children.Count > 1 && g.Children[1] is StackPanel text && text.Children.Count > 1)
        {
            var exeTb = NewHint(exePath, new Thickness(0, 2, 0, 0));
            exeTb.TextTrimming = TextTrimming.CharacterEllipsis;
            exeTb.TextWrapping = TextWrapping.NoWrap;
            exeTb.ToolTip = exePath;
            text.Children.Insert(2, exeTb);
        }
        return card;
    }

    // -- Managed / info cards --------------------------------------------------------------------------------

    private void RenderInfo()
    {
        InfoList.Children.Clear();
        foreach (var item in _snap.Info)
            InfoList.Children.Add(BuildInfoCard(item));
    }

    private FrameworkElement BuildInfoCard(OptimizeInfoItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string title = Loc.T(item.TitleKey);
        var dot = item.Id switch
        {
            OptimizeStatus.PowerPerGameId => StatusDot.FromBool(item.Status),
            OptimizeStatus.HagsId => StatusDot.FromBool(item.Status, whenFalse: StatusKind.Unknown),
            _ => StatusKind.Unknown
        };
        grid.Children.Add(new StatusDot { Status = dot, Label = title, Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top });

        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var titleTb = new TextBlock { Text = title };
        titleTb.SetResourceReference(StyleProperty, "CardTitle");
        text.Children.Add(titleTb);
        string kind = Loc.T(item.Kind switch
        {
            OptimizeInfoKind.RequiresAdmin => "optimize.ui.kind.admin",
            OptimizeInfoKind.ManagedByProfile => "optimize.ui.kind.managed",
            _ => "optimize.ui.kind.manual"
        });
        text.Children.Add(NewHint(Loc.T(item.StatusKey) + " · " + kind, new Thickness(0, 2, 0, 0)));
        if (item.Evidence.Length > 0)
            text.Children.Add(NewChips(item.Evidence));
        text.Children.Add(NewDetails(Loc.T(item.WhyKey), Loc.T(item.RiskKey), null));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // The only control: a link to where the setting really lives.
        WpfButton? link = item.Id switch
        {
            OptimizeStatus.HagsId => NewLink(Loc.T("optimize.ui.openSettings"), (_, _) => OpenShell("ms-settings:display-advancedgraphics")),
            OptimizeStatus.VisualEffectsId => NewLink(Loc.T("optimize.ui.openPerfOptions"), (_, _) => OpenShell("SystemPropertiesPerformance.exe")),
            OptimizeStatus.PowerPerGameId => NewLink(Loc.T("rec.emptyLibrary.action"), (_, _) => Host.Navigate(ShellSection.Games)),
            _ => null
        };
        if (link != null)
        {
            link.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(link, 2);
            grid.Children.Add(link);
        }
        return grid;
    }

    private void OpenShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Open " + target);
            Host.ShowToast(Loc.T("system.unavailable"));
        }
    }

    // -- Journal ---------------------------------------------------------------------------------------------

    private void RenderJournal()
    {
        JournalList.Children.Clear();
        var safe = SafeActions;
        foreach (var e in _snap.Journal)
        {
            var card = NewRowCard();
            var root = new DockPanel();
            var undoBtn = new WpfButton
            {
                Content = Loc.T("system.rollback"),
                Style = (Style)FindResource("CompactGhostButton"),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                IsEnabled = safe != null
            };
            string key = e.Key;
            undoBtn.Click += (_, _) => UndoSafeAction(key, historyDetail: key + ".undo");
            DockPanel.SetDock(undoBtn, Dock.Right);
            root.Children.Add(undoBtn);

            var text = new StackPanel();
            var title = new TextBlock { Text = TitleForAction(e.ActionId), TextWrapping = TextWrapping.Wrap };
            title.SetResourceReference(StyleProperty, "CardTitle");
            text.Children.Add(title);
            text.Children.Add(NewHint(e.Utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), new Thickness(0, 2, 0, 0)));
            var exp = ExperimentLine(e.Key);
            if (exp != null) text.Children.Add(NewHint(exp, new Thickness(0, 2, 0, 0)));
            root.Children.Add(text);
            card.Child = root;
            JournalList.Children.Add(card);
        }
        TxtJournalEmpty.Visibility = _snap.Journal.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Localized card title for an action id (per-game ids format the exe name in).</summary>
    private string TitleForAction(string actionId)
    {
        var exe = FullscreenOptimizationSafeActionTarget.ExePathFromActionId(actionId);
        if (exe != null) return Loc.Tf("optimize.fso.title", Path.GetFileName(exe));
        var target = SafeActions?.FindTarget(actionId);
        return Loc.T(target?.TitleKey ?? "optimize." + actionId + ".title");
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

    // -- SafeAction apply / undo -----------------------------------------------------------------------------

    private void GameModeToggle_Click(object sender, RoutedEventArgs e)
    {
        var current = GameModeSetting.Read();
        if (current == null)
        {
            Host.ShowToast(Loc.T("system.unavailable"));
            return;
        }
        bool next = !current.Value;
        string preview = Loc.Tf("system.preview",
            Loc.T(current.Value ? "system.gameMode.on.short" : "system.gameMode.off"),
            Loc.T(next ? "system.gameMode.on.short" : "system.gameMode.off"));
        if (!ThemedDialog.Confirm(Host.Window,
                preview + "\n\n" + Loc.T(next ? "system.gameMode.confirm.on" : "system.gameMode.confirm.off"),
                Loc.T("system.gameMode"),
                Loc.T("btn.apply"),
                danger: false))
            return;

        ApplySafeAction(
            next ? GameModeSetting.ActionIdOn : GameModeSetting.ActionIdOff,
            historyDetail: GameModeSetting.ActionId + "=" + next.ToString().ToLowerInvariant(),
            okToast: Loc.T(next ? "system.gameMode.toast.on" : "system.gameMode.toast.off"));
    }

    /// <summary>Recommendation "Enable Game Mode" — same confirm + journal path as the button.</summary>
    public void EnableGameModeFromRecommendation()
    {
        if (GameModeSetting.Read() == false)
            GameModeToggle_Click(this, new RoutedEventArgs());
    }

    private void GameModeRollback_Click(object sender, RoutedEventArgs e) =>
        UndoSafeAction(GameModeSetting.ActionId, historyDetail: "gameMode.rollback");

    private void PowerHighPerf_Click(object sender, RoutedEventArgs e)
    {
        var current = PowerPlanSafeAction.ReadAliasOrGuid() ?? Loc.T("system.unavailable");
        string preview = Loc.Tf("system.preview",
            ComboUi.PowerPlanStateLabel(current),
            Loc.T("power.high"));
        if (!ThemedDialog.Confirm(Host.Window,
                preview + "\n\n" + Loc.T("system.power.confirm"),
                Loc.T("system.power"),
                Loc.T("btn.apply"),
                danger: false))
            return;

        ApplySafeAction(PowerPlanSafeAction.ActionId, historyDetail: PowerPlanSafeAction.ActionId, okToast: Loc.T("system.power.toast"));
    }

    private void PowerRollback_Click(object sender, RoutedEventArgs e) =>
        UndoSafeAction(PowerPlanSafeAction.ActionId, historyDetail: "power.rollback");

    /// <summary>Generic confirm (title + why + risk) → apply for registry targets and per-game actions.</summary>
    private void ConfirmAndApply(string actionId, string? whyKey, string? riskKey)
    {
        var safe = SafeActions;
        if (safe == null)
        {
            Host.ShowToast(Loc.T("system.unavailable"));
            return;
        }
        var target = safe.FindTarget(actionId);
        string title = TitleForAction(actionId);
        var lines = new List<string>();
        string? why = whyKey ?? target?.WhyKey;
        string? risk = riskKey ?? target?.RiskKey;
        if (why != null) lines.Add(Loc.T(why));
        if (risk != null) lines.Add(Loc.T("optimize.ui.risk") + ": " + Loc.T(risk));
        lines.Add(Loc.T("optimize.ui.confirm.undoNote"));
        if (!ThemedDialog.Confirm(Host.Window, string.Join("\n\n", lines), title, Loc.T("btn.apply"), danger: false))
            return;
        ApplySafeAction(actionId, historyDetail: actionId, okToast: Loc.Tf("optimize.ui.toast.applied", title));
    }

    /// <summary>Apply → verify → journal. Codes are localized through SafeActionCodes.LocKey, never shown raw.</summary>
    private void ApplySafeAction(string actionId, string historyDetail, string okToast)
    {
        var safe = SafeActions;
        if (safe == null)
        {
            Host.ShowToast(Loc.T("system.unavailable"));
            return;
        }
        var r = safe.Apply(actionId);
        switch (r.Result)
        {
            case SafeActionResult.Applied:
                App.Services.History.Add("safeAction", detail: historyDetail);
                MarkExperimentApplied(safe.FindTarget(actionId)?.JournalKey ?? actionId);
                Host.ShowToast(okToast);
                break;
            case SafeActionResult.NoOp:
                Host.ShowToast(Loc.T("safeAction.noop"));
                break;
            case SafeActionResult.Blocked:
            default:
                Host.ShowToast(Loc.Tf("system.safe.fail", Loc.T(SafeActionCodes.LocKey(r.Code))));
                break;
        }
        Refresh();
    }

    /// <summary>Restores the journaled previous state; a value changed outside Visor asks before forcing.</summary>
    private void UndoSafeAction(string journalKey, string historyDetail)
    {
        var safe = SafeActions;
        if (safe == null || !safe.CanUndo(journalKey)) return;
        var r = safe.Undo(journalKey);
        if (r.Result == SafeActionUndoResult.Conflict)
        {
            if (!ThemedDialog.Confirm(Host.Window,
                    Loc.T("safeAction.undo.conflict"),
                    Loc.T("system.rollback"),
                    Loc.T("safeAction.undo.force"),
                    danger: true))
            {
                Refresh();
                return;
            }
            r = safe.Undo(journalKey, confirmedConflict: true);
        }
        if (r.Result == SafeActionUndoResult.Undone)
        {
            App.Services.History.Add("safeAction", detail: historyDetail);
            _experiments.Remove(journalKey);
            Host.ShowToast(Loc.T("system.rollback.ok"));
        }
        else
            Host.ShowToast(Loc.Tf("system.safe.fail", Loc.T(SafeActionCodes.LocKey(r.Code))));
        Refresh();
    }

    // -- Small element factories -----------------------------------------------------------------------------

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

    private static TextBlock NewHint(string text, Thickness margin)
    {
        var tb = new TextBlock { Text = text, Margin = margin, TextWrapping = TextWrapping.Wrap };
        tb.SetResourceReference(StyleProperty, "Hint");
        return tb;
    }

    /// <summary>Severity badge colored by the same tokens as the status dots.</summary>
    private static Border NewBadge(string text, string borderBrushKey, string foregroundKey)
    {
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1)
        };
        badge.SetResourceReference(Border.BorderBrushProperty, borderBrushKey);
        badge.SetResourceReference(Border.BackgroundProperty, "PillBgBrush");
        var tb = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold };
        tb.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);
        badge.Child = tb;
        return badge;
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

    private OptDetailsPanel NewDetails(string? why, string? risk, string? undo)
    {
        var p = new OptDetailsPanel();
        p.SetTexts(why, risk, undo);
        _detailPanels.Add(p);
        return p;
    }

    private static WpfButton NewLink(string text, RoutedEventHandler onClick)
    {
        var b = new WpfButton { Content = text };
        b.SetResourceReference(StyleProperty, "LinkButton");
        b.Click += onClick;
        return b;
    }
}
