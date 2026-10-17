using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Visor.Services;

/// <summary>
/// Steam games usually must be started through the Steam client (<c>steam://rungameid/…</c>),
/// not by running the game .exe directly (DRM / overlay / "launch from Steam").
/// </summary>
public static class SteamLaunch
{
    private static readonly Regex AppIdLine = new(
        @"\""appid\""\s+\""(\d+)\""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex InstallDirLine = new(
        @"\""installdir\""\s+\""([^\""]+)\""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Normalized exe path → AppID (null = resolved, not Steam / no manifest).</summary>
    private static readonly ConcurrentDictionary<string, int?> AppIdCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a Steam AppID for an exe under <c>…\steamapps\common\&lt;installdir&gt;\…</c>
    /// by reading matching <c>appmanifest_*.acf</c> files.
    /// </summary>
    public static bool TryResolveAppId(string? exePath, out int appId)
    {
        appId = 0;
        if (string.IsNullOrWhiteSpace(exePath)) return false;

        string key;
        try { key = Path.GetFullPath(exePath.Trim()); }
        catch { return false; }

        if (AppIdCache.TryGetValue(key, out int? cached))
        {
            if (cached is > 0) { appId = cached.Value; return true; }
            return false;
        }

        if (!TryGetSteamCommonParts(key, out var steamApps, out var installDir))
        {
            AppIdCache[key] = null;
            return false;
        }

        try
        {
            foreach (var manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                string text;
                try { text = File.ReadAllText(manifest); }
                catch { continue; }

                var dirMatch = InstallDirLine.Match(text);
                if (!dirMatch.Success) continue;
                string dir = dirMatch.Groups[1].Value.Replace("\\\\", "\\");
                if (!string.Equals(dir, installDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                var idMatch = AppIdLine.Match(text);
                if (!idMatch.Success) continue;
                if (!int.TryParse(idMatch.Groups[1].Value, out int id) || id <= 0)
                    continue;

                AppIdCache[key] = id;
                appId = id;
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Steam AppID resolve");
        }

        AppIdCache[key] = null;
        return false;
    }

    /// <summary>True when <paramref name="exePath"/> lives under a Steam library <c>common</c> folder.</summary>
    public static bool IsUnderSteamCommon(string? exePath) =>
        TryGetSteamCommonParts(exePath, out _, out _);

    /// <summary>
    /// Starts the game via Steam protocol (preferred) or <c>steam.exe -applaunch</c>.
    /// </summary>
    public static bool TryStartApp(int appId, out string? error)
    {
        error = null;
        if (appId <= 0)
        {
            error = "invalid appid";
            return false;
        }

        string uri = $"steam://rungameid/{appId}";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true
            })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "steam:// launch");
        }

        string? steamExe = FindSteamExe();
        if (string.IsNullOrEmpty(steamExe))
        {
            error = "steam_missing";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = steamExe,
                Arguments = $"-applaunch {appId}",
                UseShellExecute = true
            })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLog.Warn(ex, "steam.exe -applaunch");
            return false;
        }
    }

    /// <summary>Launch helper: Steam protocol when possible, otherwise direct exe.</summary>
    public static bool TryStartGame(string? exePath, int? knownAppId, out bool usedSteam, out string? error)
    {
        usedSteam = false;
        error = null;

        int appId = knownAppId is > 0 ? knownAppId.Value : 0;
        if (appId <= 0)
            TryResolveAppId(exePath, out appId);

        if (appId > 0)
        {
            usedSteam = true;
            return TryStartApp(appId, out error);
        }

        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            error = "no_exe";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                UseShellExecute = true
            })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string? FindSteamExe()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")
                            ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                            ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            var path = key?.GetValue("SteamExe") as string
                       ?? key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    return path;
                var exe = Path.Combine(path.Trim().Trim('"'), "steam.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { /* ignore */ }

        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steam.exe")
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>Pure path parse for tests — no disk I/O beyond existence checks by callers.</summary>
    public static bool TryGetSteamCommonParts(string? exePath, out string steamAppsDir, out string installDirName)
    {
        steamAppsDir = "";
        installDirName = "";
        if (string.IsNullOrWhiteSpace(exePath)) return false;

        string full;
        try { full = Path.GetFullPath(exePath.Trim()); }
        catch { return false; }

        var parts = full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 2 < parts.Length; i++)
        {
            if (!parts[i].Equals("steamapps", StringComparison.OrdinalIgnoreCase)) continue;
            if (!parts[i + 1].Equals("common", StringComparison.OrdinalIgnoreCase)) continue;
            installDirName = parts[i + 2];
            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return false;
            var segs = new string[i + 2];
            segs[0] = root;
            for (int s = 0; s <= i; s++)
                segs[s + 1] = parts[s];
            steamAppsDir = Path.Combine(segs);
            return !string.IsNullOrEmpty(installDirName);
        }
        return false;
    }
}
