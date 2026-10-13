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

/// <summary>Tools page: defaults, history, log.</summary>
public partial class MainWindow : Window
{
    private void RefreshHistoryPanel()
    {
        if (HistoryList == null) return;
        var items = App.Services.History.Recent(ActivityHistoryStore.MaxEntries)
            .Select(FormatHistoryEntry)
            .ToList();
        HistoryList.ItemsSource = items;
        TxtHistoryEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatHistoryEntry(ActivityHistoryEntry entry)
    {
        string when = entry.Utc.ToLocalTime().ToString("g");
        string body = entry.Kind switch
        {
            "profileApplied" => string.IsNullOrEmpty(entry.Detail)
                ? Loc.Tf("history.kind.profileApplied", entry.ProfileName ?? "?")
                : Loc.Tf("history.kind.profileApplied.preset", entry.ProfileName ?? "?", entry.Detail),
            "presetApplied" => Loc.Tf("history.kind.presetApplied",
                entry.Detail ?? "?", entry.ProfileName ?? "?"),
            "sessionRestored" => string.IsNullOrEmpty(entry.ProfileName)
                ? Loc.T("history.kind.sessionRestored")
                : Loc.Tf("history.kind.sessionRestored.named", entry.ProfileName),
            "emergency" => Loc.T("history.kind.emergency"),
            "paused" => Loc.T("history.kind.paused"),
            "resumed" => Loc.T("history.kind.resumed"),
            "leftoverRestored" => Loc.T(entry.Success
                ? "history.kind.leftoverRestored"
                : "history.kind.leftoverRestored.fail"),
            "safeAction" => Loc.Tf("history.kind.safeAction", FormatSafeActionDetail(entry.Detail)),
            _ => entry.Kind
        };
        if (!entry.Success && entry.Kind is not "leftoverRestored")
            body += " · " + Loc.T("history.failed");
        return when + "  ·  " + body;
    }

    private void MoreSection_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelDefaults == null || PanelLog == null || PanelHistory == null) return;
        bool log = RbMoreLog.IsChecked == true;
        bool history = RbMoreHistory.IsChecked == true;
        PanelDefaults.Visibility = (!log && !history) ? Visibility.Visible : Visibility.Collapsed;
        PanelHistory.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        PanelLog.Visibility = log ? Visibility.Visible : Visibility.Collapsed;
        if (history) RefreshHistoryPanel();
        if (log) RefreshLog();
    }

    // -- Defaults / More -------------------------------------------------------------------------------

    private void GlobalEditorChanged(object sender, RoutedEventArgs e)
    {
        if (SuppressingEditor) return;
        MarkDirty();
    }

    private void ApplyDefaultsNow_Click(object sender, RoutedEventArgs e)
    {
        FlushAutosave();
        if (App.Services.Monitor.HasActivePreset)
        {
            ShowToast(Loc.T("toast.global.kept.preset"));
            return;
        }
        App.Services.Monitor.ClearActivePreset();
        App.Services.Display.RestoreDefaults(App.Services.Config.Current.Defaults);
        ShowToast(Loc.T("toast.defaults.applied"));
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var cfg = App.Services.Config.Current;
        var factory = cfg.FactoryDefaults ?? ConfigService.CaptureFactoryDefaults();
        string res = string.IsNullOrWhiteSpace(factory.Resolution) ? "—" : factory.Resolution!;
        string power = PowerPlanLabel(factory.PowerPlan);
        if (!ThemedDialog.Confirm(this, Loc.Tf("confirm.resetDefaults", res, power),
                Loc.T("btn.reset.global"), Loc.T("btn.reset"), danger: true))
            return;

        factory.Color = ColorSettings.Neutral;
        cfg.FactoryDefaults = factory.Clone();
        cfg.Defaults = factory.Clone();
        App.Services.Config.Save(cfg, raiseChanged: false);

        using (SuppressEditor())
        {
            if (!string.IsNullOrWhiteSpace(factory.Resolution) && !CmbDefaultRes.Items.Contains(factory.Resolution))
                CmbDefaultRes.Items.Add(factory.Resolution);
            CmbDefaultRes.SelectedItem = factory.Resolution;
            SelectPowerPlan(CmbDefaultPower, factory.PowerPlan, "");
        }

        App.Services.Monitor.EmergencyRestore(historyKind: null);
        ShowToast(Loc.T("toast.defaults.reset"));
    }

    private void RefreshLog_Click(object sender, RoutedEventArgs e) => RefreshLog();

    private void RefreshLog()
    {
        try
        {
            TxtLog.Text = string.Join(Environment.NewLine, AppLog.Tail(400));
            TxtLog.ScrollToEnd();
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Refresh log view");
        }
    }

    private void LogWrap_Changed(object sender, RoutedEventArgs e)
    {
        if (TxtLog == null) return;
        bool wrap = ChkLogWrap.IsChecked == true;
        TxtLog.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        TxtLog.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = AppLog.LogFilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            else if (!string.IsNullOrWhiteSpace(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Open log");
            ShowToast(ex.Message);
        }
    }
}
