using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class MonitorSessionTests
{
    // ---------- PresentMonArgs ----------

    [Fact]
    public void Capture_args_for_exe_name()
    {
        var args = PresentMonArgs.BuildCapture(PresentMonTarget.ForExe("game"), @"C:\t\out.csv");
        Assert.StartsWith("--process_name game.exe ", args);
        Assert.Contains(@"--output_file C:\t\out.csv", args);
        Assert.Contains("--session_name VisorPM", args);
        Assert.Contains("--stop_existing_session", args);
        Assert.Contains("--terminate_on_proc_exit", args);
        Assert.Contains("--no_console_stats", args);
        Assert.Contains("--timed 3600 --terminate_after_timed", args);
        Assert.DoesNotContain("--output_stdout", args);
    }

    [Fact]
    public void Capture_args_for_pid_and_quoting()
    {
        var t = PresentMonTarget.ForPid(4242) with { SafetyCapSeconds = 0 };
        var args = PresentMonArgs.BuildCapture(t, @"C:\My Dir\out.csv", "My Session");
        Assert.StartsWith("--process_id 4242 ", args);
        Assert.Contains("--output_file \"C:\\My Dir\\out.csv\"", args);
        Assert.Contains("--session_name \"My Session\"", args);
        Assert.DoesNotContain("--timed", args);
        Assert.DoesNotContain("--terminate_after_timed", args);
    }

    [Fact]
    public void Stop_args_target_the_same_session_name()
    {
        Assert.Equal("--terminate_existing_session --session_name VisorPM --no_console_stats", PresentMonArgs.BuildStop());
        Assert.Contains("--session_name X", PresentMonArgs.BuildStop("X"));
    }

    [Fact]
    public void Invalid_targets_are_rejected()
    {
        Assert.False(new PresentMonTarget().IsValid);
        Assert.False(PresentMonTarget.ForExe(@"C:\x\game.exe").IsValid);
        Assert.False(PresentMonTarget.ForPid(0).IsValid);
        Assert.True(PresentMonTarget.ForExe("Game.EXE").IsValid);
        Assert.Equal("Game.EXE", PresentMonTarget.ForExe("Game.EXE").ExeName);
        Assert.Throws<ArgumentException>(() => PresentMonArgs.BuildCapture(new PresentMonTarget(), "x.csv"));
        Assert.Throws<ArgumentException>(() => PresentMonArgs.BuildCapture(PresentMonTarget.ForExe("a"), ""));
    }

    // ---------- CsvFileTail ----------

    [Fact]
    public void Tail_returns_only_complete_lines_and_follows_appends()
    {
        using var dir = new TempDir();
        var path = dir.File("t.csv");
        using var tail = new CsvFileTail(path);
        var lines = new List<string>();
        Assert.False(tail.TryOpen());
        Assert.Equal(0, tail.ReadNewLines(lines));

        using (var w = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            Write(w, "H1,H2\r\n1,2\r\n3,");
            Assert.Equal(2, tail.ReadNewLines(lines));
            Assert.Equal(new[] { "H1,H2", "1,2" }, lines);
            Write(w, "4\n5,6\n");
            Assert.Equal(2, tail.ReadNewLines(lines));
            Assert.Equal("3,4", lines[2]);
            Assert.Equal("5,6", lines[3]);
            Write(w, "7,8");
            Assert.Equal(0, tail.ReadNewLines(lines));
        }
        Assert.True(tail.TakePartial(out var last));
        Assert.Equal("7,8", last);
        Assert.False(tail.TakePartial(out _));
        Assert.True(tail.BytesRead > 0);
    }

    private static void Write(FileStream fs, string s)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(s);
        fs.Write(b, 0, b.Length);
        fs.Flush();
    }

    // ---------- PresentMonSession with a fake launcher ----------

    private const string Header = "Application,ProcessID,CPUStartTime,FrameTime,CPUBusy,GPUBusy,DisplayedTime";

    /// <summary>Fake "elevated PresentMon": records the start info and lets the test play the CSV writer / exit.</summary>
    private sealed class FakeLauncher : IProcessLauncher
    {
        public readonly List<ProcessStartInfo> Launched = new();
        public Func<ProcessStartInfo, IProcessHandle>? OnLaunch;
        public IProcessHandle Launch(ProcessStartInfo psi)
        {
            Launched.Add(psi);
            return OnLaunch?.Invoke(psi) ?? new FakeHandle();
        }
    }

    private sealed class FakeHandle : IProcessHandle
    {
        public bool HasExited { get; private set; }
        public int? ExitCode { get; private set; }
        public event Action? Exited;
        public void Exit(int code) { HasExited = true; ExitCode = code; Exited?.Invoke(); }
        public void Dispose() { }
    }

    private static string OutputFileFrom(ProcessStartInfo psi)
    {
        const string key = "--output_file ";
        int i = psi.Arguments.IndexOf(key, StringComparison.Ordinal) + key.Length;
        string rest = psi.Arguments[i..];
        if (rest.StartsWith('"')) return rest[1..rest.IndexOf('"', 1)];
        return rest[..rest.IndexOf(' ')];
    }

    [Fact]
    public void Session_launches_elevated_tails_csv_and_reports_exit()
    {
        using var dir = new TempDir();
        var launcher = new FakeLauncher();
        var handle = new FakeHandle();
        launcher.OnLaunch = _ => handle;
        using var session = new PresentMonSession(@"C:\fake\PresentMon.exe", launcher, dir.Path, "TestPM");

        var frames = new List<FrameRecord>();
        var errors = new List<(PresentMonErrorCode, string)>();
        int? exitCode = null; int exited = 0;
        session.Frame += frames.Add;
        session.Error += (c, d) => errors.Add((c, d));
        session.Exited += c => { exitCode = c; exited++; };

        session.Start(PresentMonTarget.ForExe("game.exe"));
        Assert.True(session.IsRunning);
        var psi = Assert.Single(launcher.Launched);
        Assert.Equal("runas", psi.Verb);
        Assert.True(psi.UseShellExecute);
        Assert.Equal(@"C:\fake\PresentMon.exe", psi.FileName);
        Assert.Contains("--process_name game.exe", psi.Arguments);
        Assert.Contains("--session_name TestPM", psi.Arguments);
        var csv = OutputFileFrom(psi);
        Assert.Equal(session.OutputPath, csv);
        Assert.StartsWith(dir.Path, csv, StringComparison.OrdinalIgnoreCase);

        session.Poll();                       // file does not exist yet → nothing, no error
        Assert.Empty(frames);
        Assert.Empty(errors);

        using (var w = new FileStream(csv, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            Write(w, Header + "\r\ngame.exe,1,0.016,16.0,4,12,0.0\r\ngame.exe,1,0.032,16.5,4,12,NA\r\n");
            session.Poll();
            Assert.Equal(2, frames.Count);
            Assert.Equal(16.5, frames[1].FrameTimeMs);
            Assert.True(frames[1].Dropped);
            Write(w, "game.exe,1,0.048,8.0,4,12,0.0");   // no trailing newline → drained at exit
            session.Poll();
            Assert.Equal(2, frames.Count);
            handle.Exit(0);
        }
        Assert.Equal(3, frames.Count);
        Assert.Equal(8.0, frames[2].FrameTimeMs);
        Assert.False(session.IsRunning);
        Assert.Equal(1, exited);
        Assert.Equal(0, exitCode);
        Assert.Empty(errors);
        session.Poll();                       // after finish: no-op
    }

    [Fact]
    public void Uac_declined_maps_to_UacDeclined_and_exits_without_process()
    {
        using var dir = new TempDir();
        var launcher = new FakeLauncher { OnLaunch = _ => throw new Win32Exception(1223) };
        using var session = new PresentMonSession(@"C:\fake\PresentMon.exe", launcher, dir.Path, "TestPM");
        var errors = new List<PresentMonErrorCode>();
        int exited = 0;
        session.Error += (c, _) => errors.Add(c);
        session.Exited += _ => exited++;
        session.Start(PresentMonTarget.ForExe("game.exe"));
        Assert.False(session.IsRunning);
        Assert.Equal(new[] { PresentMonErrorCode.UacDeclined }, errors);
        Assert.Equal(1, exited);
        // Can start again afterwards.
        launcher.OnLaunch = _ => new FakeHandle();
        session.Start(PresentMonTarget.ForExe("game.exe"));
        Assert.True(session.IsRunning);
    }

    [Fact]
    public void Other_launch_failure_maps_to_StartFailed()
    {
        using var dir = new TempDir();
        var launcher = new FakeLauncher { OnLaunch = _ => throw new Win32Exception(2, "not found") };
        using var session = new PresentMonSession(@"C:\fake\PresentMon.exe", launcher, dir.Path, "TestPM");
        PresentMonErrorCode? code = null;
        session.Error += (c, _) => code = c;
        session.Start(PresentMonTarget.ForExe("game.exe"));
        Assert.Equal(PresentMonErrorCode.StartFailed, code);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public void Nonzero_exit_without_data_is_ExitedWithError_but_with_data_is_clean()
    {
        using var dir = new TempDir();
        var launcher = new FakeLauncher();
        var h1 = new FakeHandle();
        launcher.OnLaunch = _ => h1;
        using var session = new PresentMonSession(@"C:\fake\PresentMon.exe", launcher, dir.Path, "TestPM");
        var errors = new List<PresentMonErrorCode>();
        session.Error += (c, _) => errors.Add(c);
        session.Start(PresentMonTarget.ForExe("game.exe"));
        h1.Exit(1);
        Assert.Equal(new[] { PresentMonErrorCode.ExitedWithError }, errors);

        errors.Clear();
        var h2 = new FakeHandle();
        launcher.OnLaunch = _ => h2;
        session.Start(PresentMonTarget.ForExe("game.exe"));
        File.WriteAllText(session.OutputPath!, Header + "\r\ngame.exe,1,0.016,16.0,4,12,0.0\r\n");
        session.Poll();
        h2.Exit(1);   // --terminate_after_timed etc. may exit non-zero after real data: not an error
        Assert.Empty(errors);
    }

    [Fact]
    public void RequestStop_launches_elevated_stop_helper_and_declined_helper_is_StopFailed()
    {
        using var dir = new TempDir();
        var launcher = new FakeLauncher();
        var capture = new FakeHandle();
        launcher.OnLaunch = _ => capture;
        using var session = new PresentMonSession(@"C:\fake\PresentMon.exe", launcher, dir.Path, "TestPM");
        var errors = new List<PresentMonErrorCode>();
        session.Error += (c, _) => errors.Add(c);

        session.RequestStop();                 // not running → no-op
        Assert.Empty(launcher.Launched);

        session.Start(PresentMonTarget.ForExe("game.exe"));
        launcher.OnLaunch = _ => throw new Win32Exception(1223);
        session.RequestStop();
        Assert.Equal(2, launcher.Launched.Count);
        Assert.Equal("runas", launcher.Launched[1].Verb);
        Assert.Contains("--terminate_existing_session", launcher.Launched[1].Arguments);
        Assert.Contains("--session_name TestPM", launcher.Launched[1].Arguments);
        Assert.Equal(new[] { PresentMonErrorCode.StopFailed }, errors);
        Assert.True(session.IsRunning);        // capture still alive

        launcher.OnLaunch = _ => new FakeHandle();
        session.RequestStop();                 // retry allowed after a declined helper
        Assert.Equal(3, launcher.Launched.Count);
        session.RequestStop();                 // already requested → no third helper
        Assert.Equal(3, launcher.Launched.Count);
        capture.Exit(0);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public void Start_twice_throws_and_dispose_stops_listening_without_launching_anything()
    {
        using var dir = new TempDir();
        var launcher = new FakeLauncher();
        var session = new PresentMonSession(@"C:\fake\PresentMon.exe", launcher, dir.Path, "TestPM");
        session.Start(PresentMonTarget.ForExe("game.exe"));
        Assert.Throws<InvalidOperationException>(() => session.Start(PresentMonTarget.ForExe("game.exe")));
        Assert.Throws<ArgumentException>(() => session.Start(new PresentMonTarget()));
        session.Dispose();
        Assert.False(session.IsRunning);
        Assert.Single(launcher.Launched);      // no stop helper, no UAC from Dispose
        Assert.Throws<ObjectDisposedException>(() => session.Start(PresentMonTarget.ForExe("game.exe")));
    }
}
