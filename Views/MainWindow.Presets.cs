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

/// <summary>Presets page: per-game preset editor, pack import, hotkey capture, live color backend.</summary>
public partial class MainWindow : Window
{
    // -- Presets (per-game) ------------------------------------------------------------------------------

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

    /// <summary>
    /// Presets are per-game. When opening the Presets tab (or when a game starts), select that game
    /// so its hotkeyed presets are visible.
    /// </summary>
    public void FocusPresetsForActiveOrSelectedGame(GameProfile? prefer = null)
    {
        prefer ??= _presetGame == null ? (App.Services.Monitor.CurrentProfile ?? _selected ?? _profiles.FirstOrDefault()) : null;
        if (prefer == null) return;

        var match = _profiles.FirstOrDefault(p => p.Id == prefer.Id)
                    ?? _profiles.FirstOrDefault(p =>
                        string.Equals(p.ProcessName, prefer.ProcessName, StringComparison.OrdinalIgnoreCase));
        if (match == null || ReferenceEquals(match, _presetGame)) return;
        SelectPresetGame(match);
    }

    private void PresetGameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SuppressingEditor) return;
        SelectPresetGame(PresetGameList.SelectedItem as GameProfile);
    }

    /// <summary>Compact game picker (shown instead of the tile column on narrow windows).</summary>
    private void PresetGameCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SuppressingEditor) return;
        var game = CmbPresetGame.SelectedItem as GameProfile;
        if (ReferenceEquals(game, _presetGame)) return;
        SelectPresetGame(game);
    }

    private bool _presetsCompact;

    /// <summary>Below ~760 DIP of page width (≈1000 DIP window) the game column collapses into CmbPresetGame.</summary>
    private void PresetsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PresetGamesCard == null) return;
        bool compact = e.NewSize.Width < 760;
        if (compact == _presetsCompact && e.PreviousSize.Width > 0) return;
        _presetsCompact = compact;
        PresetGamesCard.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ColPresetGames.Width = compact ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        ColPresetGames.MinWidth = compact ? 0 : 170;
        ColPresetGamesGap.Width = new GridLength(compact ? 0 : 12);
        CmbPresetGame.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        if (compact)
        {
            using (SuppressEditor())
                CmbPresetGame.SelectedItem = _presetGame;
        }
    }

    /// <summary>Switches the Presets tab to a game and explicitly loads the editor for the chosen preset.</summary>
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
            PresetGameList.SelectedItem = game;
            CmbPresetGame.SelectedItem = game;
            _presetGame = game;

            if (game != null)
            {
                game.Presets ??= new List<QuickPreset>();
                foreach (var p in game.Presets)
                    _presets.Add(p);
                LblPresetGame.Text = Loc.Tf("presets.for", game.Name);
                PresetGameList.ScrollIntoView(game);
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
        RefreshHotkeyBindings();
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

    private void UpdateDriverUiAvailability()
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
        if (!ConfirmDestructive(Loc.Tf("confirm.deletePreset", victim.Name, game.Name), Loc.T("confirm.deletePreset.title"), Loc.T("btn.delete")))
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
        RefreshHotkeyBindings();
        UpdatePresetEmptyCta();

        ShowUndoToast(Loc.Tf("toast.deleted", victim.Name), () =>
        {
            if (!ReferenceEquals(_presetGame, game))
                SelectPresetGame(game);
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
        var win = new PackGalleryWindow { Owner = this };
        win.AcceptTargetGame((_presetGame ?? _selected)?.Name);
        win.ShowDialog();
    }

    /// <summary>
    /// Apply a built-in preset pack onto the Presets-tab game. Re-applying the same pack replaces presets
    /// with the same name (keeping their Id and the user's hotkey) instead of duplicating them.
    /// </summary>
    public void ApplyPackPresets(IEnumerable<QuickPreset> presets, string packName)
    {
        var game = _presetGame ?? _selected;
        if (game == null)
        {
            UiSound.Error();
            ThemedDialog.Show(this, Loc.T("presets.pick.game"), Loc.T("presets.gallery"));
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

    // -- Hotkey capture -----------------------------------------------------------------------------------

    private IEnumerable<(System.Windows.Controls.TextBox Box, string LabelKey)> GlobalHotkeyBoxes()
    {
        yield return (HkToggleOverlay, "hotkey.overlay");
        yield return (HkEmergency, "hotkey.emergency");
        yield return (HkNextPreset, "hotkey.next");
        yield return (HkPrevPreset, "hotkey.prev");
        yield return (HkCompareAb, "hotkey.compareAb");
        yield return (HkToggleZoom, "hotkey.zoom");
    }

    private static bool SameGesture(string? a, uint mods, uint vk) =>
        !string.IsNullOrWhiteSpace(a)
        && HotkeyService.TryParseGesture(a!, out var am, out var av)
        && am == mods && av == vk;

    private bool HotkeyFreeInGame(string? gesture, string? ignorePresetId)
    {
        if (string.IsNullOrWhiteSpace(gesture) || !HotkeyService.TryParseGesture(gesture!, out var m, out var vk))
            return true;
        if (GlobalHotkeyBoxes().Any(g => SameGesture(g.Box.Text, m, vk))) return false;
        return !_presets.Any(p => p.Id != ignorePresetId && SameGesture(p.Hotkey, m, vk));
    }

    /// <summary>Localized description of what already uses this gesture, or null when it's free.</summary>
    private string? FindHotkeyConflict(System.Windows.Controls.TextBox box, string gesture)
    {
        if (!HotkeyService.TryParseGesture(gesture, out var mods, out var vk)) return null;

        foreach (var (other, labelKey) in GlobalHotkeyBoxes())
        {
            if (ReferenceEquals(other, box)) continue;
            if (SameGesture(other.Text, mods, vk))
                return Loc.Tf("hotkey.conflict.global", Loc.T(labelKey));
        }

        // Preset hotkeys are per game; global hotkeys must not collide with any game's presets.
        IEnumerable<(GameProfile Game, IEnumerable<QuickPreset> Presets)> scope = ReferenceEquals(box, TxtPresetHotkey)
            ? (_presetGame != null ? new[] { (_presetGame, (IEnumerable<QuickPreset>)_presets) } : Array.Empty<(GameProfile, IEnumerable<QuickPreset>)>())
            : _profiles.Select(p => (p, ReferenceEquals(p, _presetGame) ? (IEnumerable<QuickPreset>)_presets : p.Presets ?? new List<QuickPreset>()));

        foreach (var (game, list) in scope)
        {
            foreach (var preset in list)
            {
                if (ReferenceEquals(box, TxtPresetHotkey) && ReferenceEquals(preset, _selectedPreset)) continue;
                if (SameGesture(preset.Hotkey, mods, vk))
                    return Loc.Tf("hotkey.conflict.preset", preset.Name, game.Name);
            }
        }
        return null;
    }

    private void HotkeyBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        _hotkeyOriginal[box] = box.Text;
        box.Tag = Loc.T("hotkey.listening");
        // Announce the capture state to assistive tech (the watermark alone is visual only).
        System.Windows.Automation.AutomationProperties.SetLiveSetting(box, System.Windows.Automation.AutomationLiveSetting.Polite);
        System.Windows.Automation.AutomationProperties.SetItemStatus(box, Loc.T("hotkey.listening"));
        System.Windows.Automation.AutomationProperties.SetHelpText(box, Loc.T("hotkey.listening"));
        box.SelectAll();
    }

    private void HotkeyBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box)
        {
            box.Tag = Loc.T("hotkey.placeholder");
            System.Windows.Automation.AutomationProperties.SetItemStatus(box, "");
            System.Windows.Automation.AutomationProperties.SetHelpText(box, Loc.T("hotkey.placeholder"));
        }
    }

    private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        // Tab / Shift+Tab keep normal keyboard navigation.
        if (key == Key.Tab && (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
            return;

        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        // Esc cancels capture and keeps the previous binding.
        if (key == Key.Escape && mods == ModifierKeys.None)
        {
            box.Text = _hotkeyOriginal.TryGetValue(box, out var orig) ? orig : box.Text;
            Keyboard.ClearFocus();
            return;
        }

        // Backspace / Delete clear the binding.
        if ((key is Key.Back or Key.Delete) && mods == ModifierKeys.None)
        {
            CommitHotkey(box, "");
            return;
        }

        var gesture = HotkeyService.GestureFromKeys(mods, key);
        ApplyCapturedGesture(box, gesture);
    }

    /// <summary>
    /// Captures mouse XButton1/XButton2 as hotkeys on the same textboxes as keyboard capture
    /// (see HotkeyService's WH_MOUSE_LL fallback for how these are recognized at runtime).
    /// </summary>
    private void HotkeyBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2))
            return;

        var gesture = HotkeyService.GestureFromMouse(Keyboard.Modifiers, e.ChangedButton);
        if (gesture == null) return;

        e.Handled = true;
        box.Focus();
        ApplyCapturedGesture(box, gesture);
    }

    /// <summary>Conflicts are blocked: the old binding stays and the message names who owns the key.</summary>
    private void ApplyCapturedGesture(System.Windows.Controls.TextBox box, string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return;
        var conflict = FindHotkeyConflict(box, gesture);
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

    private void CommitHotkey(System.Windows.Controls.TextBox box, string gesture)
    {
        if (ReferenceEquals(box, TxtPresetHotkey) && _selectedPreset == null) return;
        box.Text = gesture;
        _hotkeyOriginal[box] = gesture;
        if (ReferenceEquals(box, TxtPresetHotkey))
        {
            PushPresetEditor();
            RefreshPresetListSafe();
        }
        MarkDirty();
        FlushAutosave(); // persists and re-registers hotkeys immediately
        RefreshGamesTab();
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: System.Windows.Controls.TextBox box })
            return;
        CommitHotkey(box, "");
    }

    private static string? NormHotkey(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
