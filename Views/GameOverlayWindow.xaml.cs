using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DisplayProfileManager.Models;
using DisplayProfileManager.Services;

namespace DisplayProfileManager;

/// <summary>
/// Compact topmost overlay for live color tuning over games.
/// Sliders call PreviewColor immediately; save actions commit to profile/preset.
/// </summary>
public partial class GameOverlayWindow : Window
{
    private bool _suppress;
    private bool _expanded;
    private ColorBackend _backend = ColorBackend.Driver;
    private readonly DispatcherTimer _liveTimer;
    private readonly DispatcherTimer _persistTimer;
    private GameProfile? _boundProfile;
    private double? _pendingLeft;
    private double? _pendingTop;
    private double? _pendingOpacity;

    public GameOverlayWindow()
    {
        _suppress = true;
        InitializeComponent();
        _suppress = false;

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
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
                if (ui != null && ui.OverlayLeft.HasValue && ui.OverlayTop.HasValue
                    && !double.IsNaN(ui.OverlayLeft.Value) && !double.IsNaN(ui.OverlayTop.Value)
                    && !double.IsInfinity(ui.OverlayLeft.Value) && !double.IsInfinity(ui.OverlayTop.Value))
                {
                    Left = ui.OverlayLeft.Value;
                    Top = ui.OverlayTop.Value;
                }
                else
                {
                    ColorUiHelper.PlaceOverlayDefault(this);
                }
                SetExpanded(ui?.OverlayHotkeyOnly == true ? false : (ui?.OverlayExpanded ?? false), save: false);
                ApplyHotkeyOnlyMode(ui?.OverlayHotkeyOnly == true);
                _suppress = false;
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
        // Force layout so default placement uses real size (not NaN → top-left).
        UpdateLayout();
        var ui = App.Services.Config.Current.Ui;
        bool hasPos = ui?.OverlayLeft != null && ui.OverlayTop != null
                      && !double.IsNaN(ui.OverlayLeft.Value) && !double.IsNaN(ui.OverlayTop.Value);
        if (!hasPos)
            ColorUiHelper.PlaceOverlayDefault(this);
        else
        {
            Left = ui!.OverlayLeft!.Value;
            Top = ui.OverlayTop!.Value;
        }

        bool hotkeyOnly = ui?.OverlayHotkeyOnly == true;
        if (hotkeyOnly)
            expanded = false;
        SetExpanded(expanded, save: true);
        ApplyHotkeyOnlyMode(hotkeyOnly);
        OverlayWin32.StayTopmost(this);
    }

    private void ApplyHotkeyOnlyMode(bool hotkeyOnly)
    {
        // Compact pill only — double-click / expand affordance disabled.
        MiniPill.Cursor = hotkeyOnly ? System.Windows.Input.Cursors.SizeAll : System.Windows.Input.Cursors.Hand;
        MiniPill.ToolTip = hotkeyOnly ? Loc.T("overlay.hotkeyOnly") : null;
    }

    public void HideOverlay()
    {
        Hide();
        var ui = App.Services.Config.Current.Ui ?? new UiPreferences();
        ui.OverlayVisible = false;
        App.Services.Config.Save(App.Services.Config.Current, raiseChanged: false);
        if (System.Windows.Application.Current is App app)
            app.NotifyOverlayHidden();
    }

