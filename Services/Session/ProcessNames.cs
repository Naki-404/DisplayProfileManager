using System.IO;

namespace Visor.Services;

/// <summary>
/// Single place for exe-name normalization and validation (watcher, monitor, catalog, packs).
/// </summary>
public static class ProcessNames
{
    /// <summary>
    /// Well-known Windows / shell processes a game profile must never bind to
    /// (an imported pack could otherwise arm itself on explorer.exe and friends).
    /// </summary>
    private static readonly HashSet<string> ProtectedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "svchost", "csrss", "winlogon", "lsass", "dwm", "services", "smss",
        "wininit", "taskmgr", "conhost", "rundll32", "cmd", "powershell", "pwsh"
    };

    /// <summary>
    /// Comparison key: file name only, trimmed, lower-case, without ".exe".
    /// "VALORANT-Win64-Shipping.exe", "valorant-win64-shipping" and Process.ProcessName all map to the same key.
    /// </summary>
    public static string Key(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var n = Path.GetFileName(name.Trim());
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            n = n[..^4];
        return n.Trim().ToLowerInvariant();
    }

    /// <summary>Display / storage form: original casing, guaranteed ".exe" suffix.</summary>
    public static string ToExeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var n = name.Trim();
        if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            n += ".exe";
        return n;
    }

    /// <summary>Bare name for Process.GetProcessesByName (no extension, original casing).</summary>
    public static string Bare(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var n = Path.GetFileName(name.Trim());
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    public static bool Same(string? a, string? b)
    {
        var ka = Key(a);
        return ka.Length > 0 && ka == Key(b);
    }

    /// <summary>
    /// True for a plain exe file name ("game.exe" / "game"): no directory separators,
    /// no drive / stream colon, no invalid file-name characters, no relative dots.
    /// </summary>
    public static bool IsPlainExeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim();
        if (n.Length > 260) return false;
        if (n.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return false;
        if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (n is "." or ".." || n.StartsWith('.')) return false;
        if (Key(n).Length == 0) return false;
        var ext = Path.GetExtension(n);
        if (ext.Length == 0 || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return true;
        return !LooksLikeOtherExtension(ext);
    }

    public static bool IsProtectedSystemProcess(string? name) => ProtectedKeys.Contains(Key(name));

    // "game.v2" style names are fine (ProcessName has no extension); reject script/document extensions.
    private static bool LooksLikeOtherExtension(string ext) => ext.ToLowerInvariant() is
        ".bat" or ".cmd" or ".ps1" or ".vbs" or ".js" or ".lnk" or ".url" or ".dll" or ".sys" or ".msi" or ".scr" or ".com";
}
