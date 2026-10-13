using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;

namespace Visor.Setup;

internal enum TargetKind { Fresh, Upgrade, Foreign, Protected, Invalid }

internal sealed class TargetState
{
    public TargetState(TargetKind kind, string? dir, List<string>? files = null, string? version = null)
    {
        Kind = kind;
        Dir = dir;
        ExistingFiles = files ?? new List<string>();
        ExistingVersion = version;
    }

    public TargetKind Kind { get; }
    public string? Dir { get; }
    /// <summary>Relative paths owned by the existing install (from its manifest or a recognized layout).</summary>
    public List<string> ExistingFiles { get; }
    public string? ExistingVersion { get; }
}

internal sealed class InstallOptions
{
    public string TargetDir { get; set; } = Product.DefaultInstallDir;
    public bool StartMenuShortcut { get; set; } = true;
    public bool DesktopShortcut { get; set; }
    public bool RemoveLegacy { get; set; } = true;
}

internal sealed class InstallResult
{
    public string Dir { get; set; } = "";
    public bool Upgraded { get; set; }
    public List<string> Warnings { get; } = new();
    public string ExePath => Path.Combine(Dir, Product.ExeName);
}

internal sealed class UninstallResult
{
    public bool NotFound { get; set; }
    public string? Dir { get; set; }
    /// <summary>The running uninstaller lives in the install folder and must be removed after exit.</summary>
    public string? SelfToDelete { get; set; }
}

internal static class InstallerCore
{
    private const string PayloadResource = "payload.zip";

    // ------------------------------------------------------------------ locate / inspect

    /// <summary>Selected folder → install folder. A "Visor" subfolder is appended unless already named so.</summary>
    public static string? ResolveInstallDir(string? selected)
    {
        var path = PathGuard.Normalize(selected);
        if (path == null) return null;
        return string.Equals(Path.GetFileName(path), Product.Name, StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.Combine(path, Product.Name);
    }

    /// <summary>
    /// Current install folder — only folders with a valid Visor manifest qualify (no blind
    /// "folder of the running exe" fallback). Order: the folder of a running Uninstall.exe that
    /// has a manifest (so each copy removes its own install), Apps &amp; Features, the default folder.
    /// </summary>
    public static string? FindInstallLocation()
    {
        string? selfDir = null;
        try
        {
            if (Path.GetFileName(Product.SelfPath).Equals(Product.UninstallerName, StringComparison.OrdinalIgnoreCase))
                selfDir = Path.GetDirectoryName(Product.SelfPath);
        }
        catch { }

        foreach (var candidate in new[]
                 {
                     selfDir,
                     SystemOps.ReadArpInstallLocation(Product.ArpKeyName),
                     SystemOps.ReadArpInstallLocation(Product.OldArpKeyName),
                     Product.DefaultInstallDir,
                 })
        {
            var dir = PathGuard.Normalize(candidate);
            if (dir != null && !PathGuard.IsProtected(dir) && InstallManifest.Load(dir) != null)
                return dir;
        }
        return null;
    }

    public static bool HasPayload() =>
        Assembly.GetExecutingAssembly().GetManifestResourceInfo(PayloadResource) != null;

    public static TargetState Inspect(string? dir)
    {
        if (dir == null) return new TargetState(TargetKind.Invalid, null);
        if (PathGuard.IsProtected(dir)) return new TargetState(TargetKind.Protected, dir);
        if (File.Exists(dir)) return new TargetState(TargetKind.Foreign, dir);
        if (!Directory.Exists(dir)) return new TargetState(TargetKind.Fresh, dir);

        var manifest = InstallManifest.Load(dir);
        if (manifest != null) return new TargetState(TargetKind.Upgrade, dir, manifest.Files, manifest.Version);

        // No manifest: accept only an empty folder or one holding nothing but known Visor files.
        var files = new List<string>();
        try
        {
            var root = dir.TrimEnd('\\') + "\\";
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = f.Substring(root.Length);
                if (!PathGuard.IsKnownAppFile(rel) || files.Count > 1000)
                    return new TargetState(TargetKind.Foreign, dir);
                if (!rel.Equals(Product.ManifestName, StringComparison.OrdinalIgnoreCase)) files.Add(rel);
            }
        }
        catch { return new TargetState(TargetKind.Foreign, dir); }

        // Only known Visor files (e.g. a leftover Uninstall.exe whose self-delete did not run): owned.
        return files.Count == 0
            ? new TargetState(TargetKind.Fresh, dir)
            : new TargetState(TargetKind.Upgrade, dir, files);
    }

    // ------------------------------------------------------------------ install

