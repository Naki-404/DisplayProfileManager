using System.IO;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

/// <summary>
/// AppLog is process-wide, so each test points it at its own temp file and back again. Other test
/// classes may log concurrently — assertions only look for this test's own marker lines.
/// </summary>
// AppLog is process-global: other test classes log WARN/ERROR (immediate flush) and the 1 s timer
// flush runs on its own thread, so ordering assertions against an external writer are only
// deterministic when nothing else runs. xunit runs a DisableParallelization collection alone.
[CollectionDefinition("AppLog serial", DisableParallelization = true)]
public sealed class AppLogSerialCollection { }

[Collection("AppLog serial")]
public class AppLogTests
{
    /// <summary>Redirects AppLog to a temp file for the test; restores the shared test log afterwards.</summary>
    private sealed class LogScope : IDisposable
    {
        private readonly TempDir _dir;
        private readonly string _original;

        public string Path { get; }

        public LogScope()
        {
            _dir = new TempDir();
            _original = AppLog.LogFilePath;
            Path = _dir.File("Visor.log");
            AppLog.Configure(Path);
        }

        public string Text() => File.Exists(Path) ? ReadShared(Path) : "";

        public void Dispose()
        {
            AppLog.Flush();
            AppLog.MaxLogBytes = 0;           // back to the 2 MB default
            AppLog.Configure(_original);      // flushes what is still pending into the temp file first
            _dir.Dispose();
        }
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    private static string Marker(string tag) => $"{tag}-{Guid.NewGuid():N}";

    [Fact]
    public void MaskUserPaths_replaces_user_profile_path_with_tilde()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        Assert.True(profile.Length >= 4, "test needs a real user profile path");

        var masked = AppLog.MaskUserPaths($"cfg at {profile}\\AppData\\Roaming\\Visor; upper {profile.ToUpperInvariant()}\\x");

        Assert.Equal("cfg at ~\\AppData\\Roaming\\Visor; upper ~\\x", masked);
        Assert.DoesNotContain(profile, masked, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaskUserPaths_leaves_other_text_alone()
    {
        Assert.Equal("C:\\Games\\x.exe", AppLog.MaskUserPaths("C:\\Games\\x.exe"));
    }

    [Fact]
    public void Flush_makes_buffered_info_lines_visible_in_the_file()
    {
        using var log = new LogScope();
        var marker = Marker("flush");

        AppLog.Info(marker);
        AppLog.Flush();

        var text = log.Text();
        Assert.Contains(marker, text);
        Assert.Contains("[INFO] " + marker, text);
    }

    [Fact]
    public void Warn_and_error_are_written_without_an_explicit_flush()
    {
        using var log = new LogScope();
        var warn = Marker("warn");
        var error = Marker("error");

        AppLog.Warn(warn);
        AppLog.Error(error);

        var text = log.Text();
        Assert.Contains("[WARN] " + warn, text);
        Assert.Contains("[ERROR] " + error, text);
    }

    [Fact]
    public void Lines_appended_by_another_process_between_flushes_are_not_overwritten()
    {
        using var log = new LogScope();
        var a = Marker("first");
        var b = Marker("second");
        var c = Marker("third");
        var d = Marker("fourth");

        AppLog.Info(a);
        AppLog.Flush();
        // "Second process" (second instance / --list / --emergency) appends to the same file.
        File.AppendAllText(log.Path, "external-1" + Environment.NewLine);
        AppLog.Info(b);                       // buffered — flushed after the external append
        File.AppendAllText(log.Path, "external-2" + Environment.NewLine);
        AppLog.Flush();
        File.AppendAllText(log.Path, "external-3" + Environment.NewLine);
        AppLog.Error(c);                      // immediate flush at the current end of file
        AppLog.Info(d);
        AppLog.Flush();

        var text = log.Text();
        // Every line survives: a stale-offset writer would have overwritten the external ones.
        AssertInOrder(text, a, "external-1", "external-2", "external-3", c, d);
        // b was buffered after external-1 and is on disk by the Flush before external-3. Whether it
        // lands before or after external-2 depends on other test classes logging a WARN/ERROR
        // (immediate flush) in between, so only its bounds are asserted.
        AssertInOrder(text, "external-1", b, "external-3");
    }

    private static void AssertInOrder(string text, params string[] tokens)
    {
        int last = -1;
        foreach (var token in tokens)
        {
            int at = text.IndexOf(token, StringComparison.Ordinal);
            Assert.True(at >= 0, $"missing: {token}");
            Assert.True(at > last, $"out of order: {token}");
            last = at;
        }
    }

    [Fact]
    public void Rotation_at_threshold_moves_to_dot1_and_keeps_two_files()
    {
        using var log = new LogScope();
        AppLog.MaxLogBytes = 4096;
        var rotated = log.Path + ".1";
        var padding = new string('x', 80);

        // Phase 1: fill until the first rotation.
        var markerA = Marker("rotA");
        int i;
        for (i = 0; i < 400 && !File.Exists(rotated); i++)
        {
            AppLog.Info($"{markerA} {i:000} {padding}");
            AppLog.Flush();
        }
        Assert.True(File.Exists(rotated), "log was not rotated at the size threshold");
        Assert.Contains(markerA, ReadShared(rotated));

        // Phase 2: the next rotation replaces .1 — never a .2.
        var markerB = Marker("rotB");
        for (i = 0; i < 400 && !ReadShared(rotated).Contains(markerB, StringComparison.Ordinal); i++)
        {
            AppLog.Info($"{markerB} {i:000} {padding}");
            AppLog.Flush();
        }
        var dot1 = ReadShared(rotated);
        Assert.Contains(markerB, dot1);
        Assert.DoesNotContain(markerA, dot1);
        Assert.False(File.Exists(log.Path + ".2"));
        Assert.False(File.Exists(log.Path + ".1.1"));

        // After the rotation a fresh file is started and stays under the threshold.
        AppLog.Info(Marker("after"));
        AppLog.Flush();
        Assert.True(new FileInfo(log.Path).Length < 4096);
    }

    [Fact]
    public void Tail_returns_the_most_recent_ring_lines()
    {
        using var log = new LogScope();
        var marker = Marker("tail");
        AppLog.Info(marker);

        // Other test classes log concurrently into the same ring — ask for the whole ring.
        var tail = AppLog.Tail(500);

        Assert.True(tail.Count <= 500);
        Assert.Contains(tail, l => l.Contains(marker, StringComparison.Ordinal));
        Assert.Contains(tail, l => l.Contains("[INFO] " + marker, StringComparison.Ordinal));
    }
}
