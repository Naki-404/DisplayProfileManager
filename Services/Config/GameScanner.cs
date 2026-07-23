using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DisplayProfileManager.Services;

/// <summary>Finds known games that are running or installed on disk.</summary>
public static class GameScanner
{
    public sealed record FoundGame(string Process, string Name, string? ExePath, bool IsRunning);

    public static List<FoundGame> ScanAll(Action<string>? progress = null)
    {
        var byProcess = new Dictionary<string, FoundGame>(StringComparer.OrdinalIgnoreCase);

        void Add(string process, string name, string? path, bool running)
        {
            process = GameCatalog.Normalize(process);
            if (string.IsNullOrWhiteSpace(process)) return;

            if (byProcess.TryGetValue(process, out var existing))
            {
                byProcess[process] = existing with
                {
                    ExePath = existing.ExePath ?? path,
                    IsRunning = existing.IsRunning || running,
                    Name = string.IsNullOrWhiteSpace(existing.Name) ? name : existing.Name
                };
                return;
            }

            byProcess[process] = new FoundGame(process, name, path, running);
        }

        progress?.Invoke("Checking running processes…");
        foreach (var (process, name) in GameCatalog.FindRunningKnown())
            Add(process, name, null, true);

        progress?.Invoke("Looking through Steam libraries…");
        foreach (var hit in ScanSteam())
            Add(hit.Process, hit.Name, hit.ExePath, false);

        progress?.Invoke("Checking Epic Games…");
        foreach (var hit in ScanEpic())
            Add(hit.Process, hit.Name, hit.ExePath, false);

        progress?.Invoke("Checking Riot / common folders…");
        foreach (var hit in ScanKnownPaths())
            Add(hit.Process, hit.Name, hit.ExePath, false);

        progress?.Invoke("Reading installed programs…");
        foreach (var hit in ScanUninstallRegistry())
            Add(hit.Process, hit.Name, hit.ExePath, false);

        progress?.Invoke("Searching game folders on disks…");
        foreach (var hit in ScanDriveGameFolders())
            Add(hit.Process, hit.Name, hit.ExePath, false);

        progress?.Invoke("Finishing…");
        return byProcess.Values
            .OrderByDescending(g => g.IsRunning)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<FoundGame> ScanSteam()
    {
        var roots = new List<string>();
        var steam = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "libraryfolders.vdf");

        if (File.Exists(steam))
        {
            try
            {
                var text = File.ReadAllText(steam);
                foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                {
                    var p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (Directory.Exists(p)) roots.Add(p);
                }
            }
            catch { /* ignore */ }
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            TryAddDir(roots, Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
            TryAddDir(roots, Path.Combine(drive.RootDirectory.FullName, "Steam"));
            TryAddDir(roots, Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)", "Steam"));
            TryAddDir(roots, Path.Combine(drive.RootDirectory.FullName, "Program Files", "Steam"));
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var common = Path.Combine(root, "steamapps", "common");
            if (!Directory.Exists(common)) continue;
            foreach (var hit in FindKnownExesUnder(common, maxDepth: 4))
                yield return hit;
        }
    }

    private static IEnumerable<FoundGame> ScanEpic()
    {
        var manifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestDir)) yield break;

        foreach (var file in Directory.EnumerateFiles(manifestDir, "*.item"))
        {
            FoundGame? hit = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var install = root.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
                var launch = root.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() : null;
                var display = root.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null;
                if (string.IsNullOrWhiteSpace(install) || string.IsNullOrWhiteSpace(launch)) continue;

                var exePath = Path.Combine(install, launch.Replace('/', '\\'));
                var process = Path.GetFileName(exePath);
                if (!GameCatalog.IsKnown(process) && !LooksLikeGameExe(process)) continue;
                if (!File.Exists(exePath)) continue;

                var name = GameCatalog.GetFriendlyName(process)
                           ?? display
                           ?? Path.GetFileNameWithoutExtension(process);
                hit = new FoundGame(GameCatalog.Normalize(process), name!, exePath, false);
            }
            catch { /* ignore broken manifest */ }

