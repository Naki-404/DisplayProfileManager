using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Visor.Setup;

/// <summary>
/// The old "Display Profile Manager" install (pre-rename). Removed conservatively: only file names it
/// is known to have shipped, only in a folder named like its install folder, and only shortcuts /
/// autostart entries / ARP key that point at its exe. Its settings folder %AppData%\DisplayProfileManager
/// is never touched (Visor migrates settings from there itself).
/// </summary>
internal sealed class LegacyInstall
{
    private const string ArpKey = "DisplayProfileManager";
    private const string ExeName = "DisplayProfileManager.exe";
    private const string ShortcutName = "Display Profile Manager.lnk";
    private const string TaskName = "DisplayProfileManager";

    private static readonly string[] AllowedFolderNames = { "DisplayProfileManager", "Display Profile Manager" };

    private static readonly string[] KnownFiles =
    {
        ExeName, "Uninstall.exe", "DisplayProfileManager-Uninstall.exe", "QRes.exe",
        "DisplayProfileManager.dll", "DisplayProfileManager.pdb",
        "DisplayProfileManager.deps.json", "DisplayProfileManager.runtimeconfig.json",
        "NvAPIWrapper.dll", "System.Management.dll", "System.CodeDom.dll",
        @"Assets\app.ico",
        @"Assets\Sounds\ATTRIBUTION.txt",
        @"Assets\Sounds\ui_armed.wav", @"Assets\Sounds\ui_click.wav", @"Assets\Sounds\ui_done.wav",
        @"Assets\Sounds\ui_empty.wav", @"Assets\Sounds\ui_error.wav", @"Assets\Sounds\ui_launch.wav",
        @"Assets\Sounds\ui_open.wav", @"Assets\Sounds\ui_preset.wav", @"Assets\Sounds\ui_save.wav",
        @"Assets\Sounds\ui_warn.wav", @"Assets\Sounds\ui_work_loop.wav",
        @"Assets\Packs\pubg.json", @"Assets\Packs\tarkov.json", @"Assets\Packs\valorant.json",
    };

    private static readonly string[] KnownDirs = { @"Assets\Sounds", @"Assets\Packs", "Assets" };

    private LegacyInstall(string? dir, bool hasArpKey)
    {
        Dir = dir;
        HasArpKey = hasArpKey;
    }

    /// <summary>Old install folder (contains DisplayProfileManager.exe), or null if only registry/shortcut leftovers exist.</summary>
    public string? Dir { get; }
    public bool HasArpKey { get; }
    public string? ExePath => Dir == null ? null : Path.Combine(Dir, ExeName);

    /// <summary>Files are removed only from an unprotected folder whose name matches the old install folder.</summary>
    public bool DirIsSafe => Dir != null && !PathGuard.IsProtected(Dir) &&
        AllowedFolderNames.Any(n => string.Equals(Path.GetFileName(Dir), n, StringComparison.OrdinalIgnoreCase));

    private static string StartMenuLnk => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", ShortcutName);
    private static string DesktopLnk => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);

    public static LegacyInstall? Detect()
    {
        bool hasKey = SystemOps.ArpKeyExists(ArpKey);
        string? dir = null;
        foreach (var candidate in new[]
                 {
                     SystemOps.ReadArpInstallLocation(ArpKey),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                  "Programs", "DisplayProfileManager"),
                 })
        {
            var n = PathGuard.Normalize(candidate);
            if (n != null && File.Exists(Path.Combine(n, ExeName))) { dir = n; break; }
        }

        bool hasShortcut = File.Exists(StartMenuLnk) || File.Exists(DesktopLnk);
        if (dir == null && !hasKey && !hasShortcut) return null;
        return new LegacyInstall(dir, hasKey);
    }

    /// <summary>
    /// If the old app is running with a game profile applied, let it revert its own display
    /// changes ("DisplayProfileManager.exe --emergency") before it is closed.
    /// </summary>
    public void RestoreDisplayIfActive(IEnumerable<RunningApp> running, Action<string>? log)
    {
        var exe = ExePath;
        if (exe == null || !File.Exists(exe) || !DisplayRestore.HasLegacyActiveSession()) return;
        if (!running.Any(r => r.DisplayName == AppProcesses.LegacyDisplayName)) return;
        DisplayRestore.RunEmergency(exe, log);
    }

    public void Remove(Action<string>? log, List<string> warnings)
    {
        log?.Invoke(L.StLegacy);

        bool filesGone = true;
        if (Dir != null)
        {
            if (DirIsSafe) filesGone = DeleteKnownFiles(Dir, warnings);
            else
            {
                warnings.Add(L.LegacySkipped(Dir));
                filesGone = false;
            }
        }

        // Shortcuts: exact target when the folder is known; otherwise only dangling DPM shortcuts.
        var exe = ExePath;
        bool IsLegacyTarget(string t) => exe != null
            ? PathGuard.SamePath(t, exe)
            : string.Equals(Path.GetFileName(t), ExeName, StringComparison.OrdinalIgnoreCase) && !File.Exists(t);
        SystemOps.DeleteShortcutIf(StartMenuLnk, IsLegacyTarget);
        SystemOps.DeleteShortcutIf(DesktopLnk, IsLegacyTarget);

        if (exe != null)
        {
            SystemOps.DeleteTaskIfReferences(TaskName, exe);
            SystemOps.DeleteRunValuesFor(exe);
        }

        // Keep the old ARP entry only while the old app is still on disk.
        if (HasArpKey && filesGone)
        {
            try { SystemOps.DeleteArpKey(ArpKey); } catch { }
        }
    }

    private static bool DeleteKnownFiles(string dir, List<string> warnings)
    {
        foreach (var rel in KnownFiles)
        {
            var path = PathGuard.ResolveInside(dir, rel);
            if (path == null || !File.Exists(path)) continue;
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch { }
        }

        foreach (var rel in KnownDirs) TryDeleteEmptyDir(Path.Combine(dir, rel));
        TryDeleteEmptyDir(dir);

        if (Directory.Exists(dir)) warnings.Add(L.LegacyLeftovers(dir));
        return !File.Exists(Path.Combine(dir, ExeName));
    }

    private static void TryDeleteEmptyDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir, false);
        }
        catch { }
    }
}
