using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Visor.Controls;
using Visor.Services;
using Visor.Services.Optimize.Pc;

namespace Visor;

/// <summary>Startup apps and cache cleanup sections.</summary>
public partial class OptimizePage
{
    private enum StartupFilterKind { All, Enabled, Disabled }

    private StartupFilterKind _startupFilter = StartupFilterKind.All;
    private CancellationTokenSource? _cleanupCts;
    private bool _cleaning;
    /// <summary>Last measurement (background first-visit scan, Scan click or after a clean).</summary>
    private CleanupScanResult? _cleanupScan;
    /// <summary>Delivery Optimization cache size, measured with the cache scan (WMI, no admin).</summary>
    private DeliveryOptimizationStatus? _doStatus;

    // -- Startup apps ----------------------------------------------------------------------------------------

    private void StartupFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (StartupList == null) return;
        _startupFilter = sender == RbStartupEnabled ? StartupFilterKind.Enabled
            : sender == RbStartupDisabled ? StartupFilterKind.Disabled
            : StartupFilterKind.All;
        if (_snap.Loaded) RenderStartup();
    }

    private void RenderStartup()
    {
        StartupList.Children.Clear();
        var apps = _snap.Startup;
        if (apps == null)
        {
            TxtStartupSummary.Text = "";
            TxtStartupEmpty.Text = Loc.T(_snap.Loaded ? "system.unavailable" : "system.loading");
            TxtStartupEmpty.Visibility = Visibility.Visible;
            return;
        }
        int enabled = apps.Count(a => a.Enabled);
        TxtStartupSummary.Text = Loc.Tf("optimize.startup.summary", enabled, apps.Count);
        var shown = apps.Where(a => _startupFilter switch
        {
            StartupFilterKind.Enabled => a.Enabled,
            StartupFilterKind.Disabled => !a.Enabled,
            _ => true
        }).ToList();
        foreach (var entry in shown)
            StartupList.Children.Add(BuildStartupRow(entry));
        TxtStartupEmpty.Text = Loc.T(apps.Count == 0 ? "optimize.startup.empty" : "optimize.startup.emptyFilter");
        TxtStartupEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string LocationText(StartupLocation l) => Loc.T(l switch
    {
        StartupLocation.UserRun => "optimize.startup.loc.userRun",
        StartupLocation.MachineRun => "optimize.startup.loc.machineRun",
        StartupLocation.MachineRun32 => "optimize.startup.loc.machineRun32",
        StartupLocation.UserStartupFolder => "optimize.startup.loc.userFolder",
        _ => "optimize.startup.loc.commonFolder"
    });

    private FrameworkElement BuildStartupRow(StartupEntry entry)
    {
        var card = NewRowCard();
        var root = new DockPanel();

        // Right: undo (when journaled) + the switch. The switch shows the real state; it flips only after a verified apply.
        var right = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(right, Dock.Right);
        var busy = TweakRow.Hint(Loc.T(entry.RequiresElevation ? "optimize.ui.busy.uac" : "optimize.ui.busy"), new Thickness(0, 4, 0, 0));
        busy.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        busy.Visibility = Visibility.Collapsed;

        if (Safe.CanUndo(entry.JournalKey))
        {
            var undo = AsyncButton(Loc.T("system.rollback"), "LinkButton", entry.DisplayName,
                () => UndoAndRefreshAsync(entry.JournalKey));
            undo.VerticalAlignment = VerticalAlignment.Center;
            undo.Margin = new Thickness(0, 0, 12, 0);
            right.Children.Add(undo);
        }
        var toggle = new ToggleButton
        {
            IsChecked = entry.Enabled,
            IsEnabled = entry.CanToggle,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = !entry.CanToggle ? Loc.T("optimize.startup.readOnly")
                : Loc.T(entry.Enabled ? "optimize.startup.disable" : "optimize.startup.enable")
        };
        toggle.SetResourceReference(StyleProperty, "SwitchToggle");
        ToolTipService.SetShowOnDisabled(toggle, true);
        AutomationProperties.SetName(toggle, entry.DisplayName);
        AutomationProperties.SetHelpText(toggle, Loc.T(entry.Enabled ? "optimize.startup.state.on" : "optimize.startup.state.off"));
        toggle.Click += async (_, _) =>
        {
            toggle.IsChecked = entry.Enabled; // stays at the real state until the change is verified
            if (!entry.CanToggle) return;
            toggle.IsEnabled = false;
            busy.Visibility = Visibility.Visible;
            try
            {
                await ToggleStartupAsync(entry);
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "Startup toggle");
            }
            finally
            {
                toggle.IsEnabled = entry.CanToggle;
                busy.Visibility = Visibility.Collapsed;
            }
        };
        right.Children.Add(toggle);
        root.Children.Add(right);

        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var titleRow = new DockPanel();
        if (entry.RequiresElevation && entry.CanToggle)
        {
            var admin = TweakRow.Badge(Loc.T("optimize.ui.adminBadge"), "WarningBrush", "WarningBrush");
            admin.ToolTip = Loc.T("optimize.startup.admin") + " " + Loc.T("net.tweak.status.uacHint") + ".";
            admin.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(admin, Dock.Left);
            titleRow.Children.Add(admin);
        }
        if (!entry.Enabled)
        {
            var off = TweakRow.Badge(Loc.T("optimize.startup.state.off"), "BorderBrush", "MutedBrush");
            off.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(off, Dock.Left);
            titleRow.Children.Add(off);
        }
        var title = new TextBlock { Text = entry.DisplayName, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(StyleProperty, "CardTitle");
        titleRow.Children.Add(title);
        text.Children.Add(titleRow);

        string publisher = string.IsNullOrWhiteSpace(entry.Publisher) ? Loc.T("optimize.startup.publisherUnknown") : entry.Publisher!;
        string size = entry.FileSizeBytes is { } b
            ? Loc.Tf("optimize.startup.size", SystemInfoService.FormatBytes(b))
            : Loc.T("optimize.startup.sizeUnknown");
        text.Children.Add(TweakRow.Hint(publisher + " · " + size + " · " + LocationText(entry.Location), new Thickness(0, 2, 0, 0)));

        var cmd = TweakRow.Hint(entry.Command, new Thickness(0, 2, 0, 0));
        cmd.TextWrapping = TextWrapping.NoWrap;
        cmd.TextTrimming = TextTrimming.CharacterEllipsis;
        cmd.ToolTip = entry.Command;
        text.Children.Add(cmd);

        if (!entry.CanToggle)
            text.Children.Add(TweakRow.Hint(Loc.T("optimize.startup.readOnly"), new Thickness(0, 2, 0, 0)));
        else if (entry.RequiresElevation)
            text.Children.Add(TweakRow.Hint(Loc.T("optimize.startup.admin") + " " + Loc.T("net.tweak.status.uacHint") + ".", new Thickness(0, 2, 0, 0)));
        text.Children.Add(busy);
        root.Children.Add(text);
        card.Child = root;
        return card;
    }

    private async Task ToggleStartupAsync(StartupEntry entry)
    {
        bool enable = !entry.Enabled;
        string id = enable ? entry.EnableActionId : entry.DisableActionId;
        // Targets are registered on every list refresh; register again in case the list is from before a restart.
        App.Services.Pc.Startup.Register(Safe, new[] { entry });
        await SafeActionUi.ApplyAsync(Host, id, new SafeApplyOptions
        {
            Confirm = false, // one reversible switch, like Task Manager; the toast carries Undo
            Title = Loc.Tf("optimize.startup.journalTitle", entry.DisplayName),
            OkToast = Loc.Tf(enable ? "optimize.startup.toast.enabled" : "optimize.startup.toast.disabled", entry.DisplayName),
            OfferUndo = true,
            HistoryDetail = id
        });
        await RefreshNowAsync();
    }

    // -- Cache cleanup ---------------------------------------------------------------------------------------

    private void CleanupScan_Click(object sender, RoutedEventArgs e) => _ = ScanCleanupAsync();

    private void CancelCleanupScan()
    {
        try { _cleanupCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Measures every category off the UI thread (never deletes anything).</summary>
    private async Task ScanCleanupAsync()
    {
        if (_cleanupCts != null || _cleaning) return;
        var cts = new CancellationTokenSource();
        _cleanupCts = cts;
        BtnCleanupScan.IsEnabled = false;
        TxtCleanupStatus.Text = Loc.T("optimize.cleanup.scanning");
        try
        {
            var pc = App.Services.Pc;
            var (scan, doStatus) = await Task.Run(() => (pc.Cleanup.Scan(cts.Token), pc.DeliveryOptimization.Read()), cts.Token);
            if (!Host.Window.IsLoaded) return;
            _cleanupScan = scan;
            _doStatus = doStatus;
            RenderCleanup();
        }
        catch (OperationCanceledException)
        {
            if (Host.Window.IsLoaded) RenderCleanup();
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Cleanup scan");
            if (Host.Window.IsLoaded) TxtCleanupStatus.Text = Loc.T("system.unavailable");
        }
        finally
        {
            _cleanupCts = null;
            cts.Dispose();
            if (Host.Window.IsLoaded) BtnCleanupScan.IsEnabled = !_cleaning;
        }
    }

    private void RenderCleanup()
    {
        CleanupList.Children.Clear();
        var scan = _cleanupScan;
        BtnCleanupScan.IsEnabled = _cleanupCts == null && !_cleaning;
        if (scan == null)
        {
            TxtCleanupTotal.Text = "";
            TxtCleanupStatus.Text = Loc.T(_cleanupCts != null ? "optimize.cleanup.scanning" : "optimize.cleanup.notScanned");
            return;
        }
        TxtCleanupTotal.Text = Loc.Tf("optimize.cleanup.total", SystemInfoService.FormatBytes(scan.ReclaimableBytes));
        TxtCleanupStatus.Text = Loc.Tf("optimize.cleanup.scanned", scan.Utc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
        foreach (var c in scan.Categories)
            CleanupList.Children.Add(BuildCleanupRow(c));
    }

    private TweakRow BuildCleanupRow(CleanupCategoryScan c)
    {
        if (c.Category == CleanupCategory.DeliveryOptimization)
            return BuildDoCacheRow();
        string label = Loc.T(CleanupService.CategoryLocKey(c.Category));

        long bytes = c.Bytes ?? 0;
        var row = new TweakRow(label);
        if (!c.Exists)
            row.SetStatus(Loc.T("optimize.cleanup.none"), StatusKind.Ok);
        else
            row.SetStatus(Loc.Tf("optimize.cleanup.size", SystemInfoService.FormatBytes(bytes), c.Files),
                bytes == 0 ? StatusKind.Ok : StatusKind.Unknown);
        if (c.Inaccessible > 0)
            row.AddLine(Loc.Tf("optimize.cleanup.inaccessible", c.Inaccessible));
        foreach (var root in c.Roots)
            row.AddLine(root, trim: true, tooltip: root);
        row.AddAction(Loc.T("optimize.cleanup.clean"), accent: false,
            run: () => CleanAsync(c),
            enabled: bytes > 0 && !_cleaning,
            busyText: Loc.T("optimize.cleanup.cleaning"));
        return row;
    }

    // -- Delivery Optimization cache (one UAC prompt, in-app — no Settings → Storage detour) -------------------

    private TweakRow BuildDoCacheRow()
    {
        var st = _doStatus;
        var row = new TweakRow(Loc.T(CleanupService.CategoryLocKey(CleanupCategory.DeliveryOptimization)));
        if (st?.CacheBytes is { } bytes)
            row.SetStatus(Loc.Tf("optimize.doCache.size", SystemInfoService.FormatBytes(bytes)), bytes == 0 ? StatusKind.Ok : StatusKind.Unknown);
        else if (st?.Reason == DeliveryOptimizationSizeReason.NeedsAdmin)
            row.SetStatus(Loc.T("optimize.doCache.sizeNeedsAdmin"), StatusKind.Unknown);
        else
            row.SetStatus(Loc.T(st == null ? "optimize.cleanup.notScanned" : "optimize.doCache.sizeUnavailable"), StatusKind.Unknown);
        row.SetNeedsAdmin(true);
        row.AddLine(Loc.T("optimize.doCache.note"));
        // Clean is offered when there is something to clean, or when only an administrator could tell.
        bool worth = st != null && (st.CacheBytes is > 0 || st.Reason == DeliveryOptimizationSizeReason.NeedsAdmin);
        row.AddAction(Loc.T("optimize.doCache.clean"), accent: false,
            run: CleanDoCacheAsync,
            enabled: worth && !_cleaning,
            busyText: Loc.T("optimize.ui.busy.uac"));
        return row;
    }

    /// <summary>Confirm → one UAC prompt (helper runs Delete-DeliveryOptimizationCache) → toast → re-scan.</summary>
    private async Task CleanDoCacheAsync()
    {
        if (_cleaning || _cleanupCts != null) return;
        string size = _doStatus?.CacheBytes is { } b ? SystemInfoService.FormatBytes(b) : Loc.T("optimize.doCache.sizeUnknown");
        string title = Loc.T(CleanupService.CategoryLocKey(CleanupCategory.DeliveryOptimization));
        string msg = Loc.Tf("optimize.doCache.confirm", size) + "\n\n" + Loc.T("optimize.doCache.note");
        if (!ThemedDialog.Confirm(Host.Window, msg, title, Loc.T("optimize.cleanup.clean"), danger: true))
            return;

        _cleaning = true;
        BtnCleanupScan.IsEnabled = false;
        try
        {
            var r = await Task.Run(() => App.Services.Pc.DeliveryOptimization.ClearAsync());
            if (r.Status == DeliveryOptimizationCleanStatus.Cleaned)
            {
                App.Services.History.Add("cleanup", detail: CleanupCategory.DeliveryOptimization
                    + (r.FreedBytes is { } f ? "|" + f.ToString(CultureInfo.InvariantCulture) : ""));
                Host.ShowToast(r.FreedBytes is { } freed
                    ? Loc.Tf("optimize.doCache.result", SystemInfoService.FormatBytes(freed))
                    : Loc.T("optimize.doCache.resultUnknown"));
            }
            else
            {
                Host.ShowToast(SafeActionUi.FailText(r.Code ?? SafeActionCodes.ApplyFailed));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Delivery Optimization clean");
            Host.ShowToast(Loc.T("system.unavailable"));
        }
        finally
        {
            _cleaning = false;
        }
        if (Host.Window.IsLoaded)
            await ScanCleanupAsync();
    }

    /// <summary>Confirm (irreversible but safe) → delete one category off the UI thread → toast → re-scan.</summary>
    private async Task CleanAsync(CleanupCategoryScan c)
    {
        if (_cleaning || _cleanupCts != null) return;
        string label = Loc.T(CleanupService.CategoryLocKey(c.Category));
        string msg = Loc.Tf("optimize.cleanup.confirm", SystemInfoService.FormatBytes(c.Bytes ?? 0), label)
                     + "\n\n" + Loc.T("optimize.cleanup.risk")
                     + "\n\n" + Loc.T("optimize.cleanup.undo");
        if (!ThemedDialog.Confirm(Host.Window, msg, Loc.T("optimize.cleanup.title"), Loc.T("optimize.cleanup.clean"), danger: true))
            return;

        _cleaning = true;
        BtnCleanupScan.IsEnabled = false;
        try
        {
            var r = await Task.Run(() => App.Services.Pc.Cleanup.Clean(c.Category));
            if (r.Code is "noRoots" or "infoOnly")
                Host.ShowToast(Loc.T("optimize.cleanup.none"));
            else
            {
                App.Services.History.Add("cleanup", detail: c.Category + "|" + r.FreedBytes.ToString(CultureInfo.InvariantCulture));
                Host.ShowToast(Loc.Tf("optimize.cleanup.result", SystemInfoService.FormatBytes(r.FreedBytes), r.Skipped));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Cleanup " + c.Category);
            Host.ShowToast(Loc.T("system.unavailable"));
        }
        finally
        {
            _cleaning = false;
        }
        if (Host.Window.IsLoaded)
            await ScanCleanupAsync();
    }
}
