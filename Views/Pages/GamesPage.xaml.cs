using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Visor.Models;
using Visor.Services;

namespace Visor;

/// <summary>
/// Games section: profile list, editor (Display · Color · Session · Companions · Presets), scan / import /
/// export. The former top-level Presets page is the Presets section of this page (see GamesPage.Presets.cs).
/// </summary>
public partial class GamesPage : ShellPageBase
{
    private readonly ObservableCollection<QuickPreset> _presets = new();
    /// <summary>Live, filterable view used by the list only.</summary>
    private readonly ListCollectionView _libraryView;
    private readonly DispatcherTimer _tileRefreshTimer;
    private readonly DispatcherTimer _presetListRefreshTimer;

    private GameProfile? _selected;
    private string? _lastActiveProfileId;
    private bool _gamesHeaderNarrow;
    /// <summary>True while a scan runs — guards the palette / buttons against a second concurrent scan.</summary>
    private bool _scanRunning;

    private ObservableCollection<GameProfile> _profiles => Host.Profiles;
    private bool SuppressingEditor => Host.SuppressingEditor;
    private IDisposable SuppressEditor() => Host.SuppressEditor();
    private void MarkDirty() => Host.MarkDirty();
    private void FlushAutosave() => Host.FlushAutosave();
    private void ShowToast(string message) => Host.ShowToast(message);

    public GamesPage(IShellHost host) : base(host)
    {
        InitializeComponent();
        _libraryView = new ListCollectionView(_profiles) { Filter = LibraryFilter };
        ProfileList.ItemsSource = _libraryView;
        PresetList.ItemsSource = _presets;

        _tileRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _tileRefreshTimer.Tick += (_, _) =>
        {
            _tileRefreshTimer.Stop();
            RefreshGameTiles();
        };
        _presetListRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _presetListRefreshTimer.Tick += (_, _) =>
        {
            _presetListRefreshTimer.Stop();
            RefreshPresetListSafe();
        };

        LoadResolutions();
        FillDisplays();
        FillAudioDevices();
        // Nested MaxHeight list inside the page ScrollViewer — don't let it trap the wheel.
        NestedScroll.ForwardWheelAtEdge(CompanionList);
    }

    public override ShellSection Section => ShellSection.Games;
    public override string TitleKey => "nav.games";

    /// <summary>True while a scan runs (command palette enablement).</summary>
    public bool ScanRunning => _scanRunning;

    /// <summary>Selected game (editor copy), if any.</summary>
    public GameProfile? SelectedProfile => _selected;

    /// <summary>Game whose preset hotkeys stay registered when no match is running.</summary>
    public GameProfile? HotkeyPresetFallback => _presetGame ?? _selected;

    /// <summary>Selected game for the overlay when no game is running (the live config instance, not the editor copy).</summary>
    public GameProfile? SelectedProfileForOverlay =>
        _selected == null ? null : App.Services.Config.Current.Profiles.FirstOrDefault(p => p.Id == _selected.Id);

    public void StopTimers()
    {
        _tileRefreshTimer.Stop();
        _presetListRefreshTimer.Stop();
    }

    // -- Shell lifecycle ----------------------------------------------------------------------------------

    public override void OnActivated()
    {
        if (RbGamesColor?.IsChecked == true)
            RefreshGamesTab();
        if (RbGamesPresets?.IsChecked == true)
            LoadPresetEditor(_selectedPreset); // Color-section sliders may have edited the same preset object
    }

    public override void OnDeactivated() => StopDesktopPreview();

    /// <summary>Shell: the running game changed — jump the editor to it once per change (user can still pick another).</summary>
    public void OnActiveProfileChanged(GameProfile? profile)
    {
        string? activeId = profile?.Id;
        if (activeId != _lastActiveProfileId)
        {
            _lastActiveProfileId = activeId;
            if (activeId != null)
            {
                var match = _profiles.FirstOrDefault(p => p.Id == activeId);
                if (match != null && !ReferenceEquals(match, _selected))
                    SelectProfile(match);
            }
        }
        RefreshGamesTab();
    }

