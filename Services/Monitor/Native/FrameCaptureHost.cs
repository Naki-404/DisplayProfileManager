using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using Visor.Services.Elevation;

namespace Visor.Services.FrameCapture;

/// <summary>Everything the host touches outside its own memory (seams for tests; <see cref="Production"/> for Visor.exe).</summary>
public sealed class FrameCaptureHostEnvironment
{
    public required Func<bool> IsElevated { get; init; }
    /// <summary>Connects to the pipe Visor created (client side, full duplex, overlapped).</summary>
    public required Func<string, TimeSpan, CancellationToken, Task<Stream>> ConnectPipe { get; init; }
    /// <summary>PID of the process that owns the pipe server end, or null when unknown.</summary>
    public required Func<Stream, int?> PipeServerProcessId { get; init; }
    public required Func<IPresentEventSource> CreateEventSource { get; init; }
    /// <summary>Completes when the process exits (or cannot be found); cancelled with the token.</summary>
    public required Func<int, CancellationToken, Task> WaitForProcessExit { get; init; }
    public required Func<int, bool> IsProcessAlive { get; init; }
    /// <summary>PIDs of running processes with this exe name ("game.exe").</summary>
    public required Func<string, IReadOnlyCollection<int>> FindProcessesByExe { get; init; }
    public int OwnProcessId { get; init; } = Environment.ProcessId;
    public TimeSpan TargetPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Real environment. Logs nothing and writes no file (the host has no business doing either).</summary>
    public static FrameCaptureHostEnvironment Production() => new()
    {
        IsElevated = ElevationEnvironment.IsProcessElevated,
        ConnectPipe = async (name, timeout, ct) =>
        {
            // Anonymous: the pipe server (unelevated Visor) can never impersonate this elevated client.
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Anonymous);
            try
            {
                await client.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        },
        PipeServerProcessId = stream =>
            stream is PipeStream ps && GetNamedPipeServerProcessId(ps.SafePipeHandle, out uint pid) ? (int)pid : null,
        CreateEventSource = () => new EtwSession(),
        WaitForProcessExit = WaitForExitAsync,
        IsProcessAlive = pid =>
        {
            Process p;
            try { p = Process.GetProcessById(pid); }
            catch (ArgumentException) { return false; }          // not in the process list
            catch (Exception) { return true; }                   // unknown ≠ exited (the other stop signals still apply)
            using (p)
            {
                // Anti-cheat may strip handle rights on the game: an unreadable state is "still running".
                try { return !p.HasExited; }
                catch (Exception) { return true; }
            }
        },
        FindProcessesByExe = exe =>
        {
            var list = new List<int>();
            foreach (var p in Process.GetProcessesByName(ProcessNames.Key(exe)))
            {
                using (p) list.Add(p.Id);
            }
            return list;
        }
    };

