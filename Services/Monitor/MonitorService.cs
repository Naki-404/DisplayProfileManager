using Visor.Services.FrameCapture;

namespace Visor.Services;

public enum MonitorState
{
    Idle,
    /// <summary>UAC prompt shown / capture helper starting; waiting for the first frame.</summary>
    Starting,
    Capturing,
    /// <summary>Stop byte sent; waiting for the capture helper to end its session.</summary>
    Stopping,
    /// <summary>Last capture ended with an error (<see cref="MonitorService.LastError"/>); cleared by the next start.</summary>
    Error
}

public enum MonitorErrorCode
{
    None,
    /// <summary>The user declined the elevation prompt.</summary>
    UacDeclined,
    /// <summary>The target exe is not running (checked before the UAC prompt, and again by the helper).</summary>
    ProcessNotFound,
    /// <summary>The ETW session could not start (access denied, session limit, another tool holding it).</summary>
    EtwFailed,
    /// <summary>The capture helper could not be launched or refused to run.</summary>
    StartFailed,
    /// <summary>Capturing, but the target never presented a frame.</summary>
    NoOutput,
    /// <summary>The capture helper's data stream was malformed.</summary>
    OutputUnreadable,
    /// <summary>The stop request could not be delivered; the helper still ends with the game, with Visor, or at the safety cap.</summary>
    StopFailed,
    /// <summary>Another capture is already running.</summary>
    Busy,
    /// <summary>The exe name is not a plain file name.</summary>
    InvalidTarget,
    /// <summary>No elevated helper can be started here (Visor runs under the dotnet host / a test runner, not as Visor.exe).</summary>
    ElevationUnavailable
}

public readonly record struct MonitorStartResult(bool Started, MonitorErrorCode Error, string Detail)
{
    public static MonitorStartResult Ok => new(true, MonitorErrorCode.None, "");
    public static MonitorStartResult Fail(MonitorErrorCode code, string detail) => new(false, code, detail);
}

/// <summary>
/// Facade over the built-in frame capture + stats: one capture at a time, never auto-started,
/// state machine Idle → Starting(UAC) → Capturing → Stopping → Idle | Error. Events fire on worker
/// threads; UI code marshals. Per-second summaries (with the limiter estimate from
/// <see cref="BoundEstimator"/>) go to <see cref="SummaryUpdated"/> and, as named
/// <c>MetricSample</c>s, to every attached <see cref="IMetricSink"/>.
/// </summary>
public sealed class MonitorService : IDisposable
{
    public const int SummaryIntervalMs = 1000;

    private readonly Func<IFrameCaptureSession> _sessionFactory;
    private readonly Func<string, bool> _isProcessRunning;
    private readonly Func<bool> _captureSupported;
    private readonly IBoundSignals? _signals;
    private readonly object _gate = new();
    private readonly List<IMetricSink> _sinks = new();
    private readonly Queue<double> _gpuSamples = new();
    private readonly System.Threading.Timer _summaryTimer;

    private IFrameCaptureSession? _session;
    private IDisposable? _signalToken;
    private MonitorState _state = MonitorState.Idle;
    private MonitorErrorCode _lastError = MonitorErrorCode.None;
    private string _lastErrorDetail = "";
    private MonitorErrorCode _pendingError = MonitorErrorCode.None;
    private string _pendingDetail = "";
    private string _target = "";
    private bool _disposed;

    /// <summary>Production wiring: built-in elevated capture, process check via the kernel PID list, NvAPI GPU load + display refresh.</summary>
    public MonitorService(double windowSeconds = 10)
        : this(() => new NativeFrameCaptureSession(), null, windowSeconds, new SystemBoundSignals(), () => NativeFrameCaptureSession.IsSupported) { }

