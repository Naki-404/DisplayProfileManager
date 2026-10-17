using Visor.Models;

namespace Visor.Services;

/// <summary>Localized one-line rendering of activity history entries (Home "last activity", Settings → History).</summary>
public static class HistoryText
{
    public static string Format(ActivityHistoryEntry entry)
    {
        string when = entry.Utc.ToLocalTime().ToString("g");
        return when + "  ·  " + Body(entry);
    }

    /// <summary>The entry without its timestamp (compact rows).</summary>
    public static string Body(ActivityHistoryEntry entry)
    {
        string body = entry.Kind switch
        {
            "profileApplied" => string.IsNullOrEmpty(entry.Detail)
                ? Loc.Tf("history.kind.profileApplied", entry.ProfileName ?? "?")
                : Loc.Tf("history.kind.profileApplied.preset", entry.ProfileName ?? "?", entry.Detail),
            "presetApplied" => Loc.Tf("history.kind.presetApplied",
                entry.Detail ?? "?", entry.ProfileName ?? "?"),
            "sessionRestored" => string.IsNullOrEmpty(entry.ProfileName)
                ? Loc.T("history.kind.sessionRestored")
                : Loc.Tf("history.kind.sessionRestored.named", entry.ProfileName),
            "emergency" => Loc.T("history.kind.emergency"),
            "paused" => Loc.T("history.kind.paused"),
            "resumed" => Loc.T("history.kind.resumed"),
            "leftoverRestored" => Loc.T(entry.Success
                ? "history.kind.leftoverRestored"
                : "history.kind.leftoverRestored.fail"),
            "safeAction" => Loc.Tf("history.kind.safeAction", SafeActionDetail(entry.Detail)),
            "cleanup" => Loc.Tf("history.kind.cleanup", CleanupDetail(entry.Detail)),
            _ => entry.Kind
        };
        if (!entry.Success && entry.Kind is not "leftoverRestored")
            body += " · " + Loc.T("history.failed");
        return body;
    }

    public static string SafeActionDetail(string? detail) => detail switch
    {
        "gameMode=True" or "gameMode=true" => Loc.T("system.hist.gameMode.on"),
        "gameMode=False" or "gameMode=false" => Loc.T("system.hist.gameMode.off"),
        "gameMode.rollback" => Loc.T("system.hist.gameMode.rollback"),
        "powerHighPerf" => Loc.T("system.hist.power"),
        "power.rollback" => Loc.T("system.hist.power.rollback"),
        _ => ResolveDetail(detail)
    };

    /// <summary>
    /// Localized title for a SafeAction id stored as history detail (set by the UI, which knows the targets).
    /// Returns null for ids it cannot name; the raw id is shown then.
    /// </summary>
    public static Func<string, string?>? SafeActionTitleResolver { get; set; }

    private static string ResolveDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return "?";
        var resolve = SafeActionTitleResolver;
        if (resolve == null) return detail;
        try
        {
            const string undoSuffix = ".undo";
            if (detail.EndsWith(undoSuffix, StringComparison.Ordinal))
            {
                var undone = resolve(detail[..^undoSuffix.Length]);
                if (undone != null) return Loc.Tf("history.safeAction.undo", undone);
            }
            return resolve(detail) ?? detail;
        }
        catch (Exception)
        {
            return detail;
        }
    }

    /// <summary>"UserTemp|1234" → "Temporary files (older than 24 h) · 1.2 KB".</summary>
    public static string CleanupDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return "?";
        var parts = detail.Split('|');
        string label = Enum.TryParse<Optimize.Pc.CleanupCategory>(parts[0], out var cat)
            ? Loc.T(Optimize.Pc.CleanupService.CategoryLocKey(cat))
            : parts[0];
        return parts.Length > 1 && long.TryParse(parts[1], out long bytes)
            ? label + " · " + SystemInfoService.FormatBytes(bytes)
            : label;
    }
}
