using System.IO;
using System.Windows;
using System.Windows.Controls;
using Visor.Controls;
using Visor.Services;
using Visor.Services.Optimize;
using Visor.Services.Optimize.Pc;

namespace Visor;

/// <summary>System · Graphics · Input sections: one <see cref="TweakRow"/> per reversible setting.</summary>
public partial class OptimizePage
{
    // -- System ----------------------------------------------------------------------------------------------

    private void RenderSystem()
    {
        SystemList.Children.Clear();
        bool session = _snap.SessionActive;

        // Game Mode: on/off toggle sharing one undo slot.
        var gm = _snap.GameMode;
        var gmRow = new TweakRow(Loc.T("optimize.gameMode.title"))
            .SetStatus(gm == null ? Loc.T("system.unavailable") : Loc.T(gm.Value ? "system.gameMode.on" : "system.gameMode.off"),
                StatusDot.FromBool(gm))
            .SetDetails(Loc.T("optimize.gameMode.why"), Loc.T("optimize.gameMode.risk"), Loc.T("optimize.gameMode.undo"));
        gmRow.AddAction(Loc.T(gm == true ? "system.gameMode.disable" : "system.gameMode.enable"), accent: true,
            run: GameModeToggleAsync, enabled: gm != null);
        gmRow.AddAction(Loc.T("system.rollback"), accent: false,
            run: () => UndoAndRefreshAsync(GameModeSetting.ActionId, "gameMode.rollback"),
            enabled: Safe.CanUndo(GameModeSetting.ActionId));
        SystemList.Children.Add(gmRow);

        // Desktop power plan (High performance): blocked while a game session owns the plan.
        var plan = _snap.PowerPlan;
        var powerRow = new TweakRow(Loc.T("optimize.powerHighPerf.title"))
            .SetStatus(plan == null ? Loc.T("system.unavailable") : Loc.Tf("system.power.current", ComboUi.PowerPlanStateLabel(plan)),
                plan == null ? StatusKind.Unknown : RecommendationEngine.IsPerformanceClassPlan(plan) ? StatusKind.Ok : StatusKind.Warn)
            .SetDetails(Loc.T("optimize.powerHighPerf.why"), Loc.T("optimize.powerHighPerf.risk"), Loc.T("optimize.powerHighPerf.undo"));
        powerRow.AddAction(Loc.T("system.power.highPerf"), accent: true, run: PowerHighPerfAsync,
            enabled: !session, tooltip: session ? Loc.T("safeAction.code.sessionActive") : null);
        powerRow.AddAction(Loc.T("system.rollback"), accent: false,
            run: () => UndoAndRefreshAsync(PowerPlanSafeAction.ActionId, "power.rollback"),
            enabled: Safe.CanUndo(PowerPlanSafeAction.ActionId));
        SystemList.Children.Add(powerRow);

        SystemList.Children.Add(BuildUltimateRow());

        // Xbox Game Bar background features.
        SystemList.Children.Add(BuildGenericRow(GameBarSafeActionTarget.Id, _snap.GameBarOn,
            onText: Loc.T("optimize.ui.gameBar.status.on"), offText: Loc.T("optimize.ui.gameBar.status.off"),
            applyText: Loc.T("rec.gamebar.off.action")));

        SystemGpuList.Children.Clear();
        SystemGpuList.Children.Add(BuildHagsRow());

        InfoList.Children.Clear();
        if (_snap.PowerPerGame != null)
            InfoList.Children.Add(BuildInfoCard(_snap.PowerPerGame));
    }

    /// <summary>Row for a registry target whose "apply" means "turn the feature off" (true = still on).</summary>
    private TweakRow BuildGenericRow(string actionId, bool? featureOn, string onText, string offText, string applyText)
    {
        var target = Safe.FindTarget(actionId);
        bool blocked = IsBlocked(actionId);
        var row = new TweakRow(Loc.T(target?.TitleKey ?? "optimize." + actionId + ".title"))
            .SetStatus(featureOn == null ? Loc.T("system.unavailable") : featureOn.Value ? onText : offText,
                featureOn == null ? StatusKind.Unknown : featureOn.Value ? StatusKind.Warn : StatusKind.Ok)
            .SetNeedsAdmin(target?.RequiresElevation == true);
        if (target != null)
            row.SetDetails(Loc.T(target.WhyKey), Loc.T(target.RiskKey), Loc.T(target.UndoKey));
        row.AddAction(applyText, accent: true,
            run: () => ApplyAndRefreshAsync(actionId),
            enabled: featureOn == true && target != null && !blocked,
            tooltip: blocked ? Loc.T("safeAction.code.sessionActive") : null);
        row.AddAction(Loc.T("system.rollback"), accent: false,
            run: () => UndoAndRefreshAsync(actionId),
            enabled: Safe.CanUndo(actionId));
        return row;
    }