            if (hit != null) yield return hit;
        }
    }

    private static IEnumerable<FoundGame> ScanKnownPaths()
    {
        var candidates = new List<(string Path, string Process, string Name)>
        {
            (@"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe",
                "VALORANT-Win64-Shipping.exe", "Valorant"),
            (@"C:\Riot Games\League of Legends\Game\League of Legends.exe",
                "League of Legends.exe", "League of Legends"),
        };

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var root = drive.RootDirectory.FullName;
            candidates.Add((Path.Combine(root, @"Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe"),
                "VALORANT-Win64-Shipping.exe", "Valorant"));
            candidates.Add((Path.Combine(root, @"Battlestate Games\Escape from Tarkov\EscapeFromTarkov.exe"),
                "EscapeFromTarkov.exe", "Escape from Tarkov"));
            candidates.Add((Path.Combine(root, @"Games\Escape from Tarkov\EscapeFromTarkov.exe"),
                "EscapeFromTarkov.exe", "Escape from Tarkov"));
        }

        foreach (var c in candidates)
        {
            if (File.Exists(c.Path))
                yield return new FoundGame(c.Process, c.Name, c.Path, false);
        }
    }

    private static IEnumerable<FoundGame> ScanUninstallRegistry()
    {
        string[] keys =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var hivePath in keys)
        {
            using var key = Registry.LocalMachine.OpenSubKey(hivePath);
            if (key == null) continue;
            foreach (var subName in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(subName);
                if (sub == null) continue;
                var display = sub.GetValue("DisplayName") as string;
                var location = sub.GetValue("InstallLocation") as string
                               ?? sub.GetValue("DisplayIcon") as string;
                if (string.IsNullOrWhiteSpace(display) || string.IsNullOrWhiteSpace(location)) continue;

                location = location.Trim().Trim('"');
                if (location.Contains(',')) location = location.Split(',')[0];
                if (File.Exists(location) && location.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var process = Path.GetFileName(location);
                    if (!GameCatalog.IsKnown(process)) continue;
                    yield return new FoundGame(process, GameCatalog.GetFriendlyName(process) ?? display, location, false);
                    continue;
                }

                if (!Directory.Exists(location)) continue;
                foreach (var hit in FindKnownExesUnder(location, maxDepth: 3))
                    yield return hit;
            }
        }
    }

    private static IEnumerable<FoundGame> ScanDriveGameFolders()
    {
        var folderNames = new[]
        {
            "Games", "Game", "SteamLibrary", "Epic Games", "XboxGames",
            "Program Files", "Program Files (x86)"
        };

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            foreach (var name in folderNames)
            {
                var dir = Path.Combine(drive.RootDirectory.FullName, name);
                if (!Directory.Exists(dir)) continue;
                foreach (var hit in FindKnownExesUnder(dir, maxDepth: name.StartsWith("Program") ? 3 : 4))
                    yield return hit;
            }
        }
    }

    private static IEnumerable<FoundGame> FindKnownExesUnder(string root, int maxDepth)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            if (!seen.Add(dir)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.exe"); }
            catch { continue; }

            foreach (var file in files)
            {
                var process = Path.GetFileName(file);
                if (!GameCatalog.IsKnown(process)) continue;
                var name = GameCatalog.GetFriendlyName(process) ?? Path.GetFileNameWithoutExtension(process);
                yield return new FoundGame(process, name!, file, false);
            }

            if (depth >= maxDepth) continue;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); }
            catch { continue; }

            foreach (var sub in subs)
            {
                var leaf = Path.GetFileName(sub);
                if (leaf is "Windows" or "WinSxS" or "System32" or "SysWOW64" or "$Recycle.Bin"
                    or "node_modules" or ".git")
                    continue;
                queue.Enqueue((sub, depth + 1));
            }
        }
    }

    private static void TryAddDir(List<string> list, string path)
    {
        if (Directory.Exists(path)) list.Add(path);
    }

    private static bool LooksLikeGameExe(string process) =>
        GameCatalog.IsKnown(process);
}
