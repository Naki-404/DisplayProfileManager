using System.IO;

namespace Visor.Services.Optimize.Pc;

/// <summary>Cache categories Visor can measure and (on an explicit click) empty.</summary>
public enum CleanupCategory
{
    /// <summary>User %TEMP% — only files (and empty folders) untouched for 24 h.</summary>
    UserTemp,
    /// <summary>DirectX shader cache (%LocalAppData%\D3DSCache).</summary>
    DirectXShaderCache,
    /// <summary>NVIDIA DXCache / GLCache (+ %ProgramData%\NVIDIA Corporation\NV_Cache when accessible).</summary>
    NvidiaShaderCache,
    /// <summary>AMD DxCache / DxcCache / GLCache.</summary>
    AmdShaderCache,
    /// <summary>Windows Delivery Optimization cache — info only (owned by NetworkService, needs admin).</summary>
    DeliveryOptimization
}

/// <summary>One folder of a category. <paramref name="MinAge"/>: only items older than this are counted / deleted.</summary>
public sealed record CleanupRoot(CleanupCategory Category, string Path, TimeSpan? MinAge = null);

/// <summary>Scan result per category. Sizes are measured, never estimated.</summary>
public sealed record CleanupCategoryScan
{
    public required CleanupCategory Category { get; init; }
    /// <summary>Bytes that a clean would try to remove; null for info-only categories (not measured).</summary>
    public long? Bytes { get; init; }
    public int Files { get; init; }
    /// <summary>Folders / files that could not be read (access denied) — not counted in <see cref="Bytes"/>.</summary>
    public int Inaccessible { get; init; }
    /// <summary>Roots that exist and passed the path guard.</summary>
    public IReadOnlyList<string> Roots { get; init; } = Array.Empty<string>();
    /// <summary>Roots refused by the guard (reparse point / unsafe location) — never touched.</summary>
    public IReadOnlyList<string> RefusedRoots { get; init; } = Array.Empty<string>();
    public bool InfoOnly { get; init; }
    public bool RequiresAdmin { get; init; }
    public bool Exists => Roots.Count > 0;
}

public sealed record CleanupScanResult(IReadOnlyList<CleanupCategoryScan> Categories, DateTime Utc)
{
    /// <summary>Sum over cleanable (non info-only) categories.</summary>
    public long ReclaimableBytes => Categories.Where(c => !c.InfoOnly).Sum(c => c.Bytes ?? 0);

    public CleanupCategoryScan? Get(CleanupCategory c) => Categories.FirstOrDefault(x => x.Category == c);
}

/// <summary>Result of one explicit clean. Irreversible by design (caches) — not journaled.</summary>
public sealed record CleanupRunResult
{
    public required CleanupCategory Category { get; init; }
    public long FreedBytes { get; init; }
    public int DeletedFiles { get; init; }
    /// <summary>Files left in place: locked / in use, read-only, access denied, links, or changed since the scan.</summary>
    public int Skipped { get; init; }
    public int RemovedDirectories { get; init; }
    /// <summary>Non-null when nothing ran: "infoOnly", "noRoots", "cancelled".</summary>
    public string? Code { get; init; }
}