    // -- Ultimate Performance plan ---------------------------------------------------------------------------

    private TweakRow BuildUltimateRow()
    {
        bool installed = _snap.UltimateGuid != null;
        bool active = _snap.UltimateActive == true;
        bool session = _snap.SessionActive;
        var row = new TweakRow(Loc.T("optimize.ultimatePlan.title"))
            .SetStatus(!installed ? Loc.T("optimize.ultimatePlan.missing")
                    : active ? Loc.T("optimize.ultimatePlan.active") : Loc.T("optimize.ultimatePlan.installed"),
                !installed ? StatusKind.Unknown : active ? StatusKind.Ok : StatusKind.Unknown)
            .SetNeedsAdmin(!installed) // creating takes one UAC prompt; activating / undo do not
            .SetDetails(Loc.T("optimize.ultimatePlan.why"), Loc.T("optimize.ultimatePlan.risk"), Loc.T("optimize.ultimatePlan.undo"));
        if (!installed)
        {
            row.AddAction(Loc.T("optimize.ultimatePlan.create"), accent: true, run: CreateUltimateAsync,
                busyText: Loc.T("optimize.ui.busy.uac"));
        }
        else
        {
            row.AddAction(Loc.T("optimize.ultimatePlan.activate"), accent: true,
                run: () => ApplyAndRefreshAsync(UltimatePlanTarget.Id),
                enabled: !active && !session,
                tooltip: session ? Loc.T("safeAction.code.sessionActive") : null);
        }
        row.AddAction(Loc.T("system.rollback"), accent: false,
            run: () => UndoAndRefreshAsync(UltimatePlanTarget.Key),
            enabled: Safe.CanUndo(UltimatePlanTarget.Key));
        return row;
    }

    private async Task CreateUltimateAsync()
    {
        string msg = Loc.T("optimize.ultimatePlan.why") + "\n\n"
                     + Loc.T("optimize.ui.risk") + ": " + Loc.T("optimize.ultimatePlan.risk") + "\n\n"
                     + Loc.T("optimize.ultimatePlan.createNote") + "\n\n"
                     + Loc.T("net.tweak.status.uacHint") + ".";
        if (!ThemedDialog.Confirm(Host.Window, msg, Loc.T("optimize.ultimatePlan.title"), Loc.T("optimize.ultimatePlan.create")))
            return;
        UltimateCreateResult r;
        try
        {
            r = await Task.Run(() => App.Services.Pc.Ultimate.CreateAsync());
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Ultimate plan create");
            r = new UltimateCreateResult(UltimateCreateStatus.Failed, Code: SafeActionCodes.ElevationFailed);
        }
        switch (r.Status)
        {
            case UltimateCreateStatus.Created:
            case UltimateCreateStatus.AlreadyInstalled:
                App.Services.History.Add("safeAction", detail: "ultimatePlan.create");
                Host.ShowToast(Loc.T("optimize.ultimatePlan.created"));
                break;
            default:
                Host.ShowToast(SafeActionUi.FailText(r.Code ?? SafeActionCodes.ApplyFailed));
                break;
        }
        await RefreshNowAsync();
    }

    // -- HAGS ------------------------------------------------------------------------------------------------

    /// <summary>"On" / "Off" / unsupported / unknown for the HAGS status line (kernel state first).</summary>
    private static string HagsStatusText(HagsStatus? h)
    {
        if (h == null) return Loc.T("optimize.hags.status.unknown");
        if (h.Supported == false) return Loc.T("optimize.hagsMode.unsupported");
        return h.EffectiveOn switch
        {
            true => Loc.T("optimize.hags.status.on"),
            false => Loc.T("optimize.hags.status.off"),
            null => Loc.T("optimize.hags.status.unknown")
        };
    }

