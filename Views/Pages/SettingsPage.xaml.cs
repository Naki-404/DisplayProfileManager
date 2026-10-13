using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Visor.Models;
using Visor.Services;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Visor;

/// <summary>
/// Settings section: Defaults (no-game display state), app hotkeys + zoom, activity history, log — the
/// former Tools page — plus the Preferences dialog (language, theme, behavior, sounds, data).
/// </summary>
public partial class SettingsPage : ShellPageBase
{
    private readonly Dictionary<WpfTextBox, string> _hotkeyOriginal = new();

    public SettingsPage(IShellHost host) : base(host)
    {
        InitializeComponent();
    }

    public override ShellSection Section => ShellSection.Settings;
    public override string TitleKey => "nav.settings";

    /// <summary>App-wide capture boxes with their label keys (conflict checks across pages).</summary>
    public IEnumerable<(WpfTextBox Box, string LabelKey)> GlobalHotkeyBoxes
    {
        get
        {
            yield return (HkToggleOverlay, "hotkey.overlay");
            yield return (HkEmergency, "hotkey.emergency");
            yield return (HkNextPreset, "hotkey.next");
            yield return (HkPrevPreset, "hotkey.prev");
            yield return (HkCompareAb, "hotkey.compareAb");
            yield return (HkToggleZoom, "hotkey.zoom");
        }
    }

    public bool IsHotkeyBox(WpfTextBox tb) => GlobalHotkeyBoxes.Any(b => ReferenceEquals(b.Box, tb));

    public override void ApplyLocalization()
    {
        BtnPreferences.Content = Loc.T("shell.preferences");
        BtnPreferences.ToolTip = Loc.T("shell.preferences.tip");
        RbDefaults.Content = Loc.T("tab.global");
        RbHotkeys.Content = Loc.T("settings.tab.hotkeys");
        RbMonitor.Content = Loc.T("settings.tab.monitor");
        LblMonitorTitle.Text = Loc.T("settings.tab.monitor");
        LblMonitorHint.Text = Loc.T("monitor.settings.hint");
        LblPresentMonPath.Text = Loc.T("monitor.settings.path");
        BtnPresentMonBrowse.Content = Loc.T("btn.browse");
        BtnPresentMonLocate.Content = Loc.T("monitor.settings.locate");
        BtnPresentMonDownload.Content = Loc.T("monitor.openDownload");
        BtnPresentMonDownload.ToolTip = MonitorPage.PresentMonReleasesUrl;
        ChkMonitorOsd.Content = Loc.T("monitor.settings.osd");
        LblMonitorOsdHint.Text = Loc.T("monitor.settings.osd.hint");
        DotPresentMon.Label = Loc.T("monitor.settings.path");
        if (RbMonitor.IsChecked == true) RefreshPresentMonStatus(locate: false);
        RbHistory.Content = Loc.T("tab.history");
        RbLog.Content = Loc.T("tab.log");
        LblHistoryHint.Text = Loc.T("history.hint");
        TxtHistoryEmpty.Text = Loc.T("history.empty");
        LblDefaultsTitle.Text = Loc.T("defaults.title");
        LblDefaultsHint.Text = Loc.T("defaults.hint");
        BtnApplyDefaults.Content = Loc.T("btn.applyNow");
        BtnApplyDefaults.ToolTip = Loc.T("defaults.applyNow.tip");
        LblDefaultRes.Text = Loc.T("display.resolution");
        LblDefaultPower.Text = Loc.T("defaults.power");
        ItmDefPowerKeep.Content = Loc.T("power.keep");
        ItmDefPowerBalanced.Content = Loc.T("power.balanced");
        ItmDefPowerHigh.Content = Loc.T("power.high");
        LblAppHotkeys.Text = Loc.T("hotkey.app");
        LblAppHotkeysHint.Text = Loc.T("hotkey.hint");
        LblHkOverlay.Text = Loc.T("hotkey.overlay");
        LblHkEmergency.Text = Loc.T("hotkey.emergency");
        LblHkNext.Text = Loc.T("hotkey.next");
        LblHkPrev.Text = Loc.T("hotkey.prev");
        LblHkAb.Text = Loc.T("hotkey.compareAb");
        LblHkZoom.Text = Loc.T("hotkey.zoom");
        LblHkZoom.ToolTip = Loc.T("zoom.hint");
        foreach (var b in new[] { BtnClearHk0, BtnClearHk1, BtnClearHk2, BtnClearHk3, BtnClearHk4, BtnClearHk5 })
            b.Content = Loc.T("btn.clear");
        LblZoomFactor.Text = Loc.T("zoom.factor");
        LblZoomHint.Text = Loc.T("zoom.hint");
        LblHotkeyCheat.Text = Loc.T("hotkey.cheatsheet");
        LblResetGlobalHint.Text = Loc.T("btn.reset.global.hint");
        BtnReset.Content = Loc.T("btn.reset.global");
        BtnLogRefresh.Content = Loc.T("log.refresh");
        BtnLogFolder.Content = Loc.T("log.openFolder");
        ChkLogWrap.Content = Loc.T("log.wrap");
        foreach (var (box, _) in GlobalHotkeyBoxes)
            box.Tag = Loc.T("hotkey.placeholder");
        if (RbHistory.IsChecked == true) RefreshHistoryPanel();
    }

