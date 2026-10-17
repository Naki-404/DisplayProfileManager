using System.Diagnostics;
using System.IO;
using Visor.Services.Optimize.Pc;
using Xunit;

namespace Visor.Tests;

/// <summary>Cleanup runs only against folders under %TEMP%\VisorTests (TempDir); the guard's protected list is overridden.</summary>
public class PcTweakCleanupTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static string WriteFile(string dir, string name, int bytes, bool old)
    {
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, name);
        File.WriteAllBytes(p, new byte[bytes]);
        if (old)
        {
            var t = Now.AddDays(-3);
            File.SetCreationTimeUtc(p, t);
            File.SetLastWriteTimeUtc(p, t);
        }
        return p;
    }

    private static CleanupService Service(params CleanupRoot[] roots) =>
        new(roots, () => Now, protectedFolders: Array.Empty<string?>(), windowsDir: @"C:\Windows");

    [Fact]
    public void Scan_measures_without_deleting_and_honours_the_temp_age_filter()
    {
        using var dir = new TempDir();
        string temp = Path.Combine(dir.Path, "temp");
        string shader = Path.Combine(dir.Path, "D3DSCache");
        WriteFile(temp, "old.tmp", 1000, old: true);
        WriteFile(temp, "new.tmp", 500, old: false);
        WriteFile(Path.Combine(temp, "sub"), "old2.tmp", 24, old: true);
        WriteFile(shader, "a.bin", 4096, old: false);

        var svc = Service(
            new CleanupRoot(CleanupCategory.UserTemp, temp, CleanupRoots.TempMinAge),
            new CleanupRoot(CleanupCategory.DirectXShaderCache, shader),
            new CleanupRoot(CleanupCategory.DeliveryOptimization, Path.Combine(dir.Path, "do")));
        var scan = svc.Scan();

        var t = scan.Get(CleanupCategory.UserTemp)!;
        Assert.Equal(1024, t.Bytes);
        Assert.Equal(2, t.Files);
        Assert.Equal(4096, scan.Get(CleanupCategory.DirectXShaderCache)!.Bytes);
        var d = scan.Get(CleanupCategory.DeliveryOptimization)!;
        Assert.True(d.InfoOnly);
        Assert.True(d.RequiresAdmin);
        Assert.Null(d.Bytes);
        Assert.Equal(1024 + 4096, scan.ReclaimableBytes);
        Assert.True(File.Exists(Path.Combine(temp, "old.tmp")));
    }

    [Fact]
    public void Clean_deletes_eligible_files_skips_locked_and_readonly_and_reports_freed_bytes()
    {
        using var dir = new TempDir();
        string root = Path.Combine(dir.Path, "NVIDIA", "DXCache");
        WriteFile(root, "a.bin", 100, old: false);
        WriteFile(Path.Combine(root, "nested"), "b.bin", 200, old: false);
        string locked = WriteFile(root, "locked.bin", 300, old: false);
        string ro = WriteFile(root, "ro.bin", 400, old: false);
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        try
        {
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var r = Service(new CleanupRoot(CleanupCategory.NvidiaShaderCache, root)).Clean(CleanupCategory.NvidiaShaderCache);
                Assert.Null(r.Code);
                Assert.Equal(300, r.FreedBytes);
                Assert.Equal(2, r.DeletedFiles);
                Assert.Equal(2, r.Skipped);
                Assert.Equal(1, r.RemovedDirectories); // "nested" became empty
            }
            Assert.True(Directory.Exists(root));       // the root itself is never removed
            Assert.True(File.Exists(locked));
            Assert.True(File.Exists(ro));
            Assert.False(File.Exists(Path.Combine(root, "a.bin")));
        }
        finally
        {
            File.SetAttributes(ro, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Clean_keeps_young_temp_files_and_young_folders()
    {
        using var dir = new TempDir();
        string temp = Path.Combine(dir.Path, "temp");
        WriteFile(temp, "old.tmp", 10, old: true);
        WriteFile(temp, "fresh.tmp", 10, old: false);
        Directory.CreateDirectory(Path.Combine(temp, "fresh-empty"));

        var r = Service(new CleanupRoot(CleanupCategory.UserTemp, temp, CleanupRoots.TempMinAge)).Clean(CleanupCategory.UserTemp);
        Assert.Equal(1, r.DeletedFiles);
        Assert.Equal(0, r.RemovedDirectories);
        Assert.True(File.Exists(Path.Combine(temp, "fresh.tmp")));
        Assert.True(Directory.Exists(Path.Combine(temp, "fresh-empty")));
    }

    [Fact]
    public void Clean_never_follows_a_junction_inside_the_root()
    {
        using var dir = new TempDir();
        string root = Path.Combine(dir.Path, "AMD", "DxCache");
        string outside = Path.Combine(dir.Path, "precious");
        WriteFile(root, "cache.bin", 10, old: false);
        string keep = WriteFile(outside, "keep.txt", 10, old: false);
        if (!TryJunction(Path.Combine(root, "link"), outside)) return; // junctions unavailable → nothing to prove

        var svc = Service(new CleanupRoot(CleanupCategory.AmdShaderCache, root));
        Assert.Equal(10, svc.Scan().Get(CleanupCategory.AmdShaderCache)!.Bytes);
        var r = svc.Clean(CleanupCategory.AmdShaderCache);
        Assert.Equal(1, r.DeletedFiles);
        Assert.True(File.Exists(keep));
        Assert.True(Directory.Exists(Path.Combine(root, "link"))); // the link itself is not deleted either
    }

    [Fact]
    public void A_root_that_is_itself_a_junction_is_refused()
    {
        using var dir = new TempDir();
        string outside = Path.Combine(dir.Path, "elsewhere");
        string keep = WriteFile(outside, "keep.txt", 10, old: false);
        string root = Path.Combine(dir.Path, "D3DSCache");
        if (!TryJunction(root, outside)) return;

        var svc = Service(new CleanupRoot(CleanupCategory.DirectXShaderCache, root));
        var scan = svc.Scan().Get(CleanupCategory.DirectXShaderCache)!;
        Assert.Contains(root, scan.RefusedRoots);
        Assert.Equal(0, scan.Bytes);
        Assert.Equal("noRoots", svc.Clean(CleanupCategory.DirectXShaderCache).Code);
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void Info_only_category_never_cleans()
    {
        var r = Service(new CleanupRoot(CleanupCategory.DeliveryOptimization, @"C:\nowhere\do")).Clean(CleanupCategory.DeliveryOptimization);
        Assert.Equal("infoOnly", r.Code);
        Assert.Equal(0, r.DeletedFiles);
    }

    [Theory]
    [InlineData(@"C:\", false)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"\\server\share\cache", false)]
    [InlineData(@"relative\cache", false)]
    [InlineData(@"C:\Windows\Temp", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"C:\Users\U\AppData\Local\D3DSCache", true)]
    [InlineData(@"C:\Users\U\AppData\Local", false)] // protected (see list below)
    public void Root_guard(string root, bool ok) =>
        Assert.Equal(ok, CleanupPathGuard.IsAcceptableRoot(root, new[] { @"C:\Users\U\AppData\Local" }, @"C:\Windows"));

    [Theory]
    [InlineData(@"C:\a\b\c.txt", @"C:\a\b", true)]
    [InlineData(@"C:\a\b", @"C:\a\b", false)]
    [InlineData(@"C:\a\bc\x", @"C:\a\b", false)]
    [InlineData(@"C:\A\B\x", @"c:\a\b\", true)]
    public void IsStrictlyInside(string path, string root, bool inside) =>
        Assert.Equal(inside, CleanupPathGuard.IsStrictlyInside(path, root));

    [Fact]
    public void Default_roots_cover_every_category_and_only_temp_has_an_age_filter()
    {
        var roots = CleanupRoots.Default();
        foreach (var c in Enum.GetValues<CleanupCategory>())
            Assert.Contains(roots, r => r.Category == c);
        Assert.All(roots.Where(r => r.Category != CleanupCategory.UserTemp), r => Assert.Null(r.MinAge));
        Assert.Equal(CleanupRoots.TempMinAge, roots.Single(r => r.Category == CleanupCategory.UserTemp).MinAge);
        Assert.True(CleanupService.IsInfoOnly(CleanupCategory.DeliveryOptimization));
        Assert.Equal("optimize.cleanup.cat.directXShaderCache", CleanupService.CategoryLocKey(CleanupCategory.DirectXShaderCache));
    }

    /// <summary>Directory junction via cmd mklink /J (no admin needed) — both ends inside the test temp dir.</summary>
    private static bool TryJunction(string link, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0 && (new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