    private TweakRow BuildHagsRow()
    {
        var h = _snap.Hags;
        var row = new TweakRow(Loc.T("optimize.hagsMode.title"))
            .SetStatus(HagsStatusText(h), h?.Supported == false ? StatusKind.Unknown : StatusDot.FromBool(h?.EffectiveOn, StatusKind.Unknown))
            .SetNeedsAdmin(true)
            .SetDetails(Loc.T("optimize.hagsMode.why"), Loc.T("optimize.hagsMode.risk"), Loc.T("optimize.hagsMode.undo"));
        if (h?.PendingRestart == true)
        {
            row.AddBadge(Loc.T("optimize.hagsMode.restartBadge"), "WarningBrush", "WarningBrush");
            row.AddLine(Loc.T("optimize.hagsMode.pendingRestart"));
        }
        if (h != null) row.AddChips(h.Evidence());

        // What the next boot will run: the registry request when set, else the running state.
        bool? next = h?.RegistryOn ?? h?.EffectiveOn;
        bool supported = h?.Supported != false;
        string id = next == true ? HagsTarget.OffId : HagsTarget.OnId;
        row.AddAction(Loc.T(next == true ? "optimize.hagsMode.off" : "optimize.hagsMode.on"), accent: next != true,
            run: () => ApplyAndRefreshAsync(id, new SafeApplyOptions { AfterNote = Loc.T("optimize.hagsMode.pendingRestart") }),
            enabled: supported && h != null,
            tooltip: supported ? null : Loc.T("optimize.hagsMode.unsupported"),
            busyText: Loc.T("optimize.ui.busy.uac"));
        row.AddAction(Loc.T("system.rollback"), accent: false,
            run: () => UndoAndRefreshAsync(HagsTarget.Key),
            enabled: Safe.CanUndo(HagsTarget.Key),
            busyText: Loc.T("optimize.ui.busy.uac"));
        return row;
    }

    // -- Graphics --------------------------------------------------------------------------------------------