/// <summary>
/// Path guard for cleanup roots and every deleted item (same spirit as the installer's PathGuard):
/// local absolute paths only, never a drive root / profile / AppData / Windows / Program Files folder
/// itself, never a root that is a reparse point, and every item must stay strictly inside its root.
/// </summary>
public static class CleanupPathGuard
{
    /// <summary>Full path without trailing separator, or null when not a local "X:\…" path.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string p = path.Trim().Trim('"');
        if (!StartupCommand.IsLocalAbsolute(p)) return null;
        try
        {
            string full = Path.GetFullPath(p);
            return full.Length > 3 ? full.TrimEnd('\\', '/') : full;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Strictly inside (never equal to) <paramref name="root"/>.</summary>
    public static bool IsStrictlyInside(string path, string root)
    {
        string r = root.TrimEnd('\\') + "\\";
        return path.Length > r.Length && path.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="root"/> may be a cleanup root: normalized local path at least two folders
    /// deep, not one of the well-known personal / system folders themselves, not inside the Windows folder.
    /// </summary>
    public static bool IsAcceptableRoot(string? root, IEnumerable<string?>? protectedFolders = null, string? windowsDir = null)
    {
        var full = Normalize(root);
        if (full == null) return false;
        var segments = full.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3) return false; // "C:\", "C:\Users"
        windowsDir ??= SafeFolder(Environment.SpecialFolder.Windows);
        var win = Normalize(windowsDir);
        if (win != null && (string.Equals(full, win, StringComparison.OrdinalIgnoreCase) || IsStrictlyInside(full, win)))
            return false;
        foreach (var p in protectedFolders ?? DefaultProtected())
        {
            var n = Normalize(p);
            if (n != null && string.Equals(full, n, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>Folders that are never a root themselves (their sub-folders may be).</summary>
    public static IEnumerable<string?> DefaultProtected()
    {
        string profile = SafeFolder(Environment.SpecialFolder.UserProfile);
        yield return profile;
        yield return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "AppData");
        yield return SafeFolder(Environment.SpecialFolder.ApplicationData);
        yield return SafeFolder(Environment.SpecialFolder.LocalApplicationData);
        yield return SafeFolder(Environment.SpecialFolder.CommonApplicationData);
        yield return SafeFolder(Environment.SpecialFolder.ProgramFiles);
        yield return SafeFolder(Environment.SpecialFolder.ProgramFilesX86);
        yield return SafeFolder(Environment.SpecialFolder.DesktopDirectory);
        yield return SafeFolder(Environment.SpecialFolder.MyDocuments);
        yield return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Downloads");
    }

    private static string SafeFolder(Environment.SpecialFolder f)
    {
        try { return Environment.GetFolderPath(f); }
        catch (Exception) { return ""; }
    }

    public static bool IsReparsePoint(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;
}

/// <summary>Default roots for this user / machine.</summary>
public static class CleanupRoots
{
    public static readonly TimeSpan TempMinAge = TimeSpan.FromHours(24);

    public static IReadOnlyList<CleanupRoot> Default()
    {
        string local = Folder(Environment.SpecialFolder.LocalApplicationData);
        string programData = Folder(Environment.SpecialFolder.CommonApplicationData);
        string windows = Folder(Environment.SpecialFolder.Windows);
        string temp;
        try { temp = Path.GetTempPath(); }
        catch (Exception) { temp = ""; }

        var list = new List<CleanupRoot>();
        void Add(CleanupCategory c, string? path, TimeSpan? age = null)
        {
            if (!string.IsNullOrWhiteSpace(path)) list.Add(new CleanupRoot(c, path, age));
        }
        Add(CleanupCategory.UserTemp, temp, TempMinAge);
        if (local.Length > 0)
        {
            Add(CleanupCategory.DirectXShaderCache, Path.Combine(local, "D3DSCache"));
            Add(CleanupCategory.NvidiaShaderCache, Path.Combine(local, "NVIDIA", "DXCache"));
            Add(CleanupCategory.NvidiaShaderCache, Path.Combine(local, "NVIDIA", "GLCache"));
            Add(CleanupCategory.AmdShaderCache, Path.Combine(local, "AMD", "DxCache"));
            Add(CleanupCategory.AmdShaderCache, Path.Combine(local, "AMD", "DxcCache"));
            Add(CleanupCategory.AmdShaderCache, Path.Combine(local, "AMD", "GLCache"));
        }
        if (programData.Length > 0)
            Add(CleanupCategory.NvidiaShaderCache, Path.Combine(programData, "NVIDIA Corporation", "NV_Cache"));
        if (windows.Length > 0)
            Add(CleanupCategory.DeliveryOptimization,
                Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"));
        return list;
    }

    private static string Folder(Environment.SpecialFolder f)
    {
        try { return Environment.GetFolderPath(f); }
        catch (Exception) { return ""; }
    }
}

/// <summary>
/// Measures and empties shader / temp caches. Scan never deletes. A clean runs only for one category
/// on an explicit click, deletes files only (plus folders left empty), skips locked / read-only files,
/// never follows reparse points (junctions / symlinks are neither entered nor deleted) and never leaves
/// its roots. Not journaled: caches cannot be "restored" — they are rebuilt by the driver / games.
/// </summary>
public sealed class CleanupService
{
    private readonly IReadOnlyList<CleanupRoot> _roots;
    private readonly Func<DateTime> _utcNow;
    private readonly IEnumerable<string?>? _protected;
    private readonly string? _windowsDir;

    /// <param name="roots">Default: <see cref="CleanupRoots.Default"/>. Tests pass temp folders.</param>
    /// <param name="protectedFolders">Override of <see cref="CleanupPathGuard.DefaultProtected"/> (tests).</param>
    /// <param name="windowsDir">Override of the Windows folder for the guard (tests).</param>
    public CleanupService(IReadOnlyList<CleanupRoot>? roots = null, Func<DateTime>? utcNow = null,
        IEnumerable<string?>? protectedFolders = null, string? windowsDir = null)
    {
        _roots = roots ?? CleanupRoots.Default();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _protected = protectedFolders;
        _windowsDir = windowsDir;
    }

    public static bool IsInfoOnly(CleanupCategory c) => c == CleanupCategory.DeliveryOptimization;

    /// <summary>Loc key for a category label: optimize.cleanup.cat.userTemp, …</summary>
    public static string CategoryLocKey(CleanupCategory c)
    {
        string n = c.ToString();
        return "optimize.cleanup.cat." + char.ToLowerInvariant(n[0]) + n[1..];
    }

    /// <summary>Drive (e.g. "C:\") holding the cleanable roots, for matching against disk-space readings.</summary>
    public string? PrimaryDrive()
    {
        var r = _roots.FirstOrDefault(x => !IsInfoOnly(x.Category));
        var n = r == null ? null : CleanupPathGuard.Normalize(r.Path);
        return n == null ? null : Path.GetPathRoot(n);
    }

    public CleanupScanResult Scan(CancellationToken ct = default)
    {
        var result = new List<CleanupCategoryScan>();
        foreach (var group in _roots.GroupBy(r => r.Category).OrderBy(g => g.Key))
        {
            ct.ThrowIfCancellationRequested();
            if (IsInfoOnly(group.Key))
            {
                result.Add(new CleanupCategoryScan
                {
                    Category = group.Key,
                    Bytes = null,
                    InfoOnly = true,
                    RequiresAdmin = true,
                    Roots = group.Select(g => g.Path).ToList()
                });
                continue;
            }
            long bytes = 0;
            int files = 0, inaccessible = 0;
            var roots = new List<string>();
            var refused = new List<string>();
            foreach (var root in group)
            {
                var dir = ResolveRoot(root, refused);
                if (dir == null) continue;
                roots.Add(dir.FullName);
                var cutoff = Cutoff(root);
                Walk(dir, ct, onFile: f =>
                {
                    if (IsOldEnough(f, cutoff))
                    {
                        bytes += f.Length;
                        files++;
                    }
                }, onDir: null, ref inaccessible);
            }
            result.Add(new CleanupCategoryScan
            {
                Category = group.Key,
                Bytes = bytes,
                Files = files,
                Inaccessible = inaccessible,
                Roots = roots,
                RefusedRoots = refused
            });
        }
        return new CleanupScanResult(result, _utcNow());
    }

    /// <summary>Deletes the category's eligible files. Irreversible; call only from an explicit click.</summary>
    public CleanupRunResult Clean(CleanupCategory category, CancellationToken ct = default)
    {
        if (IsInfoOnly(category)) return new CleanupRunResult { Category = category, Code = "infoOnly" };
        var roots = _roots.Where(r => r.Category == category).ToList();
        long freed = 0;
        int deleted = 0, skipped = 0, removedDirs = 0, inaccessible = 0;
        bool any = false;
        try
        {
            foreach (var root in roots)
            {
                var dir = ResolveRoot(root, null);
                if (dir == null) continue;
                any = true;
                string rootFull = dir.FullName.TrimEnd('\\');
                var cutoff = Cutoff(root);
                var dirs = new List<DirectoryInfo>();
                Walk(dir, ct, onFile: f =>
                {
                    if (!IsOldEnough(f, cutoff)) return;
                    if (TryDelete(f, rootFull, out long len)) { freed += len; deleted++; }
                    else skipped++;
                }, onDir: d => dirs.Add(d), ref inaccessible);

                // Folders deepest first; only empty ones, never the root, never a reparse point.
                foreach (var d in dirs.OrderByDescending(d => d.FullName.Length))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        d.Refresh();
                        if (!d.Exists || CleanupPathGuard.IsReparsePoint(d)) continue;
                        if (!CleanupPathGuard.IsStrictlyInside(Path.GetFullPath(d.FullName).TrimEnd('\\'), rootFull)) continue;
                        if (!IsOldEnough(d, cutoff)) continue;
                        if (d.EnumerateFileSystemInfos().Any()) continue;
                        d.Delete(recursive: false);
                        removedDirs++;
                    }
                    catch (Exception) { /* in use / not empty / denied — leave it */ }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new CleanupRunResult { Category = category, FreedBytes = freed, DeletedFiles = deleted, Skipped = skipped, RemovedDirectories = removedDirs, Code = "cancelled" };
        }
        AppLog.Info($"Cleanup {category}: freed {freed} bytes, {deleted} files deleted, {skipped} skipped, {removedDirs} folders removed.");
        return new CleanupRunResult
        {
            Category = category,
            FreedBytes = freed,
            DeletedFiles = deleted,
            Skipped = skipped + inaccessible,
            RemovedDirectories = removedDirs,
            Code = any ? null : "noRoots"
        };
    }

    private DateTime? Cutoff(CleanupRoot root) => root.MinAge is { } age ? _utcNow() - age : null;

    /// <summary>
    /// "Old" uses the newer of creation and last-write time: an installer extracting files with old
    /// timestamps right now still counts as new (its creation time is now).
    /// </summary>
    private static bool IsOldEnough(FileSystemInfo f, DateTime? cutoffUtc)
    {
        if (cutoffUtc == null) return true;
        var newest = f.CreationTimeUtc > f.LastWriteTimeUtc ? f.CreationTimeUtc : f.LastWriteTimeUtc;
        return newest < cutoffUtc.Value;
    }

    private DirectoryInfo? ResolveRoot(CleanupRoot root, List<string>? refused)
    {
        var full = CleanupPathGuard.Normalize(root.Path);
        if (full == null || !CleanupPathGuard.IsAcceptableRoot(full, _protected, _windowsDir))
        {
            refused?.Add(root.Path);
            return null;
        }
        try
        {
            var di = new DirectoryInfo(full);
            if (!di.Exists) return null;
            if (CleanupPathGuard.IsReparsePoint(di))
            {
                refused?.Add(root.Path);
                return null;
            }
            return di;
        }
        catch (Exception)
        {
            refused?.Add(root.Path);
            return null;
        }
    }

    private static readonly EnumerationOptions ListOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false
    };

    /// <summary>Iterative walk that never enters (or reports) reparse points.</summary>
    private static void Walk(DirectoryInfo root, CancellationToken ct, Action<FileInfo> onFile,
        Action<DirectoryInfo>? onDir, ref int inaccessible)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            List<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos("*", ListOptions).ToList(); }
            catch (Exception)
            {
                inaccessible++;
                continue;
            }
            foreach (var e in entries)
            {
                FileAttributes attr;
                try { attr = e.Attributes; }
                catch (Exception)
                {
                    inaccessible++;
                    continue;
                }
                if ((attr & FileAttributes.ReparsePoint) != 0) continue; // links: never followed, never deleted
                if (e is DirectoryInfo d)
                {
                    onDir?.Invoke(d);
                    stack.Push(d);
                }
                else if (e is FileInfo f)
                {
                    try { onFile(f); }
                    catch (Exception) { inaccessible++; }
                }
            }
        }
    }

    private static bool TryDelete(FileInfo f, string rootFull, out long length)
    {
        length = 0;
        try
        {
            f.Refresh();
            if (!f.Exists) return false;
            if ((f.Attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly | FileAttributes.System)) != 0) return false;
            string full = Path.GetFullPath(f.FullName);
            if (!CleanupPathGuard.IsStrictlyInside(full, rootFull)) return false;
            // The parent was a plain folder when walked; refuse if it has been swapped for a link since.
            var parent = f.Directory;
            if (parent == null || CleanupPathGuard.IsReparsePoint(parent)) return false;
            length = f.Length;
            f.Delete();
            return true;
        }
        catch (Exception)
        {
            length = 0;
            return false; // locked by a running game / driver, or access denied
        }
    }
}
