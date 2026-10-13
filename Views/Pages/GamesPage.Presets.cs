using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Visor.Models;
using Visor.Services;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Visor;

/// <summary>Games → Presets section: per-game preset editor, pack import, hotkey capture, color backend.</summary>
public partial class GamesPage
{
    private GameProfile? _presetGame;
    private QuickPreset? _selectedPreset;
    private ColorBackend _presetEditorBackend = ColorBackend.LowLevel;
    /// <summary>
    /// True only while the preset editor holds changes not yet pushed into _selectedPreset.
    /// Guards against a stale editor overwriting edits made elsewhere (Color sliders) on autosave.
    /// </summary>
    private bool _presetEditorDirty;
    private readonly Dictionary<WpfTextBox, string> _hotkeyOriginal = new();

    // Color slider rows (ColorSliderRow control) — the old Sld*/Lbl*/Val* names stay reachable.
    private Slider SldPresetB => RowPresetB.Slider;
    private TextBlock LblPresetB => RowPresetB.LabelBlock;
    private TextBlock ValPresetB => RowPresetB.ValueBlock;
    private Slider SldPresetC => RowPresetC.Slider;
    private TextBlock LblPresetC => RowPresetC.LabelBlock;
    private TextBlock ValPresetC => RowPresetC.ValueBlock;
    private Slider SldPresetG => RowPresetG.Slider;
    private TextBlock LblPresetG => RowPresetG.LabelBlock;
    private TextBlock ValPresetG => RowPresetG.ValueBlock;
    private Slider SldPresetV => RowPresetV.Slider;
    private TextBlock LblPresetV => RowPresetV.LabelBlock;
    private TextBlock ValPresetV => RowPresetV.ValueBlock;
    private Slider SldPresetH => RowPresetH.Slider;
    private TextBlock LblPresetH => RowPresetH.LabelBlock;
    private TextBlock ValPresetH => RowPresetH.ValueBlock;
    private Slider SldPresetShadow => RowPresetShadow.Slider;
    private TextBlock LblPresetShadow => RowPresetShadow.LabelBlock;
    private TextBlock ValPresetShadow => RowPresetShadow.ValueBlock;

    private void ApplyPresetsLocalization()
    {
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
        LblPresetGame.Text = _presetGame != null ? Loc.Tf("presets.for", _presetGame.Name) : Loc.T("tab.presets");
    }

    /// <summary>
    /// Presets are per-game and follow the selected game. Kept as the shell seam for "a game started": the
    /// shell already selected the running game, this only re-syncs if something slipped.
    /// </summary>
    public void FocusPresetsForActiveOrSelectedGame(GameProfile? prefer = null)
    {
        prefer ??= _presetGame == null ? (App.Services.Monitor.CurrentProfile ?? _selected ?? _profiles.FirstOrDefault()) : null;
        if (prefer == null) return;

        var match = _profiles.FirstOrDefault(p => p.Id == prefer.Id)
                    ?? _profiles.FirstOrDefault(p =>
                        string.Equals(p.ProcessName, prefer.ProcessName, StringComparison.OrdinalIgnoreCase));
        if (match == null || ReferenceEquals(match, _presetGame)) return;
        if (!ReferenceEquals(match, _selected))
            SelectProfile(match);
        else
            SelectPresetGame(match);
    }

    /// <summary>Every game with its live preset list (the working copy for the open game) — for hotkey conflicts.</summary>
    public IEnumerable<(GameProfile Game, IEnumerable<QuickPreset> Presets)> PresetHotkeyScope() =>
        _profiles.Select(p => (p, ReferenceEquals(p, _presetGame) ? (IEnumerable<QuickPreset>)_presets : p.Presets ?? new List<QuickPreset>()));