    /// <summary>Ctrl+F.</summary>
    public void FocusSearch()
    {
        Dispatcher.BeginInvoke(() =>
        {
            TxtSearch.Focus();
            TxtSearch.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>Esc inside the search box clears it. Returns true when handled.</summary>
    public bool TryClearSearchOnEscape()
    {
        if (!ReferenceEquals(Keyboard.FocusedElement, TxtSearch) || TxtSearch.Text.Length == 0) return false;
        TxtSearch.Text = "";
        return true;
    }

    /// <summary>Del removes the selected item of the focused list (with confirmation). Returns true when handled.</summary>
    public bool HandleDeleteKey()
    {
        if (ProfileList.IsKeyboardFocusWithin && _selected != null)
        {
            DeleteProfile_Click(this, new RoutedEventArgs());
            return true;
        }
        if (PresetList.IsKeyboardFocusWithin && _selectedPreset != null)
        {
            DeletePreset_Click(this, new RoutedEventArgs());
            return true;
        }
        if (CompanionList.IsKeyboardFocusWithin && CompanionList.SelectedItem != null)
        {
            RemoveCompanion_Click(this, new RoutedEventArgs());
            return true;
        }
        return false;
    }

    public bool IsHotkeyBox(System.Windows.Controls.TextBox tb) => ReferenceEquals(tb, TxtPresetHotkey);

    /// <summary>Shell "Reset display" ran: the desktop preview no longer owns the ramp.</summary>
    public void OnEmergencyRestored()
    {
        _previewOnDesktop = false;
        RefreshGamesTab();
    }

    // -- Reload / persist seams (called by the shell) --------------------------------------------------

    /// <summary>Editor pointers to restore after the profile collection is rebuilt.</summary>
    public (string? ProfileId, string? PresetId) CaptureSelection() => (_selected?.Id, _selectedPreset?.Id);

    /// <summary>Drop editor pointers before the shell clears the collections — avoids syncing into orphaned profiles.</summary>
    public void DetachEditors()
    {
        _selectedPreset = null;
        _presetGame = null;
        _selected = null;
        _playProfile = null;
        _playPreset = null;
        _presets.Clear();
    }

    public void ReloadResolutionLists(IReadOnlyList<string> resolutions)
    {
        CmbPresetRes.Items.Clear();
        foreach (var r in resolutions)
            CmbPresetRes.Items.Add(r);
    }

    /// <summary>After the shell refilled <see cref="IShellHost.Profiles"/>: re-select and reload the editors.</summary>
    public void RestoreSelection(string? keepProfileId, string? keepPresetId)
    {
        var keep = _profiles.FirstOrDefault(p => p.Id == keepProfileId) ?? _profiles.FirstOrDefault();
        SelectProfile(keep, keepPresetId);
        UpdateLibraryEmpty();
        RefreshGamesTab();
    }

    /// <summary>Autosave: write every pending editor value into the profile objects.</summary>
    public void PushPendingEdits()
    {
        if (_selected != null) PushEditorToSelected();
        PushPresetEditorIfDirty();
        SyncPresetsBackToGame();
    }

    /// <summary>Game started-up scan, once (window Loaded).</summary>
    public void MaybeFirstScan()
    {
        // Key off FirstScanDone only — the default config already contains a few core profiles.
        var cfg = App.Services.Config.Current;
        if (cfg.FirstScanDone) return;
        cfg.FirstScanDone = true;
        App.Services.Config.Save(cfg, raiseChanged: false);
        _ = RunGameScanAsync(confirmIfAny: true);
    }

    // -- Localization -----------------------------------------------------------------------------------

    public override void ApplyLocalization()
    {
        BtnGamesListMore.Content = "⋯";
        BtnGamesEditorMore.Content = "⋯";
        BtnGamesListMore.ToolTip = Loc.T("games.more");
        BtnGamesEditorMore.ToolTip = Loc.T("games.more");
        // "⋯" has no readable text of its own.
        System.Windows.Automation.AutomationProperties.SetName(BtnGamesListMore, Loc.T("games.more"));
        System.Windows.Automation.AutomationProperties.SetName(BtnGamesEditorMore, Loc.T("games.more"));
        RbGamesDisplay.Content = Loc.T("games.tab.display");
        RbGamesColor.Content = Loc.T("games.tab.color");
        RbGamesSession.Content = Loc.T("games.tab.session");
        RbGamesCompanions.Content = Loc.T("games.tab.companions");
        RbGamesPresets.Content = Loc.T("tab.presets");
        MiDup.Header = Loc.T("btn.dup");
        MiExportProfile.Header = Loc.T("profile.export");
        MiImportProfile.Header = Loc.T("profile.import");
        MiRestore.Header = Loc.T("btn.restore");
        MiOverlay.Header = Loc.T("btn.overlay");
        MiOpenPresets.Header = Loc.T("btn.openPresets");
        // The ⋯ menus replaced toolbar buttons; their explanations live on as menu tooltips.
        MiDup.ToolTip = Loc.T("btn.dup.tip");
        MiExportProfile.ToolTip = Loc.T("profile.export.tip");
        MiImportProfile.ToolTip = Loc.T("profile.import.tip");
        MiRestore.ToolTip = Loc.T("btn.restore.tip");
        MiOverlay.ToolTip = Loc.T("btn.overlay.tip");

        // Color section (live)
        LblPlayColor.Text = Loc.T("play.color");
        LblPlayPresets.Text = Loc.T("play.presets");
        ChkPlayPreview.Content = Loc.T("play.preview");
        ChkPlayPreview.ToolTip = Loc.T("play.preview.tip");
        ExpPlayHotkeys.Header = Loc.T("play.hotkeys");
        ColorUiHelper.ApplyLabelNames(LblPlayB, LblPlayC, LblPlayG, LblPlayV, LblPlayH, LblPlayS);
        LblPlayH.ToolTip = Loc.T("color.hue.tip");
        LblPlayS.ToolTip = Loc.T("color.shadow.tip");
        LblPlayV.ToolTip = Loc.T("color.vibrance.tip");

        // List + Display
        TxtSearch.Tag = Loc.T("search.placeholder");
        TxtSearch.ToolTip = Loc.T("search.tip");
        System.Windows.Automation.AutomationProperties.SetName(TxtSearch, Loc.T("search.placeholder"));
        BtnScan.Content = Loc.T("btn.scan");
        BtnScan.ToolTip = Loc.T("btn.scan.tip");
        BtnAddManual.Content = Loc.T("btn.addManual");
        BtnAddManual.ToolTip = Loc.T("btn.addManual.tip");
        BtnDelete.Content = Loc.T("btn.delete");
        BtnDelete.ToolTip = Loc.T("btn.delete.tip");
        LblProfileTitle.Text = Loc.T("profile.title");
        ChkEnabled.Content = Loc.T("profile.enabled");
        ChkEnabled.ToolTip = Loc.T("profile.enabled.tip");
        LblName.Text = Loc.T("profile.name");
        LblProcess.Text = Loc.T("profile.process");
        BtnBrowseExe.Content = Loc.T("btn.browse");
        BtnBrowseExe.ToolTip = Loc.T("btn.browse.tip");
        BtnPickRunning.Content = Loc.T("btn.running");
        BtnPickRunning.ToolTip = Loc.T("btn.running.tip");
        BtnLaunchGame.Content = Loc.T("btn.launchGame");
        BtnLaunchGame.ToolTip = Loc.T("btn.launchGame.tip");
        BtnApplyProfileNow.Content = Loc.T("btn.applyNow");
        BtnApplyProfileNow.ToolTip = Loc.T("profile.applyNow.tip");
        LblDisplaySection.Text = Loc.T("display");
        ChkApplyRes.Content = Loc.T("display.changeRes");
        LblResolution.Text = Loc.T("display.resolution");
        LblRefreshRate.Text = Loc.T("display.refresh");
        LblDisplay.Text = Loc.T("display.monitor");
        ChkApplyPower.Content = Loc.T("display.changePower");
        ItmPowerHigh.Content = Loc.T("power.high");
        ItmPowerBalanced.Content = Loc.T("power.balanced");
        LblPresetsSection.Text = Loc.T("tab.presets");
        LblStartupPreset.Text = Loc.T("profile.startup");
        ExpAdvanced.Header = Loc.T("profile.advanced");
        LblAliases.Text = Loc.T("profile.aliases");
        TxtAliases.ToolTip = Loc.T("profile.aliases.tip");
        ChkApplyOnFocus.Content = Loc.T("profile.focus");
        ChkSoftAltTab.Content = Loc.T("profile.softAltTab");
        LblFpsLimit.Text = Loc.T("profile.fpsLimit");
        ItmFpsOff.Content = Loc.T("fps.off");
        LblRestoreMode.Text = Loc.T("restore.mode.lbl");
        ItmRestoreSnapshot.Content = Loc.T("restore.mode.snapshot");
        ItmRestoreDefaults.Content = Loc.T("restore.mode.defaults");
        ItmRestoreNothing.Content = Loc.T("restore.mode.nothing");
        ExpSession.Header = Loc.T("session.title");
        LblSessionSub.Text = Loc.T("session.sub");
        LblSessionTemplates.Text = Loc.T("session.templates");
        BtnTplCompetitive.Content = Loc.T("session.tpl.competitive");
        BtnTplImmersive.Content = Loc.T("session.tpl.immersive");
        BtnTplMinimal.Content = Loc.T("session.tpl.minimal");
        BtnTplCompetitive.ToolTip = Loc.T("session.tpl.competitive.tip");
        BtnTplImmersive.ToolTip = Loc.T("session.tpl.immersive.tip");
        BtnTplMinimal.ToolTip = Loc.T("session.tpl.minimal.tip");
        LblMonitorLayout.Text = Loc.T("session.layout");
        ItmLayoutKeep.Content = Loc.T("session.layout.keepAll");
        ItmLayoutIsolate.Content = Loc.T("session.layout.isolate");
        ItmLayoutPrimary.Content = Loc.T("session.layout.primaryOnly");
        ChkQuietToast.Content = Loc.T("session.quiet");
        ChkNoAutoHdr.Content = Loc.T("session.hdr");
        ChkMonBright.Content = Loc.T("session.bright");
        ChkSwitchAudio.Content = Loc.T("session.audio");
        LblScaling.Text = Loc.T("session.scaling");
        ItmScaleDefault.Content = Loc.T("session.scaling.default");
        ItmScaleStretch.Content = Loc.T("session.scaling.stretch");
        ItmScaleCenter.Content = Loc.T("session.scaling.center");
        LblCompanions.Text = Loc.T("companions.title");
        LblCompanionsEmpty.Text = Loc.T("companions.empty");
        BtnCompanionAdd.Content = Loc.T("btn.add");
        BtnCompanionEdit.Content = Loc.T("btn.edit");
        BtnCompanionRemove.Content = Loc.T("btn.remove");
        LblEditorEmpty.Text = Loc.T("library.pick");

        ApplyPresetsLocalization();
        ApplyInfoTips();

        // Derived labels (combos, summaries) that embed localized text.
        UpdateSessionLabels();
        UpdateFpsLimitAvailability();
        UpdateDriverUiAvailability();
        FillDisplays(keepSelection: true);
        FillAudioDevices();
        RefreshRefreshRateCombo();
        if (_selected != null)
        {
            using (SuppressEditor())
            {
                FillStartupPresetCombo(_selected, _selected.StartupPresetId);
                SelectOrSetFpsLimit(CmbFpsLimit, _selected.FpsLimit);
            }
        }
        UpdateColorSummary(_selected);
        UpdatePresetEmptyCta();
        UpdateLibraryEmpty();
        RefreshPresetListSafe();
        CompanionList.Items.Refresh();
        UpdatePresetLabels();
        UpdateBackendActiveLabel(ColorUiHelper.ReadBackendToggle(TogPresetBackend) == ColorBackend.LowLevel);
        RefreshGamesTab();
    }

    private void ApplyInfoTips()
    {
        SetTip(InfoRestoreMode, "restore.mode.tip");
        SetTip(InfoSoftAltTab, "profile.softAltTab.tip");
        SetTip(InfoFpsLimit, "profile.fpsLimit.tip");
        SetTip(InfoDeferred, "session.deferred.tip");
        SetTip(InfoQuiet, "session.quiet.tip");
        SetTip(InfoHdr, "session.hdr.tip");
        SetTip(InfoMonBright, "session.bright.tip");
        SetTip(InfoAudio, "session.audio.tip");
        SetTip(InfoScaling, "session.scaling.tip");
        SetTip(InfoCompanions, "companions.tip");
        SetTip(InfoPresetBackend, "color.backend.tip");
        SetTip(InfoPresetLock, "color.lock.tip");
        ChkApplyRes.ToolTip = Loc.T("display.res.tip");
        ChkApplyPower.ToolTip = Loc.T("display.power.tip");
        BtnPresetExport.ToolTip = Loc.T("presets.export.tip");
        BtnPresetImport.ToolTip = Loc.T("presets.import.tip");
    }

    private static void SetTip(InfoHint? hint, string key)
    {
        if (hint != null)
            hint.Tip = Loc.T(key);
    }

    // -- Header / sections ---------------------------------------------------------------------------------

    private void GamesListMore_Click(object sender, RoutedEventArgs e) => OpenButtonContextMenu(sender);

    private void GamesEditorMore_Click(object sender, RoutedEventArgs e) => OpenButtonContextMenu(sender);

    private static void OpenButtonContextMenu(object sender)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.ContextMenu == null) return;
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.IsOpen = true;
    }

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
        if (PanelGamesDisplay == null || PanelGamesPresets == null) return;
        if (_selected == null)
        {
            PanelGamesDisplay.Visibility = Visibility.Collapsed;
            PanelGamesColor.Visibility = Visibility.Collapsed;
            PanelGamesSession.Visibility = Visibility.Collapsed;
            PanelGamesCompanions.Visibility = Visibility.Collapsed;
            PanelGamesPresets.Visibility = Visibility.Collapsed;
            StopDesktopPreview();
            return;
        }

        bool color = RbGamesColor?.IsChecked == true;
        bool session = RbGamesSession?.IsChecked == true;
        bool companions = RbGamesCompanions?.IsChecked == true;
        bool presets = RbGamesPresets?.IsChecked == true;

        PanelGamesDisplay.Visibility = (!color && !session && !companions && !presets) ? Visibility.Visible : Visibility.Collapsed;
        PanelGamesColor.Visibility = color ? Visibility.Visible : Visibility.Collapsed;
        PanelGamesSession.Visibility = session ? Visibility.Visible : Visibility.Collapsed;
        PanelGamesCompanions.Visibility = companions ? Visibility.Visible : Visibility.Collapsed;
        PanelGamesPresets.Visibility = presets ? Visibility.Visible : Visibility.Collapsed;

        if (!color)
            StopDesktopPreview();
        else
            RefreshGamesTab();
        if (presets)
            LoadPresetEditor(_selectedPreset); // Color-section sliders may have edited the same preset object
    }

