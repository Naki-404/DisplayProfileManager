using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Visor.Setup;

/// <summary>Constants shared by Setup and Uninstall.</summary>
internal static class Product
{
    public const string Name = "Visor";
    public const string Publisher = "Nakidev";
    public const string ExeName = "Visor.exe";
    public const string UninstallerName = "Uninstall.exe";
    public const string ManifestName = ".visor-install.json";
    /// <summary>Apps and Features key (publisher-qualified so it cannot collide with another "Visor").</summary>
    public const string ArpKeyName = "Nakidev.Visor";
    /// <summary>Key name used by early Visor builds; removed only when it points at our folder.</summary>
    public const string OldArpKeyName = "Visor";
    public const string ShortcutName = "Visor.lnk";
    /// <summary>Optional Intel PresentMon payload (FPS monitor); bundled only by build-release.ps1 -FetchPresentMon.</summary>
    public const string PresentMonExe = @"tools\presentmon\PresentMon.exe";
    public const string PresentMonReadme = @"tools\presentmon\README.md";
    public const string Url = "https://github.com/Naki-404/DisplayProfileManager";
    /// <summary>Named event the app may listen on to exit cleanly (restoring the display first).</summary>
    public const string ExitEventName = @"Local\Visor.ExitRequest";
    public const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static readonly string Version = ReadVersion();

    public static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);

    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    public static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", ShortcutName);

    public static string DesktopShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);

    public static string SelfPath => Process.GetCurrentProcess().MainModule!.FileName;

    private static string ReadVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
            info = asm.GetName().Version?.ToString(3) ?? "0.0.0";
        int plus = info!.IndexOf('+');
        return plus > 0 ? info.Substring(0, plus) : info;
    }
}

/// <summary>Thrown for expected, user-presentable failures.</summary>
internal sealed class InstallerException : Exception
{
    public InstallerException(string message) : base(message) { }
}

/// <summary>Guards against installing into / deleting from folders that are not ours.</summary>
internal static class PathGuard
{
    /// <summary>Full, normalized path without trailing separator; null if not a local absolute path.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = Environment.ExpandEnvironmentVariables(path!.Trim().Trim('"'));
        // Local drive paths only ("C:\..."); no UNC, device or relative paths.
        if (p.Length < 3 || !char.IsLetter(p[0]) || p[1] != ':' || (p[2] != '\\' && p[2] != '/'))
            return null;
        try
        {
            var full = Path.GetFullPath(p);
            return full.Length > 3 ? full.TrimEnd('\\', '/') : full;
        }
        catch { return null; }
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="path"/> equals or is inside <paramref name="dir"/>.</summary>
    public static bool IsInsideOrSame(string path, string dir)
    {
        var d = dir.TrimEnd('\\') + "\\";
        return SamePath(path, dir) || path.StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for folders Visor must never install into or delete from: the app's own data folders
    /// (and anything inside them), the Windows folder tree, drive roots and well-known personal or
    /// system roots (user profile, Desktop, Documents, Downloads, AppData, Program Files, ...).
    /// </summary>
    public static bool IsProtected(string fullDir)
    {
        var dir = fullDir.TrimEnd('\\');
        if (dir.Length <= 2 || SamePath(dir + "\\", Path.GetPathRoot(fullDir) ?? "")) return true;

        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var appData = F(Environment.SpecialFolder.ApplicationData);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var profile = F(Environment.SpecialFolder.UserProfile);

        var subtrees = new[]
        {
            Path.Combine(appData, "Visor"),
            Path.Combine(appData, "DisplayProfileManager"),
            F(Environment.SpecialFolder.Windows),
        };
        foreach (var s in subtrees)
            if (!string.IsNullOrEmpty(s) && IsInsideOrSame(dir, s)) return true;

        var exact = new List<string?>
        {
            profile,
            string.IsNullOrEmpty(profile) ? null : Path.GetDirectoryName(profile), // C:\Users
            string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "AppData"),
            string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Downloads"),
            Native.GetDownloadsFolder(),
            F(Environment.SpecialFolder.DesktopDirectory),
            F(Environment.SpecialFolder.CommonDesktopDirectory),
            F(Environment.SpecialFolder.MyDocuments),
            F(Environment.SpecialFolder.MyPictures),
            F(Environment.SpecialFolder.MyMusic),
            F(Environment.SpecialFolder.MyVideos),
            appData,
            local,
            Path.Combine(local, "Programs"),
            F(Environment.SpecialFolder.ProgramFiles),
            F(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432"),
            F(Environment.SpecialFolder.CommonApplicationData),
            F(Environment.SpecialFolder.StartMenu),
            F(Environment.SpecialFolder.Programs),
            Path.GetTempPath(),
        };
        foreach (var e in exact)
            if (!string.IsNullOrEmpty(e) && SamePath(dir, e!)) return true;

        return false;
    }

    /// <summary>
    /// Validates a manifest/payload relative path: no rooting, no "..", no ADS, no empty segments.
    /// Returns it with backslashes, or null if unsafe.
    /// </summary>
    public static string? SafeRelative(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return null;
        var r = rel!.Replace('/', '\\');
        if (r.StartsWith("\\") || r.IndexOf(':') >= 0 || Path.IsPathRooted(r)) return null;
        if (r.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        var parts = r.Split('\\');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part == "." || part == "..") return null;
            if (part.Trim().Length == 0 || part.EndsWith(".") || part.EndsWith(" ")) return null;
        }
        return r;
    }

