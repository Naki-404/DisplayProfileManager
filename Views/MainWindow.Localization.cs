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

/// <summary>Localization: ApplyLocalization entry point and per-page label refreshers.</summary>
public partial class MainWindow : Window
{
    // -- Home / command palette / history ------------------------------------------------------------

    private void ApplyHomeLocalization()
    {
        if (LblHomeSession == null) return;
        LblHomeSession.Text = Loc.T("home.session");
        LblHomeDisplay.Text = Loc.T("home.display");
        LblHomeLibrary.Text = Loc.T("home.library");
        LblHomeHealth.Text = Loc.T("home.health");
        LblHomeQuick.Text = Loc.T("home.quick");
        LblHomeActivity.Text = Loc.T("home.activity");
        BtnHomeScan.Content = Loc.T("btn.scan");
        BtnHomeAdd.Content = Loc.T("btn.add");
        BtnHomeEmergency.Content = Loc.T("btn.emergency");
        BtnHomeEmergency.ToolTip = Loc.T("btn.emergency.tip");
        BtnHomeOpenGames.Content = Loc.T("home.openGames");
        BtnHomeOpenHistory.Content = Loc.T("home.openHistory");
        TxtHomeQuickEmpty.Text = Loc.T("home.quick.empty");
        TxtHomeActivityEmpty.Text = Loc.T("home.activity.empty");
        LblHomeRec.Text = Loc.T("rec.header");
        TxtHomeRecHint.Text = Loc.T("rec.hint");
        TxtHomeRecEmpty.Text = Loc.T("rec.empty");
        BtnHomeRecReset.Content = Loc.T("rec.reset");
        RefreshPauseButton();
        RefreshHomeDashboard();
    }

    private void ApplySystemLocalization()
    {
        if (LblSystemOverview == null) return;
        LblSystemOverview.Text = Loc.T("system.overview");
        LblSystemCpu.Text = Loc.T("system.cpu");
        LblSystemRam.Text = Loc.T("system.ram");
        LblSystemGpu.Text = Loc.T("system.gpu");
        LblSystemOs.Text = Loc.T("system.os");
        LblSystemDisks.Text = Loc.T("system.disks");
        TxtSystemDisksEmpty.Text = Loc.T("system.disks.empty");
        LblSystemSafe.Text = Loc.T("system.safe");
        TxtSystemSafeHint.Text = Loc.T("system.safe.hint");
        LblGameMode.Text = Loc.T("system.gameMode");
        LblPowerPlan.Text = Loc.T("system.power");
        LblSystemProcesses.Text = Loc.T("system.processes");
        TxtProcessHint.Text = Loc.T("system.processes.hint");
        TxtProcessEmpty.Text = Loc.T("system.processes.empty");
        BtnSystemRefresh.Content = Loc.T("system.refresh");
        BtnProcessRefresh.Content = Loc.T("system.refresh");
        BtnGameModeRollback.Content = Loc.T("system.rollback");
        BtnPowerRollback.Content = Loc.T("system.rollback");
        BtnPowerHighPerf.Content = Loc.T("system.power.highPerf");
        RefreshSystemSafeLabels();
    }

    private void ApplyNetworkLocalization()
    {
        if (LblNetOverview == null) return;
        LblNetOverview.Text = Loc.T("net.overview");
        BtnNetRefresh.Content = Loc.T("system.refresh");
        TxtNetEmpty.Text = Loc.T("net.empty");
        LblNetLatency.Text = Loc.T("net.latency");
        TxtNetLatencyHint.Text = Loc.T("net.latency.hint");
        LblNetHost.Text = Loc.T("net.host");
        BtnNetPingStart.Content = Loc.T("net.ping.start");
        BtnNetPingStop.Content = Loc.T("net.ping.stop");
        TxtNetJitterNote.Text = Loc.T("net.jitter.note");
        LblNetDns.Text = Loc.T("net.dns");
        TxtNetDnsHint.Text = Loc.T("net.dns.hint");
        BtnNetDns.Content = Loc.T("net.dns.run");
        BtnNetDnsStop.Content = Loc.T("net.ping.stop");
        TxtNetDnsEmpty.Text = Loc.T("net.dns.empty");
        if (NetworkPageIsLive)
            RefreshNetworkPage();
        else
        {
            RebuildNetHosts(_netHasGateway);
            _networkStale = true;
        }
    }

