using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Visor.Services.Elevation;

namespace Visor.Services.FrameCapture;

/// <summary>
/// Unelevated side of one capture. Creates a one-instance named pipe <c>VisorFrames-&lt;guid&gt;</c>
/// (DACL: current user + Administrators, network denied), writes the request file into the
/// per-user exchange folder, starts <c>Visor.exe --frame-capture &lt;request&gt;</c> with the "runas"
/// verb (the one UAC prompt of a capture), and reads frames / statuses from the pipe on a
/// background task. <see cref="RequestStop"/> writes the stop byte — no second prompt. Disposing
/// closes the pipe, which makes the host stop by itself.
/// </summary>
public sealed class NativeFrameCaptureSession : IFrameCaptureSession
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DefaultNoOutputTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HostExitGrace = TimeSpan.FromSeconds(2);
    private const int ErrorCancelled = 1223;

    private readonly IProcessLauncher _launcher;
    private readonly string _exchangeDirectory;
    private readonly string _exePath;
    private readonly Func<string, IReadOnlyCollection<int>> _findPids;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _noOutputTimeout;
    private readonly object _gate = new();

    private NamedPipeServerStream? _pipe;
    private IProcessHandle? _process;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _noOutputTimer;
    private string? _requestPath;
    private bool _running;
    private bool _connected;
    private bool _stopRequested;
    private bool _stopPending;
    private bool _finished;
    private bool _disposed;
    private long _frames;
    private FrameCaptureErrorCode _pendingError;
    private string _pendingDetail = "";

    /// <summary>Production: real "runas" launch of this very exe, default exchange folder.</summary>
    public NativeFrameCaptureSession()
        : this(new ShellProcessLauncher(), ElevationExchangeDirectory.Default, Environment.ProcessPath ?? "", null) { }

    internal NativeFrameCaptureSession(IProcessLauncher launcher, string exchangeDirectory, string exePath,
        Func<string, IReadOnlyCollection<int>>? findPids, TimeSpan? connectTimeout = null, TimeSpan? noOutputTimeout = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _exchangeDirectory = exchangeDirectory ?? throw new ArgumentNullException(nameof(exchangeDirectory));
        _exePath = exePath ?? "";
        _findPids = findPids ?? DefaultFindPids;
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
        _noOutputTimeout = noOutputTimeout ?? DefaultNoOutputTimeout;
    }

    /// <summary>True when this process is the real Visor.exe (not the dotnet host / a test runner) — otherwise no helper can be elevated.</summary>
    public static bool IsSupported => ElevationExchangeDirectory.IsHelperExeSupported(Environment.ProcessPath);

    public bool IsRunning { get { lock (_gate) return _running; } }
    public string? PipeName { get; private set; }
    public string? RequestPath { get { lock (_gate) return _requestPath; } }
    public long FramesReceived => Interlocked.Read(ref _frames);
    /// <summary>Why the host said it stopped (None until its Stopped message arrives).</summary>
    public HostStopReason HostStopReason { get; private set; }

    public event Action<FrameRecord>? Frame;
    public event Action<FrameCaptureErrorCode, string>? Error;
    public event Action<int?>? Exited;

    public void Start(FrameCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsValid) throw new ArgumentException("Invalid capture target.", nameof(target));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running || _finished) throw new InvalidOperationException("A session captures once.");
            _running = true;
            _cts = new CancellationTokenSource();
        }

        string id = Guid.NewGuid().ToString("N");
        string pipeName = FrameCaptureRequestJson.NewPipeName();
        string requestPath = Path.Combine(_exchangeDirectory, id + FrameCaptureRequestJson.FileSuffix);
        PipeName = pipeName;

        var request = new FrameCaptureRequest
        {
            RequestId = id,
            PipeName = pipeName,
            ParentProcessId = Environment.ProcessId,
            ProcessIds = ResolvePids(target),
            ExeName = target.ExeName,
            SafetyCapSeconds = Math.Clamp(target.SafetyCapSeconds, 1, FrameCaptureRequestJson.MaxSafetyCapSeconds)
        };
        string? invalid = FrameCaptureRequestJson.Validate(request);
        if (invalid != null)
        {
            Finish(FrameCaptureErrorCode.StartFailed, "invalid capture request: " + invalid);
            return;
        }

        NamedPipeServerStream pipe;
        try
        {
            pipe = CreatePipe(pipeName);
            if (!ElevationExchangeDirectory.Prepare(_exchangeDirectory))
            {
                pipe.Dispose();
                Finish(FrameCaptureErrorCode.StartFailed, "exchange folder unavailable");
                return;
            }
            using (var fs = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                var bytes = new UTF8Encoding(false).GetBytes(FrameCaptureRequestJson.Serialize(request));
                fs.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Warn(ex, "Frame capture: pipe / request");
            TryDelete(requestPath);
            Finish(FrameCaptureErrorCode.StartFailed, ex.Message);
            return;
        }
        lock (_gate)
        {
            _pipe = pipe;
            _requestPath = requestPath;
        }

        var psi = new ProcessStartInfo(_exePath)
        {
            UseShellExecute = true,           // required for the "runas" verb
            Verb = "runas",                   // the one UAC prompt — only on an explicit user action
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = FrameCaptureHost.CliSwitch + " \"" + requestPath + "\"",
            WorkingDirectory = Path.GetDirectoryName(_exePath) is { Length: > 0 } d ? d : Environment.SystemDirectory
        };

        IProcessHandle handle;
        try
        {
            AppLog.Info($"Frame capture: starting elevated host for {target.Label} ({request.ProcessIds.Count} pid(s))");
            handle = _launcher.Launch(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Finish(FrameCaptureErrorCode.UacDeclined, "UAC prompt declined");
            return;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error(ex, "Frame capture: host launch");
            Finish(FrameCaptureErrorCode.StartFailed, ex.Message);
            return;
        }

        CancellationToken token;
        lock (_gate)
        {
            if (_finished) { handle.Dispose(); return; }   // disposed while the prompt was up
            _process = handle;
            token = _cts!.Token;
        }
        handle.Exited += OnProcessExited;
        _ = Task.Run(() => ReadLoopAsync(pipe, token));
        if (handle.HasExited) OnProcessExited();
    }

    private List<int> ResolvePids(FrameCaptureTarget target)
    {
        var set = new HashSet<int>(target.ProcessIds.Where(p => p > 4));
        if (target.ExeName != null)
        {
            try
            {
                foreach (int pid in _findPids(target.ExeName))
                    if (pid > 4) set.Add(pid);
            }
            catch (Exception ex) { AppLog.Warn(ex, "Frame capture: pid lookup"); }
        }
        set.Remove(Environment.ProcessId);
        return set.OrderBy(p => p).Take(FrameCaptureRequestJson.MaxProcessIds).ToList();
    }

    private async Task ReadLoopAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var decoder = new FrameWireDecoder();
        bool stoppedSeen = false;
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connect.CancelAfter(_connectTimeout);
                try { await pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Finish(FrameCaptureErrorCode.StartFailed, $"capture helper did not connect within {_connectTimeout.TotalSeconds:F0} s");
                    return;
                }
            }

            bool sendStop;
            string? request;
            lock (_gate)
            {
                _connected = true;
                sendStop = _stopPending;
                request = _requestPath;
            }
            if (request != null) TryDelete(request);   // the host has read it before connecting
            if (sendStop) SendStopByte(pipe);
            ArmNoOutputTimer();

            var buffer = new byte[4096];
            var messages = new List<WireMessage>();
            while (true)
            {
                int n = await pipe.ReadAsync(buffer, token).ConfigureAwait(false);
                if (n == 0) break;
                messages.Clear();
                bool ok = decoder.Feed(buffer.AsSpan(0, n), messages);
                foreach (var m in messages)
                    if (Dispatch(m)) stoppedSeen = true;
                if (!ok)
                {
                    SetPending(FrameCaptureErrorCode.StreamUnreadable, decoder.FaultReason);
                    break;
                }
            }
            if (!stoppedSeen && decoder.PendingBytes > 0)
                AppLog.Warn($"Frame capture: stream ended mid-message ({decoder.PendingBytes} bytes)");
        }
        catch (OperationCanceledException) { /* disposed */ }
        catch (ObjectDisposedException) { /* disposed */ }
        catch (IOException ex) { AppLog.Info("Frame capture: pipe closed (" + ex.Message + ")"); }

        // The host closes the pipe a moment before its process ends; give it that moment so the exit code is known.
        if (!token.IsCancellationRequested) await WaitForHostExitAsync(HostExitGrace).ConfigureAwait(false);

        FrameCaptureErrorCode code;
        string detail;
        lock (_gate) { code = _pendingError; detail = _pendingDetail; }
        Finish(code, detail);
    }

    private async Task WaitForHostExitAsync(TimeSpan max)
    {
        IProcessHandle? process;
        lock (_gate) process = _process;
        if (process == null) return;
        var sw = Stopwatch.StartNew();
        while (!process.HasExited && sw.Elapsed < max)
            await Task.Delay(20).ConfigureAwait(false);
    }

    /// <summary>Handles one message; returns true for the host's final Stopped status.</summary>
    private bool Dispatch(in WireMessage m)
    {
        if (m.IsFrame)
        {
            if (Interlocked.Increment(ref _frames) == 1) DisarmNoOutputTimer();
            try { Frame?.Invoke(m.Frame); }
            catch (Exception ex) { AppLog.Warn(ex, "Frame capture: frame handler"); }
            return false;
        }
        var s = m.Status;
        switch (s.Kind)
        {
            case HostStatusKind.Started:
                AppLog.Info($"Frame capture: ETW session running ({s.Detail} target process(es))");
                return false;
            case HostStatusKind.Error:
                var (code, detail) = MapHostError(s.Error, s.Detail);
                SetPending(code, detail);
                return false;
            case HostStatusKind.TargetExited:
                AppLog.Info("Frame capture: target exited");
                return false;
            case HostStatusKind.Stopped:
                HostStopReason = s.StopReason;
                AppLog.Info("Frame capture: host stopped (" + s.StopReason + ")");
                return true;
            default:
                return false;
        }
    }

    /// <summary>Host error → session error code + raw detail (pure; tests cover the table).</summary>
    internal static (FrameCaptureErrorCode Code, string Detail) MapHostError(HostError error, int win32) => error switch
    {
        HostError.EtwStartFailed => (FrameCaptureErrorCode.EtwFailed, win32 switch
        {
            5 => "ETW session refused: access denied (error 5)",
            1450 => "ETW session limit reached (error 1450) — close other tracing tools",
            _ => $"ETW session could not start (error {win32})"
        }),
        HostError.ProcessNotFound => (FrameCaptureErrorCode.ProcessNotFound, "target process not running"),
        HostError.NotElevated => (FrameCaptureErrorCode.StartFailed, "capture helper is not elevated"),
        HostError.BadRequest => (FrameCaptureErrorCode.StartFailed, "capture helper refused the request"),
        HostError.PipeRejected => (FrameCaptureErrorCode.StartFailed, "capture helper rejected the pipe owner"),
        _ => (FrameCaptureErrorCode.StartFailed, $"capture helper failed (error {win32})")
    };

    private void SetPending(FrameCaptureErrorCode code, string detail)
    {
        lock (_gate)
        {
            if (_pendingError != FrameCaptureErrorCode.None) return;   // the first reason wins
            _pendingError = code;
            _pendingDetail = detail;
        }
    }

    private void ArmNoOutputTimer()
    {
        lock (_gate)
        {
            if (_finished || Interlocked.Read(ref _frames) > 0) return;
            _noOutputTimer = new System.Threading.Timer(_ => OnNoOutput(), null, _noOutputTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void DisarmNoOutputTimer()
    {
        System.Threading.Timer? t;
        lock (_gate) { t = _noOutputTimer; _noOutputTimer = null; }
        t?.Dispose();
    }

    private void OnNoOutput()
    {
        lock (_gate)
        {
            if (_finished || !_running || Interlocked.Read(ref _frames) > 0) return;
        }
        // Advisory: the game may be loading or minimized. The facade turns it into the final error only if no frame ever arrives.
        try
        {
            Error?.Invoke(FrameCaptureErrorCode.NoOutput,
                $"no frames for {_noOutputTimeout.TotalSeconds:F0} s — the game may not be presenting (paused / minimized) or uses an API the capture does not see");
        }
        catch (Exception ex) { AppLog.Warn(ex, "Frame capture: error handler"); }
    }

    public void RequestStop()
    {
        NamedPipeServerStream? pipe;
        lock (_gate)
        {
            if (!_running || _stopRequested) return;
            _stopRequested = true;
            if (!_connected) { _stopPending = true; return; }
            pipe = _pipe;
        }
        if (pipe != null) SendStopByte(pipe);
    }

    private static void SendStopByte(NamedPipeServerStream pipe)
    {
        try
        {
            pipe.Write(new[] { FrameWire.StopByte }, 0, 1);
            pipe.Flush();
            AppLog.Info("Frame capture: stop requested");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The host is already gone (pipe broken) — the read loop finishes the session.
            AppLog.Info("Frame capture: stop byte not delivered (" + ex.Message + ")");
        }
    }

    private void OnProcessExited()
    {
        bool connected;
        int? code;
        lock (_gate)
        {
            if (_finished) return;
            connected = _connected;
            code = _process?.ExitCode;
        }
        // Connected: the read loop sees EOF and finishes (after draining). Not connected: nothing will ever connect.
        if (connected) return;
        Finish(FrameCaptureErrorCode.StartFailed, code == FrameCaptureHost.ExitBadRequest
            ? "capture helper refused the request"
            : $"capture helper exited (code {code?.ToString() ?? "?"}) before connecting");
    }

    private void Finish(FrameCaptureErrorCode error, string detail)
    {
        NamedPipeServerStream? pipe;
        IProcessHandle? process;
        CancellationTokenSource? cts;
        System.Threading.Timer? timer;
        string? request;
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
            _running = false;
            pipe = _pipe; _pipe = null;
            process = _process; _process = null;
            cts = _cts; _cts = null;
            timer = _noOutputTimer; _noOutputTimer = null;
            request = _requestPath;
        }
        timer?.Dispose();
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        cts?.Dispose();
        try { pipe?.Dispose(); } catch (Exception) { /* closing */ }
        int? exitCode = null;
        if (process != null)
        {
            process.Exited -= OnProcessExited;
            exitCode = process.ExitCode;
            process.Dispose();
        }
        if (request != null) TryDelete(request);

        if (error != FrameCaptureErrorCode.None)
        {
            AppLog.Warn($"Frame capture: {error} — {detail}");
            try { Error?.Invoke(error, detail); } catch (Exception ex) { AppLog.Warn(ex, "Frame capture: error handler"); }
        }
        else AppLog.Info($"Frame capture ended ({FramesReceived} frames, host exit {exitCode?.ToString() ?? "?"})");
        try { Exited?.Invoke(exitCode); } catch (Exception ex) { AppLog.Warn(ex, "Frame capture: exited handler"); }
    }

    /// <summary>One-instance pipe: current user full control, Administrators read/write, network logons denied.</summary>
    internal static NamedPipeServerStream CreatePipe(string name)
    {
        var sec = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No user SID.");
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        sec.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, sec);
    }

    private static IReadOnlyCollection<int> DefaultFindPids(string exe)
    {
        var set = new HashSet<int>();
        ProcessSnapshot.PidsOf(ProcessNames.Key(exe), set);
        return set;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* stale-file cleanup removes it later */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Closing the pipe is the stop signal: the host sees a broken pipe and ends its ETW session.
        Finish(FrameCaptureErrorCode.None, "");
    }
}
