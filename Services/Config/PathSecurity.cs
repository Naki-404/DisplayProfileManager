using System.IO;
using System.Text.RegularExpressions;

namespace Visor.Services;

/// <summary>
/// Local-only path / task validation — no network, no shell metacharacters.
/// Executables must live on a local drive (C:\…): UNC shares, \\?\ / \\.\ device paths,
/// relative paths and links that resolve off the local disk are rejected, so nothing we
/// launch or read an icon from can make Windows authenticate to a remote SMB host.
/// </summary>
public static class PathSecurity
{
    private static readonly Regex SafeTaskName = new(
        @"^[\w.\-]{1,120}(\\([\w.\-]{1,120}))*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly char[] ShellMetaChars = { '"', '\'', '&', '|', '>', '<', '^', '\n', '\r', '\0' };
    /// <summary>The Run command is double-quoted, so an apostrophe (C:\Users\O'Brien) is harmless there.</summary>
    private static readonly char[] AutostartBlockedChars = { '"', '&', '|', '>', '<', '^', '\n', '\r', '\0' };
    private const int MaxLinkHops = 8;

    public static bool IsSafeScheduledTaskName(string? taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName)) return false;
        if (taskName.Length > 200) return false;
        if (taskName.IndexOfAny(ShellMetaChars) >= 0)
            return false;
        return SafeTaskName.IsMatch(taskName);
    }

    /// <summary>
    /// Shape check only — does not touch the file (config migrate, editor validation):
    /// absolute local-drive path ending in .exe.
    /// </summary>
    public static bool IsPlausibleExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.IndexOfAny(ShellMetaChars) >= 0) return false;
        if (!TryGetLocalFullPath(path.Trim(), out var full)) return false;
        return string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates a companion exe and resolves symlinks / junctions to the final local target
    /// (each hop is validated before it is followed). <paramref name="fullPath"/> is the final target.
    /// </summary>
    public static bool TryNormalizeExecutable(string? path, out string fullPath, out string error)
    {
        fullPath = "";
        error = "";
        if (!IsPlausibleExecutablePath(path))
        {
            error = "Invalid or blocked companion path.";
            return false;
        }

        if (!TryResolveLocalTarget(path!.Trim(), out var final, out error))
            return false;

        if (!string.Equals(Path.GetExtension(final), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            error = "Link target is not an .exe.";
            return false;
        }

        if (!File.Exists(final))
        {
            error = "File not found.";
            return false;
        }

        fullPath = final;
        return true;
    }

    public static bool IsSafeAutostartExe(string? path, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.IndexOfAny(AutostartBlockedChars) >= 0) return false;
        if (!TryGetLocalFullPath(path.Trim(), out var full)) return false;
        if (!string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(full)) return false;
        fullPath = full;
        return true;
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

    /// <summary>
    /// "X:\…" only: rejects UNC (\\server\share), device / extended paths (\\?\, \\.\, \??\),
    /// relative and drive-relative paths (C:foo), alternate data streams and mapped network drives.
    /// </summary>
    private static bool TryGetLocalFullPath(string path, out string full)
    {
        full = "";
        if (path.Length < 4) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || path.StartsWith(@"\??\", StringComparison.Ordinal))
            return false;
        if (!char.IsAsciiLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
            return false;
        if (path.IndexOf(':', 2) >= 0) return false; // ADS (file.exe:stream)

        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        if (full.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(full))
            return false;

        try
        {
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network)
                return false;
        }
        catch
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Follows symlinks / junctions one hop at a time (reading link data is local), validating every
    /// intermediate and the final target, including reparse points in parent directories.
    /// </summary>
    private static bool TryResolveLocalTarget(string path, out string final, out string error)
    {
        final = "";
        error = "";
        string current = path;
        for (int hop = 0; hop <= MaxLinkHops; hop++)
        {
            if (!TryGetLocalFullPath(current, out var full))
            {
                error = "Path (or link target) is not on a local drive.";
                return false;
            }
            if (!AncestorsStayLocal(full))
            {
                error = "A folder in the path links to a non-local location.";
                return false;
            }

            string? link;
            try { link = new FileInfo(full).LinkTarget; }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            if (link == null)
            {
                final = full;
                return true;
            }

            current = Path.IsPathFullyQualified(link)
                ? link
                : Path.Combine(Path.GetDirectoryName(full) ?? "", link);
        }

        error = "Too many link hops.";
        return false;
    }

    private static bool AncestorsStayLocal(string full)
    {
        // Root first: a folder is only inspected after every folder above it was proven local.
        var ancestors = new List<string>();
        for (var d = Path.GetDirectoryName(full); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            ancestors.Add(d);
        ancestors.Reverse();

        foreach (var dir in ancestors)
        {
            try
            {
                var di = new DirectoryInfo(dir);
                // LinkTarget reads the reparse data of this folder only — it does not traverse it.
                var link = di.LinkTarget;
                if (link != null)
                {
                    var parent = Path.GetDirectoryName(dir) ?? "";
                    var target = Path.IsPathFullyQualified(link) ? link : Path.Combine(parent, link);
                    if (!TryGetLocalFullPath(target, out _))
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }
        return true;
    }
}
