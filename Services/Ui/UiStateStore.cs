using System.IO;
using System.Text.Json;

namespace Visor.Services;

/// <summary>
/// Small per-user UI state that is not part of AppConfig (window bounds, one-time notices).
/// Lives next to the config as ui-state.json. Failures are silent — this is cosmetic state only.
/// TODO(core): candidates to move into UiPreferences (WindowBounds, TrayNoticeShown).
/// </summary>
public sealed class UiState
{
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    /// <summary>"Visor keeps running in the tray" notice was shown once.</summary>
    public bool TrayNoticeShown { get; set; }
}

public static class UiStateStore
{
    private static UiState? _state;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string? FilePath
    {
        get
        {
            try
            {
                var dir = App.Services?.Config?.ConfigDirectory;
                return string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, "ui-state.json");
            }
            catch { return null; }
        }
    }

    public static UiState Current
    {
        get
        {
            if (_state != null) return _state;
            _state = new UiState();
            try
            {
                var path = FilePath;
                if (path != null && File.Exists(path))
                    _state = JsonSerializer.Deserialize<UiState>(File.ReadAllText(path)) ?? new UiState();
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "ui-state.json read");
                _state = new UiState();
            }
            return _state;
        }
    }

    public static void Save()
    {
        try
        {
            var path = FilePath;
            if (path == null) return;
            File.WriteAllText(path, JsonSerializer.Serialize(Current, Json));
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "ui-state.json write");
        }
    }
}
