using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Visor.Models;
using Visor.Services;

namespace Visor;

/// <summary>
/// Compact topmost overlay for live color tuning over games.
/// Sliders call PreviewColor immediately; Save commits to the active preset (or creates one — never
/// silently overwrites another preset).
/// </summary>
public partial class GameOverlayWindow : Window
{
    private bool _suppress;
    private ColorBackend _backend = ColorBackend.Driver;
    private readonly DispatcherTimer _liveTimer;
    private readonly DispatcherTimer _persistTimer;
    private GameProfile? _boundProfile;
    private QuickPreset? _boundPreset;
    private double? _pendingLeft;
    private double? _pendingTop;
    private double? _pendingOpacity;
    private bool _placing;

    public GameOverlayWindow()
    {
        _suppress = true;
        InitializeComponent();
        _suppress = false;

        // One-shot debounce (restarted per slider tick), never periodic: ≤10 ramp pushes/s while dragging.
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _liveTimer.Tick += (_, _) =>
        {
            _liveTimer.Stop();
            PushLivePreview();
        };

        _persistTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _persistTimer.Tick += (_, _) =>
        {
            _persistTimer.Stop();
            FlushPendingUiSave();
        };

        Loaded += (_, _) =>
        {
            try
            {
                _suppress = true;
                ApplyPanelOpacityFromConfig();
                ApplyLocalization();
                var ui = App.Services.Config.Current.Ui;
                SetExpanded(ui?.OverlayHotkeyOnly == true ? false : (ui?.OverlayExpanded ?? false), save: false);
                ApplyHotkeyOnlyMode(ui?.OverlayHotkeyOnly == true);
                _suppress = false;
                PlaceOnGameMonitor();
                if (ui?.OverlayVisible == true)
                    ShowOverlay(expanded: ui.OverlayHotkeyOnly == true ? false : ui.OverlayExpanded);
            }
            catch (Exception ex)
            {
                _suppress = false;
                AppLog.Error("Overlay Loaded: " + ex.Message);
            }
        };

        SourceInitialized += (_, _) =>
        {
            OverlayWin32.StayTopmost(this);
            OverlayWin32.SetNoActivate(this);
        };

        LocationChanged += (_, _) => SavePosition();
    }

    public bool IsOverlayVisible => IsVisible;

    public void ShowOverlay(bool expanded = true)
    {
        SyncFromActiveContext();
        if (!IsVisible)
            Show();

        var ui = App.Services.Config.Current.Ui;
        bool hotkeyOnly = ui?.OverlayHotkeyOnly == true;
        if (hotkeyOnly)
            expanded = false;
        SetExpanded(expanded, save: true);
        ApplyHotkeyOnlyMode(hotkeyOnly);
        // Force layout so placement uses the real size (SizeToContent).
        UpdateLayout();
        PlaceOnGameMonitor();
        OverlayWin32.StayTopmost(this);
    }

    /// <summary>
    /// Restores the saved position only if it is still on a connected monitor AND on the monitor the game
    /// is on; otherwise docks to the top-right of the game's monitor (cursor monitor when no game).
    /// </summary>
    private void PlaceOnGameMonitor()
    {
        if (!IsVisible) return;
        _placing = true;
        try
        {
            var ui = App.Services.Config.Current.Ui;
            bool hasPos = ui?.OverlayLeft is double l && ui.OverlayTop is double t
                          && !double.IsNaN(l) && !double.IsNaN(t) && !double.IsInfinity(l) && !double.IsInfinity(t);
            if (hasPos)
            {
                Left = ui!.OverlayLeft!.Value;
                Top = ui.OverlayTop!.Value;
                UpdateLayout();
                if (OverlayWin32.IsReasonablyOnScreen(this) && OverlayWin32.IsOnTargetMonitor(this))
                {
                    // Partially off the edge (monitor layout / DPI changed since it was saved): pull it in.
                    OverlayWin32.EnsureInsideWorkArea(this);
                    return;
                }
            }
            ColorUiHelper.PlaceOverlayDefault(this);
            OverlayWin32.EnsureInsideWorkArea(this);
        }
        finally
        {
            _placing = false;
        }
    }

