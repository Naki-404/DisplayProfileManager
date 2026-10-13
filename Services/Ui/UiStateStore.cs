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
    /// <summary>Recommendation ids the user dismissed (Home card).</summary>
    public List<string> DismissedRecommendations { get; set; } = new();
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

    /// <summary>
    /// Pure bounds clamp for a saved window rect (DIPs): the rect is shrunk to fit the largest
    /// work area when oversized, and moved onto the primary (first) work area when it has less than
    /// <paramref name="minVisibleWidth"/>×<paramref name="minVisibleHeight"/> visible on any monitor
    /// (off-screen or on a monitor that no longer exists). A rect already visible is returned as-is.
    /// </summary>
    internal static System.Windows.Rect ClampToWorkAreas(
        System.Windows.Rect window,
        IReadOnlyList<System.Windows.Rect> workAreas,
        double minVisibleWidth = 48,
        double minVisibleHeight = 24)
    {
        if (workAreas.Count == 0 || window.IsEmpty) return window;

        double maxW = workAreas.Max(a => a.Width);
        double maxH = workAreas.Max(a => a.Height);
        double w = Math.Min(window.Width, maxW);
        double h = Math.Min(window.Height, maxH);
        var r = new System.Windows.Rect(window.Left, window.Top, w, h);

        foreach (var area in workAreas)
        {
            double visW = Math.Min(r.Right, area.Right) - Math.Max(r.Left, area.Left);
            double visH = Math.Min(r.Bottom, area.Bottom) - Math.Max(r.Top, area.Top);
            if (visW >= minVisibleWidth && visH >= minVisibleHeight)
                return r;
        }

        var primary = workAreas[0];
        double left = Math.Max(primary.Left, Math.Min(r.Left, primary.Right - r.Width));
        double top = Math.Max(primary.Top, Math.Min(r.Top, primary.Bottom - r.Height));
        return new System.Windows.Rect(left, top, Math.Min(r.Width, primary.Width), Math.Min(r.Height, primary.Height));
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