    /// <summary>Switches the Presets section to a game and explicitly loads the editor for the chosen preset.</summary>
    private void SelectPresetGame(GameProfile? game, string? preferPresetId = null)
    {
        PushPresetEditorIfDirty();
        SyncPresetsBackToGame();

        using (SuppressEditor())
        {
            // Detach before Clear — otherwise a sync could write an empty list into the old game.
            _selectedPreset = null;
            _presetGame = null;
            _presets.Clear();
            _presetGame = game;

            if (game != null)
            {
                game.Presets ??= new List<QuickPreset>();
                foreach (var p in game.Presets)
                    _presets.Add(p);
                LblPresetGame.Text = Loc.Tf("presets.for", game.Name);
            }
            else
            {
                LblPresetGame.Text = Loc.T("tab.presets");
            }
        }

        var pick = (preferPresetId != null ? _presets.FirstOrDefault(p => p.Id == preferPresetId) : null)
                   ?? _presets.FirstOrDefault();
        SelectPreset(pick);

        bool hasGame = game != null;
        BtnPresetAdd.IsEnabled = hasGame;
        BtnPresetExport.IsEnabled = hasGame && _presets.Count > 0;
        BtnPresetImport.IsEnabled = hasGame;
        BtnPackGallery.IsEnabled = hasGame;
        Host.RefreshHotkeyBindings();
        UpdatePresetEmptyCta();
    }