    public void SyncFromActiveContext()
    {
        _boundProfile = App.Services.Monitor.CurrentProfile
                        ?? (System.Windows.Application.Current.MainWindow as MainWindow)?.SelectedProfileForOverlay;

        ColorSettings source;
        string ctx;
        string presetLabel = "";
        if (_boundProfile != null)
        {
            _boundProfile.EnsureDualColorSlots();
            var presetId = App.Services.Monitor.ActivePresetId;
            var preset = presetId == null
                ? null
                : _boundProfile.Presets?.FirstOrDefault(p => p.Id == presetId);
            if (preset != null)
            {
                source = preset.Color.Clone();
                presetLabel = Loc.Tf("overlay.preset", preset.Name);
            }
            else if (_boundProfile.Presets is { Count: > 0 } presets)
            {
                source = presets[0].Color.Clone();
                presetLabel = Loc.Tf("overlay.preset", presets[0].Name);
            }
            else
            {
                source = App.Services.Display.LiveColor.Clone();
            }
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
        if (ChkLockColor != null) ChkLockColor.IsChecked = source.LockColor;
        RefreshLabels();
        UpdateShadowEnabled();
        TxtContext.Text = ctx;
        TxtPreset.Text = presetLabel;
        BtnUpdatePreset.IsEnabled = App.Services.Monitor.HasActivePreset;
        _suppress = false;
    }

    private void SetExpanded(bool expanded, bool save)
    {
        _expanded = expanded;
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
        if (!IsLoaded) return;
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
        if (LblB == null || LblS == null || ValS == null || SldB == null || SldS == null) return;
        ColorUiHelper.UpdateLabels(LblB, LblC, LblG, LblV, SldB, SldC, SldG, SldV, _backend, LblH, SldH);
        if (ValB != null) ValB.Text = LblB.Text.Replace("Bright ", "");
        if (ValC != null) ValC.Text = LblC.Text.Replace("Contr ", "");
        if (ValG != null) ValG.Text = LblG.Text.Replace("Gamma ", "");
        if (ValV != null && LblV != null) ValV.Text = LblV.Text.Replace("Vibr ", "");
        if (ValH != null && LblH != null) ValH.Text = LblH.Text.Replace("Hue ", "").Replace("°", "");
        LblS.Text = $"Shadow {(int)SldS.Value}";
        ValS.Text = $"{(int)SldS.Value}";
    }

    private void UpdateShadowEnabled()
    {
        if (SldS == null || LblS == null || ValS == null) return;
        bool low = _backend is ColorBackend.LowLevel or ColorBackend.Gdi;
        SldS.IsEnabled = low;
        LblS.Opacity = low ? 1 : 0.45;
        ValS.Opacity = low ? 1 : 0.45;
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
            UpdateShadowEnabled();
            RefreshLabels();
            return;
        }

        if (_boundProfile != null)
        {
            _boundProfile.EnsureDualColorSlots();
            var prev = ReadSliders();
            prev.Backend = _backend;
            _boundProfile.Color = prev;
            _boundProfile.SaveActiveToDualSlots();
            _backend = next;
            var loaded = _boundProfile.ActivateBackend(next);
            _suppress = true;
            try
            {
                ColorUiHelper.ApplyColorSliders(loaded, SldB, SldC, SldG, SldV, SldS, SldH);
            }
            finally
            {
                _suppress = false;
            }
        }
        else
        {
            _backend = next;
            ColorUiHelper.ConfigureGammaRangeForBackend(next, SldG);
        }

        UpdateShadowEnabled();
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

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var c = ReadSliders();
        App.Services.Display.ApplyColor(c);

        if (_boundProfile == null)
        {
            ThemedDialog.Show(this, Loc.T("overlay.need.profile"));
            return;
        }

        // Color is presets-only — write the active preset (or first preset), never profile.Color.
        var presetId = App.Services.Monitor.ActivePresetId;
        var preset = presetId == null
            ? null
            : _boundProfile.Presets?.FirstOrDefault(p => p.Id == presetId);
        preset ??= _boundProfile.Presets?.FirstOrDefault();
        if (preset == null)
        {
            ThemedDialog.Show(this, Loc.T("overlay.need.preset"));
            return;
        }

        preset.ApplyColor = true;
        preset.Color = c.Clone();
        preset.SaveActiveToDualSlots();
        App.Services.Config.Save(App.Services.Config.Current, raiseChanged: false);
        App.Services.Monitor.ApplyPreset(preset);
        TxtContext.Text = _boundProfile.Name + " ✓";
        TxtPreset.Text = Loc.Tf("overlay.preset", preset.Name);

        (System.Windows.Application.Current.MainWindow as MainWindow)?.NotifyOverlayApplied();
    }

