using System.IO;
using System.Text.RegularExpressions;

namespace DisplayProfileManager.Services;

/// <summary>Local-only path / task validation — no network, no shell metacharacters.</summary>
public static class PathSecurity
{
    private static readonly Regex SafeTaskName = new(
        @"^[\w.\-]{1,120}(\\([\w.\-]{1,120}))*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".wsh",
        ".msi", ".msp", ".scr", ".com", ".pif", ".hta", ".lnk", ".url"
    };

    public static bool IsSafeScheduledTaskName(string? taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName)) return false;
        if (taskName.Length > 200) return false;
        if (taskName.IndexOfAny(new[] { '"', '\'', '&', '|', '>', '<', '^', '\n', '\r', '\0' }) >= 0)
            return false;
        return SafeTaskName.IsMatch(taskName);
    }

    /// <summary>Shape check only — does not require the file to exist (config migrate).</summary>
    public static bool IsPlausibleExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.IndexOfAny(new[] { '"', '\'', '&', '|', '>', '<', '^', '\n', '\r', '\0' }) >= 0)
            return false;
        try
        {
            var full = Path.GetFullPath(path.Trim());
            var ext = Path.GetExtension(full);
            return string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase)
                   && !BlockedExtensions.Contains(ext);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryNormalizeExecutable(string? path, out string fullPath, out string error)
    {
        fullPath = "";
        error = "";
        if (!IsPlausibleExecutablePath(path))
        {
            error = "Invalid or blocked companion path.";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path!.Trim());
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = "File not found.";
            return false;
        }

        return true;
    }

    public static bool IsSafeAutostartExe(string? path, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.IndexOfAny(new[] { '"', '&', '|', '>', '<', '^', '\n', '\r', '\0' }) >= 0)
            return false;
        try
        {
            fullPath = Path.GetFullPath(path.Trim());
        }
        catch
        {
            return false;
        }

        return string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase)
               && File.Exists(fullPath);
    }

    /// <summary>
    /// Companion launch args — no shell metacharacters. Empty is allowed.
    /// Example: -launchapp elkagffjjeonbcmfpdndkckppafabjeklmdidong
    /// </summary>
    public static bool IsSafeArguments(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return true;
        if (args.Length > 1024) return false;
        if (args.IndexOfAny(new[] { '&', '|', '>', '<', '^', '\n', '\r', '\0', ';' }) >= 0)
            return false;
        return true;
    }

    /// <summary>Split args for ProcessStartInfo.ArgumentList (handles quoted tokens).</summary>
    public static List<string> SplitArguments(string? args)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(args)) return list;

        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (char c in args.Trim())
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0)
            list.Add(sb.ToString());
        return list;
    }
}
