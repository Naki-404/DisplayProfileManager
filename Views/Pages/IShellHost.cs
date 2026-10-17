using System.Collections.ObjectModel;
using System.Windows;
using Visor.Models;

namespace Visor;

/// <summary>Top-level sections of the shell, in nav-rail order (Ctrl+1 … Ctrl+7).</summary>
public enum ShellSection
{
    Home,
    MyPc,
    Games,
    Optimize,
    Network,
    Monitor,
    Settings
}

/// <summary>
/// What a page may ask of the shell. <see cref="MainWindow"/> implements it; pages never reach for the
/// window directly, so a page can be hosted (or unit-faked) without the chrome.
/// </summary>
public interface IShellHost
{
    /// <summary>Owner for modal dialogs / file pickers.</summary>
    Window Window { get; }

    // -- Navigation ------------------------------------------------------------------------------
    ShellSection CurrentSection { get; }
    /// <summary>True when the window is visible and <paramref name="section"/> is the selected one.</summary>
    bool IsSectionLive(ShellSection section);
    void Navigate(ShellSection section);
    /// <summary>Section plus one of its sub-sections (e.g. Network → "dns", Optimize → "startup").</summary>
    void Navigate(ShellSection section, string? subSection);
    /// <summary>Games section with this profile selected in the list/editor.</summary>
    void OpenGame(GameProfile profile);
    /// <summary>Games section, new blank profile, name box focused.</summary>
    void AddGame();
    /// <summary>Runs the installed-games scan (Games section).</summary>
    void ScanGames();
    /// <summary>Settings section → History tab.</summary>
    void OpenHistory();
    /// <summary>Modal preferences dialog (language, theme, behavior, sounds, data).</summary>
    void OpenPreferences();

    // -- Feedback --------------------------------------------------------------------------------
    void ShowToast(string message);
    void ShowToast(string message, string? actionText, Action? action);
    /// <summary>Snackbar with an Undo action for a destructive step.</summary>
    void ShowUndoToast(string message, Action undo);
    /// <summary>Danger confirmation that honors the "Ask before deleting" preference.</summary>
    bool ConfirmDestructive(string message, string title, string actionText);
    /// <summary>Full-window "scanning…" overlay (Games scan).</summary>
    ScanOverlay ScanBusy { get; }

    // -- Shared document state (profiles + autosave) ---------------------------------------------
    /// <summary>Editor copies of the config profiles; the Games page edits these in place.</summary>
    ObservableCollection<GameProfile> Profiles { get; }
    bool IsDirty { get; }
    /// <summary>Schedules the debounced autosave and shows the dirty marker.</summary>
    void MarkDirty();
    /// <summary>Pushes every page editor into <see cref="Profiles"/> and writes the config now.</summary>
    void FlushAutosave();
    /// <summary>Programmatic editor updates inside this scope don't fire change handlers (shared, nestable).</summary>
    IDisposable SuppressEditor();
    bool SuppressingEditor { get; }
    /// <summary>Re-registers global + preset hotkeys from the current config.</summary>
    void RefreshHotkeyBindings();
    /// <summary>App hotkeys were edited (Settings) — pages showing them (Games cheat sheet) refresh.</summary>
    void NotifyGlobalHotkeysChanged();

    // -- Cross-page queries --------------------------------------------------------------------
    /// <summary>App-wide hotkey capture boxes (Settings page) with their label keys — for conflict checks.</summary>
    IEnumerable<(System.Windows.Controls.TextBox Box, string LabelKey)> GlobalHotkeyBoxes { get; }
    /// <summary>Every game with its live preset list (the Games page's working copy for the open game).</summary>
    IEnumerable<(GameProfile Game, IEnumerable<QuickPreset> Presets)> PresetHotkeyScope();
    /// <summary>Game selected in the Games page (editor copy), if any.</summary>
    GameProfile? SelectedProfile { get; }

    // -- Session actions shared by several pages ---------------------------------------------------
    void TogglePauseMonitoring();
    /// <summary>Immediate "Reset display" (no confirmation — callers confirm when the button is easy to hit by mistake).</summary>
    void EmergencyRestore();
    /// <summary>Tooltip mode: explanations on hover for every control that has an InfoHint.</summary>
    bool TooltipMode { get; set; }
}
