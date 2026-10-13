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

/// <summary>Games page: profile list, editor, companions, scan / import / export, live color (former Play tab).</summary>
public partial class MainWindow : Window
{
    private void GamesListMore_Click(object sender, RoutedEventArgs e) =>
        OpenButtonContextMenu(sender);

    private void GamesEditorMore_Click(object sender, RoutedEventArgs e) =>
        OpenButtonContextMenu(sender);

    private static void OpenButtonContextMenu(object sender)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.ContextMenu == null) return;
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.IsOpen = true;
    }

    private bool _gamesHeaderNarrow;

    /// <summary>
    /// 1366×768 @125 % in Russian leaves ~560 DIP for the editor: the action group (Enabled · Apply · Launch · ⋯)
    /// no longer fits beside the game name, so it drops to a second row and wraps there.
    /// </summary>
    private void GamesHeader_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (GamesHeaderActions == null) return;
        bool narrow = e.NewSize.Width < 600;
        if (narrow == _gamesHeaderNarrow && e.PreviousSize.Width > 0) return;
        _gamesHeaderNarrow = narrow;
        Grid.SetRow(GamesHeaderActions, narrow ? 1 : 0);
        Grid.SetColumn(GamesHeaderActions, narrow ? 0 : 1);
        Grid.SetColumnSpan(GamesHeaderActions, narrow ? 2 : 1);
        GamesHeaderActions.HorizontalAlignment = narrow ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
        GamesHeaderActions.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(8, 0, 0, 0);
    }

    private void GamesEditorSection_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelGamesDisplay == null) return;
        if (_selected == null)
        {
            PanelGamesDisplay.Visibility = Visibility.Collapsed;
            PanelGamesColor.Visibility = Visibility.Collapsed;
            PanelGamesSession.Visibility = Visibility.Collapsed;
            PanelGamesCompanions.Visibility = Visibility.Collapsed;
            StopDesktopPreview();
            return;
        }

        bool color = RbGamesColor?.IsChecked == true;
        bool session = RbGamesSession?.IsChecked == true;
        bool companions = RbGamesCompanions?.IsChecked == true;

        PanelGamesDisplay.Visibility = (!color && !session && !companions) ? Visibility.Visible : Visibility.Collapsed;
        PanelGamesColor.Visibility = color ? Visibility.Visible : Visibility.Collapsed;
        PanelGamesSession.Visibility = session ? Visibility.Visible : Visibility.Collapsed;
        PanelGamesCompanions.Visibility = companions ? Visibility.Visible : Visibility.Collapsed;

        if (!color)
            StopDesktopPreview();
        else
            RefreshGamesTab();
    }

    private void MaybeFirstScan()
    {
        // Key off FirstScanDone only — the default config already contains a few core profiles.
        var cfg = App.Services.Config.Current;
        if (cfg.FirstScanDone) return;
        cfg.FirstScanDone = true;
        App.Services.Config.Save(cfg, raiseChanged: false);
        _ = RunGameScanAsync(confirmIfAny: true);
    }

    // -- Library ------------------------------------------------------------------------------------------

    private bool LibraryFilter(object item)
    {
        var q = TxtSearch?.Text?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return item is GameProfile p &&
               ((p.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (p.ProcessName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_libraryView == null) return;
        _libraryView.Refresh();
        // Never leave the editor on a game the list doesn't show — pick the first visible one.
        if (!SuppressingEditor && ProfileList.SelectedItem == null && !_libraryView.IsEmpty)
            SelectProfile(_libraryView.Cast<GameProfile>().First());
        UpdateLibraryEmpty();
    }

    private void UpdateLibraryEmpty()
    {
        if (LibraryEmpty == null) return;
        bool none = _profiles.Count == 0;
        bool noMatch = !none && _libraryView.IsEmpty;
        LibraryEmpty.Visibility = none || noMatch ? Visibility.Visible : Visibility.Collapsed;
        LblLibraryEmpty.Text = none ? Loc.T("library.empty") : Loc.Tf("library.noMatch", TxtSearch.Text.Trim());
        BtnLibraryEmptyAction.Content = none ? Loc.T("btn.scanGames") : Loc.T("btn.clearSearch");
    }

    private void LibraryEmptyAction_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0)
            ScanGames_Click(sender, e);
        else
            TxtSearch.Text = "";
    }

    /// <summary>
    /// Selects a game in the Games list and editor. If the search filter hides it, the
    /// filter is cleared first so the editor never edits an invisible game.
    /// </summary>
    private void SelectProfile(GameProfile? p)
    {
        if (_selected != null && !ReferenceEquals(_selected, p) && _dirty)
            PushEditorToSelected();

        using (SuppressEditor())
        {
            if (p != null && !_libraryView.Contains(p))
            {
                TxtSearch.Text = "";
                _libraryView.Refresh();
            }
            ProfileList.SelectedItem = p;
        }
        _selected = p;
        LoadEditor(p);
        if (p != null)
            ProfileList.ScrollIntoView(p);
        UpdateLibraryEmpty();
        RefreshGamesTab();
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SuppressingEditor) return;
        var next = ProfileList.SelectedItem as GameProfile;
        if (ReferenceEquals(next, _selected)) return;
        if (_dirty && _selected != null) PushEditorToSelected();
        _selected = next;
        LoadEditor(_selected);
        RefreshGamesTab();
    }

    private void RefreshGameTiles()
    {
        using (SuppressEditor())
        {
            ProfileList.Items.Refresh();
            PresetGameList.Items.Refresh();
            CmbPresetGame.Items.Refresh();
            if (_selected != null)
                ProfileList.SelectedItem = _selected;
            if (_presetGame != null)
                PresetGameList.SelectedItem = _presetGame;
        }
    }

    private void SetGamesEditorChromeEnabled(bool has)
    {
        EditorPanel.IsEnabled = has;
        PlayEditor.IsEnabled = has;
        PanelGamesSession.IsEnabled = has;
        PanelGamesCompanions.IsEnabled = has;
        BtnDelete.IsEnabled = has;
        BtnLaunchGame.IsEnabled = has;
        BtnApplyProfileNow.IsEnabled = has;
        ChkEnabled.IsEnabled = has;
        BtnGamesEditorMore.IsEnabled = has;
        MiDup.IsEnabled = has;
        MiExportProfile.IsEnabled = has;
        RbGamesDisplay.IsEnabled = has;
        RbGamesColor.IsEnabled = has;
        RbGamesSession.IsEnabled = has;
        RbGamesCompanions.IsEnabled = has;
        if (!has)
        {
            PanelGamesDisplay.Visibility = Visibility.Collapsed;
            PanelGamesColor.Visibility = Visibility.Collapsed;
            PanelGamesSession.Visibility = Visibility.Collapsed;
            PanelGamesCompanions.Visibility = Visibility.Collapsed;
            LblEditorEmpty.Visibility = Visibility.Visible;
            StopDesktopPreview();
        }
        else
        {
            LblEditorEmpty.Visibility = Visibility.Collapsed;
            GamesEditorSection_Checked(this, new RoutedEventArgs());
        }
    }

    private void LoadEditor(GameProfile? p)
    {
        using (SuppressEditor())
        {
            bool has = p != null;
            SetGamesEditorChromeEnabled(has);

            if (p == null)
            {
                ClearEditor();
                return;
            }

            ChkEnabled.IsChecked = p.Enabled;
            TxtName.Text = p.Name;
            TxtProcess.Text = p.ProcessName;
            TxtAliases.Text = p.ProcessAliases is { Count: > 0 } ? string.Join(", ", p.ProcessAliases) : "";
            ChkApplyRes.IsChecked = p.ApplyResolution;
            ChkApplyPower.IsChecked = p.ApplyPowerPlan;
            p.ApplyColor = false;

            SelectComboByTag(CmbDisplay, p.DisplayDevice ?? "");
            if (string.IsNullOrWhiteSpace(p.Resolution))
            {
                CmbResolution.SelectedIndex = -1;
                CmbResolution.Text = "";
            }
            else
            {
                if (!CmbResolution.Items.Contains(p.Resolution)) CmbResolution.Items.Add(p.Resolution);
                CmbResolution.SelectedItem = p.Resolution;
                CmbResolution.Text = p.Resolution;
            }

            RefreshRefreshRateCombo(p.RefreshRate);
            FillStartupPresetCombo(p, p.StartupPresetId);
            SldApplyDelay.Value = Math.Clamp(p.ApplyDelaySeconds, 0, 120);
            ChkApplyOnFocus.IsChecked = p.ApplyOnFocus;
            ChkSoftAltTab.IsChecked = p.SoftRestoreOnAltTab;
            SelectOrSetFpsLimit(CmbFpsLimit, p.FpsLimit);

            SelectPowerPlan(CmbPower, p.PowerPlan, "highPerformance");
            SelectComboByTag(CmbRestoreMode, p.RestoreMode.ToString());
            var sx = p.Session ?? new SessionExtras();
            SldDeferred.Value = sx.DeferredApplySeconds;
            ChkQuietToast.IsChecked = sx.QuietNotifications;
            ChkNoAutoHdr.IsChecked = sx.DisableAutoHdr;
            var layout = sx.MonitorLayout;
            if (string.IsNullOrWhiteSpace(layout))
                layout = sx.IsolatePrimaryMonitor ? "isolatePrimary" : "keepAll";
            SelectComboByTag(CmbMonitorLayout, layout);
            ChkMonBright.IsChecked = sx.ApplyMonitorBrightness;
            SldMonBright.Value = sx.MonitorBrightness;
            ChkSwitchAudio.IsChecked = sx.SwitchAudioDevice;
            SelectAudioDevice(sx.AudioDeviceId);
            SelectComboByTag(CmbScaling, string.IsNullOrWhiteSpace(sx.ScalingMode) ? "default" : sx.ScalingMode!);
            RefreshCompanionList(p);
            UpdateSessionLabels();
            UpdateColorSummary(p);
            UpdateDependentEnables();
        }
        // Keep _dirty — profile switch must not pretend changes were saved to disk
    }

    /// <summary>Resets every editor field so no stale value from the previous game can leak into a save.</summary>
    private void ClearEditor()
    {
        using (SuppressEditor())
        {
            ChkEnabled.IsChecked = false;
            TxtName.Text = "";
            TxtProcess.Text = "";
            TxtAliases.Text = "";
            ChkApplyRes.IsChecked = false;
            CmbResolution.SelectedIndex = -1;
            CmbResolution.Text = "";
            RefreshRefreshRateCombo(0);
            SelectComboByTag(CmbDisplay, "");
            ChkApplyPower.IsChecked = false;
            SelectPowerPlan(CmbPower, null, "highPerformance");
            FillStartupPresetCombo(null, "");
            SldApplyDelay.Value = 0;
            ChkApplyOnFocus.IsChecked = false;
            ChkSoftAltTab.IsChecked = false;
            SelectOrSetFpsLimit(CmbFpsLimit, 0);
            SelectComboByTag(CmbRestoreMode, nameof(RestoreMode.PreviousSnapshot));
            SldDeferred.Value = 0;
            ChkQuietToast.IsChecked = false;
            ChkNoAutoHdr.IsChecked = false;
            SelectComboByTag(CmbMonitorLayout, "keepAll");
            ChkMonBright.IsChecked = false;
            SldMonBright.Value = 80;
            ChkSwitchAudio.IsChecked = false;
            SelectAudioDevice(null);
            SelectComboByTag(CmbScaling, "default");
            RefreshCompanionList(null);
            UpdateColorSummary(null);
            UpdateSessionLabels();
            UpdateDependentEnables();
        }
    }

    /// <summary>Controls that only matter when their checkbox is on are disabled otherwise.</summary>
    private void UpdateDependentEnables()
    {
        ResolutionPanel.IsEnabled = ChkApplyRes.IsChecked == true;
        CmbPower.IsEnabled = ChkApplyPower.IsChecked == true;
        MonBrightPanel.IsEnabled = ChkMonBright.IsChecked == true;
        CmbAudio.IsEnabled = ChkSwitchAudio.IsChecked == true;
        CmbPresetRes.IsEnabled = ChkPresetRes.IsChecked == true;
    }

    private void EditorChanged(object sender, RoutedEventArgs e)
    {
        if (SuppressingEditor || _selected == null) return;

        // Turning on "Change resolution" with an empty box: start from the current desktop mode.
        if (ReferenceEquals(sender, ChkApplyRes) && ChkApplyRes.IsChecked == true && string.IsNullOrWhiteSpace(ReadResolutionText()))
        {
            using (SuppressEditor())
            {
                var cur = DisplayEngine.GetCurrentResolution();
                if (!string.IsNullOrWhiteSpace(cur))
                {
                    if (!CmbResolution.Items.Contains(cur)) CmbResolution.Items.Add(cur);
                    CmbResolution.SelectedItem = cur;
                    CmbResolution.Text = cur;
                    RefreshRefreshRateCombo();
                }
            }
        }

        UpdateDependentEnables();
        MarkDirty();
        PushEditorToSelected();
        UpdateColorSummary(_selected);
        RefreshGamesQuickFields();
        if (ReferenceEquals(sender, TxtName) || ReferenceEquals(sender, TxtProcess) || ReferenceEquals(sender, ChkEnabled))
        {
            _tileRefreshTimer.Stop();
            _tileRefreshTimer.Start();
        }
    }

    private void EditorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SuppressingEditor) return;
        UpdateSessionLabels();
        if (_selected == null) return;
        MarkDirty();
        PushEditorToSelected();
    }

    private void Display_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SuppressingEditor) return;
        RefreshRefreshRateCombo();
        EditorChanged(sender, e);
    }

    private void FillDisplays(bool keepSelection = false)
    {
        string? keep = keepSelection ? (CmbDisplay.SelectedItem as ComboBoxItem)?.Tag?.ToString() : null;
        using (SuppressEditor())
        {
            CmbDisplay.Items.Clear();
            CmbDisplay.Items.Add(new ComboBoxItem { Content = Loc.T("display.monitor.default"), Tag = "" });
            foreach (var d in DisplayEngine.GetDisplays())
            {
                var label = d.Primary ? $"{d.Friendly} ★" : d.Friendly;
                CmbDisplay.Items.Add(new ComboBoxItem { Content = label, Tag = d.DeviceName, ToolTip = d.DeviceName });
            }
            SelectComboByTag(CmbDisplay, keep ?? "");
        }
    }

    private void LoadResolutions()
    {
        CmbResolution.Items.Clear();
        foreach (var r in DisplayEngine.GetAvailableResolutions())
            CmbResolution.Items.Add(r);
    }

    private void Resolution_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SuppressingEditor) return;
        RefreshRefreshRateCombo();
        EditorChanged(sender, e);
    }

    private void Resolution_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SuppressingEditor || _selected == null || CmbResolution.IsKeyboardFocusWithin) return;
        var typed = ReadResolutionText();
        if (typed != null && !DisplayEngine.TryParseRes(typed, out _, out _))
        {
            ShowToast(Loc.Tf("display.res.invalid", typed));
            using (SuppressEditor())
                CmbResolution.Text = _selected.Resolution ?? "";
            return;
        }
        RefreshRefreshRateCombo();
        MarkDirty();
        PushEditorToSelected();
    }

    private string? ReadResolutionText()
    {
        var text = CmbResolution.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
            return text.Replace('×', 'x').Replace(" ", "");
        var sel = CmbResolution.SelectedItem?.ToString();
        return string.IsNullOrWhiteSpace(sel) ? null : sel;
    }

    private void RefreshRefreshRateCombo(int preferHz = -1)
    {
        using (SuppressEditor())
        {
            int keep = preferHz >= 0
                ? preferHz
                : (CmbRefreshRate.SelectedItem is ComboBoxItem cur && int.TryParse(cur.Tag?.ToString(), out var hz) ? hz : 0);

            var res = ReadResolutionText();
            string? device = (CmbDisplay.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (string.IsNullOrWhiteSpace(device)) device = null;

            CmbRefreshRate.Items.Clear();
            CmbRefreshRate.Items.Add(new ComboBoxItem { Content = Loc.T("display.refresh.none"), Tag = "0" });
            if (!string.IsNullOrWhiteSpace(res))
            {
                foreach (var rate in DisplayEngine.GetAvailableRefreshRates(res, device))
                    CmbRefreshRate.Items.Add(new ComboBoxItem { Content = $"{rate} Hz", Tag = rate.ToString() });
            }
            SelectComboByTag(CmbRefreshRate, keep.ToString());
        }
    }

    private void FillStartupPresetCombo(GameProfile? p, string? preferId = null)
    {
        string? keep = preferId ?? p?.StartupPresetId;
        CmbStartupPreset.Items.Clear();
        CmbStartupPreset.Items.Add(new ComboBoxItem { Content = Loc.T("profile.startup.none"), Tag = "" });
        if (p?.Presets != null)
        {
            foreach (var pr in p.Presets)
                CmbStartupPreset.Items.Add(new ComboBoxItem { Content = pr.Name, Tag = pr.Id });
        }
        SelectComboByTag(CmbStartupPreset, keep ?? "");
    }

    /// <summary>Library "Presets" section: what this game has and what applies on start.</summary>
    private void UpdateColorSummary(GameProfile? p)
    {
        if (p == null)
        {
            LblColorSummary.Text = "";
            return;
        }
        var presets = p.Presets ?? new List<QuickPreset>();
        if (presets.Count == 0)
        {
            LblColorSummary.Text = Loc.T("library.presets.none");
            return;
        }
        var lines = new List<string> { Loc.Tf("library.presets.count", presets.Count, string.Join(", ", presets.Select(x => x.Name))) };
        var startup = !string.IsNullOrWhiteSpace(p.StartupPresetId)
            ? presets.FirstOrDefault(pr => pr.Id == p.StartupPresetId)
            : null;
        lines.Add(startup != null
            ? Loc.Tf("library.presets.startup", startup.Name, ColorUiHelper.Describe(startup))
            : Loc.T("color.summary.none"));
        LblColorSummary.Text = string.Join("\n", lines);
    }

    private void PushEditorToSelected()
    {
        if (_selected == null) return;
        _selected.Enabled = ChkEnabled.IsChecked == true;
        _selected.Name = TxtName.Text.Trim();
        _selected.ProcessName = TxtProcess.Text.Trim();
        _selected.ProcessAliases = ParseAliases(TxtAliases.Text);
        _selected.ApplyResolution = ChkApplyRes.IsChecked == true;
        _selected.ApplyPowerPlan = ChkApplyPower.IsChecked == true;
        _selected.ApplyColor = false;

        var res = ReadResolutionText();
        _selected.Resolution = res != null && DisplayEngine.TryParseRes(res, out _, out _) ? res : _selected.Resolution;
        if (string.IsNullOrWhiteSpace(res)) _selected.Resolution = null;

        _selected.RefreshRate = CmbRefreshRate.SelectedItem is ComboBoxItem hzItem && int.TryParse(hzItem.Tag?.ToString(), out var hz)
            ? hz
            : 0;

        if (CmbStartupPreset.SelectedItem is ComboBoxItem sp)
            _selected.StartupPresetId = string.IsNullOrWhiteSpace(sp.Tag?.ToString()) ? null : sp.Tag!.ToString();

        _selected.ApplyDelaySeconds = (int)SldApplyDelay.Value;
        _selected.ApplyOnFocus = ChkApplyOnFocus.IsChecked == true;
        _selected.SoftRestoreOnAltTab = ChkSoftAltTab.IsChecked == true;
        _selected.FpsLimit = ReadFpsLimit(CmbFpsLimit);

        if (CmbPower.SelectedItem is ComboBoxItem pi)
            _selected.PowerPlan = string.IsNullOrWhiteSpace(pi.Tag?.ToString()) ? null : pi.Tag!.ToString();

        if (CmbDisplay.SelectedItem is ComboBoxItem di)
            _selected.DisplayDevice = string.IsNullOrWhiteSpace(di.Tag?.ToString()) ? null : di.Tag!.ToString();

        if (CmbRestoreMode.SelectedItem is ComboBoxItem rm
            && Enum.TryParse<RestoreMode>(rm.Tag?.ToString(), out var restoreMode))
            _selected.RestoreMode = restoreMode;

        _selected.Session ??= new SessionExtras();
        _selected.Session.DeferredApplySeconds = (int)SldDeferred.Value;
        _selected.Session.QuietNotifications = ChkQuietToast.IsChecked == true;
        _selected.Session.DisableAutoHdr = ChkNoAutoHdr.IsChecked == true;
        if (CmbMonitorLayout.SelectedItem is ComboBoxItem layoutItem)
        {
            var layout = layoutItem.Tag?.ToString() ?? "keepAll";
            _selected.Session.MonitorLayout = layout;
            // IsolatePrimaryMonitor mirrors the layout combo for older consumers.
            _selected.Session.IsolatePrimaryMonitor =
                string.Equals(layout, "isolatePrimary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(layout, "primaryOnly", StringComparison.OrdinalIgnoreCase);
        }
        _selected.Session.ApplyMonitorBrightness = ChkMonBright.IsChecked == true;
        _selected.Session.MonitorBrightness = (int)SldMonBright.Value;
        _selected.Session.SwitchAudioDevice = ChkSwitchAudio.IsChecked == true;
        var audioTag = (CmbAudio.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        _selected.Session.AudioDeviceId = string.IsNullOrWhiteSpace(audioTag) ? null : audioTag;
        if (CmbScaling.SelectedItem is ComboBoxItem sc)
            _selected.Session.ScalingMode = sc.Tag?.ToString() ?? "default";
    }

    private static List<string> ParseAliases(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => GameCatalog.Normalize(s.Trim()))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FpsOffText => Loc.T("fps.off");

    private static void SelectOrSetFpsLimit(System.Windows.Controls.ComboBox combo, int fps)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem ci && string.Equals(ci.Tag?.ToString(), fps.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = ci;
                combo.Text = ci.Content?.ToString() ?? fps.ToString();
                return;
            }
        }
        combo.SelectedItem = null;
        combo.Text = fps > 0 ? fps.ToString() : FpsOffText;
    }

    private static int ReadFpsLimit(System.Windows.Controls.ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem ci && int.TryParse(ci.Tag?.ToString(), out var fromTag)
            && string.Equals(combo.Text?.Trim(), ci.Content?.ToString(), StringComparison.OrdinalIgnoreCase))
            return Math.Clamp(fromTag, 0, 1023);

        var typed = combo.Text?.Trim() ?? "";
        if (typed.Length == 0 || string.Equals(typed, FpsOffText, StringComparison.OrdinalIgnoreCase))
            return 0;
        var digits = new string(typed.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? Math.Clamp(n, 0, 1023) : 0;
    }

    private void FpsLimit_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SuppressingEditor || _selected == null || CmbFpsLimit.IsKeyboardFocusWithin) return;
        int fps = ReadFpsLimit(CmbFpsLimit);
        using (SuppressEditor())
            SelectOrSetFpsLimit(CmbFpsLimit, fps);
        MarkDirty();
        PushEditorToSelected();
    }

    private void UpdateFpsLimitAvailability()
    {
        bool available = FpsLimitService.IsNvidiaAvailable;
        LblFpsLimitHint.Text = available
            ? Loc.T("profile.fpsLimit.hint")
            : Loc.T("profile.fpsLimit.unavailable");
        CmbFpsLimit.ToolTip = LblFpsLimitHint.Text;
    }

    private void UpdateSessionLabels()
    {
        LblDeferred.Text = Loc.Tf("session.deferred", (int)SldDeferred.Value);
        LblMonBright.Text = Loc.Tf("session.bright.lbl", (int)SldMonBright.Value);
        LblApplyDelay.Text = Loc.Tf("profile.delay", (int)SldApplyDelay.Value);
    }

    /// <summary>Keeps the Play tab's quick fields in sync without rebuilding chips (preserves a "Live" chip).</summary>
    private void RefreshGamesQuickFields()
    {
        if (_playProfile == null || _selected == null || _selected.Id != _playProfile.Id) return;
        TxtPlayName.Text = _playProfile.Name;
        TxtPlayProcess.Text = _playProfile.ProcessName;
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var p = new GameProfile
        {
            Name = Loc.T("profile.new"),
            ProcessName = "",
            ApplyResolution = false,
            ApplyColor = false,
            ApplyPowerPlan = true,
            Color = ColorSettings.Neutral
        };
        _profiles.Add(p);
        GameVisuals.Apply(p);
        SelectProfile(p);
        MarkDirty();
        MainTabs.SelectedItem = TabGames;
        RbGamesDisplay.IsChecked = true;
        Dispatcher.BeginInvoke(() =>
        {
            TxtName.Focus();
            TxtName.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>Copy = new identity: new Id, new preset Ids, no hotkeys, disabled (no two profiles fight over one exe).</summary>
    private void DupProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        PushEditorToSelected();
        var copy = _selected.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = Loc.Tf("profile.copyName", _selected.Name);
        copy.Enabled = false;
        var idMap = new Dictionary<string, string>();
        foreach (var pr in copy.Presets ?? new List<QuickPreset>())
        {
            var newId = copy.Id + "_" + Guid.NewGuid().ToString("N")[..8];
            idMap[pr.Id] = newId;
            pr.Id = newId;
            pr.Hotkey = null;
        }
        copy.StartupPresetId = copy.StartupPresetId != null && idMap.TryGetValue(copy.StartupPresetId, out var mapped)
            ? mapped
            : null;

        int index = _profiles.IndexOf(_selected);
        _profiles.Insert(index + 1, copy);
        GameVisuals.Apply(copy);
        SelectProfile(copy);
        MarkDirty();
        ShowToast(Loc.Tf("toast.duplicated", copy.Name));
    }

    private bool ConfirmDestructive(string message, string title, string actionText)
    {
        if (App.Services.Config.Current.Ui?.ConfirmDelete == false)
            return true;
        return ThemedDialog.Confirm(this, message, title, actionText, danger: true);
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var victim = _selected;
        string name = string.IsNullOrWhiteSpace(victim.Name) ? victim.ProcessName : victim.Name;
        if (!ConfirmDestructive(Loc.Tf("confirm.deleteProfile", name), Loc.T("confirm.deleteProfile.title"), Loc.T("btn.delete")))
            return;

        PushEditorToSelected();
        int index = _profiles.IndexOf(victim);
        bool wasPresetGame = ReferenceEquals(_presetGame, victim);
        if (wasPresetGame)
        {
            // Detach first so SyncPresetsBackToGame can't write into the removed profile.
            PushPresetEditorIfDirty();
            SyncPresetsBackToGame();
            _presetGame = null;
        }

        using (SuppressEditor())
            _profiles.Remove(victim);
        var next = _profiles.Count == 0 ? null : _profiles[Math.Clamp(index, 0, _profiles.Count - 1)];
        _selected = null;
        SelectProfile(next);
        if (wasPresetGame) SelectPresetGame(next);
        MarkDirty();
        FlushAutosave();

        ShowUndoToast(Loc.Tf("toast.deleted", name), () =>
        {
            using (SuppressEditor())
                _profiles.Insert(Math.Clamp(index, 0, _profiles.Count), victim);
            SelectProfile(victim);
            MarkDirty();
            FlushAutosave();
            ShowToast(Loc.Tf("toast.restored", name));
        });
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.T("filter.exe"),
            Title = Loc.T("profile.browse.title")
        };
        if (dlg.ShowDialog(this) != true) return;

        using (SuppressEditor())
        {
            TxtProcess.Text = Path.GetFileName(dlg.FileName);
            if (string.IsNullOrWhiteSpace(TxtName.Text) || TxtName.Text == Loc.T("profile.new"))
                TxtName.Text = GameCatalog.GetFriendlyName(TxtProcess.Text)
                               ?? Path.GetFileNameWithoutExtension(dlg.FileName);
        }
        _selected.ExePath = dlg.FileName;
        PushEditorToSelected();
        GameVisuals.Apply(_selected);
        RefreshGameTiles();
        MarkDirty();
        RefreshGamesTab();
    }

    private void PickRunning_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var procs = ProcessWatcher.GetRunningExeNames().ToList();
        var pick = new ProcessPickerWindow(procs) { Owner = this };
        if (pick.ShowDialog() != true || string.IsNullOrWhiteSpace(pick.SelectedProcess)) return;

        using (SuppressEditor())
        {
            TxtProcess.Text = pick.SelectedProcess;
            if (string.IsNullOrWhiteSpace(TxtName.Text) || TxtName.Text == Loc.T("profile.new"))
                TxtName.Text = GameCatalog.GetFriendlyName(pick.SelectedProcess)
                               ?? Path.GetFileNameWithoutExtension(pick.SelectedProcess);
        }
        MarkDirty();
        PushEditorToSelected();
        GameVisuals.Apply(_selected);
        RefreshGameTiles();
    }

    // -- Companions ------------------------------------------------------------------------------------------

    private void RefreshCompanionList(GameProfile? p, CompanionApp? select = null)
    {
        CompanionList.ItemsSource = null;
        CompanionList.ItemsSource = p?.Companions;
        if (select != null) CompanionList.SelectedItem = select;
        bool empty = p?.Companions == null || p.Companions.Count == 0;
        LblCompanionsEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        UpdateCompanionButtons();
    }

    private void UpdateCompanionButtons()
    {
        bool sel = CompanionList.SelectedItem is CompanionApp;
        BtnCompanionEdit.IsEnabled = sel;
        BtnCompanionRemove.IsEnabled = sel;
    }

    private void CompanionList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCompanionButtons();

    private void CompanionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CompanionList.SelectedItem is CompanionApp)
            EditCompanion_Click(sender, e);
    }

    private void AddCompanion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var c = new CompanionApp();
        var dlg = new CompanionEditWindow(c) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _selected.Companions ??= new List<CompanionApp>();
        _selected.Companions.Add(c);
        RefreshCompanionList(_selected, c);
        MarkDirty();
    }

    private void EditCompanion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || CompanionList.SelectedItem is not CompanionApp c) return;
        var dlg = new CompanionEditWindow(c) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        RefreshCompanionList(_selected, c);
        MarkDirty();
    }

    private void RemoveCompanion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || CompanionList.SelectedItem is not CompanionApp c) return;
        var owner = _selected;
        string label = CompanionEditWindow.Describe(c);
        if (!ConfirmDestructive(Loc.Tf("confirm.removeCompanion", label), Loc.T("confirm.removeCompanion.title"), Loc.T("btn.remove")))
            return;
        int index = owner.Companions.IndexOf(c);
        owner.Companions.Remove(c);
        RefreshCompanionList(owner);
        MarkDirty();
        FlushAutosave();
        ShowUndoToast(Loc.T("toast.companion.removed"), () =>
        {
            owner.Companions.Insert(Math.Clamp(index, 0, owner.Companions.Count), c);
            if (ReferenceEquals(_selected, owner))
                RefreshCompanionList(owner, c);
            MarkDirty();
            FlushAutosave();
        });
    }

    // -- Library: scan / import / export / restore -------------------------------------------------------

    private async void ScanGames_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunGameScanAsync(confirmIfAny: false);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Scan games");
            ScanBusy.HideAnimated();
            ThemedDialog.Show(this, Loc.T("scan.failed") + "\n" + ex.Message, Loc.T("scan.title"));
        }
    }

    /// <summary>True while a scan runs — guards the palette / buttons against a second concurrent scan.</summary>
    private bool _scanRunning;

    private async Task RunGameScanAsync(bool confirmIfAny)
    {
        if (_scanRunning) return;
        _scanRunning = true;
        try
        {
            await RunGameScanCoreAsync(confirmIfAny);
        }
        finally
        {
            _scanRunning = false;
        }
    }

    private async Task RunGameScanCoreAsync(bool confirmIfAny)
    {
        ScanBusy.ShowAnimated();
        UiSound.BeginWork();

        var minDelay = Task.Delay(1200);
        List<GameScanner.FoundGame> found;
        try
        {
            found = await Task.Run(() =>
                GameScanner.ScanAll(msg => Dispatcher.BeginInvoke(() => ScanBusy.SetStatus(msg))));
        }
        catch (Exception ex)
        {
            UiSound.EndWork(UiWorkResult.Cancel);
            UiSound.Error();
            ScanBusy.HideAnimated();
            AppLog.Error(ex, "Game scan");
            ThemedDialog.Show(this, Loc.T("scan.failed") + "\n" + ex.Message, Loc.T("scan.title"));
            return;
        }

        await minDelay;
        ScanBusy.HideAnimated();

        if (found.Count == 0)
        {
            UiSound.EndWork(UiWorkResult.Empty);
            ShowToast(Loc.T("scan.none"));
            return;
        }

        UiSound.EndWork(UiWorkResult.Done);

        var fresh = found.Where(g => !_profiles.Any(p =>
            string.Equals(p.ProcessName, g.Process, StringComparison.OrdinalIgnoreCase))).ToList();
        if (confirmIfAny && fresh.Count > 0)
        {
            var names = string.Join(", ", fresh.Select(f => f.Name).Distinct());
            if (!ThemedDialog.Confirm(this, Loc.Tf("scan.found", names), Loc.T("scan.title"), Loc.T("scan.addAll")))
                return;
        }

        var added = AddFoundGames(found);
        ShowToast(added == 0 ? Loc.T("scan.allListed") : Loc.Tf("scan.added", added));
    }

    private int AddFoundGames(List<GameScanner.FoundGame> found)
    {
        int added = 0;
        GameProfile? last = null;
        foreach (var g in found)
        {
            var existing = _profiles.FirstOrDefault(p =>
                string.Equals(p.ProcessName, g.Process, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (string.IsNullOrWhiteSpace(existing.ExePath) && !string.IsNullOrWhiteSpace(g.ExePath))
                {
                    existing.ExePath = g.ExePath;
                    GameVisuals.Apply(existing);
                    MarkDirty();
                }
                continue;
            }

            var profile = GameCatalog.CreateProfileSkeleton(g.Process, g.Name, g.ExePath);
            GameVisuals.Apply(profile);
            _profiles.Add(profile);
            last = profile;
            added++;
        }
        if (added > 0 || _dirty)
        {
            MarkDirty();
            RefreshGameTiles();
        }
        if (last != null)
            SelectProfile(last);
        UpdateLibraryEmpty();
        return added;
    }

    private void RestoreCoreGames_Click(object sender, RoutedEventArgs e)
    {
        int added = 0;
        GameProfile? last = null;
        foreach (var core in GameCatalog.CreateCoreProfiles())
        {
            if (_profiles.Any(p => string.Equals(p.ProcessName, core.ProcessName, StringComparison.OrdinalIgnoreCase)))
                continue;
            _profiles.Add(core);
            GameVisuals.Apply(core);
            last = core;
            added++;
        }
        if (added > 0)
        {
            MarkDirty();
            SelectProfile(last);
            UiSound.Done();
            ShowToast(Loc.Tf("toast.restoredGames", added));
        }
        else
        {
            UiSound.Empty();
            ShowToast(Loc.T("toast.restoredGames.none"));
        }
        UpdateLibraryEmpty();
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            ShowToast(Loc.T("profile.pick"));
            return;
        }
        PushEditorToSelected();
        if (ProfilePackService.Export(_selected))
            ShowToast(Loc.T("toast.profile.exported"));
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var imported = ProfilePackService.Import();
        if (imported == null) return;
        GameVisuals.Apply(imported);
        _profiles.Add(imported);
        SelectProfile(imported);
        MarkDirty();
        FlushAutosave();
        ShowToast(Loc.Tf("toast.profile.imported", imported.Name));
    }

    private void LaunchGame_Click(object sender, RoutedEventArgs e) => LaunchProfile(_selected);

    private void LaunchProfile(GameProfile? profile)
    {
        if (profile == null)
        {
            ShowToast(Loc.T("profile.pick"));
            return;
        }

        string? path = profile.ExePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ThemedDialog.Show(this, Loc.T("launch.noPath"), Loc.T("btn.launchGame"));
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                UseShellExecute = true
            });
            ShowToast(Loc.Tf("toast.launching", profile.Name));
            UiSound.Launch();
        }
        catch (Exception ex)
        {
            UiSound.Error();
            ThemedDialog.Show(this, Loc.T("launch.failed") + "\n" + ex.Message, Loc.T("btn.launchGame"));
        }
    }

    /// <summary>Library: push this game's display/power settings to the desktop right now (test without launching).</summary>
    private void ApplyProfileNow_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        PushEditorToSelected();
        MarkDirty();
        FlushAutosave();
        var cfg = App.Services.Config.Current;
        var live = cfg.Profiles.FirstOrDefault(p => p.Id == _selected.Id) ?? _selected;
        App.Services.Monitor.ClearActivePreset();
        App.Services.Display.ApplyProfile(live, cfg.Defaults);
        ShowToast(Loc.Tf("toast.applied.profile", _selected.Name));
        // Flash the control the user just pressed (ActivePill may be collapsed).
        UiMotion.FlashSuccess(BtnApplyProfileNow);
    }

    private void Overlay_Click(object sender, RoutedEventArgs e)
    {
        FlushAutosave();
        if (System.Windows.Application.Current is App app)
            app.ToggleGameOverlay();
    }

    private void EmergencyRestore_Click(object sender, RoutedEventArgs e)
    {
        _previewOnDesktop = false;
        App.Services.Zoom.Off();
        App.Services.Monitor.EmergencyRestore();
        RefreshGamesTab();
        ShowToast(Loc.T("toast.emergency"));
    }

    private void OpenPresets_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = TabPresets;
        if (_selected != null)
            SelectPresetGame(_selected);
    }

    // -- Play tab ----------------------------------------------------------------------------------------

    // Color slider rows (ColorSliderRow control) — the old Sld*/Lbl*/Val* names stay reachable.
    private Slider SldPlayB => RowPlayB.Slider;
    private TextBlock LblPlayB => RowPlayB.LabelBlock;
    private TextBlock ValPlayB => RowPlayB.ValueBlock;
    private Slider SldPlayC => RowPlayC.Slider;
    private TextBlock LblPlayC => RowPlayC.LabelBlock;
    private TextBlock ValPlayC => RowPlayC.ValueBlock;
    private Slider SldPlayG => RowPlayG.Slider;
    private TextBlock LblPlayG => RowPlayG.LabelBlock;
    private TextBlock ValPlayG => RowPlayG.ValueBlock;
    private Slider SldPlayV => RowPlayV.Slider;
    private TextBlock LblPlayV => RowPlayV.LabelBlock;
    private TextBlock ValPlayV => RowPlayV.ValueBlock;
    private Slider SldPlayH => RowPlayH.Slider;
    private TextBlock LblPlayH => RowPlayH.LabelBlock;
    private TextBlock ValPlayH => RowPlayH.ValueBlock;
    private Slider SldPlayS => RowPlayS.Slider;
    private TextBlock LblPlayS => RowPlayS.LabelBlock;
    private TextBlock ValPlayS => RowPlayS.ValueBlock;

    private bool PlayIsRunning =>
        _playProfile != null && App.Services.Monitor.CurrentProfile?.Id == _playProfile.Id;

    /// <summary>Sliders touch the real display only for the running game, or when the user opted into a desktop preview.</summary>
    private bool PlayCanApplyLive => PlayIsRunning || _previewOnDesktop;

    public void RefreshGamesTab()
    {
        if (TxtPlayName == null || SuppressingPlay) return;
        var profile = _selected;
        if (!ReferenceEquals(profile, _playProfile))
        {
            StopDesktopPreview();
            _playPreset = null;
            _playLive = false;
        }
        _playProfile = profile;

        using (SuppressPlay())
        {
            bool anyRunning = App.Services.Monitor.CurrentProfile != null;
            BtnLaunchGame.IsEnabled = profile != null;
            BtnApplyProfileNow.IsEnabled = profile != null;

            if (profile == null)
            {
                TxtPlayName.Text = Loc.T("play.noGame");
                TxtPlayProcess.Text = "";
                PlayStatusPill.Visibility = Visibility.Collapsed;
                PlayPresetChips.Children.Clear();
                TxtPlayHotkeys.Text = "";
                ChkPlayPreview.Visibility = Visibility.Collapsed;
                PlaySliders.IsEnabled = false;
                BtnPlayEditPreset.Visibility = Visibility.Collapsed;
                SetPlayHint(Loc.T("play.hint.pick"), active: false);
                _playPreset = null;
                return;
            }

            bool running = PlayIsRunning;
            TxtPlayName.Text = profile.Name;
            TxtPlayProcess.Text = profile.ProcessName;
            PlayStatusPill.Visibility = Visibility.Visible;
            TxtPlayStatus.Text = running ? Loc.T("play.status.active") : Loc.T("play.status.idle");
            PlayStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, running ? "SuccessBrush" : "MutedBrush");

            ChkPlayPreview.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            ChkPlayPreview.IsEnabled = !anyRunning;
            ChkPlayPreview.IsChecked = _previewOnDesktop;

            if (_playLive && !PlayCanApplyLive) _playLive = false;
            string? activePresetId = running && !_playLive ? App.Services.Monitor.ActivePresetId : null;
            QuickPreset? preset = _playLive ? null : activePresetId != null
                ? profile.Presets?.FirstOrDefault(p => p.Id == activePresetId)
                : (_playPreset != null ? profile.Presets?.FirstOrDefault(p => p.Id == _playPreset.Id) : null);
            if (!_playLive)
            {
                if (preset == null && !string.IsNullOrWhiteSpace(profile.StartupPresetId))
                    preset = profile.Presets?.FirstOrDefault(p => p.Id == profile.StartupPresetId);
                preset ??= profile.Presets?.FirstOrDefault();
            }
            _playPreset = preset;

            RebuildPlayPresetChips(profile, preset);
            LoadPlaySlidersFrom(preset);
            PlaySliders.IsEnabled = preset != null || PlayCanApplyLive;
            BtnPlayEditPreset.Visibility = preset != null ? Visibility.Visible : Visibility.Collapsed;
            BtnPlayEditPreset.Content = preset != null ? Loc.Tf("play.editPreset", preset.Name) : "";
            UpdatePlayHint();
            RefreshPlayHotkeyCheatSheet(profile);
        }
    }

    private void UpdatePlayHint()
    {
        if (_playProfile == null) return;
        string presetName = _playPreset?.Name ?? "";
        if (PlayIsRunning)
            SetPlayHint(_playPreset != null
                ? Loc.Tf("play.hint.running", _playProfile.Name, presetName)
                : Loc.Tf("play.hint.runningLive", _playProfile.Name), active: true);
        else if (_previewOnDesktop)
            SetPlayHint(Loc.T("play.hint.preview"), active: true);
        else if (App.Services.Monitor.CurrentProfile != null)
            SetPlayHint(Loc.Tf("play.hint.otherRunning", App.Services.Monitor.CurrentProfile.Name), active: false);
        else if (_playPreset == null)
            SetPlayHint(Loc.T("play.hint.noPresets"), active: false);
        else
            SetPlayHint(Loc.Tf("play.hint.idle", _playProfile.Name, presetName), active: false);
    }

    private void SetPlayHint(string text, bool active)
    {
        LblPlayColorHint.Text = text;
        PlayHintDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, active ? "SuccessBrush" : "MutedBrush");
    }

    private void RebuildPlayPresetChips(GameProfile profile, QuickPreset? selected)
    {
        PlayPresetChips.Children.Clear();

        if (PlayCanApplyLive)
        {
            var liveChip = MakePlayChip(Loc.T("play.liveChip"), selected == null, Loc.T("play.liveChip.tip"));
            liveChip.Click += (_, _) => PlayPresetChip_Click(null);
            PlayPresetChips.Children.Add(liveChip);
        }

        foreach (var p in profile.Presets ?? new List<QuickPreset>())
        {
            string tip = ColorUiHelper.Describe(p) + (string.IsNullOrWhiteSpace(p.Hotkey) ? "" : "  ·  " + p.Hotkey);
            var chip = MakePlayChip(p.Name, selected != null && selected.Id == p.Id, tip);
            chip.Click += (_, _) => PlayPresetChip_Click(p);
            PlayPresetChips.Children.Add(chip);
        }

        if ((profile.Presets?.Count ?? 0) == 0)
        {
            var add = MakePlayChip("+ " + Loc.T("play.addPreset"), false, Loc.T("presets.add.tip"));
            add.Click += (_, _) =>
            {
                MainTabs.SelectedItem = TabPresets;
                SelectPresetGame(profile);
                AddPreset_Click(add, new RoutedEventArgs());
            };
            PlayPresetChips.Children.Add(add);
        }
    }

    private System.Windows.Controls.Button MakePlayChip(string text, bool selected, string? tip) => new()
    {
        Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 },
        ToolTip = tip,
        Style = (Style)FindResource(selected ? "ChipButtonSelected" : "ChipButton"),
        Margin = new Thickness(0, 0, 8, 8)
    };

    private void PlayPresetChip_Click(QuickPreset? preset)
    {
        if (_playProfile == null) return;
        _playPreset = preset;
        _playLive = preset == null;
        using (SuppressPlay())
        {
            RebuildPlayPresetChips(_playProfile, preset);
            LoadPlaySlidersFrom(preset);
            PlaySliders.IsEnabled = preset != null || PlayCanApplyLive;
            BtnPlayEditPreset.Visibility = preset != null ? Visibility.Visible : Visibility.Collapsed;
            BtnPlayEditPreset.Content = preset != null ? Loc.Tf("play.editPreset", preset.Name) : "";
        }
        UpdatePlayHint();

        if (preset == null) return;
        if (PlayIsRunning)
            App.Services.Monitor.ApplyPreset(preset);
        else if (_previewOnDesktop)
        {
            var c = (preset.Color ?? ColorSettings.Neutral).Clone();
            c.Clamp();
            App.Services.Display.ApplyColor(c);
        }
        // Not running and no preview: the chip only selects which preset the sliders edit.
    }

    private void LoadPlaySlidersFrom(QuickPreset? preset)
    {
        ColorSettings source;
        if (preset != null)
        {
            preset.EnsureDualColorSlots();
            source = preset.Color.Clone();
        }
        else
        {
            source = App.Services.Display.LiveColor.Clone();
        }
        _playBackend = source.Backend;

        using (SuppressPlay())
        {
            ColorUiHelper.ApplyColorSliders(source, SldPlayB, SldPlayC, SldPlayG, SldPlayV, SldPlayS, SldPlayH);
            bool low = ColorUiHelper.IsLowLevel(_playBackend);
            SldPlayS.IsEnabled = low;
            SldPlayH.IsEnabled = !low;
            RefreshPlayValues();
        }
    }

    private void RefreshPlayValues()
    {
        ColorUiHelper.UpdateValues(ValPlayB, ValPlayC, ValPlayG, ValPlayV, SldPlayB, SldPlayC, SldPlayG, SldPlayV,
            _playBackend, ValPlayH, SldPlayH, ValPlayS, SldPlayS);
    }

    private void PlaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SuppressingPlay || !IsInitialized || _playProfile == null) return;
        RefreshPlayValues();

        var c = ColorUiHelper.ReadColorFromSliders(_playBackend, SldPlayB, SldPlayC, SldPlayG, SldPlayV, SldPlayS,
            lockColor: _playPreset?.Color?.LockColor ?? true, hue: SldPlayH);
        if (PlayCanApplyLive)
            App.Services.Display.ApplyColor(c);

        if (_playPreset != null)
        {
            _playPreset.ApplyColor = true;
            _playPreset.Color = c;
            _playPreset.SaveActiveToDualSlots();
            MarkDirty();

            if (PlayIsRunning && App.Services.Monitor.ActivePresetId == _playPreset.Id)
                App.Services.Monitor.SetColorLockPaused(false);

            if (ReferenceEquals(_playPreset, _selectedPreset))
            {
                PushPresetEditorIfDirty(); // keep pending name edits, then reload colors from the preset
                _playPreset.Color = c;
                _playPreset.SaveActiveToDualSlots();
                LoadPresetEditor(_selectedPreset); // runs inside SuppressEditor()
            }
        }
    }

    private void PlayPreview_Changed(object sender, RoutedEventArgs e)
    {
        if (SuppressingPlay || _playProfile == null) return;
        bool on = ChkPlayPreview.IsChecked == true;
        if (on)
        {
            _previewOnDesktop = true;
            var c = ColorUiHelper.ReadColorFromSliders(_playBackend, SldPlayB, SldPlayC, SldPlayG, SldPlayV, SldPlayS,
                lockColor: false, hue: SldPlayH);
            App.Services.Display.ApplyColor(c);
        }
        else
        {
            StopDesktopPreview();
        }
        RefreshGamesTab();
    }

    /// <summary>Ends a desktop preview and puts the factory ramp back (only when no game owns the display).</summary>
    private void StopDesktopPreview()
    {
        if (!_previewOnDesktop) return;
        _previewOnDesktop = false;
        if (App.Services.Monitor.CurrentProfile == null)
        {
            try { App.Services.Display.ApplyNeutralOrFactoryRamp(); }
            catch (Exception ex) { AppLog.Warn(ex, "Stop desktop preview"); }
        }
        if (ChkPlayPreview != null)
        {
            using (SuppressPlay())
                ChkPlayPreview.IsChecked = false;
        }
    }

    private void PlayEditPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_playProfile == null) return;
        var presetId = _playPreset?.Id;
        MainTabs.SelectedItem = TabPresets;
        SelectPresetGame(_playProfile, presetId);
    }

    private void RefreshPlayHotkeyCheatSheet(GameProfile profile)
    {
        var gh = App.Services.Config.Current.GlobalHotkeys;
        var lines = new List<string>();

        void AddGlobal(string? gesture, string labelKey)
        {
            if (!string.IsNullOrWhiteSpace(gesture))
                lines.Add($"{gesture}  —  {Loc.T(labelKey)}");
        }
        AddGlobal(gh?.ToggleOverlay, "hotkey.overlay");
        AddGlobal(gh?.EmergencyRestore, "hotkey.emergency");
        AddGlobal(gh?.NextPreset, "hotkey.next");
        AddGlobal(gh?.PreviousPreset, "hotkey.prev");
        AddGlobal(gh?.CompareAb, "hotkey.compareAb");
        AddGlobal(gh?.ToggleZoom, "hotkey.zoom");

        if (profile.Presets != null)
        {
            foreach (var p in profile.Presets.Where(p => !string.IsNullOrWhiteSpace(p.Hotkey)))
                lines.Add($"{p.Hotkey}  —  {p.Name}");
        }

        TxtPlayHotkeys.Text = lines.Count > 0 ? string.Join("\n", lines) : Loc.T("play.hotkeys.none");
    }

    // -- Audio / session ------------------------------------------------------------------------------------

    private void FillAudioDevices()
    {
        var prev = (CmbAudio.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        using (SuppressEditor())
        {
            CmbAudio.Items.Clear();
            CmbAudio.Items.Add(new ComboBoxItem { Content = Loc.T("session.audio.none"), Tag = "" });
            try
            {
                foreach (var d in AudioEndpoint.ListDevices())
                    CmbAudio.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "Enumerate audio devices");
            }
            SelectAudioDevice(prev);
        }
    }

    private void SelectAudioDevice(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            CmbAudio.SelectedIndex = 0;
            return;
        }
        foreach (var item in CmbAudio.Items)
        {
            if (item is ComboBoxItem ci && string.Equals(ci.Tag?.ToString(), id, StringComparison.OrdinalIgnoreCase))
            {
                CmbAudio.SelectedItem = ci;
                return;
            }
        }
        CmbAudio.SelectedIndex = 0;
    }

    private void SessionTemplateCompetitive_Click(object sender, RoutedEventArgs e)
        => ApplySessionTemplate(quiet: true, hdr: true, layout: "isolatePrimary", scaling: "stretch");

    private void SessionTemplateImmersive_Click(object sender, RoutedEventArgs e)
        => ApplySessionTemplate(quiet: true, hdr: false, layout: "keepAll", scaling: "default", bright: true, brightPct: 70);

    private void SessionTemplateMinimal_Click(object sender, RoutedEventArgs e)
        => ApplySessionTemplate(quiet: false, hdr: false, layout: "keepAll", scaling: "default");

    private void ApplySessionTemplate(bool quiet, bool hdr, string layout, string scaling,
        bool bright = false, int brightPct = 80)
    {
        if (_selected == null) return;
        using (SuppressEditor())
        {
            ChkQuietToast.IsChecked = quiet;
            ChkNoAutoHdr.IsChecked = hdr;
            SelectComboByTag(CmbMonitorLayout, layout);
            SelectComboByTag(CmbScaling, scaling);
            ChkMonBright.IsChecked = bright;
            if (bright) SldMonBright.Value = brightPct;
            SldDeferred.Value = 0;
        }
        UpdateSessionLabels();
        UpdateDependentEnables();
        MarkDirty();
        PushEditorToSelected();
        ShowToast(Loc.T("toast.session.template"));
    }
}
