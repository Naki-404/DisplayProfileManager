using System.Windows;
using System.Windows.Controls;
using Visor.Controls;
using Visor.Models;
using Visor.Services;

namespace Visor;

/// <summary>Games → Color section: live color sliders, preset chips, desktop preview, hotkey cheat sheet.</summary>
public partial class GamesPage
{
    private GameProfile? _playProfile;
    private QuickPreset? _playPreset;
    private ColorBackend _playBackend = ColorBackend.LowLevel;
    private bool _previewOnDesktop;
    /// <summary>User picked the "Live" chip (one-off color, not saved to a preset).</summary>
    private bool _playLive;
    private int _playSuppress;

    private bool SuppressingPlay => _playSuppress > 0;

    private sealed class Scope : IDisposable
    {
        private Action? _onDispose;
        public Scope(Action onDispose) => _onDispose = onDispose;
        public void Dispose()
        {
            _onDispose?.Invoke();
            _onDispose = null;
        }
    }

    private IDisposable SuppressPlay()
    {
        _playSuppress++;
        return new Scope(() => _playSuppress--);
    }

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
            PlayStatusDot.Status = running ? StatusKind.Ok : StatusKind.Unknown;

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
        PlayHintDot.Status = active ? StatusKind.Ok : StatusKind.Unknown;
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
                ShowPresetsSection(profile);
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
    public void StopDesktopPreview()
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
        ShowPresetsSection(_playProfile, _playPreset?.Id);
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
}