    public override void OnActivated()
    {
        if (RbLog.IsChecked == true) RefreshLog();
        if (RbMonitor.IsChecked == true) RefreshMonitorGroup();
        if (RbHistory.IsChecked == true) RefreshHistoryPanel();
    }

    /// <summary>History store changed: rebuild the list when it is on screen.</summary>
    public void OnHistoryChanged()
    {
        if (RbHistory?.IsChecked == true && IsLive)
            RefreshHistoryPanel();
    }

    /// <summary>Shell: open the History tab (Home card, command palette).</summary>
    public void ShowHistory()
    {
        RbHistory.IsChecked = true;
        RefreshHistoryPanel();
    }

    public void RefreshLogIfVisible()
    {
        if (RbLog.IsChecked == true) RefreshLog();
    }

    private void Section_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelDefaults == null || PanelLog == null || PanelHistory == null || PanelHotkeys == null || PanelMonitor == null) return;
        bool log = RbLog.IsChecked == true;
        bool history = RbHistory.IsChecked == true;
        bool hotkeys = RbHotkeys.IsChecked == true;
        bool monitor = RbMonitor.IsChecked == true;
        PanelDefaults.Visibility = (!log && !history && !hotkeys && !monitor) ? Visibility.Visible : Visibility.Collapsed;
        PanelHotkeys.Visibility = hotkeys ? Visibility.Visible : Visibility.Collapsed;
        PanelMonitor.Visibility = monitor ? Visibility.Visible : Visibility.Collapsed;
        if (monitor) RefreshMonitorGroup();
        PanelHistory.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        PanelLog.Visibility = log ? Visibility.Visible : Visibility.Collapsed;
        if (history) RefreshHistoryPanel();
        if (log) RefreshLog();
    }

    private void Preferences_Click(object sender, RoutedEventArgs e) => Host.OpenPreferences();

    // -- Config seams (shell ReloadFromConfig / PersistConfigToDisk) ------------------------------------------

    /// <summary>Loads defaults, hotkeys and zoom from the config (inside the shell's editor suppression).</summary>
    public void LoadFromConfig(AppConfig cfg, IReadOnlyList<string> resolutions)
    {
        CmbDefaultRes.Items.Clear();
        foreach (var r in resolutions)
            CmbDefaultRes.Items.Add(r);
        var defRes = cfg.Defaults.Resolution ?? DisplayEngine.GetCurrentResolution();
        if (!string.IsNullOrWhiteSpace(defRes) && !CmbDefaultRes.Items.Contains(defRes))
            CmbDefaultRes.Items.Add(defRes);
        CmbDefaultRes.SelectedItem = defRes;
        ComboUi.SelectPowerPlan(CmbDefaultPower, cfg.Defaults.PowerPlan, "");

        cfg.GlobalHotkeys ??= new GlobalHotkeys();
        HkToggleOverlay.Text = cfg.GlobalHotkeys.ToggleOverlay ?? "";
        HkEmergency.Text = cfg.GlobalHotkeys.EmergencyRestore ?? "";
        HkNextPreset.Text = cfg.GlobalHotkeys.NextPreset ?? "";
        HkPrevPreset.Text = cfg.GlobalHotkeys.PreviousPreset ?? "";
        HkCompareAb.Text = cfg.GlobalHotkeys.CompareAb ?? "";
        HkToggleZoom.Text = cfg.GlobalHotkeys.ToggleZoom ?? "";
        ComboUi.SelectByTag(CmbZoomFactor, (cfg.Ui?.ZoomFactor ?? 4).ToString());

        _loadedPresentMonPath = cfg.Ui?.PresentMonPath ?? "";
        TxtPresentMonPath.Text = _loadedPresentMonPath;
        ChkMonitorOsd.IsChecked = cfg.Ui?.MonitorOsdEnabled != false;
        var svc = MonitorPage.FrameMonitor;
        if (string.IsNullOrWhiteSpace(svc.UserConfiguredPath) && !string.IsNullOrWhiteSpace(_loadedPresentMonPath))
            svc.UserConfiguredPath = _loadedPresentMonPath;
    }

    /// <summary>Writes defaults, hotkeys and zoom from the editors into the config (before save).</summary>
    public void WriteTo(AppConfig cfg)
    {
        cfg.Defaults.Resolution = CmbDefaultRes.SelectedItem?.ToString() ?? cfg.Defaults.Resolution;
        var defPower = (CmbDefaultPower.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        cfg.Defaults.PowerPlan = string.IsNullOrWhiteSpace(defPower) ? null : defPower;

        cfg.GlobalHotkeys ??= new GlobalHotkeys();
        cfg.GlobalHotkeys.ToggleOverlay = HotkeyCapture.Normalize(HkToggleOverlay.Text);
        cfg.GlobalHotkeys.EmergencyRestore = HotkeyCapture.Normalize(HkEmergency.Text);
        cfg.GlobalHotkeys.NextPreset = HotkeyCapture.Normalize(HkNextPreset.Text);
        cfg.GlobalHotkeys.PreviousPreset = HotkeyCapture.Normalize(HkPrevPreset.Text);
        cfg.GlobalHotkeys.CompareAb = HotkeyCapture.Normalize(HkCompareAb.Text);
        cfg.GlobalHotkeys.ToggleZoom = HotkeyCapture.Normalize(HkToggleZoom.Text);
        cfg.Ui ??= new UiPreferences();
        cfg.Ui.ZoomFactor = ReadZoomFactor();

        // The Monitor page may have set the path itself ("Choose PresentMon.exe…"); only an edit made in
        // this box overrides it, so a stale box cannot roll that choice back on the next autosave.
        string box = TxtPresentMonPath.Text.Trim();
        if (!string.Equals(box, _loadedPresentMonPath, StringComparison.Ordinal))
        {
            cfg.Ui.PresentMonPath = string.IsNullOrWhiteSpace(box) ? null : box;
            _loadedPresentMonPath = box;
            MonitorPage.FrameMonitor.UserConfiguredPath = cfg.Ui.PresentMonPath;
            RefreshPresentMonStatus(locate: true);
        }
        cfg.Ui.MonitorOsdEnabled = ChkMonitorOsd.IsChecked != false;
        FpsOsdWindow.Enabled = cfg.Ui.MonitorOsdEnabled;
    }

    // -- Monitor group ---------------------------------------------------------------------------------------

    /// <summary>Path the box was last loaded with / written from — detects edits made in this box.</summary>
    private string _loadedPresentMonPath = "";

    /// <summary>Re-reads the persisted path (the Monitor page may have changed it) and shows the locate result.</summary>
    private void RefreshMonitorGroup()
    {
        if (TxtPresentMonPath == null) return;
        string persisted = App.Services.Config.Current.Ui?.PresentMonPath ?? "";
        if (string.Equals(TxtPresentMonPath.Text.Trim(), _loadedPresentMonPath, StringComparison.Ordinal)
            && !string.Equals(persisted, _loadedPresentMonPath, StringComparison.Ordinal))
        {
            using (Host.SuppressEditor())
                TxtPresentMonPath.Text = persisted;
            _loadedPresentMonPath = persisted;
        }
        RefreshPresentMonStatus(locate: true);
    }

    /// <summary>Shows where PresentMon was found (or why not). Locate never runs the binary.</summary>
    private void RefreshPresentMonStatus(bool locate)
    {
        if (TxtPresentMonStatus == null) return;
        var svc = MonitorPage.FrameMonitor;
        PresentMonLocateResult? r = svc.LastLocate;
        if (locate || r == null)
        {
            try { r = svc.Locate(); }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "PresentMon locate");
                TxtPresentMonStatus.Text = ex.Message;
                DotPresentMon.Status = Visor.Controls.StatusKind.Bad;
                return;
            }
        }
        TxtPresentMonStatus.Text = MonitorPage.DescribeLocate(r);
        DotPresentMon.Status = r.Status switch
        {
            PresentMonStatus.Found => Visor.Controls.StatusKind.Ok,
            PresentMonStatus.Unverified => Visor.Controls.StatusKind.Warn,
            _ => Visor.Controls.StatusKind.Bad
        };
    }

    private void PresentMonPath_TextChanged(object sender, TextChangedEventArgs e) => GlobalEditorChanged(sender, e);

    private void PresentMonLocate_Click(object sender, RoutedEventArgs e)
    {
        Host.FlushAutosave(); // applies a pending edit of the box first
        RefreshPresentMonStatus(locate: true);
    }

    private void PresentMonBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "PresentMon.exe|PresentMon.exe|" + Loc.T("filter.exe"),
            Title = Loc.T("monitor.choosePath"),
            CheckFileExists = true
        };
        if (dlg.ShowDialog(Host.Window) != true) return;
        TxtPresentMonPath.Text = dlg.FileName; // TextChanged → dirty → autosave → WriteTo applies + locates
        Host.FlushAutosave();
    }

    private void PresentMonDownload_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = MonitorPage.PresentMonReleasesUrl, UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Warn(ex, "Open PresentMon releases"); Host.ShowToast(ex.Message); }
    }

    private int ReadZoomFactor()
    {
        if (CmbZoomFactor.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out int f)
            && f >= 2 && f <= 12)
            return f;
        return App.Services.Config.Current.Ui?.ZoomFactor ?? 4;
    }

    // -- Defaults --------------------------------------------------------------------------------------------

    private void GlobalEditorChanged(object sender, RoutedEventArgs e)
    {
        if (Host.SuppressingEditor) return;
        Host.MarkDirty();
    }

    private void ApplyDefaultsNow_Click(object sender, RoutedEventArgs e)
    {
        Host.FlushAutosave();
        if (App.Services.Monitor.HasActivePreset)
        {
            Host.ShowToast(Loc.T("toast.global.kept.preset"));
            return;
        }
        App.Services.Monitor.ClearActivePreset();
        App.Services.Display.RestoreDefaults(App.Services.Config.Current.Defaults);
        Host.ShowToast(Loc.T("toast.defaults.applied"));
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var cfg = App.Services.Config.Current;
        var factory = cfg.FactoryDefaults ?? ConfigService.CaptureFactoryDefaults();
        string res = string.IsNullOrWhiteSpace(factory.Resolution) ? "—" : factory.Resolution!;
        string power = ComboUi.PowerPlanLabel(factory.PowerPlan);
        if (!ThemedDialog.Confirm(Host.Window, Loc.Tf("confirm.resetDefaults", res, power),
                Loc.T("btn.reset.global"), Loc.T("btn.reset"), danger: true))
            return;

        factory.Color = ColorSettings.Neutral;
        cfg.FactoryDefaults = factory.Clone();
        cfg.Defaults = factory.Clone();
        App.Services.Config.Save(cfg, raiseChanged: false);

        using (Host.SuppressEditor())
        {
            if (!string.IsNullOrWhiteSpace(factory.Resolution) && !CmbDefaultRes.Items.Contains(factory.Resolution))
                CmbDefaultRes.Items.Add(factory.Resolution);
            CmbDefaultRes.SelectedItem = factory.Resolution;
            ComboUi.SelectPowerPlan(CmbDefaultPower, factory.PowerPlan, "");
        }

        App.Services.Monitor.EmergencyRestore(historyKind: null);
        Host.ShowToast(Loc.T("toast.defaults.reset"));
    }

    // -- History / log ---------------------------------------------------------------------------------------

    private void RefreshHistoryPanel()
    {
        if (HistoryList == null) return;
        var items = App.Services.History.Recent(ActivityHistoryStore.MaxEntries)
            .Select(HistoryText.Format)
            .ToList();
        HistoryList.ItemsSource = items;
        TxtHistoryEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            Host.ShowToast(ex.Message);
        }
    }

    // -- Hotkey capture (app hotkeys) ---------------------------------------------------------------------

    /// <summary>Localized description of what already uses this gesture, or null when it's free.</summary>
    private string? FindHotkeyConflict(WpfTextBox box, string gesture)
    {
        if (!HotkeyService.TryParseGesture(gesture, out var mods, out var vk)) return null;

        foreach (var (other, labelKey) in GlobalHotkeyBoxes)
        {
            if (ReferenceEquals(other, box)) continue;
            if (HotkeyCapture.SameGesture(other.Text, mods, vk))
                return Loc.Tf("hotkey.conflict.global", Loc.T(labelKey));
        }

        // Preset hotkeys are per game; app hotkeys must not collide with any game's presets.
        foreach (var (game, list) in Host.PresetHotkeyScope())
        {
            foreach (var preset in list)
            {
                if (HotkeyCapture.SameGesture(preset.Hotkey, mods, vk))
                    return Loc.Tf("hotkey.conflict.preset", preset.Name, game.Name);
            }
        }
        return null;
    }

    private void HotkeyBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is WpfTextBox box) HotkeyCapture.OnGotFocus(box, _hotkeyOriginal);
    }

    private void HotkeyBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is WpfTextBox box) HotkeyCapture.OnLostFocus(box);
    }

    private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not WpfTextBox box) return;
        switch (HotkeyCapture.Read(e, out var gesture))
        {
            case HotkeyCaptureKind.Cancel:
                box.Text = _hotkeyOriginal.TryGetValue(box, out var orig) ? orig : box.Text;
                Keyboard.ClearFocus();
                break;
            case HotkeyCaptureKind.Clear:
                CommitHotkey(box, "");
                break;
            case HotkeyCaptureKind.Gesture:
                ApplyCapturedGesture(box, gesture);
                break;
        }
    }

    private void HotkeyBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not WpfTextBox box) return;
        var gesture = HotkeyCapture.ReadMouse(e);
        if (gesture == null) return;
        e.Handled = true;
        box.Focus();
        ApplyCapturedGesture(box, gesture);
    }

    /// <summary>Conflicts are blocked: the old binding stays and the message names who owns the key.</summary>
    private void ApplyCapturedGesture(WpfTextBox box, string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return;
        var conflict = FindHotkeyConflict(box, gesture);
        if (conflict != null)
        {
            UiSound.Warn();
            box.Text = _hotkeyOriginal.TryGetValue(box, out var orig) ? orig : box.Text;
            box.SelectAll();
            Host.ShowToast(Loc.Tf("hotkey.conflict.blocked", gesture, conflict));
            return;
        }
        CommitHotkey(box, gesture);
        box.SelectAll();
    }

    private void CommitHotkey(WpfTextBox box, string gesture)
    {
        box.Text = gesture;
        _hotkeyOriginal[box] = gesture;
        Host.MarkDirty();
        Host.FlushAutosave(); // persists and re-registers hotkeys immediately
        Host.NotifyGlobalHotkeysChanged();
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: WpfTextBox box })
            return;
        CommitHotkey(box, "");
    }
}