    /// <param name="sessionFactory">One new session per capture.</param>
    /// <param name="isProcessRunning">Pre-flight "is the game running" (default: kernel PID list).</param>
    /// <param name="windowSeconds">Stats window.</param>
    /// <param name="boundSignals">GPU load / refresh for the limiter estimate; null = estimate stays Unknown.</param>
    /// <param name="captureSupported">False → <see cref="MonitorErrorCode.ElevationUnavailable"/> without any prompt. Default: true.</param>
    public MonitorService(Func<IFrameCaptureSession> sessionFactory, Func<string, bool>? isProcessRunning = null,
        double windowSeconds = 10, IBoundSignals? boundSignals = null, Func<bool>? captureSupported = null)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _isProcessRunning = isProcessRunning ?? (exe => ProcessSnapshot.IsRunning(ProcessNames.Key(exe)));
        _captureSupported = captureSupported ?? (() => true);
        _signals = boundSignals;
        Stats = new FrameStats(windowSeconds);
        _summaryTimer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public MonitorState State { get { lock (_gate) return _state; } }
    public MonitorErrorCode LastError { get { lock (_gate) return _lastError; } }
    public string LastErrorDetail { get { lock (_gate) return _lastErrorDetail; } }
    /// <summary>Exe name of the current / last capture ("game.exe").</summary>
    public string Target { get { lock (_gate) return _target; } }
    public bool IsCapturing { get { lock (_gate) return _state is MonitorState.Capturing or MonitorState.Starting or MonitorState.Stopping; } }

    public FrameStats Stats { get; }
    public FrameStatsSummary LastSummary { get; private set; } = FrameStatsSummary.Empty;

    /// <summary>When true (default) the target exe must already be running; otherwise the UAC prompt is skipped and <see cref="MonitorErrorCode.ProcessNotFound"/> is returned.</summary>
    public bool RequireRunningTarget { get; set; } = true;

    /// <summary>True when a capture can be started at all (Visor runs as its own exe, so the helper can be elevated).</summary>
    public bool IsCaptureSupported
    {
        get
        {
            try { return _captureSupported(); }
            catch (Exception) { return false; }
        }
    }

    public event Action<MonitorState, MonitorErrorCode>? StateChanged;
    public event Action<FrameStatsSummary>? SummaryUpdated;
    public event Action<FrameRecord>? FrameReceived;