    /// <summary>Combines root + relative and verifies the result stays inside root.</summary>
    public static string? ResolveInside(string root, string rel)
    {
        var safe = SafeRelative(rel);
        if (safe == null) return null;
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, safe)); }
        catch { return null; }
        var r = root.TrimEnd('\\') + "\\";
        return full.StartsWith(r, StringComparison.OrdinalIgnoreCase) && full.Length > r.Length ? full : null;
    }

    /// <summary>
    /// Files a Visor install folder may legitimately contain (used for folders without a manifest,
    /// e.g. one where only a leftover Uninstall.exe remains).
    /// </summary>
    public static bool IsKnownAppFile(string rel)
    {
        var r = rel.Replace('/', '\\');
        return r.Equals(Product.ExeName, StringComparison.OrdinalIgnoreCase)
            || r.Equals(Product.UninstallerName, StringComparison.OrdinalIgnoreCase)
            || r.Equals(Product.ManifestName, StringComparison.OrdinalIgnoreCase)
            || r.Equals(Product.ManifestName + ".tmp", StringComparison.OrdinalIgnoreCase)
            || (r.StartsWith(@"Assets\Sounds\", StringComparison.OrdinalIgnoreCase) && r.IndexOf('\\', 14) < 0)
            || (r.StartsWith(@"Assets\Packs\", StringComparison.OrdinalIgnoreCase) && r.IndexOf('\\', 13) < 0)
            || r.Equals(Product.PresentMonExe, StringComparison.OrdinalIgnoreCase)
            || r.Equals(Product.PresentMonReadme, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>.visor-install.json: what an install put on disk (relative paths) and which version.</summary>
[DataContract]
internal sealed class InstallManifest
{
    [DataMember(Name = "app", Order = 0)] public string App { get; set; } = Product.Name;
    [DataMember(Name = "version", Order = 1)] public string Version { get; set; } = Product.Version;
    [DataMember(Name = "installedUtc", Order = 2)] public string InstalledUtc { get; set; } = "";
    [DataMember(Name = "files", Order = 3)] public List<string> Files { get; set; } = new();

    public static string PathIn(string dir) => Path.Combine(dir, Product.ManifestName);

    /// <summary>Loads and validates the manifest; null if missing, unreadable or not a Visor manifest.</summary>
    public static InstallManifest? Load(string dir)
    {
        var path = PathIn(dir);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length > 4 * 1024 * 1024) return null;
            var m = (InstallManifest?)new DataContractJsonSerializer(typeof(InstallManifest)).ReadObject(fs);
            if (m == null || !string.Equals(m.App, Product.Name, StringComparison.OrdinalIgnoreCase)) return null;
            m.Files = (m.Files ?? new List<string>())
                .Select(PathGuard.SafeRelative)
                .Where(r => r != null && !r.Equals(Product.ManifestName, StringComparison.OrdinalIgnoreCase))
                .Select(r => r!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return m;
        }
        catch { return null; }
    }

    public void Save(string dir)
    {
        InstalledUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var path = PathIn(dir);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, new UTF8Encoding(false), false, true))
        {
            new DataContractJsonSerializer(typeof(InstallManifest)).WriteObject(w, this);
            w.Flush();
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }
}
