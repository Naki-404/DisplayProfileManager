using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Visor.Services;
using Visor.Services.FrameCapture;
using Xunit;

namespace Visor.Tests;

/// <summary>
/// Unelevated session + capture host wired through a real in-process named pipe. The "launcher"
/// runs <see cref="FrameCaptureHost"/> on a background task instead of starting an elevated
/// process, and the ETW session is a scripted fake — nothing is elevated, no trace session starts.
/// </summary>
public class FrameCaptureSessionTests
{
    private const int TargetPid = 4242;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class FakeSource : IPresentEventSource
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim StartedSignal = new();
        public Action<PresentEvent>? OnEvent;
        public PresentEventFilter? Filter;
        public uint StartResult;
        public int StartCalls, StopCalls;
        public long QpcFrequency => 10_000_000;
        public Task Completion => _completion.Task;
        public string EnableSummary => "fake";

        public uint Start(PresentEventFilter filter, Action<PresentEvent> onEvent)
        {
            StartCalls++;
            Filter = filter;
            OnEvent = onEvent;
            StartedSignal.Set();
            return StartResult;
        }

        public void Stop()
        {
            Interlocked.Increment(ref StopCalls);
            _completion.TrySetResult();
        }

        public void EndFromOutside() => _completion.TrySetResult();
        public void Dispose() { }