    /// <summary>
    /// Installs or upgrades. Running apps must already be closed (see <see cref="AppProcesses"/>).
    /// Payload is extracted to a staging folder next to the target, then swapped in file by file;
    /// on any failure the previous files are moved back.
    /// </summary>
    public static InstallResult Install(InstallOptions o, LegacyInstall? legacy, Action<string>? log)
    {
        var state = Inspect(ResolveInstallDir(o.TargetDir));
        switch (state.Kind)
        {
            case TargetKind.Invalid: throw new InstallerException(L.InvalidPath);
            case TargetKind.Protected: throw new InstallerException(L.ProtectedFolder(state.Dir!));
            case TargetKind.Foreign: throw new InstallerException(L.FolderNotEmpty(state.Dir!));
        }
        if (!HasPayload()) throw new InstallerException(L.PayloadMissing);

        var dir = state.Dir!;
        var result = new InstallResult { Dir = dir, Upgraded = state.Kind == TargetKind.Upgrade };

        // Running apps lock files (and the legacy app shares Visor's hotkeys). The UI asks the user
        // before calling this; silent mode closes them directly.
        // CloseAll throws if anything survives (e.g. an elevated instance), so no file is touched then.
        bool removeLegacy = legacy != null && o.RemoveLegacy;
        var running = AppProcesses.Find(dir, removeLegacy, legacy?.Dir);
        if (removeLegacy) legacy!.RestoreDisplayIfActive(running, log);
        if (AppProcesses.CloseAll(running, log))
            DisplayRestore.AfterAppClosed(null, appWasKilled: true, log);

        var parent = Path.GetDirectoryName(dir)!;
        Directory.CreateDirectory(parent);
        var id = Guid.NewGuid().ToString("N").Substring(0, 8);
        var staging = Path.Combine(parent, ".visor-staging-" + id);
        var backup = Path.Combine(parent, ".visor-backup-" + id);

        try
        {
            log?.Invoke(L.StExtracting);
            var newFiles = ExtractPayload(staging);
            log?.Invoke(L.StInstalling);
            SwapIn(dir, staging, backup, state.ExistingFiles, newFiles);
        }
        finally
        {
            TryDeleteTree(staging);
        }

        if (legacy != null && o.RemoveLegacy)
        {
            try { legacy.Remove(log, result.Warnings); }
            catch (Exception ex) { result.Warnings.Add(ex.Message); }
        }

        log?.Invoke(L.StRegister);
        try { SystemOps.WriteArpKey(dir); }
        catch (Exception ex) { result.Warnings.Add(L.RegisterFailed(ex.Message)); }
        SystemOps.DeleteArpKeyIfFor(Product.OldArpKeyName, dir); // early-build key name, ours only

        log?.Invoke(L.StShortcuts);
        var exe = result.ExePath;
        if (o.StartMenuShortcut) TryShortcut(Product.StartMenuShortcut, exe, dir, "Start Menu", result.Warnings);
        if (o.DesktopShortcut) TryShortcut(Product.DesktopShortcut, exe, dir, "Desktop", result.Warnings);

        log?.Invoke(L.StDone);
        return result;
    }

    /// <summary>Creates the shortcut if missing; overwrites only one that already points into our install folder.</summary>
    private static void TryShortcut(string lnk, string exe, string dir, string which, List<string> warnings)
    {
        try
        {
            if (File.Exists(lnk))
            {
                var target = Native.GetShortcutTarget(lnk);
                if (target == null || !PathGuard.IsInsideOrSame(target, dir))
                {
                    warnings.Add(L.ShortcutExists(which, lnk));
                    return;
                }
            }
            Native.CreateShortcut(lnk, exe, dir, Product.Name + " by " + Product.Publisher);
        }
        catch (Exception ex) { warnings.Add(L.ShortcutFailed(which, ex.Message)); }
    }

    /// <summary>Extracts the embedded payload into <paramref name="staging"/>; returns relative file paths.</summary>
    private static List<string> ExtractPayload(string staging)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var src = asm.GetManifestResourceStream(PayloadResource)
            ?? throw new InstallerException(L.PayloadMissing);
        using var zip = new ZipArchive(src, ZipArchiveMode.Read);

