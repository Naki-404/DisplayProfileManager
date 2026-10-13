using System.Diagnostics;
using System.IO;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class PathSecurityTests
{
    [Fact]
    public void PlausibleExecutablePath_accepts_plain_local_exe()
    {
        Assert.True(PathSecurity.IsPlausibleExecutablePath(@"C:\Games\Tool\x.exe"));
        Assert.True(PathSecurity.IsPlausibleExecutablePath(@"D:\x.exe"));
        Assert.True(PathSecurity.IsPlausibleExecutablePath(@"c:/games/x.EXE"));
    }

    [Theory]
    [InlineData(@"\\srv\share\x.exe")]       // UNC
    [InlineData(@"//srv/share/x.exe")]       // UNC, forward slashes
    [InlineData(@"\\?\C:\x.exe")]            // extended path
    [InlineData(@"\\.\C:\x.exe")]            // device path
    [InlineData(@"\??\C:\x.exe")]            // NT path
    [InlineData(@"x.exe")]                   // relative
    [InlineData(@"..\x.exe")]                // relative
    [InlineData(@"C:x.exe")]                 // drive-relative
    [InlineData(@"C:\x.txt")]                // not an exe
    [InlineData(@"C:\x")]                    // no extension
    [InlineData(@"C:\x.exe:stream")]         // alternate data stream
    [InlineData(@"C:\a & b\x.exe")]          // shell metachar
    [InlineData(@"C:\a|b\x.exe")]
    [InlineData(@"C:\a""b\x.exe")]
    [InlineData(@"C:\a'b\x.exe")]            // apostrophe is blocked for companions
    [InlineData("C:\\a\nb\\x.exe")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void PlausibleExecutablePath_rejects(string? path)
    {
        Assert.False(PathSecurity.IsPlausibleExecutablePath(path));
    }

    [Fact]
    public void SafeAutostartExe_accepts_apostrophe_in_path_and_requires_existing_file()
    {
        using var dir = new TempDir();
        var sub = Directory.CreateDirectory(Path.Combine(dir.Path, "O'Brien")).FullName;
        var exe = Path.Combine(sub, "Visor.exe");
        File.WriteAllText(exe, "not really an exe");

        Assert.True(PathSecurity.IsSafeAutostartExe(exe, out var full));
        Assert.Equal(exe, full);

        Assert.False(PathSecurity.IsSafeAutostartExe(Path.Combine(sub, "missing.exe"), out _));
        Assert.False(PathSecurity.IsSafeAutostartExe(@"\\srv\s\Visor.exe", out _));
        Assert.False(PathSecurity.IsSafeAutostartExe(exe.Replace("Visor.exe", "a&b.exe"), out _));
    }

    [Fact]
    public void NormalizeExecutable_requires_existing_exe()
    {
        using var dir = new TempDir();
        var exe = dir.Write("game.exe", "x");
        Assert.True(PathSecurity.TryNormalizeExecutable(exe, out var full, out var err));
        Assert.Equal(exe, full);
        Assert.Equal("", err);

        Assert.False(PathSecurity.TryNormalizeExecutable(dir.File("missing.exe"), out _, out err));
        Assert.Equal("File not found.", err);

        Assert.False(PathSecurity.TryNormalizeExecutable(@"\\srv\s\x.exe", out _, out err));
        Assert.NotEqual("", err);
    }

    [SkippableFact]
    public void NormalizeExecutable_follows_junction_to_local_target()
    {
        using var dir = new TempDir();
        var real = Directory.CreateDirectory(dir.File("real")).FullName;
        var exe = Path.Combine(real, "game.exe");
        File.WriteAllText(exe, "x");

        var junction = dir.File("link");
        Skip.IfNot(TryMklink("/J", junction, real), "mklink /J not permitted in this environment");

        var viaJunction = Path.Combine(junction, "game.exe");
        Assert.True(PathSecurity.TryNormalizeExecutable(viaJunction, out var full, out var err), err);
        Assert.True(File.Exists(full));
        Assert.EndsWith("game.exe", full, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void NormalizeExecutable_resolves_file_symlink_to_final_target()
    {
        using var dir = new TempDir();
        var exe = dir.Write("target.exe", "x");
        var link = dir.File("alias.exe");
        Skip.IfNot(TryMklink("", link, exe), "file symlinks not permitted (needs Developer Mode or admin)");

        Assert.True(PathSecurity.TryNormalizeExecutable(link, out var full, out var err), err);
        Assert.Equal(exe, full, ignoreCase: true);
    }

    [SkippableFact]
    public void NormalizeExecutable_rejects_symlink_whose_target_is_not_an_exe()
    {
        using var dir = new TempDir();
        var txt = dir.Write("target.txt", "x");
        var link = dir.File("alias.exe");
        Skip.IfNot(TryMklink("", link, txt), "file symlinks not permitted (needs Developer Mode or admin)");

        Assert.False(PathSecurity.TryNormalizeExecutable(link, out _, out var err));
        Assert.Equal("Link target is not an .exe.", err);
    }

    [SkippableFact]
    public void NormalizeExecutable_rejects_directory_symlink_to_unc()
    {
        using var dir = new TempDir();
        var link = dir.File("remote");
        Skip.IfNot(TryMklink("/D", link, @"\\localhost\VisorTestsNoSuchShare\bin"),
            "directory symlinks not permitted (needs Developer Mode or admin)");

        Assert.False(PathSecurity.TryNormalizeExecutable(Path.Combine(link, "x.exe"), out _, out var err));
        Assert.Contains("local", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScheduledTaskName_validation()
    {
        Assert.True(PathSecurity.IsSafeScheduledTaskName("MyTool"));
        Assert.True(PathSecurity.IsSafeScheduledTaskName(@"Folder\My-Tool.v2"));
        Assert.False(PathSecurity.IsSafeScheduledTaskName(""));
        Assert.False(PathSecurity.IsSafeScheduledTaskName(null));
        Assert.False(PathSecurity.IsSafeScheduledTaskName("a&b"));
        Assert.False(PathSecurity.IsSafeScheduledTaskName("a b"));
        Assert.False(PathSecurity.IsSafeScheduledTaskName(new string('a', 201)));
    }

    [Fact]
    public void Arguments_validation_and_split()
    {
        Assert.True(PathSecurity.IsSafeArguments(null));
        Assert.True(PathSecurity.IsSafeArguments("-launchapp abc"));
        Assert.False(PathSecurity.IsSafeArguments("a & b"));
        Assert.False(PathSecurity.IsSafeArguments("a; b"));
        Assert.False(PathSecurity.IsSafeArguments(new string('a', 1025)));

        Assert.Empty(PathSecurity.SplitArguments(null));
        Assert.Equal(new[] { "-x", "a b", "c" }, PathSecurity.SplitArguments("-x \"a b\"  c"));
    }

    /// <summary>Runs cmd /c mklink; false when the link could not be created (privilege / policy).</summary>
    private static bool TryMklink(string flag, string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = $"/c mklink {flag} \"{link}\" \"{target}\"".Replace("  ", " ")
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
            return p.ExitCode == 0 && (File.Exists(link) || Directory.Exists(link));
        }
        catch
        {
            return false;
        }
    }
}