        public void EmitFrames(int count, double fps, int pid = TargetPid)
        {
            for (int i = 0; i < count; i++)
                OnEvent!(new PresentEvent(1_000_000 + (long)(i * 10_000_000 / fps), pid, PresentSource.Dxgi, PresentProviders.DxgiPresentStart, 0xA));
        }
    }

    private sealed class FakeHandle : IProcessHandle
    {
        private int _raised;
        public int? Code;
        public bool HasExited => Code != null;
        public int? ExitCode => Code;
        public event Action? Exited;
        public void Exit(int code)
        {
            Code = code;
            if (Interlocked.Exchange(ref _raised, 1) == 0) Exited?.Invoke();
        }
        public void Dispose() { }
    }

    /// <summary>Runs the host in-process (or simulates a launch failure / an early exit).</summary>
    private sealed class FakeLauncher : IProcessLauncher
    {
        private readonly Func<FrameCaptureHost> _host;
        public Exception? Throw;
        public int? ExitWithoutRunning;
        public ProcessStartInfo? Psi;
        public Task<int>? HostTask;
        public FrameCaptureHost? Host;

        public FakeLauncher(Func<FrameCaptureHost> host) => _host = host;

        public IProcessHandle Launch(ProcessStartInfo psi)
        {
            Psi = psi;
            if (Throw != null) throw Throw;
            var handle = new FakeHandle();
            if (ExitWithoutRunning is int code)
            {
                handle.Exit(code);
                return handle;
            }
            string args = psi.Arguments;
            Assert.StartsWith(FrameCaptureHost.CliSwitch + " \"", args);
            string path = args[(FrameCaptureHost.CliSwitch.Length + 2)..^1];
            Host = _host();
            HostTask = Task.Run(() => Host.ExecuteAsync(path));
            HostTask.ContinueWith(t => handle.Exit(t.IsCompletedSuccessfully ? t.Result : 99));
            return handle;
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly FakeSource Source = new();
        public readonly TaskCompletionSource ParentExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ConnectGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool TargetAlive = true;
        public bool Elevated = true;
        public int? ServerPidOverride;
        public readonly FakeLauncher Launcher;
        public readonly NativeFrameCaptureSession Session;
        public readonly List<FrameRecord> Frames = new();
        public readonly List<(FrameCaptureErrorCode Code, string Detail)> Errors = new();
        public readonly TaskCompletionSource<int?> Exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Folder { get; }

        public Rig(TimeSpan? noOutput = null, bool gateConnect = false)
        {
            Folder = Path.Combine(Dir.Path, "Visor", "elevation");
            if (!gateConnect) ConnectGate.SetResult();
            var production = FrameCaptureHostEnvironment.Production();
            Launcher = new FakeLauncher(() => new FrameCaptureHost(new FrameCaptureHostEnvironment
            {
                IsElevated = () => Elevated,
                ConnectPipe = async (name, timeout, ct) =>
                {
                    await ConnectGate.Task.WaitAsync(ct);
                    return await production.ConnectPipe(name, timeout, ct);   // the real client: anonymous impersonation level
                },
                PipeServerProcessId = s => ServerPidOverride ?? production.PipeServerProcessId(s),   // real GetNamedPipeServerProcessId
                CreateEventSource = () => Source,
                WaitForProcessExit = (_, ct) => ParentExit.Task.WaitAsync(ct),
                IsProcessAlive = pid => pid == TargetPid && TargetAlive,
                FindProcessesByExe = _ => new[] { TargetPid },
                OwnProcessId = -1,
                TargetPollInterval = TimeSpan.FromMilliseconds(20)
            }));
            Session = new NativeFrameCaptureSession(Launcher, Folder, @"C:\Program Files\Visor\Visor.exe",
                _ => new[] { TargetPid }, connectTimeout: TimeSpan.FromSeconds(5), noOutputTimeout: noOutput ?? TimeSpan.FromSeconds(30));
            Session.Frame += f => { lock (Frames) Frames.Add(f); };
            Session.Error += (c, d) => { lock (Errors) Errors.Add((c, d)); };
            Session.Exited += code => Exited.TrySetResult(code);
        }

        public int FrameCount { get { lock (Frames) return Frames.Count; } }

        public void Start(int capSeconds = 3600) =>
            Session.Start(FrameCaptureTarget.ForExe("game.exe") with { SafetyCapSeconds = capSeconds });

        public int? AwaitExit()
        {
            Assert.True(Exited.Task.Wait(Wait), "session did not end");
            return Exited.Task.Result;
        }

        public void Dispose()
        {
            Session.Dispose();
            ParentExit.TrySetResult();
            Launcher.HostTask?.Wait(Wait);
            Dir.Dispose();
        }
    }

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > Wait) Assert.Fail("timed out waiting for " + what);
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Start_frames_stop_byte_and_the_host_stops_its_session()
    {
        using var rig = new Rig();
        rig.Start();
        Assert.True(rig.Session.IsRunning);
        Assert.Equal("runas", rig.Launcher.Psi!.Verb);
        Assert.True(rig.Launcher.Psi.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Hidden, rig.Launcher.Psi.WindowStyle);
        Assert.Equal(@"C:\Program Files\Visor\Visor.exe", rig.Launcher.Psi.FileName);
        Assert.Matches("^VisorFrames-[0-9a-f]{32}$", rig.Session.PipeName);

        Assert.True(rig.Source.StartedSignal.Wait(Wait));
        Assert.Equal(new[] { TargetPid }, rig.Source.Filter!.ProcessIds);
        WaitUntil(() => rig.Session.RequestPath != null && !File.Exists(rig.Session.RequestPath), "request file cleanup after connect");

        rig.Source.EmitFrames(50, 100);
        WaitUntil(() => rig.FrameCount == 48, "48 frames");
        lock (rig.Frames) Assert.All(rig.Frames, f => Assert.Equal(10, f.FrameTimeMs, 3));

        rig.Session.RequestStop();
        rig.Session.RequestStop();   // idempotent, still one byte
        int? code = rig.AwaitExit();

        Assert.Equal(0, code);
        Assert.Equal(1, rig.Source.StopCalls);
        Assert.Equal(HostStopReason.StopRequested, rig.Launcher.Host!.LastStopReason);
        Assert.Equal(HostStopReason.StopRequested, rig.Session.HostStopReason);
        Assert.Empty(rig.Errors);
        Assert.False(rig.Session.IsRunning);
        Assert.Empty(Directory.GetFiles(rig.Folder));
    }

    [Fact]
    public void Uac_declined_maps_to_UacDeclined_synchronously()
    {
        using var rig = new Rig();
        rig.Launcher.Throw = new Win32Exception(1223);
        rig.Start();
        Assert.True(rig.Exited.Task.IsCompleted);   // inside Start, like the facade expects
        Assert.Equal(FrameCaptureErrorCode.UacDeclined, Assert.Single(rig.Errors).Code);
        Assert.False(rig.Session.IsRunning);
        Assert.Empty(Directory.GetFiles(rig.Folder));   // request file removed
    }

    [Fact]
    public void Other_launch_failures_are_StartFailed()
    {
        using var rig = new Rig();
        rig.Launcher.Throw = new Win32Exception(2, "file not found");
        rig.Start();
        Assert.Equal(FrameCaptureErrorCode.StartFailed, Assert.Single(rig.Errors).Code);
    }

    [Fact]
    public async Task No_frames_raises_NoOutput_once_but_keeps_capturing_until_disposed()
    {
        using var rig = new Rig(noOutput: TimeSpan.FromMilliseconds(150));
        rig.Start();
        Assert.True(rig.Source.StartedSignal.Wait(Wait));
        WaitUntil(() => { lock (rig.Errors) return rig.Errors.Count > 0; }, "NoOutput");
        Thread.Sleep(300);
        lock (rig.Errors) Assert.Equal(FrameCaptureErrorCode.NoOutput, Assert.Single(rig.Errors).Code);
        Assert.True(rig.Session.IsRunning);

        // Disposing closes the pipe: the host notices and stops the ETW session on its own.
        rig.Session.Dispose();
        Assert.Equal(0, await rig.Launcher.HostTask!.WaitAsync(Wait));
        Assert.Equal(HostStopReason.PipeClosed, rig.Launcher.Host!.LastStopReason);
        Assert.Equal(1, rig.Source.StopCalls);
    }

    [Fact]
    public void Parent_exit_stops_the_host()
    {
        using var rig = new Rig();
        rig.Start();
        Assert.True(rig.Source.StartedSignal.Wait(Wait));
        rig.ParentExit.SetResult();
        rig.AwaitExit();
        Assert.Equal(HostStopReason.ParentExited, rig.Launcher.Host!.LastStopReason);
        Assert.Equal(HostStopReason.ParentExited, rig.Session.HostStopReason);
        Assert.Equal(1, rig.Source.StopCalls);
    }

    [Fact]
    public void Target_exit_stops_the_host_with_TargetExited()
    {
        using var rig = new Rig();
        rig.Start();
        Assert.True(rig.Source.StartedSignal.Wait(Wait));
        rig.Source.EmitFrames(10, 60);
        WaitUntil(() => rig.FrameCount == 8, "frames");
        rig.TargetAlive = false;
        Assert.Equal(0, rig.AwaitExit());
        Assert.Equal(HostStopReason.TargetExited, rig.Session.HostStopReason);
        Assert.Empty(rig.Errors);
        Assert.Equal(1, rig.Source.StopCalls);
    }

    [Fact]
    public void Session_killed_from_outside_ends_the_capture()
    {
        using var rig = new Rig();
        rig.Start();
        Assert.True(rig.Source.StartedSignal.Wait(Wait));
        rig.Source.EndFromOutside();   // e.g. "logman stop VisorFrameCapture -ets"
        rig.AwaitExit();
        Assert.Equal(HostStopReason.SessionEnded, rig.Session.HostStopReason);
        Assert.Equal(1, rig.Source.StopCalls);   // Stop still runs (idempotent cleanup)
    }

    [Fact]
    public void Safety_cap_stops_the_host()
    {
        using var rig = new Rig();
        rig.Start(capSeconds: 1);
        rig.AwaitExit();
        Assert.Equal(HostStopReason.SafetyCap, rig.Session.HostStopReason);
        Assert.Equal(1, rig.Source.StopCalls);
    }

    [Fact]
    public void Etw_start_failure_is_reported_with_the_win32_error()
    {
        using var rig = new Rig();
        rig.Source.StartResult = 5;
        rig.Start();
        Assert.Equal(1, rig.AwaitExit());
        var e = Assert.Single(rig.Errors);
        Assert.Equal(FrameCaptureErrorCode.EtwFailed, e.Code);
        Assert.Contains("error 5", e.Detail);
        Assert.Equal(1, rig.Source.StopCalls);   // a half-started session is still stopped
    }

    [Fact]
    public void Unelevated_host_refuses_before_creating_a_session()
    {
        using var rig = new Rig();
        rig.Elevated = false;
        rig.Start();
        Assert.Equal(1, rig.AwaitExit());
        Assert.Equal(FrameCaptureErrorCode.StartFailed, Assert.Single(rig.Errors).Code);
        Assert.Contains("not elevated", rig.Errors[0].Detail);
        Assert.Equal(0, rig.Source.StartCalls);
    }

    [Fact]
    public void Host_refuses_a_pipe_owned_by_another_process()
    {
        using var rig = new Rig();
        rig.ServerPidOverride = 999_999;
        rig.Start();
        Assert.Equal(1, rig.AwaitExit());
        Assert.Contains("pipe owner", Assert.Single(rig.Errors).Detail);
        Assert.Equal(0, rig.Source.StartCalls);
    }

    [Fact]
    public void Real_pipe_server_pid_is_this_process()
    {
        using var pipe = NativeFrameCaptureSession.CreatePipe(FrameCaptureRequestJson.NewPipeName());
        Assert.Equal(Environment.ProcessId, FrameCaptureHostEnvironment.Production().PipeServerProcessId(pipe));
    }

    [Fact]
    public void Host_that_exits_before_connecting_is_StartFailed()
    {
        using var rig = new Rig();
        rig.Launcher.ExitWithoutRunning = FrameCaptureHost.ExitBadRequest;
        rig.Start();
        rig.AwaitExit();
        var e = Assert.Single(rig.Errors);
        Assert.Equal(FrameCaptureErrorCode.StartFailed, e.Code);
        Assert.Contains("refused", e.Detail);
        Assert.Empty(Directory.GetFiles(rig.Folder));
    }

    [Fact]
    public void Stop_requested_before_the_host_connects_is_delivered_after_connecting()
    {
        using var rig = new Rig(gateConnect: true);
        rig.Start();
        rig.Session.RequestStop();
        rig.ConnectGate.SetResult();
        rig.AwaitExit();
        Assert.Equal(HostStopReason.StopRequested, rig.Session.HostStopReason);
    }

    [Fact]
    public async Task Host_rejects_a_bad_request_file_without_connecting()
    {
        var host = new FrameCaptureHost(new FrameCaptureHostEnvironment
        {
            IsElevated = () => true,
            ConnectPipe = (_, _, _) => throw new InvalidOperationException("must not connect"),
            PipeServerProcessId = _ => null,
            CreateEventSource = () => throw new InvalidOperationException("must not start"),
            WaitForProcessExit = (_, _) => Task.CompletedTask,
            IsProcessAlive = _ => true,
            FindProcessesByExe = _ => Array.Empty<int>()
        });
        Assert.Equal(FrameCaptureHost.ExitBadRequest, await host.ExecuteAsync(@"C:\Windows\Temp\x.cap.json"));
        Assert.Equal(FrameCaptureHost.ExitBadRequest, await host.ExecuteAsync(null));
    }
}