    private void RenderGraphics()
    {
        PerGameList.Children.Clear();
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
                applyEnabled: s.FsoDisabled == false));
        }

        // Journaled per-game changes whose profile is gone / disabled: still undoable.
        foreach (var e in _snap.PerGameJournal)
        {
            var exe = FullscreenOptimizationSafeActionTarget.ExePathFromActionId(e.ActionId);
            if (exe == null || !seen.Add(exe)) continue;
            PerGameList.Children.Add(BuildPerGameRow(e.ActionId, Path.GetFileName(exe), exe,
                Loc.T("optimize.ui.fso.status.off"), StatusKind.Ok, applyEnabled: false));
        }
        TxtPerGameEmpty.Visibility = PerGameList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // HAGS shortcut: state here, the switch lives in System (one place to change it).
        GfxHagsList.Children.Clear();
        var h = _snap.Hags;
        var hags = new TweakRow(Loc.T("optimize.hagsMode.title"))
            .SetStatus(HagsStatusText(h), h?.Supported == false ? StatusKind.Unknown : StatusDot.FromBool(h?.EffectiveOn, StatusKind.Unknown));
        if (h?.PendingRestart == true)
            hags.AddBadge(Loc.T("optimize.hagsMode.restartBadge"), "WarningBrush", "WarningBrush", Loc.T("optimize.hagsMode.pendingRestart"));
        hags.AddAction(Loc.T("optimize.ui.goSystem"), accent: false, link: true, run: () =>
        {
            ShowSection(OptimizeSection.System);
            return Task.CompletedTask;
        });
        hags.AddAction(Loc.T("optimize.ui.openSettings"), accent: false, link: true, run: () =>
        {
            OpenShell("ms-settings:display-advancedgraphics");
            return Task.CompletedTask;
        });
        GfxHagsList.Children.Add(hags);

        GfxInfoList.Children.Clear();
        GfxInfoList.Children.Add(BuildInfoCard(OptimizeStatus.VisualEffectsItem()));
    }

    private TweakRow BuildPerGameRow(string actionId, string name, string exePath, string status, StatusKind dot, bool applyEnabled)
    {
        var row = new TweakRow(name)
            .SetStatus(status, dot)
            .AddLine(exePath, trim: true, tooltip: exePath)
            .SetDetails(Loc.T("optimize.fso.why"), Loc.T("optimize.fso.risk"), Loc.T("optimize.fso.undo"));
        row.AddAction(Loc.T("rec.fso.game.action"), accent: true,
            run: () => ApplyAndRefreshAsync(actionId, new SafeApplyOptions { WhyKey = "optimize.fso.why", RiskKey = "optimize.fso.risk" }),
            enabled: applyEnabled);
        row.AddAction(Loc.T("system.rollback"), accent: false,
            run: () => UndoAndRefreshAsync(actionId, "fso.undo"),
            enabled: Safe.CanUndo(actionId));
        return row;
    }

    // -- Input -----------------------------------------------------------------------------------------------

    private void RenderInput()
    {
        InputList.Children.Clear();
        InputList.Children.Add(BuildGenericRow(MouseAccelerationSafeActionTarget.Id, _snap.MouseAccelOn,
            onText: Loc.T("optimize.ui.mouseAccel.status.on"), offText: Loc.T("optimize.ui.mouseAccel.status.off"),
            applyText: Loc.T("rec.mouse.accel.action")));
    }

    // -- Managed / info cards --------------------------------------------------------------------------------

    private FrameworkElement BuildInfoCard(OptimizeInfoItem item)
    {
        string kind = Loc.T(item.Kind switch
        {
            OptimizeInfoKind.RequiresAdmin => "optimize.ui.kind.admin",
            OptimizeInfoKind.ManagedByProfile => "optimize.ui.kind.managed",
            _ => "optimize.ui.kind.manual"
        });
        var row = new TweakRow(Loc.T(item.TitleKey))
            .SetStatus(Loc.T(item.StatusKey) + " · " + kind,
                item.Id == OptimizeStatus.PowerPerGameId ? StatusDot.FromBool(item.Status) : StatusKind.Unknown)
            .SetDetails(Loc.T(item.WhyKey), Loc.T(item.RiskKey), null);
        if (item.Evidence.Length > 0) row.AddChips(item.Evidence);

        // The only control: a link to where the setting really lives.
        switch (item.Id)
        {
            case OptimizeStatus.VisualEffectsId:
                row.AddAction(Loc.T("optimize.ui.openPerfOptions"), accent: false, link: true, run: () =>
                {
                    OpenShell("SystemPropertiesPerformance.exe");
                    return Task.CompletedTask;
                });
                break;
            case OptimizeStatus.PowerPerGameId:
                row.AddAction(Loc.T("rec.emptyLibrary.action"), accent: false, link: true, run: () =>
                {
                    Host.Navigate(ShellSection.Games);
                    return Task.CompletedTask;
                });
                break;
        }
        return row;
    }

    // -- Game Mode / power plan (custom confirm with a before → after preview) --------------------------------

    private async Task GameModeToggleAsync()
    {
        var current = await Task.Run(() => GameModeSetting.Read());
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

        await ApplyAndRefreshAsync(next ? GameModeSetting.ActionIdOn : GameModeSetting.ActionIdOff, new SafeApplyOptions
        {
            Confirm = false,
            HistoryDetail = GameModeSetting.ActionId + "=" + next.ToString().ToLowerInvariant(),
            OkToast = Loc.T(next ? "system.gameMode.toast.on" : "system.gameMode.toast.off")
        });
    }

    /// <summary>Recommendation "Enable Game Mode" — same confirm + journal path as the button.</summary>
    private async Task EnableGameModeFromRecommendationAsync()
    {
        if (await Task.Run(() => GameModeSetting.Read()) == false)
            await GameModeToggleAsync();
    }

    private async Task PowerHighPerfAsync()
    {
        var current = await Task.Run(() => PowerPlanSafeAction.ReadAliasOrGuid()) ?? Loc.T("system.unavailable");
        string preview = Loc.Tf("system.preview",
            ComboUi.PowerPlanStateLabel(current),
            Loc.T("power.high"));
        if (!ThemedDialog.Confirm(Host.Window,
                preview + "\n\n" + Loc.T("system.power.confirm"),
                Loc.T("system.power"),
                Loc.T("btn.apply"),
                danger: false))
            return;

        await ApplyAndRefreshAsync(PowerPlanSafeAction.ActionId, new SafeApplyOptions
        {
            Confirm = false,
            HistoryDetail = PowerPlanSafeAction.ActionId,
            OkToast = Loc.T("system.power.toast")
        });
    }
}