    /// <summary>Selects a preset in the list AND loads it into the editor (selection under suppression never does that by itself).</summary>
    private void SelectPreset(QuickPreset? p)
    {
        if (!ReferenceEquals(_selectedPreset, p))
            PushPresetEditorIfDirty();
        using (SuppressEditor())
            PresetList.SelectedItem = p;
        _selectedPreset = p;
        LoadPresetEditor(p);
        if (p != null) PresetList.ScrollIntoView(p);
        BtnPresetDelete.IsEnabled = p != null;
    }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SuppressingEditor) return;
        var next = PresetList.SelectedItem as QuickPreset;
        if (ReferenceEquals(next, _selectedPreset)) return;
        SelectPreset(next);
    }

    private void SyncPresetsBackToGame()
    {
        if (_presetGame == null) return;
        _presetGame.Presets = _presets.ToList();
        // A deleted startup preset must not linger as a dangling id.
        if (_presetGame.StartupPresetId != null && _presetGame.Presets.All(p => p.Id != _presetGame.StartupPresetId))
            _presetGame.StartupPresetId = null;
        if (_selected != null && _selected.Id == _presetGame.Id)
        {
            using (SuppressEditor())
                FillStartupPresetCombo(_selected, _selected.StartupPresetId);
            UpdateColorSummary(_selected);
        }
    }

    private void LoadPresetEditor(QuickPreset? p)
    {
        _presetEditorDirty = false;
        using (SuppressEditor())
        {
            bool has = p != null;
            PresetEditor.IsEnabled = has;
            PresetEditor.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            LblPresetEditorEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            LblPresetEditorEmpty.Text = _presetGame == null ? Loc.T("presets.pick.game") : Loc.T("presets.pick.preset");

            if (p == null)
            {
                // Reset ALL fields so nothing from the previous preset can be pushed into another one.
                TxtPresetName.Text = "";
                TxtPresetHotkey.Text = "";
                ChkPresetRes.IsChecked = false;
                CmbPresetRes.SelectedIndex = -1;
                ChkPresetLock.IsChecked = false;
                var neutral = ColorSettings.Neutral;
                _presetEditorBackend = neutral.Backend;
                ColorUiHelper.SetBackendToggle(neutral.Backend, TogPresetBackend);
                ColorUiHelper.ApplyColorSliders(neutral, SldPresetB, SldPresetC, SldPresetG, SldPresetV, SldPresetShadow, SldPresetH);
                UpdatePresetLabels();
                UpdateDependentEnables();
                return;
            }

            TxtPresetName.Text = p.Name;
            TxtPresetHotkey.Text = p.Hotkey ?? "";
            _hotkeyOriginal[TxtPresetHotkey] = TxtPresetHotkey.Text;
            ChkPresetRes.IsChecked = p.ApplyResolution;
            p.EnsureDualColorSlots();
            _presetEditorBackend = p.Color.Backend;
            ColorUiHelper.SetBackendToggle(p.Color.Backend, TogPresetBackend);
            ChkPresetLock.IsChecked = p.Color.LockColor;
            if (!string.IsNullOrWhiteSpace(p.Resolution))
            {
                if (!CmbPresetRes.Items.Contains(p.Resolution)) CmbPresetRes.Items.Add(p.Resolution);
                CmbPresetRes.SelectedItem = p.Resolution;
            }
            else
                CmbPresetRes.SelectedItem = DisplayEngine.GetCurrentResolution();
            ColorUiHelper.ApplyColorSliders(p.Color, SldPresetB, SldPresetC, SldPresetG, SldPresetV, SldPresetShadow, SldPresetH);
            UpdatePresetLabels();
            UpdatePresetBackendDependent();
            UpdateBackendActiveLabel(ColorUiHelper.IsLowLevel(p.Color.Backend));
            UpdateDependentEnables();
        }
    }

    /// <summary>Name typing must not Refresh the list (that reloads the TextBox and eats characters).</summary>
    private void PresetName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SuppressingEditor || _selectedPreset == null) return;
        _selectedPreset.Name = TxtPresetName.Text;
        _presetEditorDirty = true;
        MarkDirty();
    }

    private void PresetName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_selectedPreset == null || SuppressingEditor) return;
        var trimmed = TxtPresetName.Text.Trim();
        if (trimmed.Length == 0) trimmed = Loc.T("preset.new");
        _selectedPreset.Name = trimmed;
        if (TxtPresetName.Text != trimmed)
        {
            using (SuppressEditor())
                TxtPresetName.Text = trimmed;
        }
        RefreshPresetListSafe();
        SyncPresetsBackToGame();
        MarkDirty();
    }

    private void PresetEditorChanged(object sender, RoutedEventArgs e)
    {
        if (SuppressingEditor || _selectedPreset == null) return;
        UpdateDependentEnables();
        if (ReferenceEquals(sender, ChkPresetRes) && ChkPresetRes.IsChecked == true && CmbPresetRes.SelectedItem == null)
        {
            using (SuppressEditor())
                CmbPresetRes.SelectedItem = DisplayEngine.GetCurrentResolution();
        }
        MarkDirty();
        PushPresetEditor();
        RefreshPresetListSafe();
    }

    private void PresetColor_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SuppressingEditor) return;
        UpdatePresetLabels();
        if (_selectedPreset == null) return;
        _selectedPreset.ApplyColor = true;
        MarkDirty();
        PushPresetEditor();
        // Don't refresh the list on every slider tick — debounce the summary update.
        _presetListRefreshTimer.Stop();
        _presetListRefreshTimer.Start();
    }

    private void RefreshPresetListSafe()
    {
        using (SuppressEditor())
        {
            PresetList.Items.Refresh();
            if (_selectedPreset != null && !ReferenceEquals(PresetList.SelectedItem, _selectedPreset))
                PresetList.SelectedItem = _selectedPreset;
        }
        UpdatePresetEmptyCta();
    }

    private void UpdatePresetEmptyCta()
    {
        bool empty = _presets.Count == 0 && _presetGame != null;
        PresetEmptyCta.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        LblPresetEmpty.Text = Loc.T("presets.empty");
        BtnPresetEmptyGallery.Content = Loc.T("presets.empty.gallery");
        BtnPresetExport.IsEnabled = _presetGame != null && _presets.Count > 0;
    }

    private void UpdatePresetLabels()
    {
        var backend = ColorUiHelper.ReadBackendToggle(TogPresetBackend);
        ColorUiHelper.UpdateValues(ValPresetB, ValPresetC, ValPresetG, ValPresetV, SldPresetB, SldPresetC, SldPresetG, SldPresetV,
            backend, ValPresetH, SldPresetH, ValPresetShadow, SldPresetShadow);
    }

    /// <summary>Shadows only exist on the Low Level ramp, hue only on the driver path.</summary>
    private void UpdatePresetBackendDependent()
    {
        bool low = ColorUiHelper.ReadBackendToggle(TogPresetBackend) == ColorBackend.LowLevel;
        SldPresetShadow.IsEnabled = low;
        LblPresetShadow.Opacity = low ? 1.0 : 0.45;
        ValPresetShadow.Opacity = low ? 1.0 : 0.45;
        SldPresetH.IsEnabled = !low;
        LblPresetH.Opacity = low ? 0.45 : 1.0;
        ValPresetH.Opacity = low ? 0.45 : 1.0;
    }

    private void PresetBackendToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (SuppressingEditor) return;
        var next = ColorUiHelper.ReadBackendToggle(TogPresetBackend);

        if (_selectedPreset == null || next == _presetEditorBackend)
        {
            UpdateBackendActiveLabel(next == ColorBackend.LowLevel);
            UpdatePresetLabels();
            UpdatePresetBackendDependent();
            return;
        }

        _selectedPreset.EnsureDualColorSlots();
        var prev = ColorUiHelper.ReadColorFromSliders(_presetEditorBackend, SldPresetB, SldPresetC, SldPresetG, SldPresetV, SldPresetShadow, ChkPresetLock.IsChecked == true, SldPresetH);
        _selectedPreset.Color = prev;
        _selectedPreset.SaveActiveToDualSlots();
        _presetEditorBackend = next;
        var loaded = _selectedPreset.ActivateBackend(next);

        using (SuppressEditor())
        {
            ColorUiHelper.ApplyColorSliders(loaded, SldPresetB, SldPresetC, SldPresetG, SldPresetV, SldPresetShadow, SldPresetH);
            ChkPresetLock.IsChecked = loaded.LockColor;
            ColorUiHelper.ConfigureGammaRangeForBackend(next, SldPresetG);
        }

        UpdatePresetLabels();
        UpdatePresetBackendDependent();
        UpdateBackendActiveLabel(next == ColorBackend.LowLevel);
        MarkDirty();
        PushPresetEditor();
        RefreshPresetListSafe();
    }

    public void UpdateDriverUiAvailability()
    {
        ColorUiHelper.LabelBackendToggle(TogPresetBackend);
        TogPresetBackend.IsEnabled = true;
    }

    private void UpdateBackendActiveLabel(bool lowLevel)
    {
        LblPresetBackendActive.Text = lowLevel
            ? Loc.T("color.backend.low.active")
            : Loc.Tf("color.backend.driver.active", GpuVendorDetect.DriverLabel);
    }

    private void PushPresetEditorIfDirty()
    {
        if (_selectedPreset != null && _presetEditorDirty)
            PushPresetEditor();
    }

    private void PushPresetEditor()
    {
        _presetEditorDirty = false;
        if (_selectedPreset == null) return;
        // Keep live name from TextBox (trim only on LostFocus) so mid-type spaces aren't eaten.
        _selectedPreset.Name = ReferenceEquals(Keyboard.FocusedElement, TxtPresetName)
            ? TxtPresetName.Text
            : TxtPresetName.Text.Trim();
        _selectedPreset.Hotkey = string.IsNullOrWhiteSpace(TxtPresetHotkey.Text) ? null : TxtPresetHotkey.Text.Trim();
        _selectedPreset.ApplyResolution = ChkPresetRes.IsChecked == true;
        // Keep ApplyColor as-is (res-only presets stay false until color sliders change).
        _selectedPreset.Resolution = CmbPresetRes.SelectedItem?.ToString() ?? _selectedPreset.Resolution;

        var c = ColorUiHelper.ReadColorFromSliders(
            ColorUiHelper.ReadBackendToggle(TogPresetBackend),
            SldPresetB, SldPresetC, SldPresetG, SldPresetV, SldPresetShadow,
            ChkPresetLock.IsChecked == true, SldPresetH);
        // Guard against saving unloaded slider defaults (0/0/0/0) over a good preset.
        if (c.Brightness <= 0.02 && c.Contrast <= 0.02 && c.Vibrance == 0
            && _selectedPreset.Color != null
            && !(_selectedPreset.Color.Brightness <= 0.02 && _selectedPreset.Color.Contrast <= 0.02 && _selectedPreset.Color.Vibrance == 0))
        {
            c = _selectedPreset.Color.Clone();
            c.Backend = ColorUiHelper.ReadBackendToggle(TogPresetBackend);
        }

        _selectedPreset.Color = c;
        _presetEditorBackend = _selectedPreset.Color.Backend;
        _selectedPreset.SaveActiveToDualSlots();
    }

    private string UniquePresetName(string baseName)
    {
        if (_presets.All(p => !string.Equals(p.Name, baseName, StringComparison.OrdinalIgnoreCase)))
            return baseName;
        for (int i = 2; i < 100; i++)
        {
            var candidate = $"{baseName} {i}";
            if (_presets.All(p => !string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return baseName;
    }

    private void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_presetGame == null)
        {
            ShowToast(Loc.T("presets.pick.game"));
            return;
        }
        var p = new QuickPreset
        {
            Id = _presetGame.Id + "_" + Guid.NewGuid().ToString("N")[..8],
            Name = UniquePresetName(Loc.T("preset.new")),
            ApplyColor = true,
            Color = ColorSettings.Neutral,
            Hotkey = null
        };
        p.EnsureDualColorSlots();
        _presets.Add(p);
        SyncPresetsBackToGame();
        SelectPreset(p);
        MarkDirty();
        UpdatePresetEmptyCta();
        Dispatcher.BeginInvoke(() =>
        {
            TxtPresetName.Focus();
            TxtPresetName.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPreset == null || _presetGame == null) return;
        var victim = _selectedPreset;
        var game = _presetGame;
        if (!Host.ConfirmDestructive(Loc.Tf("confirm.deletePreset", victim.Name, game.Name), Loc.T("confirm.deletePreset.title"), Loc.T("btn.delete")))
            return;

        PushPresetEditorIfDirty();
        int index = _presets.IndexOf(victim);
        bool wasStartup = game.StartupPresetId == victim.Id;
        _selectedPreset = null; // don't push the deleted preset back
        using (SuppressEditor())
            _presets.Remove(victim);
        SyncPresetsBackToGame(); // persists even when the list is now empty
        SelectPreset(_presets.Count == 0 ? null : _presets[Math.Clamp(index, 0, _presets.Count - 1)]);
        MarkDirty();
        FlushAutosave();
        Host.RefreshHotkeyBindings();
        UpdatePresetEmptyCta();

        Host.ShowUndoToast(Loc.Tf("toast.deleted", victim.Name), () =>
        {
            if (!ReferenceEquals(_presetGame, game))
                SelectProfile(game);
            using (SuppressEditor())
                _presets.Insert(Math.Clamp(index, 0, _presets.Count), victim);
            if (wasStartup) game.StartupPresetId = victim.Id;
            SyncPresetsBackToGame();
            SelectPreset(victim);
            MarkDirty();
            FlushAutosave();
            UpdatePresetEmptyCta();
            ShowToast(Loc.Tf("toast.restored", victim.Name));
        });
    }

    private void ApplyPresetNow_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPreset == null) return;
        PushPresetEditorIfDirty();
        MarkDirty();
        FlushAutosave();
        App.Services.Monitor.ApplyPreset(_selectedPreset);
        ShowToast(Loc.Tf("toast.preset.applied", _selectedPreset.Name));
        RefreshGamesTab();
    }

    private void ExportPresets_Click(object sender, RoutedEventArgs e)
    {
        if (_presetGame == null)
        {
            ShowToast(Loc.T("presets.pick.game"));
            return;
        }
        PushPresetEditorIfDirty();
        SyncPresetsBackToGame();
        if (PresetPackService.Export(_presetGame))
            ShowToast(Loc.T("toast.presets.exported"));
    }

    private void ImportPresets_Click(object sender, RoutedEventArgs e)
    {
        if (_presetGame == null)
        {
            ShowToast(Loc.T("presets.pick.game"));
            return;
        }
        PushPresetEditorIfDirty();
        SyncPresetsBackToGame();
        var game = _presetGame;
        int before = game.Presets?.Count ?? 0;
        int n = PresetPackService.Import(game);
        if (n <= 0) return;

        var firstNew = game.Presets?.Skip(before).FirstOrDefault();
        SelectPresetGame(game, firstNew?.Id);
        MarkDirty();
        FlushAutosave();
        ShowToast(Loc.Tf("toast.presets.imported", n));
    }

    private void PackGallery_Click(object sender, RoutedEventArgs e)
    {
        var win = new PackGalleryWindow { Owner = Host.Window };
        win.AcceptTargetGame((_presetGame ?? _selected)?.Name);
        win.ShowDialog();
    }

    /// <summary>
    /// Apply a built-in preset pack onto the Presets-section game. Re-applying the same pack replaces presets
    /// with the same name (keeping their Id and the user's hotkey) instead of duplicating them.
    /// </summary>
    public void ApplyPackPresets(IEnumerable<QuickPreset> presets, string packName)
    {
        var game = _presetGame ?? _selected;
        if (game == null)
        {
            UiSound.Error();
            ThemedDialog.Show(Host.Window, Loc.T("presets.pick.game"), Loc.T("presets.gallery"));
            return;
        }
        if (!ReferenceEquals(game, _presetGame))
            SelectPresetGame(game);

        PushPresetEditorIfDirty();
        SyncPresetsBackToGame();

        int added = 0, replaced = 0;
        QuickPreset? last = null;
        foreach (var src in presets)
        {
            var clone = QuickPreset.CloneOf(src);
            var existing = _presets.FirstOrDefault(p => string.Equals(p.Name?.Trim(), src.Name?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                clone.Id = existing.Id;
                clone.Hotkey = existing.Hotkey ?? (HotkeyFreeInGame(clone.Hotkey, existing.Id) ? clone.Hotkey : null);
                using (SuppressEditor())
                    _presets[_presets.IndexOf(existing)] = clone;
                replaced++;
            }
            else
            {
                clone.Id = game.Id + "_" + Guid.NewGuid().ToString("N")[..8];
                if (!HotkeyFreeInGame(clone.Hotkey, null)) clone.Hotkey = null;
                using (SuppressEditor())
                    _presets.Add(clone);
                added++;
            }
            last = clone;
        }

        _selectedPreset = null;
        SyncPresetsBackToGame();
        SelectPreset(last ?? _presets.FirstOrDefault());
        MarkDirty();
        FlushAutosave();
        RefreshPresetListSafe();
        ShowToast(Loc.Tf("toast.pack.applied", packName, added, replaced));
    }

    // -- Hotkey capture (preset box) ---------------------------------------------------------------------

    private bool HotkeyFreeInGame(string? gesture, string? ignorePresetId)
    {
        if (string.IsNullOrWhiteSpace(gesture) || !HotkeyService.TryParseGesture(gesture!, out var m, out var vk))
            return true;
        if (Host.GlobalHotkeyBoxes.Any(g => HotkeyCapture.SameGesture(g.Box.Text, m, vk))) return false;
        return !_presets.Any(p => p.Id != ignorePresetId && HotkeyCapture.SameGesture(p.Hotkey, m, vk));
    }

    /// <summary>Localized description of what already uses this gesture, or null when it's free.</summary>
    private string? FindHotkeyConflict(string gesture)
    {
        if (!HotkeyService.TryParseGesture(gesture, out var mods, out var vk)) return null;

        foreach (var (other, labelKey) in Host.GlobalHotkeyBoxes)
        {
            if (HotkeyCapture.SameGesture(other.Text, mods, vk))
                return Loc.Tf("hotkey.conflict.global", Loc.T(labelKey));
        }

        // Preset hotkeys are per game: only the open game's presets can collide.
        if (_presetGame != null)
        {
            foreach (var preset in _presets)
            {
                if (ReferenceEquals(preset, _selectedPreset)) continue;
                if (HotkeyCapture.SameGesture(preset.Hotkey, mods, vk))
                    return Loc.Tf("hotkey.conflict.preset", preset.Name, _presetGame.Name);
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
                // Esc cancels capture and keeps the previous binding.
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
        var conflict = FindHotkeyConflict(gesture);
        if (conflict != null)
        {
            UiSound.Warn();
            box.Text = _hotkeyOriginal.TryGetValue(box, out var orig) ? orig : box.Text;
            box.SelectAll();
            ShowToast(Loc.Tf("hotkey.conflict.blocked", gesture, conflict));
            return;
        }
        CommitHotkey(box, gesture);
        box.SelectAll();
    }

    private void CommitHotkey(WpfTextBox box, string gesture)
    {
        if (_selectedPreset == null) return;
        box.Text = gesture;
        _hotkeyOriginal[box] = gesture;
        PushPresetEditor();
        RefreshPresetListSafe();
        MarkDirty();
        FlushAutosave(); // persists and re-registers hotkeys immediately
        RefreshGamesTab();
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e) => CommitHotkey(TxtPresetHotkey, "");
}