    private static async Task WaitForExitAsync(int pid, CancellationToken ct)
    {
        Process p;
        try { p = Process.GetProcessById(pid); }
        catch (Exception) { return; }   // already gone → "exited"
        using (p)
        {
            try { await p.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (InvalidOperationException) { /* no handle → treat as exited */ }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}

/// <summary>
/// Body of <c>Visor.exe --frame-capture &lt;id&gt;.cap.json</c> — the only elevated part of the FPS
/// monitor. Runs before any GUI, mutex, config, hook or log exists and touches none of them; it
/// reads the request, streams frames, and exits. Fixed order:
/// request path + content validated → connect to Visor's pipe (anonymous impersonation level) →
/// pipe server must be the parent Visor process → refuse unless elevated → resolve targets →
/// start the ETW session → stream until Visor's stop byte, a broken pipe, the parent's exit,
/// every target's exit, the safety cap, or the session ending — and always <c>ControlTrace STOP</c>
/// in <c>finally</c>. It writes no file, no registry value, and runs nothing.
/// Exit codes: 0 = capture ran and stopped, 1 = refused / failed, 2 = bad request (nothing reported).
/// </summary>
public sealed class FrameCaptureHost
{
    public const string CliSwitch = "--frame-capture";
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitBadRequest = 2;
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);
    private const int ErrorInternal = 1359; // ERROR_INTERNAL_ERROR

    private readonly FrameCaptureHostEnvironment _env;

    public FrameCaptureHost(FrameCaptureHostEnvironment env)
    {
        _env = env ?? throw new ArgumentNullException(nameof(env));
    }

    /// <summary>Last stop reason (diagnostics / tests).</summary>
    public HostStopReason LastStopReason { get; private set; }

    /// <summary>Production entry point (App CLI branch). Blocks until the capture ends.</summary>
    public static int Run(string requestPath)
    {
        var host = new FrameCaptureHost(FrameCaptureHostEnvironment.Production());
        // Off the WPF dispatcher thread: nothing here may need it.
        return Task.Run(() => host.ExecuteAsync(requestPath)).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(string? requestPath, CancellationToken ct = default)
    {
        var request = FrameCaptureRequestJson.Load(requestPath, out _);
        if (request == null || request.ParentProcessId == _env.OwnProcessId)
            return ExitBadRequest;

        Stream pipe;
        try { pipe = await _env.ConnectPipe(request.PipeName, ConnectTimeout, ct).ConfigureAwait(false); }
        catch (Exception) { return ExitFailed; }

        await using (pipe.ConfigureAwait(false))
        {
            var writer = new HostWriter(pipe);
            try
            {
                writer.Send(FrameWire.Hello());

                int? server;
                try { server = _env.PipeServerProcessId(pipe); }
                catch (Exception) { server = null; }
                if (server != request.ParentProcessId)
                    return await FailAsync(writer, pipe, HostError.PipeRejected).ConfigureAwait(false);

                bool elevated;
                try { elevated = _env.IsElevated(); }
                catch (Exception) { elevated = false; }
                if (!elevated)
                    return await FailAsync(writer, pipe, HostError.NotElevated).ConfigureAwait(false);

                var targets = ResolveTargets(request);
                if (targets.Count == 0)
                    return await FailAsync(writer, pipe, HostError.ProcessNotFound).ConfigureAwait(false);

                return await CaptureAsync(request, targets, pipe, writer, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await FailAsync(writer, pipe, HostError.Internal, ErrorInternal).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Request PIDs that are alive, plus every running process with the request's exe name.</summary>
    internal List<int> ResolveTargets(FrameCaptureRequest request)
    {
        var set = new HashSet<int>();
        foreach (int pid in request.ProcessIds)
        {
            bool alive;
            try { alive = _env.IsProcessAlive(pid); }
            catch (Exception) { alive = false; }
            if (alive) set.Add(pid);
        }
        if (request.ExeName != null)
        {
            try
            {
                foreach (int pid in _env.FindProcessesByExe(request.ExeName))
                    if (pid > 4 && pid != request.ParentProcessId) set.Add(pid);
            }
            catch (Exception) { /* PIDs from the request still count */ }
        }
        return set.OrderBy(p => p).Take(FrameCaptureRequestJson.MaxProcessIds).ToList();
    }

    private async Task<int> CaptureAsync(FrameCaptureRequest request, List<int> targets, Stream pipe, HostWriter writer, CancellationToken ct)
    {
        IPresentEventSource source = _env.CreateEventSource();
        try
        {
            var tracker = new PresentTracker(source.QpcFrequency, targets);
            var trackerGate = new object();
            void OnEvent(PresentEvent ev)
            {
                FrameRecord frame;
                bool emitted;
                lock (trackerGate) emitted = tracker.TryFeed(ev, out frame);
                if (emitted) writer.TrySendFrame(frame, ev.Source);
            }

            uint err;
            try { err = source.Start(new PresentEventFilter(targets), OnEvent); }
            catch (Exception) { err = ErrorInternal; }
            if (err != 0)
            {
                source.Stop();
                return await FailAsync(writer, pipe, HostError.EtwStartFailed, unchecked((int)err)).ConfigureAwait(false);
            }

            HostStopReason reason;
            try
            {
                writer.SendStatus(HostStatus.Started(targets.Count));
                reason = await WaitForStopAsync(request, targets, pipe, source, writer, ct).ConfigureAwait(false);
            }
            finally
            {
                source.Stop();   // ControlTrace STOP — on every path, exceptions included
            }

            LastStopReason = reason;
            if (reason == HostStopReason.TargetExited) writer.SendStatus(HostStatus.TargetExited());
            writer.SendStatus(HostStatus.Stopped(reason));
            await FinishAsync(writer, pipe).ConfigureAwait(false);
            return ExitOk;
        }
        finally
        {
            try { source.Dispose(); } catch (Exception) { /* Stop already ran */ }
        }
    }

    private async Task<HostStopReason> WaitForStopAsync(FrameCaptureRequest request, List<int> targets, Stream pipe,
        IPresentEventSource source, HostWriter writer, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;

        var stopRead = ReadUntilStopAsync(pipe, token);
        var parent = Guard(() => _env.WaitForProcessExit(request.ParentProcessId, token));
        var targetsGone = PollTargetsAsync(targets, token);
        var cap = Task.Delay(TimeSpan.FromSeconds(request.SafetyCapSeconds), token);
        var sessionEnded = source.Completion;
        var writeFailed = writer.Faulted;
        var external = Task.Delay(Timeout.Infinite, ct);

        var all = new[] { stopRead, parent, targetsGone, cap, sessionEnded, writeFailed, external };
        var winner = await Task.WhenAny(all).ConfigureAwait(false);
        HostStopReason reason =
            winner == stopRead ? await stopRead.ConfigureAwait(false)
            : winner == parent ? HostStopReason.ParentExited
            : winner == targetsGone ? (targetsGone.IsCompletedSuccessfully ? HostStopReason.TargetExited : HostStopReason.PipeClosed)
            : winner == cap ? HostStopReason.SafetyCap
            : winner == sessionEnded ? HostStopReason.SessionEnded
            : HostStopReason.PipeClosed;   // write failure or external cancellation

        cts.Cancel();
        foreach (var t in all) Observe(t);
        return reason;
    }

    /// <summary>Visor → host: the stop byte ends the capture; EOF / a broken pipe does too.</summary>
    private static async Task<HostStopReason> ReadUntilStopAsync(Stream pipe, CancellationToken ct)
    {
        var buffer = new byte[16];
        while (true)
        {
            int n;
            try { n = await pipe.ReadAsync(buffer, ct).ConfigureAwait(false); }
            catch (Exception) { return HostStopReason.PipeClosed; }
            if (n == 0) return HostStopReason.PipeClosed;
            if (buffer.AsSpan(0, n).IndexOf(FrameWire.StopByte) >= 0) return HostStopReason.StopRequested;
        }
    }

    private async Task PollTargetsAsync(List<int> targets, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(_env.TargetPollInterval, ct).ConfigureAwait(false);
            bool any = false;
            foreach (int pid in targets)
            {
                bool alive;
                try { alive = _env.IsProcessAlive(pid); }
                catch (Exception) { alive = false; }
                if (alive) { any = true; break; }
            }
            if (!any) return;
        }
    }

    private static Task Guard(Func<Task> start)
    {
        try { return start() ?? Task.CompletedTask; }
        catch (Exception ex) { return Task.FromException(ex); }
    }

    private static void Observe(Task t)
    {
        if (t.IsCompleted) { _ = t.Exception; return; }
        t.ContinueWith(static x => _ = x.Exception, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static async Task<int> FailAsync(HostWriter writer, Stream pipe, HostError error, int win32 = 0)
    {
        writer.SendStatus(HostStatus.Failed(error, win32));
        await FinishAsync(writer, pipe).ConfigureAwait(false);
        return ExitFailed;
    }

    /// <summary>Flushes queued messages and waits (bounded) until Visor has read them before the pipe closes.</summary>
    private static async Task FinishAsync(HostWriter writer, Stream pipe)
    {
        await writer.CompleteAsync(DrainTimeout).ConfigureAwait(false);
        if (pipe is PipeStream ps && !writer.Faulted.IsCompleted)
        {
            try
            {
                var drain = Task.Run(() => { try { ps.WaitForPipeDrain(); } catch (Exception) { } });
                await Task.WhenAny(drain, Task.Delay(DrainTimeout)).ConfigureAwait(false);
            }
            catch (Exception) { /* best effort */ }
        }
    }

    /// <summary>
    /// Single writer for the pipe: the ETW callback only enqueues (never blocks on the pipe). Frames
    /// beyond <see cref="MaxQueuedFrames"/> are dropped (Visor is not reading); statuses never are.
    /// </summary>
    internal sealed class HostWriter
    {
        public const int MaxQueuedFrames = 8192;
        private readonly Stream _pipe;
        private readonly Channel<(byte[] Data, bool IsFrame)> _queue =
            Channel.CreateUnbounded<(byte[], bool)>(new UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource _faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _loop;
        private int _queuedFrames;
        private long _dropped;

        public HostWriter(Stream pipe)
        {
            _pipe = pipe;
            _loop = Task.Run(LoopAsync);
        }

        public Task Faulted => _faulted.Task;
        public long FramesDropped => Interlocked.Read(ref _dropped);

        public void Send(byte[] message) => _queue.Writer.TryWrite((message, false));

        public void SendStatus(HostStatus status) => Send(FrameWire.EncodeStatus(status));

        public void TrySendFrame(in FrameRecord frame, PresentSource source)
        {
            if (Interlocked.Increment(ref _queuedFrames) > MaxQueuedFrames)
            {
                Interlocked.Decrement(ref _queuedFrames);
                Interlocked.Increment(ref _dropped);
                return;
            }
            if (!_queue.Writer.TryWrite((FrameWire.EncodeFrame(frame, source), true)))
                Interlocked.Decrement(ref _queuedFrames);
        }

        private async Task LoopAsync()
        {
            var batch = new byte[8192];
            try
            {
                while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    int len = 0;
                    while (len <= batch.Length - FrameWire.FrameMessageSize && _queue.Reader.TryRead(out var item))
                    {
                        item.Data.CopyTo(batch, len);
                        len += item.Data.Length;
                        if (item.IsFrame) Interlocked.Decrement(ref _queuedFrames);
                    }
                    if (len == 0) continue;
                    await _pipe.WriteAsync(batch.AsMemory(0, len)).ConfigureAwait(false);
                    await _pipe.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                _faulted.TrySetResult();
                _queue.Writer.TryComplete();
                while (_queue.Reader.TryRead(out _)) { }
            }
        }

        public async Task CompleteAsync(TimeSpan timeout)
        {
            _queue.Writer.TryComplete();
            await Task.WhenAny(_loop, Task.Delay(timeout)).ConfigureAwait(false);
        }
    }
}