    /// <summary>Opens the Presets section for the selected game (⋯ → Edit presets, "+ preset" chip, Color → Edit).</summary>
    public void ShowPresetsSection(GameProfile? game = null, string? preferPresetId = null)
    {
        Host.Navigate(ShellSection.Games);
        if (game != null && !ReferenceEquals(game, _selected))
            SelectProfile(game, preferPresetId);
        else if (preferPresetId != null)
            SelectPreset(_presets.FirstOrDefault(p => p.Id == preferPresetId) ?? _selectedPreset);
        RbGamesPresets.IsChecked = true;
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

    public void UpdateLibraryEmpty()
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
    /// Selects a game in the list and editor (and makes it the Presets-section game). If the search filter
    /// hides it, the filter is cleared first so the editor never edits an invisible game.
    /// </summary>
    public void SelectProfile(GameProfile? p, string? preferPresetId = null)
    {
        if (_selected != null && !ReferenceEquals(_selected, p) && Host.IsDirty)
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
        SelectPresetGame(p, preferPresetId);
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
        if (Host.IsDirty && _selected != null) PushEditorToSelected();
        _selected = next;
        LoadEditor(_selected);
        SelectPresetGame(_selected);
        RefreshGamesTab();
    }

    private void RefreshGameTiles()
    {
        using (SuppressEditor())
        {
            ProfileList.Items.Refresh();
            if (_selected != null)
                ProfileList.SelectedItem = _selected;
        }
    }

    private void SetGamesEditorChromeEnabled(bool has)
    {
        EditorPanel.IsEnabled = has;
        PlayEditor.IsEnabled = has;
        PanelGamesSession.IsEnabled = has;
        PanelGamesCompanions.IsEnabled = has;
        PanelGamesPresets.IsEnabled = has;
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
        RbGamesPresets.IsEnabled = has;
        if (!has)
        {
            PanelGamesDisplay.Visibility = Visibility.Collapsed;
            PanelGamesColor.Visibility = Visibility.Collapsed;
            PanelGamesSession.Visibility = Visibility.Collapsed;
            PanelGamesCompanions.Visibility = Visibility.Collapsed;
            PanelGamesPresets.Visibility = Visibility.Collapsed;
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

            ComboUi.SelectByTag(CmbDisplay, p.DisplayDevice ?? "");
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

            ComboUi.SelectPowerPlan(CmbPower, p.PowerPlan, "highPerformance");
            ComboUi.SelectByTag(CmbRestoreMode, p.RestoreMode.ToString());
            var sx = p.Session ?? new SessionExtras();
            SldDeferred.Value = sx.DeferredApplySeconds;
            ChkQuietToast.IsChecked = sx.QuietNotifications;
            ChkNoAutoHdr.IsChecked = sx.DisableAutoHdr;
            var layout = sx.MonitorLayout;
            if (string.IsNullOrWhiteSpace(layout))
                layout = sx.IsolatePrimaryMonitor ? "isolatePrimary" : "keepAll";
            ComboUi.SelectByTag(CmbMonitorLayout, layout);
            ChkMonBright.IsChecked = sx.ApplyMonitorBrightness;
            SldMonBright.Value = sx.MonitorBrightness;
            ChkSwitchAudio.IsChecked = sx.SwitchAudioDevice;
            SelectAudioDevice(sx.AudioDeviceId);
            ComboUi.SelectByTag(CmbScaling, string.IsNullOrWhiteSpace(sx.ScalingMode) ? "default" : sx.ScalingMode!);
            RefreshCompanionList(p);
            UpdateSessionLabels();
            UpdateColorSummary(p);
            UpdateDependentEnables();
        }
        // Keep dirty — profile switch must not pretend changes were saved to disk
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
            ComboUi.SelectByTag(CmbDisplay, "");
            ChkApplyPower.IsChecked = false;
            ComboUi.SelectPowerPlan(CmbPower, null, "highPerformance");
            FillStartupPresetCombo(null, "");
            SldApplyDelay.Value = 0;
            ChkApplyOnFocus.IsChecked = false;
            ChkSoftAltTab.IsChecked = false;
            SelectOrSetFpsLimit(CmbFpsLimit, 0);
            ComboUi.SelectByTag(CmbRestoreMode, nameof(RestoreMode.PreviousSnapshot));
            SldDeferred.Value = 0;
            ChkQuietToast.IsChecked = false;
            ChkNoAutoHdr.IsChecked = false;
            ComboUi.SelectByTag(CmbMonitorLayout, "keepAll");
            ChkMonBright.IsChecked = false;
            SldMonBright.Value = 80;
            ChkSwitchAudio.IsChecked = false;
            SelectAudioDevice(null);
            ComboUi.SelectByTag(CmbScaling, "default");
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
            ComboUi.SelectByTag(CmbDisplay, keep ?? "");
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
            ComboUi.SelectByTag(CmbRefreshRate, keep.ToString());
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
        ComboUi.SelectByTag(CmbStartupPreset, keep ?? "");
    }

    /// <summary>Display section "Presets" summary: what this game has and what applies on start.</summary>
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

    /// <summary>Keeps the header quick fields in sync without rebuilding chips (preserves a "Live" chip).</summary>
    private void RefreshGamesQuickFields()
    {
        if (_playProfile == null || _selected == null || _selected.Id != _playProfile.Id) return;
        TxtPlayName.Text = _playProfile.Name;
        TxtPlayProcess.Text = _playProfile.ProcessName;
    }

    /// <summary>New blank game, name box focused (also the shell's "Add game").</summary>
    public void AddProfile_Click(object sender, RoutedEventArgs e)
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
        Host.Navigate(ShellSection.Games);
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

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var victim = _selected;
        string name = string.IsNullOrWhiteSpace(victim.Name) ? victim.ProcessName : victim.Name;
        if (!Host.ConfirmDestructive(Loc.Tf("confirm.deleteProfile", name), Loc.T("confirm.deleteProfile.title"), Loc.T("btn.delete")))
            return;

        PushEditorToSelected();
        int index = _profiles.IndexOf(victim);
        if (ReferenceEquals(_presetGame, victim))
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
        MarkDirty();
        FlushAutosave();

        Host.ShowUndoToast(Loc.Tf("toast.deleted", name), () =>
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
        if (dlg.ShowDialog(Host.Window) != true) return;

        using (SuppressEditor())
        {
            TxtProcess.Text = Path.GetFileName(dlg.FileName);
            if (string.IsNullOrWhiteSpace(TxtName.Text) || TxtName.Text == Loc.T("profile.new"))
                TxtName.Text = GameCatalog.GetFriendlyName(TxtProcess.Text)
                               ?? Path.GetFileNameWithoutExtension(dlg.FileName);
        }
        _selected.ExePath = dlg.FileName;
        if (SteamLaunch.TryResolveAppId(dlg.FileName, out int steamId))
            _selected.SteamAppId = steamId;
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
        var pick = new ProcessPickerWindow(procs) { Owner = Host.Window };
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
        var dlg = new CompanionEditWindow(c) { Owner = Host.Window };
        if (dlg.ShowDialog() != true) return;
        _selected.Companions ??= new List<CompanionApp>();
        _selected.Companions.Add(c);
        RefreshCompanionList(_selected, c);
        MarkDirty();
    }

    private void EditCompanion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || CompanionList.SelectedItem is not CompanionApp c) return;
        var dlg = new CompanionEditWindow(c) { Owner = Host.Window };
        if (dlg.ShowDialog() != true) return;
        RefreshCompanionList(_selected, c);
        MarkDirty();
    }

    private void RemoveCompanion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || CompanionList.SelectedItem is not CompanionApp c) return;
        var owner = _selected;
        string label = CompanionEditWindow.Describe(c);
        if (!Host.ConfirmDestructive(Loc.Tf("confirm.removeCompanion", label), Loc.T("confirm.removeCompanion.title"), Loc.T("btn.remove")))
            return;
        int index = owner.Companions.IndexOf(c);
        owner.Companions.Remove(c);
        RefreshCompanionList(owner);
        MarkDirty();
        FlushAutosave();
        Host.ShowUndoToast(Loc.T("toast.companion.removed"), () =>
        {
            owner.Companions.Insert(Math.Clamp(index, 0, owner.Companions.Count), c);
            if (ReferenceEquals(_selected, owner))
                RefreshCompanionList(owner, c);
            MarkDirty();
            FlushAutosave();
        });
    }

    // -- Library: scan / import / export / restore -------------------------------------------------------

    /// <summary>Scan button (also the shell's "Scan games").</summary>
    public async void ScanGames_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunGameScanAsync(confirmIfAny: false);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Scan games");
            Host.ScanBusy.HideAnimated();
            ThemedDialog.Show(Host.Window, Loc.T("scan.failed") + "\n" + ex.Message, Loc.T("scan.title"));
        }
    }

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
        var busy = Host.ScanBusy;
        busy.ShowAnimated();
        UiSound.BeginWork();

        var minDelay = Task.Delay(1200);
        List<GameScanner.FoundGame> found;
        try
        {
            found = await Task.Run(() =>
                GameScanner.ScanAll(msg => Dispatcher.BeginInvoke(() => busy.SetStatus(msg))));
        }
        catch (Exception ex)
        {
            UiSound.EndWork(UiWorkResult.Cancel);
            UiSound.Error();
            busy.HideAnimated();
            AppLog.Error(ex, "Game scan");
            ThemedDialog.Show(Host.Window, Loc.T("scan.failed") + "\n" + ex.Message, Loc.T("scan.title"));
            return;
        }

        await minDelay;
        busy.HideAnimated();

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
            if (!ThemedDialog.Confirm(Host.Window, Loc.Tf("scan.found", names), Loc.T("scan.title"), Loc.T("scan.addAll")))
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
                bool touched = false;
                if (string.IsNullOrWhiteSpace(existing.ExePath) && !string.IsNullOrWhiteSpace(g.ExePath))
                {
                    existing.ExePath = g.ExePath;
                    touched = true;
                }
                if (existing.SteamAppId is null or <= 0 && !string.IsNullOrWhiteSpace(existing.ExePath)
                    && SteamLaunch.TryResolveAppId(existing.ExePath, out int sid))
                {
                    existing.SteamAppId = sid;
                    touched = true;
                }
                if (touched)
                {
                    GameVisuals.Apply(existing);
                    MarkDirty();
                }
                continue;
            }

            var profile = GameCatalog.CreateProfileSkeleton(g.Process, g.Name, g.ExePath);
            if (SteamLaunch.TryResolveAppId(g.ExePath, out int newSid))
                profile.SteamAppId = newSid;
            GameVisuals.Apply(profile);
            _profiles.Add(profile);
            last = profile;
            added++;
        }
        if (added > 0 || Host.IsDirty)
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
        bool hasSteam = profile.SteamAppId is > 0
                        || SteamLaunch.IsUnderSteamCommon(path);
        if (!hasSteam && (string.IsNullOrWhiteSpace(path) || !File.Exists(path)))
        {
            ThemedDialog.Show(Host.Window, Loc.T("launch.noPath"), Loc.T("btn.launchGame"));
            return;
        }

        // Cache AppID when we resolve it so the next launch skips disk scans.
        if (profile.SteamAppId is null or <= 0
            && SteamLaunch.TryResolveAppId(path, out int resolved))
        {
            profile.SteamAppId = resolved;
            MarkDirty();
        }

        if (!SteamLaunch.TryStartGame(path, profile.SteamAppId, out bool viaSteam, out string? err))
        {
            UiSound.Error();
            string detail = err switch
            {
                "steam_missing" => Loc.T("launch.steam.missing"),
                "no_exe" => Loc.T("launch.noPath"),
                _ => err ?? "?"
            };
            ThemedDialog.Show(Host.Window, Loc.T("launch.failed") + "\n" + detail, Loc.T("btn.launchGame"));
            return;
        }

        ShowToast(viaSteam
            ? Loc.Tf("toast.launching.steam", profile.Name)
            : Loc.Tf("toast.launching", profile.Name));
        UiSound.Launch();
    }

    /// <summary>Push this game's display/power settings to the desktop right now (test without launching).</summary>
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

    /// <summary>Live overlay (⋯ menu; also the command palette).</summary>
    public void Overlay_Click(object sender, RoutedEventArgs e)
    {
        FlushAutosave();
        if (System.Windows.Application.Current is App app)
            app.ToggleGameOverlay();
    }

    private void OpenPresets_Click(object sender, RoutedEventArgs e) => ShowPresetsSection(_selected);

    // -- Audio / session ------------------------------------------------------------------------------------

    public void FillAudioDevices()
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
            ComboUi.SelectByTag(CmbMonitorLayout, layout);
            ComboUi.SelectByTag(CmbScaling, scaling);
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