    public void AttachSink(IMetricSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate) if (!_sinks.Contains(sink)) _sinks.Add(sink);
    }

    public void DetachSink(IMetricSink sink)
    {
        lock (_gate) _sinks.Remove(sink);
    }

    /// <summary>
    /// Explicit user action only. Synchronously launches the capture helper; the UAC prompt appears
    /// inside this call. Returns a failure result instead of throwing for every expected case.
    /// </summary>
    public MonitorStartResult StartCapture(string exeName)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is MonitorState.Starting or MonitorState.Capturing or MonitorState.Stopping)
                return MonitorStartResult.Fail(MonitorErrorCode.Busy, $"already capturing {_target}");
        }

        var exe = ProcessNames.ToExeName(exeName);
        if (!ProcessNames.IsPlainExeName(exe))
            return FailNow(MonitorErrorCode.InvalidTarget, $"'{exeName}' is not a plain exe name", exe);

        if (!IsCaptureSupported)
            return FailNow(MonitorErrorCode.ElevationUnavailable, "built-in capture needs Visor.exe (dotnet host / test runner)", exe);

        if (RequireRunningTarget)
        {
            bool running;
            try { running = _isProcessRunning(exe); }
            catch (Exception ex) { AppLog.Warn(ex, "process check"); running = true; }   // unknown ≠ not running
            if (!running)
                return FailNow(MonitorErrorCode.ProcessNotFound, $"{exe} is not running", exe);
        }

        IFrameCaptureSession session;
        try { session = _sessionFactory(); }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Frame capture session");
            return FailNow(MonitorErrorCode.StartFailed, ex.Message, exe);
        }

        lock (_gate)
        {
            _session = session;
            _pendingError = MonitorErrorCode.None;
            _pendingDetail = "";
            _gpuSamples.Clear();
        }
        Stats.Clear();
        LastSummary = FrameStatsSummary.Empty;
        session.Frame += OnFrame;
        session.Error += OnSessionError;
        session.Exited += OnSessionExited;
        SetState(MonitorState.Starting, MonitorErrorCode.None, "", target: exe);

        try
        {
            session.Start(FrameCaptureTarget.ForExe(exe));
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Frame capture start");
            DetachSession(session);
            return FailNow(MonitorErrorCode.StartFailed, ex.Message);
        }

        lock (_gate)
        {
            // Start() may have failed synchronously (UAC declined → Error + Exited already raised).
            if (_state == MonitorState.Error)
                return MonitorStartResult.Fail(_lastError, _lastErrorDetail);
            if (_state == MonitorState.Idle)
                return MonitorStartResult.Fail(MonitorErrorCode.NoOutput, "capture ended immediately");
        }
        BeginSignals();
        _summaryTimer.Change(SummaryIntervalMs, SummaryIntervalMs);
        return MonitorStartResult.Ok;
    }

    /// <summary>Explicit user action. Sends the stop byte — no second UAC prompt.</summary>
    public void StopCapture()
    {
        IFrameCaptureSession? session;
        lock (_gate)
        {
            if (_state is not (MonitorState.Starting or MonitorState.Capturing)) return;
            session = _session;
        }
        SetState(MonitorState.Stopping, MonitorErrorCode.None, "");
        try { session?.RequestStop(); }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Frame capture stop");
            OnSessionError(FrameCaptureErrorCode.StopFailed, ex.Message);
        }
    }

    /// <summary>Clears an Error state back to Idle (the UI's "dismiss").</summary>
    public void ClearError()
    {
        lock (_gate) if (_state != MonitorState.Error) return;
        SetState(MonitorState.Idle, MonitorErrorCode.None, "");
    }

    /// <summary>Produces a summary now (the timer calls this every second while capturing).</summary>
    public FrameStatsSummary Tick()
    {
        IMetricSink[] sinks;
        lock (_gate)
        {
            if (_state is not (MonitorState.Capturing or MonitorState.Stopping)) return LastSummary;
            sinks = _sinks.ToArray();
        }
        var summary = WithBound(Stats.Snapshot(), SampleGpu(), SafeRefresh());
        LastSummary = summary;
        try { SummaryUpdated?.Invoke(summary); } catch (Exception ex) { AppLog.Warn(ex, "Monitor summary handler"); }
        if (summary.FrameCount > 0 && sinks.Length > 0)
        {
            var samples = summary.ToMetricSamples();
            foreach (var s in sinks)
            {
                try
                {
                    foreach (var sample in samples) s.OnSample(sample);
                }
                catch (Exception ex) { AppLog.Warn(ex, "Metric sink"); }
            }
        }
        return summary;
    }

    /// <summary>Adds the limiter estimate to a stats snapshot (pure).</summary>
    internal static FrameStatsSummary WithBound(FrameStatsSummary summary, double? avgGpuPercent, double? refreshHz)
    {
        if (summary.FrameCount == 0) return summary;
        var (bound, evidence) = BoundEstimator.Classify(new BoundInputs(
            summary.FrameCount, summary.AvgFps, summary.FrameTimeCv, avgGpuPercent, refreshHz));
        return summary with { Bound = bound, BoundEvidence = evidence };
    }

    /// <summary>Reads the GPU load once per tick and returns the average over the stats window (null when never available).</summary>
    private double? SampleGpu()
    {
        double? now = null;
        try { now = _signals?.GpuUtilizationPercent(); }
        catch (Exception ex) { AppLog.Warn(ex, "Monitor: GPU load"); }
        lock (_gate)
        {
            if (now is double v && double.IsFinite(v))
            {
                _gpuSamples.Enqueue(v);
                int max = Math.Max(1, (int)Math.Ceiling(Stats.WindowSeconds * 1000 / SummaryIntervalMs));
                while (_gpuSamples.Count > max) _gpuSamples.Dequeue();
            }
            return _gpuSamples.Count > 0 ? _gpuSamples.Average() : null;
        }
    }

    private double? SafeRefresh()
    {
        try { return _signals?.DisplayRefreshHz(); }
        catch (Exception) { return null; }
    }

    private void BeginSignals()
    {
        if (_signals == null) return;
        IDisposable? token = null;
        try { token = _signals.BeginCapture(); }
        catch (Exception ex) { AppLog.Warn(ex, "Monitor: bound signals"); }
        IDisposable? old;
        lock (_gate) { old = _signalToken; _signalToken = token; }
        old?.Dispose();
    }

    private void EndSignals()
    {
        IDisposable? token;
        lock (_gate) { token = _signalToken; _signalToken = null; }
        try { token?.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Monitor: bound signals end"); }
    }

    private void OnFrame(FrameRecord frame)
    {
        Stats.Add(frame);
        bool first;
        lock (_gate) first = _state == MonitorState.Starting;
        if (first) SetState(MonitorState.Capturing, MonitorErrorCode.None, "");
        try { FrameReceived?.Invoke(frame); } catch (Exception ex) { AppLog.Warn(ex, "Monitor frame handler"); }
    }

    private void OnSessionError(FrameCaptureErrorCode code, string detail)
    {
        var mapped = Map(code);
        if (mapped == MonitorErrorCode.StopFailed)
        {
            // Not fatal: the capture is still running. Go back to Capturing and surface the detail.
            bool wasStopping;
            lock (_gate)
            {
                _lastError = mapped;
                _lastErrorDetail = detail;
                wasStopping = _state == MonitorState.Stopping;
            }
            if (wasStopping) SetState(MonitorState.Capturing, mapped, detail, keepError: true);
            return;
        }
        // NoOutput is advisory while the helper runs (game not presenting yet); every code becomes
        // the final reason only if the session then ends without data.
        lock (_gate) { _pendingError = mapped; _pendingDetail = detail; }
    }

    private void OnSessionExited(int? exitCode)
    {
        IFrameCaptureSession? session;
        MonitorErrorCode pending;
        string pendingDetail;
        bool hadFrames = Stats.TotalAdded > 0;
        lock (_gate)
        {
            session = _session;
            pending = _pendingError;
            pendingDetail = _pendingDetail;
            _pendingError = MonitorErrorCode.None;
            _pendingDetail = "";
        }
        _summaryTimer.Change(Timeout.Infinite, Timeout.Infinite);
        EndSignals();
        if (session != null) DetachSession(session);
        if (pending != MonitorErrorCode.None && !(pending == MonitorErrorCode.NoOutput && hadFrames))
            SetState(MonitorState.Error, pending, pendingDetail);
        else if (!hadFrames && pending == MonitorErrorCode.None && State == MonitorState.Starting)
            SetState(MonitorState.Error, MonitorErrorCode.NoOutput, $"capture ended (helper exit {exitCode?.ToString() ?? "?"}) without frames");
        else
            SetState(MonitorState.Idle, MonitorErrorCode.None, "");
    }

    internal static MonitorErrorCode Map(FrameCaptureErrorCode code) => code switch
    {
        FrameCaptureErrorCode.UacDeclined => MonitorErrorCode.UacDeclined,
        FrameCaptureErrorCode.StartFailed => MonitorErrorCode.StartFailed,
        FrameCaptureErrorCode.EtwFailed => MonitorErrorCode.EtwFailed,
        FrameCaptureErrorCode.ProcessNotFound => MonitorErrorCode.ProcessNotFound,
        FrameCaptureErrorCode.NoOutput => MonitorErrorCode.NoOutput,
        FrameCaptureErrorCode.StreamUnreadable => MonitorErrorCode.OutputUnreadable,
        FrameCaptureErrorCode.ElevationUnavailable => MonitorErrorCode.ElevationUnavailable,
        FrameCaptureErrorCode.StopFailed => MonitorErrorCode.StopFailed,
        _ => MonitorErrorCode.None
    };

    private MonitorStartResult FailNow(MonitorErrorCode code, string detail, string? target = null)
    {
        SetState(MonitorState.Error, code, detail, target);
        return MonitorStartResult.Fail(code, detail);
    }

    private void DetachSession(IFrameCaptureSession session)
    {
        session.Frame -= OnFrame;
        session.Error -= OnSessionError;
        session.Exited -= OnSessionExited;
        lock (_gate) if (ReferenceEquals(_session, session)) _session = null;
        try { session.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Frame capture session dispose"); }
    }

    private void SetState(MonitorState state, MonitorErrorCode error, string detail, string? target = null, bool keepError = false)
    {
        lock (_gate)
        {
            _state = state;
            if (!keepError)
            {
                _lastError = error;
                _lastErrorDetail = detail;
            }
            if (target != null) _target = target;
        }
        if (error != MonitorErrorCode.None) AppLog.Warn($"Monitor: {state} ({error}) {detail}");
        else AppLog.Info($"Monitor: {state}");
        try { StateChanged?.Invoke(state, error); } catch (Exception ex) { AppLog.Warn(ex, "Monitor state handler"); }
    }

    public void Dispose()
    {
        IFrameCaptureSession? session;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            session = _session;
            _session = null;
        }
        _summaryTimer.Dispose();
        EndSignals();
        if (session != null)
        {
            // Visor is closing: disposing the session closes the pipe, and the elevated helper stops
            // its ETW session on the broken pipe (and on Visor's exit). No UAC prompt from Dispose, ever.
            session.Frame -= OnFrame;
            session.Error -= OnSessionError;
            session.Exited -= OnSessionExited;
            try { session.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Frame capture session dispose"); }
        }
    }
}