    private void ApplyHotkeyOnlyMode(bool hotkeyOnly)
    {
        // Compact pill only — double-click / expand affordance disabled.
        MiniPill.Cursor = hotkeyOnly ? System.Windows.Input.Cursors.SizeAll : System.Windows.Input.Cursors.Hand;
        MiniPill.ToolTip = hotkeyOnly ? Loc.T("overlay.hotkeyOnly") : Loc.T("overlay.pill.tip");
    }

    public void HideOverlay()
    {
        Hide();
        // Nothing may keep ticking while hidden: drop a pending live push, flush a pending position save.
        _liveTimer.Stop();
        if (_persistTimer.IsEnabled)
        {
            _persistTimer.Stop();
            FlushPendingUiSave();
        }
        var ui = App.Services.Config.Current.Ui ?? new UiPreferences();
        ui.OverlayVisible = false;
        App.Services.Config.Save(App.Services.Config.Current, raiseChanged: false);
        if (System.Windows.Application.Current is App app)
            app.NotifyOverlayHidden();
    }

    private static MainWindow? Main => System.Windows.Application.Current.MainWindow as MainWindow;

    public void SyncFromActiveContext()
    {
        // The main window edits copies — make sure its pending edits are in the config we're about to read.
        Main?.FlushPendingEdits();
        _boundProfile = App.Services.Monitor.CurrentProfile ?? Main?.SelectedProfileForOverlay;
        _boundPreset = null;
        RebindToConfig();

        ColorSettings source;
        string ctx;
        _boundPreset = null;
        if (_boundProfile != null)
        {
            _boundProfile.EnsureDualColorSlots();
            var presetId = App.Services.Monitor.ActivePresetId;
            _boundPreset = presetId == null
                ? null
                : _boundProfile.Presets?.FirstOrDefault(p => p.Id == presetId);
            source = _boundPreset?.Color.Clone() ?? App.Services.Display.LiveColor.Clone();
            _backend = source.Backend;
            ctx = _boundProfile.Name;
        }
        else
        {
            source = App.Services.Display.LiveColor.Clone();
            _backend = source.Backend;
            ctx = Loc.T("overlay.no.game");
        }

        _suppress = true;
        ColorUiHelper.SetBackendToggle(_backend, TogBackend);
        ColorUiHelper.ApplyColorSliders(source, SldB, SldC, SldG, SldV, SldS, SldH);
        ChkLockColor.IsChecked = source.LockColor;
        RefreshLabels();
        UpdateBackendDependent();
        TxtContext.Text = ctx;
        UpdatePresetUi();
        _suppress = false;
    }

    /// <summary>
    /// Main.FlushPendingEdits() writes the editor copies into the config as NEW objects — re-bind to the live
    /// config instances by Id so overlay edits land in what gets saved.
    /// </summary>
    private void RebindToConfig()
    {
        var cfg = App.Services.Config.Current;
        if (_boundProfile != null)
            _boundProfile = cfg.Profiles.FirstOrDefault(p => p.Id == _boundProfile.Id) ?? _boundProfile;
        if (_boundPreset != null)
            _boundPreset = _boundProfile?.Presets?.FirstOrDefault(p => p.Id == _boundPreset.Id);
    }

    /// <summary>Save targets the active preset; with none active it becomes "Save as preset…" (no silent overwrite).</summary>
    private void UpdatePresetUi()
    {
        TxtPreset.Text = _boundPreset != null ? Loc.Tf("overlay.preset", _boundPreset.Name) : Loc.T("overlay.preset.none");
        TxtApply.Text = _boundPreset != null
            ? Loc.Tf("overlay.save.to", _boundPreset.Name)
            : Loc.T("overlay.save.new");
        BtnApply.ToolTip = _boundPreset != null ? Loc.T("overlay.save.to.tip") : Loc.T("overlay.save.new.tip");
        BtnApply.IsEnabled = _boundProfile != null;
        BtnSavePreset.IsEnabled = _boundProfile != null;
    }