    /// <summary>Single entry point: relabels every page for the current locale, then refreshes derived UI.</summary>
    public void ApplyLocalization()
    {
        _appliedLocale = Loc.Locale;
        UpdateTitleDirtyState();
        ApplyShellLocalization();
        ApplyHomeLocalization();
        ApplySystemLocalization();
        ApplyNetworkLocalization();
        ApplyGamesLocalization();
        ApplyPresetsLocalization();
        ApplyToolsLocalization();
        RefreshAfterLocalization();
    }

    /// <summary>Sidebar, caption buttons, footer.</summary>
    private void ApplyShellLocalization()
    {
        // Headers stay blank — ShellTabControl is headerless; sidebar owns labels.
        TabHome.Header = TabGames.Header = TabPresets.Header = TabSystem.Header = TabNetwork.Header = TabMore.Header = "";

        LblNavMain.Text = Loc.T("nav.section.main");
        NavHome.Content = Loc.T("nav.home");
        NavGames.Content = Loc.T("nav.games");
        NavPresets.Content = Loc.T("nav.presets");
        NavSystem.Content = Loc.T("nav.system");
        NavNetwork.Content = Loc.T("nav.network");
        NavTools.Content = Loc.T("nav.tools");
        NavSettings.Content = Loc.T("nav.settings");
        NavHome.ToolTip = Loc.T("nav.home.tip");
        NavGames.ToolTip = Loc.T("nav.games.tip");
        NavPresets.ToolTip = Loc.T("nav.presets.tip");
        NavSystem.ToolTip = Loc.T("nav.system.tip");
        NavNetwork.ToolTip = Loc.T("nav.network.tip");
        NavTools.ToolTip = Loc.T("nav.tools.tip");
        System.Windows.Automation.AutomationProperties.SetName(NavHome, Loc.T("nav.home"));
        System.Windows.Automation.AutomationProperties.SetName(NavGames, Loc.T("nav.games"));
        System.Windows.Automation.AutomationProperties.SetName(NavPresets, Loc.T("nav.presets"));
        System.Windows.Automation.AutomationProperties.SetName(NavSystem, Loc.T("nav.system"));
        System.Windows.Automation.AutomationProperties.SetName(NavNetwork, Loc.T("nav.network"));
        System.Windows.Automation.AutomationProperties.SetName(NavTools, Loc.T("nav.tools"));
        System.Windows.Automation.AutomationProperties.SetName(NavSettings, Loc.T("nav.settings"));

        BtnEmergency.Content = Loc.T("btn.emergency");
        BtnEmergency.ToolTip = Loc.T("btn.emergency.tip");
        System.Windows.Automation.AutomationProperties.SetName(BtnEmergency, Loc.T("btn.emergency"));
        RefreshPauseButton();
        SyncNavFromTabs();

        BtnMinimize.ToolTip = Loc.T("caption.minimize");
        BtnCloseCaption.ToolTip = Loc.T("caption.close");
        System.Windows.Automation.AutomationProperties.SetName(BtnMinimize, Loc.T("caption.minimize"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseCaption, Loc.T("caption.close"));
        UpdateMaximizeCaption();
        TxtShortcuts.Text = Loc.T("shortcuts.footer");
        TxtCredit.Text = "Nakidev \u00B7 @nakidev";
        TxtCredit.ToolTip = Loc.T("about.contacts");
    }

    /// <summary>Games page: list, editor sections, ⋯ menus, live color.</summary>
    private void ApplyGamesLocalization()
    {
        BtnGamesListMore.Content = "\u22EF";
        BtnGamesEditorMore.Content = "\u22EF";
        BtnGamesListMore.ToolTip = Loc.T("games.more");
        BtnGamesEditorMore.ToolTip = Loc.T("games.more");
        // "⋯" has no readable text of its own.
        System.Windows.Automation.AutomationProperties.SetName(BtnGamesListMore, Loc.T("games.more"));
        System.Windows.Automation.AutomationProperties.SetName(BtnGamesEditorMore, Loc.T("games.more"));
        RbGamesDisplay.Content = Loc.T("games.tab.display");
        RbGamesColor.Content = Loc.T("games.tab.color");
        RbGamesSession.Content = Loc.T("games.tab.session");
        RbGamesCompanions.Content = Loc.T("games.tab.companions");
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

        // Games — Color tab (live)
        LblPlayColor.Text = Loc.T("play.color");
        LblPlayPresets.Text = Loc.T("play.presets");
        ChkPlayPreview.Content = Loc.T("play.preview");
        ChkPlayPreview.ToolTip = Loc.T("play.preview.tip");
        ExpPlayHotkeys.Header = Loc.T("play.hotkeys");
        ColorUiHelper.ApplyLabelNames(LblPlayB, LblPlayC, LblPlayG, LblPlayV, LblPlayH, LblPlayS);
        LblPlayH.ToolTip = Loc.T("color.hue.tip");
        LblPlayS.ToolTip = Loc.T("color.shadow.tip");
        LblPlayV.ToolTip = Loc.T("color.vibrance.tip");

        // Games — list + Display
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
    }

    private void ApplyPresetsLocalization()
    {
        LblPresetGamePick.Text = Loc.T("presets.game");
        System.Windows.Automation.AutomationProperties.SetName(CmbPresetGame, Loc.T("presets.game"));
        BtnPresetAdd.Content = Loc.T("btn.add");
        BtnPresetAdd.ToolTip = Loc.T("presets.add.tip");
        BtnPresetDelete.Content = Loc.T("btn.delete");
        BtnPresetExport.Content = Loc.T("presets.export");
        BtnPresetImport.Content = Loc.T("presets.import");
        BtnPackGallery.Content = Loc.T("presets.gallery");
        BtnPackGallery.ToolTip = Loc.T("presets.gallery.hint");
        LblPresetEditorTitle.Text = Loc.T("preset.title");
        BtnApplyPreset.Content = Loc.T("btn.applyNow");
        BtnApplyPreset.ToolTip = Loc.T("preset.applyNow.tip");
        LblPresetName.Text = Loc.T("preset.name");
        LblPresetHotkey.Text = Loc.T("preset.hotkey");
        LblPresetHotkeyHint.Text = Loc.T("preset.hotkey.hint");
        BtnPresetHotkeyClear.Content = Loc.T("btn.clear");
        ChkPresetRes.Content = Loc.T("preset.setRes");
        LblPresetColor.Text = Loc.T("preset.color");
        LblPresetColorHint.Text = Loc.T("preset.color.hint");
        LblPresetBackend.Text = Loc.T("color.backend");
        ChkPresetLock.Content = Loc.T("color.lock");
        ColorUiHelper.ApplyLabelNames(LblPresetB, LblPresetC, LblPresetG, LblPresetV, LblPresetH, LblPresetShadow);
        LblPresetH.ToolTip = Loc.T("color.hue.tip");
        LblPresetShadow.ToolTip = Loc.T("color.shadow.tip");
        LblPresetV.ToolTip = Loc.T("color.vibrance.tip");
        LblPresetB.ToolTip = Loc.T("color.brightness.tip");
        ColorUiHelper.LabelBackendToggle(TogPresetBackend);
        TxtPresetHotkey.Tag = Loc.T("hotkey.placeholder");
    }

    /// <summary>Tools page: defaults, app hotkeys, history, log.</summary>
    private void ApplyToolsLocalization()
    {
        RbMoreDefaults.Content = Loc.T("tab.global");
        RbMoreHistory.Content = Loc.T("tab.history");
        RbMoreLog.Content = Loc.T("tab.log");
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

        foreach (var (box, _) in GlobalHotkeyBoxes())
            box.Tag = Loc.T("hotkey.placeholder");
    }

    /// <summary>Derived labels (combos, summaries, header) that embed localized text.</summary>
    private void RefreshAfterLocalization()
    {
        ApplyInfoTips();
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
        UpdateActiveHeader(App.Services.Monitor.CurrentProfile);
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
}
