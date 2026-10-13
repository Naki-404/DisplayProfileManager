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

/// <summary>System page: PC overview, safe Windows tweaks, process sample.</summary>
public partial class MainWindow : Window
{
    private SafeActionService? _safeActions;

    /// <summary>Journaled, reversible Windows tweaks (Game Mode, High performance plan). Created on first use.</summary>
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
    /// <summary>True while the PC snapshot is being captured off-thread; Safe-tweak buttons stay disabled meanwhile.</summary>
    private bool _systemBusy;
    private bool _processBusy;
    /// <summary>A refresh was requested while the page was hidden — honored on the next selection.</summary>
    private bool _systemStale = true;

    private bool SystemPageIsLive => IsVisible && ReferenceEquals(MainTabs.SelectedItem, TabSystem);

    private sealed class ProcessRowVm
    {
        public required string Title { get; init; }
        public required string CpuText { get; init; }
        public required string RamText { get; init; }
    }

    /// <summary>Captures the PC snapshot on a worker thread (WMI can take a second) and shows a loading state meanwhile.</summary>
    private async void RefreshSystemPage(bool includeProcesses)
    {
        if (TxtSystemCpu == null) return;
        if (!SystemPageIsLive)
        {
            _systemStale = true;
            return;
        }
        if (_systemBusy) return;

        _systemBusy = true;
        _systemStale = false;
        string loading = Loc.T("system.loading");
        TxtSystemCpu.Text = loading;
        TxtSystemRam.Text = loading;
        TxtSystemGpu.Text = loading;
        TxtSystemOs.Text = loading;
        RefreshSystemSafeLabels();

        SystemInfoSnapshot? snap = null;
        try
        {
            snap = await Task.Run(SystemInfoService.Capture).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "System snapshot");
        }
        finally
        {
            _systemBusy = false;
        }