    private void SetExpanded(bool expanded, bool save)
    {
        MiniPill.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        PanelRoot.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        if (save)
        {
            var ui = App.Services.Config.Current.Ui ?? new UiPreferences();
            ui.OverlayExpanded = expanded;
            ui.OverlayVisible = true;
            App.Services.Config.Save(App.Services.Config.Current, raiseChanged: false);
        }
    }

    private void SavePosition()
    {
        if (!IsLoaded || _placing) return;
        _pendingLeft = Left;
        _pendingTop = Top;
        _persistTimer.Stop();
        _persistTimer.Start();
    }

    private void FlushPendingUiSave()
    {
        try
        {
            var ui = App.Services.Config.Current.Ui ?? new UiPreferences();
            if (_pendingLeft is double l) ui.OverlayLeft = l;
            if (_pendingTop is double t) ui.OverlayTop = t;
            if (_pendingOpacity is double o) ui.OverlayPanelOpacity = o;
            _pendingLeft = _pendingTop = _pendingOpacity = null;
            App.Services.Config.Save(App.Services.Config.Current, raiseChanged: false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Overlay UI save: " + ex.Message);
        }
    }

    private void ApplyPanelOpacityFromConfig()
    {
        var ui = App.Services.Config.Current.Ui;
        double op = ui?.OverlayPanelOpacity ?? 0.92;
        op = Math.Clamp(op, 0.55, 1.0);
        SldPanelOpacity.Value = op;
        PanelRoot.Opacity = op;
        LblPanelOpacity.Text = $"{(int)Math.Round(op * 100)}%";
    }

    private void RefreshLabels()
    {
        if (ValB == null || SldB == null) return;
        ColorUiHelper.UpdateValues(ValB, ValC, ValG, ValV, SldB, SldC, SldG, SldV, _backend, ValH, SldH, ValS, SldS);
    }

    /// <summary>Shadows exist only on the Low Level ramp, hue only on the driver path.</summary>
    private void UpdateBackendDependent()
    {
        if (SldS == null || LblS == null || ValS == null) return;
        bool low = ColorUiHelper.IsLowLevel(_backend);
        SldS.IsEnabled = low;
        LblS.Opacity = low ? 1 : 0.45;
        ValS.Opacity = low ? 1 : 0.45;
        SldH.IsEnabled = !low;
        LblH.Opacity = low ? 0.45 : 1;
        ValH.Opacity = low ? 0.45 : 1;
    }

    private ColorSettings ReadSliders() =>
        ColorUiHelper.ReadColorFromSliders(_backend, SldB, SldC, SldG, SldV, SldS,
            lockColor: ChkLockColor?.IsChecked != false, hue: SldH);

    private void ScheduleLivePreview()
    {
        if (_suppress) return;
        _liveTimer.Stop();
        _liveTimer.Start();
    }

    private void PushLivePreview()
    {
        if (_suppress) return;
        var c = ReadSliders();
        App.Services.Display.PreviewColor(c);
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Collapse_Click(sender, e);
            return;
        }
        try { DragMove(); } catch { }
    }

