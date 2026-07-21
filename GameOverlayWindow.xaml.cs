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
    private GameProfile? _boundProfile;

    public GameOverlayWindow()
    {
        InitializeComponent();
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _liveTimer.Tick += (_, _) =>
        {
            _liveTimer.Stop();
            PushLivePreview();
        };

        Loaded += (_, _) =>
        {
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
            SetExpanded(ui?.OverlayExpanded ?? false, save: false);
            if (ui?.OverlayVisible == true)
                ShowOverlay(expanded: ui.OverlayExpanded);
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
        SetExpanded(expanded, save: true);
        OverlayWin32.StayTopmost(this);
    }

    public void HideOverlay()
    {
        Hide();
        var ui = App.Services.Config.Current.Ui ?? new UiPreferences();
        ui.OverlayVisible = false;
        App.Services.Config.Save(App.Services.Config.Current, raiseChanged: false);
    }

    public void SyncFromActiveContext()
    {
        _boundProfile = App.Services.Monitor.CurrentProfile
                        ?? (System.Windows.Application.Current.MainWindow as MainWindow)?.SelectedProfileForOverlay;

        ColorSettings source;
        string ctx;
        string presetLabel = "";
        if (_boundProfile != null && _boundProfile.ApplyColor)
        {
            _boundProfile.EnsureDualColorSlots();
            var presetId = App.Services.Monitor.ActivePresetId;
            var preset = presetId == null
                ? null
                : _boundProfile.Presets?.FirstOrDefault(p => p.Id == presetId);
            if (preset != null && preset.ApplyColor)
            {
                source = preset.Color.Clone();
                presetLabel = Loc.Tf("overlay.preset", preset.Name);
            }
            else
            {
                source = _boundProfile.Color.Clone();
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
        ColorUiHelper.ApplyColorSliders(source, SldB, SldC, SldG, SldV, SldS);
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
        var ui = App.Services.Config.Current.Ui ?? new UiPreferences();
        ui.OverlayLeft = Left;
        ui.OverlayTop = Top;
        App.Services.Config.Save(App.Services.Config.Current, raiseChanged: false);
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
        ColorUiHelper.UpdateLabels(LblB, LblC, LblG, LblV, SldB, SldC, SldG, SldV, _backend);
        ValB.Text = LblB.Text.Replace("B ", "");
        ValC.Text = LblC.Text.Replace("C ", "");
        ValG.Text = LblG.Text.Replace("G ", "");
        ValV.Text = LblV.Text.Replace("V ", "");
        LblS.Text = $"S {(int)SldS.Value}";
        ValS.Text = $"{(int)SldS.Value}";
    }

    private void UpdateShadowEnabled()
    {
        bool low = _backend is ColorBackend.LowLevel or ColorBackend.Gdi;
        SldS.IsEnabled = low;
        LblS.Opacity = low ? 1 : 0.45;
        ValS.Opacity = low ? 1 : 0.45;
    }

    private ColorSettings ReadSliders() =>
        ColorUiHelper.ReadColorFromSliders(_backend, SldB, SldC, SldG, SldV, SldS, lockColor: true);

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
        if (next == _backend) return;

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
            ColorUiHelper.ApplyColorSliders(loaded, SldB, SldC, SldG, SldV, SldS);
            _suppress = false;
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
        if (_suppress) return;
        RefreshLabels();
        ScheduleLivePreview();
    }

    private void PanelOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress || PanelRoot == null) return;
        double op = SldPanelOpacity.Value;
        PanelRoot.Opacity = op;
        LblPanelOpacity.Text = $"{(int)Math.Round(op * 100)}%";
        var ui = App.Services.Config.Current.Ui ?? new UiPreferences();
        ui.OverlayPanelOpacity = op;
        App.Services.Config.Save(App.Services.Config.Current, raiseChanged: false);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var c = ReadSliders();
        App.Services.Display.ApplyColor(c);

        if (_boundProfile != null)
        {
            _boundProfile.Color = c.Clone();
            _boundProfile.SaveActiveToDualSlots();
            App.Services.Config.Save(App.Services.Config.Current, raiseChanged: false);
            TxtContext.Text = _boundProfile.Name + " ✓";
        }

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
        ColorUiHelper.ApplyColorSliders(c, SldB, SldC, SldG, SldV, SldS);
        _suppress = false;
        RefreshLabels();
        PushLivePreview();
    }

    private void Emergency_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Monitor.EmergencyRestore();
        HideOverlay();
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
        TxtTitle.Text = Loc.T("overlay.title");
        LblLiveHint.Text = Loc.T("overlay.live.hint");
        BtnApply.Content = Loc.T("overlay.apply");
        BtnSavePreset.Content = Loc.T("overlay.save.preset");
        BtnUpdatePreset.Content = Loc.T("overlay.update.preset");
        BtnReset.Content = Loc.T("overlay.reset");
        BtnEmergency.Content = Loc.T("overlay.emergency");
        BtnCollapse.ToolTip = Loc.T("overlay.collapse");
        BtnHide.ToolTip = Loc.T("overlay.hide");
        TxtMini.Text = Loc.T("overlay.mini");
        if (LblPanelOpacityTitle != null) LblPanelOpacityTitle.Text = Loc.T("overlay.panelOpacity");
    }
}
