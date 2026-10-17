using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Visor.Services.Elevation;

/// <summary>
/// Shared helpers for the per-user exchange folder <c>%LocalAppData%\Visor\elevation</c> used by
/// elevated Visor helpers (same rules as <see cref="ElevationBroker"/>: folder restricted to the
/// current user, Administrators and SYSTEM; never a junction; helper exe must be the real Visor.exe).
/// Used by the frame-capture launcher; additive — the broker keeps its own copy for now.
/// </summary>
public static class ElevationExchangeDirectory
{
    public static string Default => ElevationBroker.DefaultDirectory;

    /// <summary>Creates the folder and tightens its ACL. False when it cannot be created or is a reparse point.</summary>
    public static bool Prepare(string directory)
    {
        try
        {
            var di = Directory.CreateDirectory(directory);
            if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            try
            {
                var sec = new DirectorySecurity();
                sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                var user = WindowsIdentity.GetCurrent().User;
                if (user != null)
                    sec.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                di.SetAccessControl(sec);
            }
            catch (Exception ex)
            {
                // %LocalAppData% is already per-user; a failed tightening is logged, not fatal.
                AppLog.Warn(ex, "Elevation exchange: folder ACL");
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Elevation exchange: folder");
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="exePath"/> can be started as an elevated Visor helper: Windows, a real
    /// exe (not the dotnet host or a test runner), and the file exists.
    /// </summary>
    public static bool IsHelperExeSupported(string? exePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(exePath)) return false;
        string name = Path.GetFileNameWithoutExtension(exePath);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase))
            return false;
        try { return File.Exists(exePath); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Validates a path handed to an elevated helper: fully qualified, local (no UNC), no "..", named
    /// <c>&lt;32-hex id&gt;&lt;suffix&gt;</c>, directly inside a "...\Visor\elevation" folder, and neither that
    /// folder nor its parent is a junction / symlink.
    /// </summary>
    public static bool TryCheckExchangeFile(string? path, string suffix, out string id)
    {
        id = "";
        if (string.IsNullOrEmpty(path) || path.Length > 512) return false;
        if (!Path.IsPathFullyQualified(path)) return false;
        if (path.Contains("..", StringComparison.Ordinal) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        if (path.Contains('"')) return false;

        string name = Path.GetFileName(path);
        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        string stem = name[..^suffix.Length];
        if (!ElevationJson.IsValidRequestId(stem)) return false;

        string? dir = Path.GetDirectoryName(path);
        if (dir == null || !string.Equals(Path.GetFileName(dir), ElevationBroker.DirectoryName, StringComparison.OrdinalIgnoreCase)) return false;
        string? parent = Path.GetDirectoryName(dir);
        if (parent == null || !string.Equals(Path.GetFileName(parent), ElevationBroker.AppFolderName, StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var dirInfo = new DirectoryInfo(dir);
            if (!dirInfo.Exists || dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            if (new DirectoryInfo(parent).Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        }
        catch (Exception) { return false; }

        id = stem.ToLowerInvariant();
        return true;
    }
}