    private void MiniPill_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Button) return;
        if (App.Services.Config.Current.Ui?.OverlayHotkeyOnly == true)
        {
            try { DragMove(); } catch { }
            return;
        }
        if (e.ClickCount >= 2)
        {
            try { DragMove(); } catch { }
            return;
        }
        SyncFromActiveContext();
        SetExpanded(true, save: true);
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) => SetExpanded(false, save: true);
    private void Hide_Click(object sender, RoutedEventArgs e) => HideOverlay();

    private void Backend_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var next = ColorUiHelper.ReadBackendToggle(TogBackend);
        if (next == _backend)
        {
            UpdateBackendDependent();
            RefreshLabels();
            return;
        }

        // Keep separate slider sets per backend: the preset's dual slots when bound, otherwise neutral defaults.
        var slotOwner = _boundPreset;
        if (slotOwner != null)
        {
            slotOwner.EnsureDualColorSlots();
            var prev = ReadSliders();
            prev.Backend = _backend;
            slotOwner.Color = prev;
            slotOwner.SaveActiveToDualSlots();
            _backend = next;
            var loaded = slotOwner.ActivateBackend(next);
            _suppress = true;
            try { ColorUiHelper.ApplyColorSliders(loaded, SldB, SldC, SldG, SldV, SldS, SldH); }
            finally { _suppress = false; }
        }
        else
        {
            _backend = next;
            ColorUiHelper.ConfigureGammaRangeForBackend(next, SldG);
        }

        UpdateBackendDependent();
        RefreshLabels();
        ScheduleLivePreview();
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress || !IsInitialized) return;
        RefreshLabels();
        ScheduleLivePreview();
    }

    private void PanelOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress || !IsInitialized || PanelRoot == null || LblPanelOpacity == null) return;
        double op = SldPanelOpacity.Value;
        PanelRoot.Opacity = op;
        LblPanelOpacity.Text = $"{(int)Math.Round(op * 100)}%";
        if (!IsLoaded) return;
        _pendingOpacity = op;
        _persistTimer.Stop();
        _persistTimer.Start();
    }

    /// <summary>Primary action: update the active preset, or create a new one when none is active.</summary>
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_boundProfile == null)
        {
            ThemedDialog.Show(this, Loc.T("overlay.need.profile"), Loc.T("overlay.title"));
            return;
        }
        if (_boundPreset == null)
        {
            SaveAsPreset_Click(sender, e);
            return;
        }

        Main?.FlushPendingEdits();
        RebindToConfig(); // the flush replaced cfg.Profiles with fresh objects
        if (_boundPreset == null) return;
        var c = ReadSliders();
        App.Services.Display.ApplyColor(c);
        _boundPreset.ApplyColor = true;
        _boundPreset.Color = c.Clone();
        _boundPreset.SaveActiveToDualSlots();
        App.Services.Config.Save(App.Services.Config.Current, raiseChanged: false);
        App.Services.Monitor.ApplyPreset(_boundPreset);
        TxtPreset.Text = Loc.Tf("overlay.preset", _boundPreset.Name) + "  ✓";
        UiSound.Save();
        Main?.NotifyOverlayApplied();
    }

    private void SaveAsPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_boundProfile == null)
        {
            ThemedDialog.Show(this, Loc.T("overlay.need.profile"), Loc.T("overlay.title"));
            return;
        }

        string defaultName = Loc.Tf("overlay.preset.default", (_boundProfile.Presets?.Count ?? 0) + 1);
        string? name = ThemedDialog.Prompt(this, Loc.T("overlay.save.new"), Loc.Tf("overlay.preset.name", _boundProfile.Name),
            defaultName, Loc.T("btn.save"));
        if (string.IsNullOrWhiteSpace(name)) return;

        Main?.FlushPendingEdits();
        RebindToConfig(); // the flush replaced cfg.Profiles with fresh objects
        if (_boundProfile == null) return;
        var c = ReadSliders();
        App.Services.Display.ApplyColor(c);

        var preset = new QuickPreset
        {
            Id = _boundProfile.Id + "_" + Guid.NewGuid().ToString("N")[..8],
            Name = name.Trim(),
            ApplyColor = true,
            Color = c.Clone()
        };
        preset.EnsureDualColorSlots();
        preset.SaveActiveToDualSlots();

        _boundProfile.Presets ??= new List<QuickPreset>();
        _boundProfile.Presets.Add(preset);
        App.Services.Monitor.ApplyPreset(preset);
        App.Services.Config.Save(App.Services.Config.Current, raiseChanged: true);
        _boundPreset = preset;
        UpdatePresetUi();
        TxtPreset.Text = Loc.Tf("overlay.preset", preset.Name) + "  ✓";
        UiSound.Save();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var c = ColorUiHelper.IsLowLevel(_backend) ? ColorSettings.Neutral.Clone() : ColorSettings.DriverNeutral.Clone();
        c.Backend = ColorUiHelper.IsLowLevel(_backend) ? ColorBackend.LowLevel : ColorBackend.Driver;
        _suppress = true;
        ColorUiHelper.ApplyColorSliders(c, SldB, SldC, SldG, SldV, SldS, SldH);
        _suppress = false;
        RefreshLabels();
        PushLivePreview();
    }

    private void Emergency_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Zoom.Off();
        App.Services.Monitor.EmergencyRestore();
        HideOverlay(); // also restores main via NotifyOverlayHidden
    }

    private void Ab_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Services.Display.ToggleAbCompare())
            PushLivePreview();
        App.Services.Monitor.SetColorLockPaused(App.Services.Display.IsAbShowingFactory);
        BtnAb.Content = App.Services.Display.IsAbShowingFactory ? Loc.T("overlay.ab.factory") : Loc.T("overlay.ab");
    }

    private void NextPreset_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Monitor.CyclePreset(+1);
        SyncFromActiveContext();
        FlashPresetName();
    }

    private void PrevPreset_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Monitor.CyclePreset(-1);
        SyncFromActiveContext();
        FlashPresetName();
    }

    /// <summary>In compact pill mode there is no preset label — show it briefly in the pill text.</summary>
    private void FlashPresetName()
    {
        if (_boundPreset == null || PanelRoot.Visibility == Visibility.Visible) return;
        TxtMini.Text = _boundPreset.Name;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            TxtMini.Text = Loc.T("overlay.mini");
        };
        t.Start();
    }

    public void ApplyLocalization()
    {
        if (TxtTitle == null) return;
        TxtTitle.Text = Loc.T("overlay.title");
        LblLiveHint.Text = Loc.T("overlay.live.hint");
        BtnSavePreset.Content = Loc.T("overlay.save.newShort");
        BtnSavePreset.ToolTip = Loc.T("overlay.save.new.tip");
        BtnReset.Content = Loc.T("overlay.reset");
        BtnReset.ToolTip = Loc.T("overlay.reset.tip");
        BtnEmergency.Content = Loc.T("btn.emergency");
        BtnEmergency.ToolTip = Loc.T("btn.emergency.tip");
        BtnAb.Content = Loc.T("overlay.ab");
        BtnAb.ToolTip = Loc.T("overlay.ab.tip");
        BtnMiniAb.ToolTip = Loc.T("overlay.ab.tip");
        BtnMiniPrev.ToolTip = Loc.T("hotkey.prev");
        BtnMiniNext.ToolTip = Loc.T("hotkey.next");
        BtnCollapse.ToolTip = Loc.T("overlay.collapse");
        BtnHide.ToolTip = Loc.T("overlay.hide");
        // Glyph-only buttons: expose a name to UI Automation (ToolTip is only help text).
        System.Windows.Automation.AutomationProperties.SetName(BtnMiniPrev, Loc.T("hotkey.prev"));
        System.Windows.Automation.AutomationProperties.SetName(BtnMiniNext, Loc.T("hotkey.next"));
        System.Windows.Automation.AutomationProperties.SetName(BtnMiniAb, Loc.T("overlay.ab"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCollapse, Loc.T("overlay.collapse"));
        System.Windows.Automation.AutomationProperties.SetName(BtnHide, Loc.T("overlay.hide"));
        TxtMini.Text = Loc.T("overlay.mini");
        LblPanelOpacityTitle.Text = Loc.T("overlay.panelOpacity");
        ChkLockColor.Content = Loc.T("color.lock");
        ChkLockColor.ToolTip = Loc.T("color.lock.tip");
        ExpMore.Header = Loc.T("overlay.more");
        ColorUiHelper.ApplyLabelNames(LblB, LblC, LblG, LblV, LblH, LblS);
        LblH.ToolTip = Loc.T("color.hue.tip");
        LblS.ToolTip = Loc.T("color.shadow.tip");
        LblV.ToolTip = Loc.T("color.vibrance.tip");
        ColorUiHelper.LabelBackendToggle(TogBackend);
        UpdatePresetUi();
    }
}