        ApplySystemSnapshot(snap);
        RefreshSystemSafeLabels();
        if (includeProcesses)
            RefreshProcessList();
    }

    private void ApplySystemSnapshot(SystemInfoSnapshot? snap)
    {
        string unavailable = Loc.T("system.unavailable");
        if (snap == null)
        {
            TxtSystemCpu.Text = unavailable;
            TxtSystemRam.Text = unavailable;
            TxtSystemGpu.Text = unavailable;
            TxtSystemOs.Text = unavailable;
            SystemDiskList.Children.Clear();
            TxtSystemDisksEmpty.Visibility = Visibility.Visible;
            return;
        }

        TxtSystemCpu.Text = snap.CpuName == null
            ? unavailable
            : Loc.Tf("system.cpu.value", snap.CpuName, snap.LogicalProcessors);
        TxtSystemRam.Text = snap.RamTotalBytes <= 0
            ? unavailable
            : Loc.Tf("system.ram.value",
                SystemInfoService.FormatBytes(snap.RamAvailableBytes),
                SystemInfoService.FormatBytes(snap.RamTotalBytes));
        TxtSystemGpu.Text = string.IsNullOrWhiteSpace(snap.GpuLabel)
            ? unavailable
            : snap.GpuLabel!;
        TxtSystemOs.Text = snap.WindowsVersion ?? unavailable;
        // Power plan + Game Mode also appear in Safe tweaks; mirror facts in the OS card footer.
        if (TxtSystemOs.Text.Length > 0 && snap.PowerPlan != null)
            TxtSystemOs.Text += " · " + Loc.Tf("system.power.current", FormatPowerPlanLabel(snap.PowerPlan));
        if (snap.GameModeEnabled != null)
            TxtSystemOs.Text += " · " + Loc.T(snap.GameModeEnabled.Value ? "system.gameMode.on.short" : "system.gameMode.off");

        SystemDiskList.Children.Clear();
        if (snap.Disks.Count == 0)
            TxtSystemDisksEmpty.Visibility = Visibility.Visible;
        else
        {
            TxtSystemDisksEmpty.Visibility = Visibility.Collapsed;
            foreach (var d in snap.Disks)
            {
                SystemDiskList.Children.Add(new TextBlock
                {
                    Text = Loc.Tf("system.disk.row", d.Name,
                        SystemInfoService.FormatBytes(d.FreeBytes),
                        SystemInfoService.FormatBytes(d.TotalBytes)),
                    Style = (Style)FindResource("Hint"),
                    Margin = new Thickness(0, 0, 0, 4),
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }
    }

    private void RefreshSystemSafeLabels()
    {
        if (TxtGameModeStatus == null) return;
        var gm = GameModeSetting.Read();
        TxtGameModeStatus.Text = gm == null
            ? Loc.T("system.unavailable")
            : Loc.T(gm.Value ? "system.gameMode.on" : "system.gameMode.off");
        BtnGameModeToggle.Content = Loc.T(gm == true ? "system.gameMode.disable" : "system.gameMode.enable");
        var safe = SafeActions;
        BtnGameModeToggle.IsEnabled = gm != null && !_systemBusy && safe != null;
        BtnGameModeRollback.IsEnabled = safe?.CanUndo(GameModeSetting.ActionId) == true && !_systemBusy;

        var plan = PowerPlanSafeAction.ReadAliasOrGuid();
        TxtPowerPlanStatus.Text = plan == null
            ? Loc.T("system.unavailable")
            : Loc.Tf("system.power.current", FormatPowerPlanLabel(plan));
        // While a game session owns the plan, Visor restores it on exit — a manual switch would be undone.
        bool sessionActive = App.Services.Monitor.SnapshotActive;
        BtnPowerHighPerf.IsEnabled = !_systemBusy && !sessionActive && safe != null;
        BtnPowerHighPerf.ToolTip = sessionActive ? Loc.T("safeAction.code.sessionActive") : null;
        BtnPowerRollback.IsEnabled = safe?.CanUndo(PowerPlanSafeAction.ActionId) == true && !_systemBusy;
    }

    private static string FormatPowerPlanLabel(string aliasOrGuid) => aliasOrGuid switch
    {
        "highPerformance" => Loc.T("power.high"),
        "balanced" => Loc.T("power.balanced"),
        "powerSaver" => Loc.T("power.saver"),
        "ultimate" => Loc.T("power.ultimate"),
        _ => aliasOrGuid
    };

    /// <summary>Read-only CPU/RAM sample of the busiest processes; never runs while a snapshot or another sample is in flight.</summary>
    private async void RefreshProcessList()
    {
        if (ProcessList == null || _systemBusy || _processBusy) return;
        _processBusy = true;
        BtnProcessRefresh.IsEnabled = false;
        try
        {
            var sample = await Task.Run(() => SystemInfoService.SampleTopProcesses(12, 180))
                .ConfigureAwait(true);
            var rows = sample
                .Select(r => new ProcessRowVm
                {
                    Title = Loc.Tf("system.process.row", r.Name, r.Pid),
                    CpuText = Loc.Tf("system.process.cpu", r.CpuPercent.ToString("0.0")),
                    RamText = SystemInfoService.FormatBytes(r.WorkingSetBytes)
                })
                .ToList();
            ProcessList.ItemsSource = rows;
            TxtProcessEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Process sample");
            ShowToast(Loc.Tf("system.processes.fail", ex.Message));
        }
        finally
        {
            _processBusy = false;
            BtnProcessRefresh.IsEnabled = true;
        }
    }

    private void SystemRefresh_Click(object sender, RoutedEventArgs e) =>
        RefreshSystemPage(includeProcesses: true);

    private void ProcessRefresh_Click(object sender, RoutedEventArgs e) =>
        RefreshProcessList();

    private static string FormatSafeActionDetail(string? detail) => detail switch
    {
        "gameMode=True" or "gameMode=true" => Loc.T("system.hist.gameMode.on"),
        "gameMode=False" or "gameMode=false" => Loc.T("system.hist.gameMode.off"),
        "gameMode.rollback" => Loc.T("system.hist.gameMode.rollback"),
        "powerHighPerf" => Loc.T("system.hist.power"),
        "power.rollback" => Loc.T("system.hist.power.rollback"),
        _ => detail ?? "?"
    };

    private void GameModeToggle_Click(object sender, RoutedEventArgs e)
    {
        var current = GameModeSetting.Read();
        if (current == null)
        {
            ShowToast(Loc.T("system.unavailable"));
            return;
        }
        bool next = !current.Value;
        string preview = Loc.Tf("system.preview",
            Loc.T(current.Value ? "system.gameMode.on.short" : "system.gameMode.off"),
            Loc.T(next ? "system.gameMode.on.short" : "system.gameMode.off"));
        if (!ThemedDialog.Confirm(this,
                preview + "\n\n" + Loc.T(next ? "system.gameMode.confirm.on" : "system.gameMode.confirm.off"),
                Loc.T("system.gameMode"),
                Loc.T("btn.apply"),
                danger: false))
            return;

        ApplySafeAction(
            next ? GameModeSetting.ActionIdOn : GameModeSetting.ActionIdOff,
            historyDetail: GameModeSetting.ActionId + "=" + next.ToString().ToLowerInvariant(),
            okToastKey: next ? "system.gameMode.toast.on" : "system.gameMode.toast.off");
    }

    private void GameModeRollback_Click(object sender, RoutedEventArgs e) =>
        UndoSafeAction(GameModeSetting.ActionId, historyDetail: "gameMode.rollback");

    private void PowerHighPerf_Click(object sender, RoutedEventArgs e)
    {
        var current = PowerPlanSafeAction.ReadAliasOrGuid() ?? Loc.T("system.unavailable");
        string preview = Loc.Tf("system.preview",
            FormatPowerPlanLabel(current),
            Loc.T("power.high"));
        if (!ThemedDialog.Confirm(this,
                preview + "\n\n" + Loc.T("system.power.confirm"),
                Loc.T("system.power"),
                Loc.T("btn.apply"),
                danger: false))
            return;

        ApplySafeAction(PowerPlanSafeAction.ActionId, historyDetail: PowerPlanSafeAction.ActionId, okToastKey: "system.power.toast");
    }

    private void PowerRollback_Click(object sender, RoutedEventArgs e) =>
        UndoSafeAction(PowerPlanSafeAction.ActionId, historyDetail: "power.rollback");

    /// <summary>Apply → verify → journal. Codes are localized through SafeActionCodes.LocKey, never shown raw.</summary>
    private void ApplySafeAction(string actionId, string historyDetail, string okToastKey)
    {
        var safe = SafeActions;
        if (safe == null)
        {
            ShowToast(Loc.T("system.unavailable"));
            return;
        }
        var r = safe.Apply(actionId);
        switch (r.Result)
        {
            case SafeActionResult.Applied:
                App.Services.History.Add("safeAction", detail: historyDetail);
                ShowToast(Loc.T(okToastKey));
                break;
            case SafeActionResult.NoOp:
                ShowToast(Loc.T("safeAction.noop"));
                break;
            default:
                ShowToast(Loc.Tf("system.safe.fail", Loc.T(SafeActionCodes.LocKey(r.Code))));
                break;
        }
        RefreshSystemSafeLabels();
    }

    /// <summary>Restores the journaled previous state; a value changed outside Visor asks before forcing.</summary>
    private void UndoSafeAction(string journalKey, string historyDetail)
    {
        var safe = SafeActions;
        if (safe == null || !safe.CanUndo(journalKey)) return;
        var r = safe.Undo(journalKey);
        if (r.Result == SafeActionUndoResult.Conflict)
        {
            if (!ThemedDialog.Confirm(this,
                    Loc.T("safeAction.undo.conflict"),
                    Loc.T("system.rollback"),
                    Loc.T("safeAction.undo.force"),
                    danger: true))
            {
                RefreshSystemSafeLabels();
                return;
            }
            r = safe.Undo(journalKey, confirmedConflict: true);
        }
        if (r.Result == SafeActionUndoResult.Undone)
        {
            App.Services.History.Add("safeAction", detail: historyDetail);
            ShowToast(Loc.T("system.rollback.ok"));
        }
        else
            ShowToast(Loc.Tf("system.safe.fail", Loc.T(SafeActionCodes.LocKey(r.Code))));
        RefreshSystemSafeLabels();
    }
}