    private void SaveAsPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_boundProfile == null)
        {
            ThemedDialog.Show(this, Loc.T("overlay.need.profile"));
            return;
        }

        string? name = PromptText(Loc.T("overlay.preset.name"), "Overlay preset");
        if (string.IsNullOrWhiteSpace(name)) return;

        var c = ReadSliders();
        App.Services.Display.ApplyColor(c);

        var preset = new QuickPreset
        {
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
        TxtPreset.Text = Loc.Tf("overlay.preset", preset.Name);
        BtnUpdatePreset.IsEnabled = true;
        (System.Windows.Application.Current.MainWindow as MainWindow)?.NotifyOverlayApplied();
    }

    private void UpdatePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_boundProfile == null || !App.Services.Monitor.HasActivePreset) return;
        var id = App.Services.Monitor.ActivePresetId;
        var preset = _boundProfile.Presets?.FirstOrDefault(p => p.Id == id);
        if (preset == null) return;

        var c = ReadSliders();
        App.Services.Display.ApplyColor(c);
        preset.ApplyColor = true;
        preset.Color = c.Clone();
        preset.SaveActiveToDualSlots();
        App.Services.Config.Save(App.Services.Config.Current, raiseChanged: false);
        TxtPreset.Text = Loc.Tf("overlay.preset", preset.Name) + " ✓";
        (System.Windows.Application.Current.MainWindow as MainWindow)?.NotifyOverlayApplied();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var c = ColorSettings.Neutral.Clone();
        c.Backend = _backend;
        if (_backend == ColorBackend.Driver)
            c = ColorSettings.DriverNeutral.Clone();
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
    }

    private void NextPreset_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Monitor.CyclePreset(+1);
        SyncFromActiveContext();
    }

    private void PrevPreset_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Monitor.CyclePreset(-1);
        SyncFromActiveContext();
    }

    private static string? PromptText(string title, string defaultName)
    {
        var win = new Window
        {
            Title = title,
            Width = 360,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["BgBrush"]
        };
        var box = new System.Windows.Controls.TextBox { Text = defaultName, Margin = new Thickness(16, 16, 16, 8) };
        var ok = new System.Windows.Controls.Button
        {
            Content = "OK",
            Width = 80,
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)System.Windows.Application.Current.Resources["AccentButton"]
        };
        var cancel = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Width = 80,
            Style = (Style)System.Windows.Application.Current.Resources["GhostButton"]
        };
        string? result = null;
        ok.Click += (_, _) => { result = box.Text; win.DialogResult = true; };
        cancel.Click += (_, _) => { win.DialogResult = false; };
        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 16)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new System.Windows.Controls.DockPanel();
        System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(box);
        win.Content = root;
        box.SelectAll();
        box.Focus();
        return win.ShowDialog() == true ? result : null;
    }

    public void ApplyLocalization()
    {
        if (TxtTitle == null) return;
        TxtTitle.Text = Loc.T("overlay.title");
        if (LblLiveHint != null) LblLiveHint.Text = Loc.T("overlay.live.hint");
        if (BtnApply != null) BtnApply.Content = Loc.T("overlay.apply");
        if (BtnSavePreset != null) BtnSavePreset.Content = Loc.T("overlay.save.preset");
        if (BtnUpdatePreset != null) BtnUpdatePreset.Content = Loc.T("overlay.update.preset");
        if (BtnReset != null) BtnReset.Content = Loc.T("overlay.reset");
        if (BtnEmergency != null) BtnEmergency.Content = Loc.T("overlay.emergency");
        if (BtnCollapse != null) BtnCollapse.ToolTip = Loc.T("overlay.collapse");
        if (BtnHide != null) BtnHide.ToolTip = Loc.T("overlay.hide");
        if (TxtMini != null) TxtMini.Text = Loc.T("overlay.mini");
        if (LblPanelOpacityTitle != null) LblPanelOpacityTitle.Text = Loc.T("overlay.panelOpacity");
        if (ChkLockColor != null) ChkLockColor.Content = Loc.T("overlay.lock");
        if (ExpMore != null) ExpMore.Header = Loc.T("overlay.more");
    }
}
