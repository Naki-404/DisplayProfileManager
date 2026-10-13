using System.Globalization;

namespace Visor.Services;

public sealed class CommandPaletteItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string Group { get; init; } = "";
    public string? SearchText { get; init; }
    /// <summary>False = shown greyed and never executed (e.g. a scan is already running).</summary>
    public bool IsEnabled { get; init; } = true;

    public CommandPaletteItem WithEnabled(bool enabled) => enabled == IsEnabled ? this : new CommandPaletteItem
    {
        Id = Id,
        Title = Title,
        Subtitle = Subtitle,
        Group = Group,
        SearchText = SearchText,
        IsEnabled = enabled
    };
}

/// <summary>Pure filter/rank helper for the Ctrl+K palette (unit-testable).</summary>
public static class CommandPaletteCatalog
{
    /// <summary>Marks items via the UI's enablement seam (null = everything enabled). Ids are never matched by search.</summary>
    public static IReadOnlyList<CommandPaletteItem> ApplyEnabled(IEnumerable<CommandPaletteItem> items, Func<string, bool>? isEnabled)
    {
        var list = items?.ToList() ?? new List<CommandPaletteItem>();
        if (isEnabled == null) return list;
        for (int i = 0; i < list.Count; i++)
        {
            bool enabled;
            try { enabled = isEnabled(list[i].Id); }
            catch { enabled = true; }
            list[i] = list[i].WithEnabled(enabled);
        }
        return list;
    }

    public static IReadOnlyList<CommandPaletteItem> Filter(string? query, IEnumerable<CommandPaletteItem> items)
    {
        var list = items?.ToList() ?? new List<CommandPaletteItem>();
        if (list.Count == 0) return list;

        string q = (query ?? "").Trim();
        if (q.Length == 0)
            return list;

        var scored = new List<(int Score, int Index, CommandPaletteItem Item)>();
        for (int i = 0; i < list.Count; i++)
        {
            int score = Score(list[i], q);
            if (score > 0)
                scored.Add((score, i, list[i]));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Index)
            .Select(s => s.Item)
            .ToList();
    }

    private static int Score(CommandPaletteItem item, string query)
    {
        // Internal ids ("cmd.pause", "game.<guid>") are not user-facing and must not match.
        string hay = string.Join(' ',
            item.Title ?? "",
            item.Subtitle ?? "",
            item.Group ?? "",
            item.SearchText ?? "");
        if (hay.Trim().Length == 0) return 0;

        var cmp = CultureInfo.CurrentCulture.CompareInfo;
        if (cmp.IndexOf(item.Title ?? "", query, CompareOptions.IgnoreCase) == 0)
            return 300;
        if (cmp.IndexOf(item.Title ?? "", query, CompareOptions.IgnoreCase) >= 0)
            return 200;
        if (cmp.IndexOf(hay, query, CompareOptions.IgnoreCase) >= 0)
            return 100;

        // Token match: all query words appear somewhere.
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length > 1 && words.All(w => cmp.IndexOf(hay, w, CompareOptions.IgnoreCase) >= 0))
            return 80;

        return 0;
    }
}