        Directory.CreateDirectory(staging);
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
            var rel = PathGuard.SafeRelative(entry.FullName);
            if (rel == null || !IsAllowedPayloadPath(rel) || !seen.Add(rel)) continue;
            var dest = PathGuard.ResolveInside(staging, rel);
            if (dest == null) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using (var es = entry.Open())
            using (var fs = File.Create(dest))
                es.CopyTo(fs);
            files.Add(rel);
        }

        if (!files.Any(f => f.Equals(Product.ExeName, StringComparison.OrdinalIgnoreCase)))
            throw new InstallerException(L.PayloadMissing);

        // Release builds ship a payload-less Uninstall.exe inside payload.zip; dev builds fall back to a copy of Setup.
        if (!files.Any(f => f.Equals(Product.UninstallerName, StringComparison.OrdinalIgnoreCase)))
        {
            File.Copy(Product.SelfPath, Path.Combine(staging, Product.UninstallerName), true);
            files.Add(Product.UninstallerName);
        }
        return files;
    }

    private static bool IsAllowedPayloadPath(string rel) =>
        rel.Equals(Product.ExeName, StringComparison.OrdinalIgnoreCase) ||
        rel.Equals(Product.UninstallerName, StringComparison.OrdinalIgnoreCase) ||
        rel.StartsWith(@"Assets\", StringComparison.OrdinalIgnoreCase) ||
        rel.Equals(Product.PresentMonExe, StringComparison.OrdinalIgnoreCase) ||
        rel.Equals(Product.PresentMonReadme, StringComparison.OrdinalIgnoreCase);

    /// <summary>Moves old owned files to a backup folder, moves staged files in, writes the manifest; rolls back on failure.</summary>
    private static void SwapIn(string dir, string staging, string backup, List<string> oldFiles, List<string> newFiles)
    {
        var moved = new List<string>();
        var placed = new List<string>();
        bool dirExisted = Directory.Exists(dir);
        bool manifestWritten = false;
        var manifestPath = InstallManifest.PathIn(dir);
        var oldOwned = oldFiles.Concat(new[] { Product.ManifestName })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var rel in oldOwned)
            {
                var src = PathGuard.ResolveInside(dir, rel);
                if (src == null || !File.Exists(src)) continue;
                var dst = Path.Combine(backup, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.SetAttributes(src, FileAttributes.Normal);
                File.Move(src, dst);
                moved.Add(rel);
            }

            foreach (var rel in newFiles)
            {
                var dst = PathGuard.ResolveInside(dir, rel)
                    ?? throw new InvalidOperationException("Unsafe payload path: " + rel);
                if (File.Exists(dst))
                    throw new InvalidOperationException("File already exists and is not part of the previous install: " + dst);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Move(Path.Combine(staging, rel), dst);
                placed.Add(rel);
            }

            manifestWritten = true;
            new InstallManifest { Files = newFiles }.Save(dir);
        }
        catch (Exception ex)
        {
            try
            {
                if (manifestWritten)
                {
                    if (File.Exists(manifestPath)) File.Delete(manifestPath);
                    if (File.Exists(manifestPath + ".tmp")) File.Delete(manifestPath + ".tmp");
                }
                foreach (var rel in placed)
                {
                    var p = Path.Combine(dir, rel);
                    if (File.Exists(p)) File.Delete(p);
                }
                foreach (var rel in moved)
                {
                    var target = Path.Combine(dir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(Path.Combine(backup, rel), target);
                }
                PruneEmptyDirs(dir, placed);
            }
            catch (Exception rex)
            {
                throw new InstallerException(L.RollbackFailed(ex.Message + " / " + rex.Message, backup));
            }
            TryDeleteTree(backup);
            if (!dirExisted) TryDeleteEmptyDir(dir);
            throw new InstallerException(L.RolledBack(ex.Message));
        }

        TryDeleteTree(backup);
        PruneEmptyDirs(dir, oldFiles.Except(newFiles, StringComparer.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ uninstall

    /// <summary>
    /// Removes the install: closes Visor, lets it restore the display, removes autostart / shortcuts,
    /// deletes only manifest-listed files, then removes the Apps &amp; Features entry.
    /// </summary>
    public static UninstallResult Uninstall(Action<string>? log)
    {
        var dir = FindInstallLocation();
        if (dir == null) return new UninstallResult { NotFound = true };

        var manifest = InstallManifest.Load(dir)!;
        var exe = Path.Combine(dir, Product.ExeName);
        var result = new UninstallResult { Dir = dir };

        // CloseAll throws if Visor survives (e.g. elevated) — nothing is deleted then.
        bool killed = AppProcesses.CloseAll(AppProcesses.Find(dir, includeLegacy: false), log);
        DisplayRestore.AfterAppClosed(exe, killed, log);

        log?.Invoke(L.StAutostart);
        SystemOps.DeleteTaskIfReferences("Visor", exe);
        SystemOps.DeleteTaskIfReferences("DisplayProfileManager", exe);
        SystemOps.DeleteRunValuesFor(exe);
        bool IsOurExe(string t) => PathGuard.SamePath(t, exe);
        SystemOps.DeleteShortcutIf(Product.StartMenuShortcut, IsOurExe);
        SystemOps.DeleteShortcutIf(Product.DesktopShortcut, IsOurExe);

        log?.Invoke(L.StDeleting);
        var self = Product.SelfPath;
        var failed = new List<string>();
        foreach (var rel in manifest.Files)
        {
            var path = PathGuard.ResolveInside(dir, rel);
            if (path == null || !File.Exists(path)) continue;
            if (PathGuard.SamePath(path, self)) { result.SelfToDelete = path; continue; }
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch { failed.Add(rel); }
        }

        if (failed.Count > 0)
        {
            // Keep the manifest (now listing only what is left) and the ARP entry so a retry works.
            try
            {
                manifest.Files = failed.Concat(result.SelfToDelete != null ? new[] { Product.UninstallerName } : new string[0]).ToList();
                manifest.Save(dir);
            }
            catch { }
            result.SelfToDelete = null;
            throw new InstallerException(L.UninstallIncomplete(string.Join("\n", failed.Take(10))));
        }

        PruneEmptyDirs(dir, manifest.Files);
        if (result.SelfToDelete != null)
        {
            // The running Uninstall.exe stays until the cleanup command removes it. Keep a manifest
            // that lists only it, so the folder is still recognized as ours if that cleanup never runs;
            // the cleanup deletes the manifest last.
            try
            {
                manifest.Files = new List<string> { Product.UninstallerName };
                manifest.Save(dir);
            }
            catch { }
        }
        else
        {
            try { File.Delete(InstallManifest.PathIn(dir)); } catch { }
        }

        log?.Invoke(L.StUnregister);
        var arpDir = PathGuard.Normalize(SystemOps.ReadArpInstallLocation(Product.ArpKeyName));
        if (arpDir == null || PathGuard.SamePath(arpDir, dir))
        {
            try { SystemOps.DeleteArpKey(Product.ArpKeyName); } catch { }
        }
        SystemOps.DeleteArpKeyIfFor(Product.OldArpKeyName, dir);

        if (result.SelfToDelete == null) TryDeleteEmptyDir(dir);
        log?.Invoke(L.StDone);
        return result;
    }

    /// <summary>
    /// The uninstaller cannot delete its own exe. Spawn a hidden cmd that waits for this process to
    /// exit, retries deleting the exe (about 20 s in total), then deletes the manifest and removes the
    /// install folder if it is empty (rmdir without /s never deletes contents).
    /// Paths are passed through environment variables and cmd runs with /V:OFF, so characters such
    /// as % ! &amp; ^ ( ) in paths are never interpreted. No FOR loop is used, because a %i loop variable
    /// would make cmd pair the % signs differently.
    /// </summary>
    public static void ScheduleSelfDelete(string selfPath, string dir)
    {
        if (selfPath.IndexOf('"') >= 0 || dir.IndexOf('"') >= 0) return; // impossible in Windows paths
        if (!PathGuard.IsInsideOrSame(selfPath, dir) || PathGuard.IsProtected(dir)) return;

        const string self = "\"%VISOR_UNINST_SELF%\"";
        const string del = "del /f /q " + self + " >nul 2>&1";
        const string cleanup = "del /f /q \"%VISOR_UNINST_MANIFEST%\" >nul 2>&1 & rmdir \"%VISOR_UNINST_DIR%\" >nul 2>&1";
        static string Wait(int s) => "ping -n " + s + " 127.0.0.1 >nul";

        // W3 & D & if exist S (W4 & D & if exist S (W6 & D & if exist S (W11 & D & if not exist S (C)) else (C)) else (C)) else (C)
        string step = "if not exist " + self + " (" + cleanup + ")";
        foreach (var wait in new[] { 11, 6, 4 })
            step = "if exist " + self + " (" + Wait(wait) + " & " + del + " & " + step + ") else (" + cleanup + ")";
        var script = Wait(3) + " & " + del + " & " + step;

        try
        {
            var psi = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                "/d /v:off /s /c \"" + script + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath()
            };
            psi.EnvironmentVariables["VISOR_UNINST_SELF"] = selfPath;
            psi.EnvironmentVariables["VISOR_UNINST_MANIFEST"] = InstallManifest.PathIn(dir);
            psi.EnvironmentVariables["VISOR_UNINST_DIR"] = dir;
            Process.Start(psi)?.Dispose();
        }
        catch { /* leaves Uninstall.exe + manifest; the folder stays recognized as ours for reinstall */ }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Deletes now-empty parent folders of the given relative files (never <paramref name="dir"/> itself).</summary>
    private static void PruneEmptyDirs(string dir, IEnumerable<string> relFiles)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in relFiles)
        {
            var p = Path.GetDirectoryName(rel);
            while (!string.IsNullOrEmpty(p))
            {
                dirs.Add(p!);
                p = Path.GetDirectoryName(p);
            }
        }
        foreach (var rel in dirs.OrderByDescending(d => d.Length))
        {
            var full = PathGuard.ResolveInside(dir, rel);
            if (full != null) TryDeleteEmptyDir(full);
        }
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

    /// <summary>Only used on the installer's own uniquely named staging / backup folders.</summary>
    private static void TryDeleteTree(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
