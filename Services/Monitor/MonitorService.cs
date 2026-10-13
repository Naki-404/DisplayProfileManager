namespace Visor.Services;

public enum MonitorState
{
    Idle,
    /// <summary>Looking for / verifying PresentMon.exe (synchronous, fast).</summary>
    Locating,
    /// <summary>UAC prompt shown; waiting for the first frame.</summary>
    Starting,
    Capturing,
    /// <summary>Stop helper launched (second UAC); waiting for the capture to exit.</summary>
    Stopping,
    /// <summary>Last capture ended with an error (<see cref="MonitorService.LastError"/>); cleared by the next start.</summary>
    Error
}

public enum MonitorErrorCode
{
    None,
    /// <summary>PresentMon.exe is not installed (see docs\MONITOR-PRESENTMON.md to drop it in).</summary>
    BinaryMissing,
    /// <summary>A PresentMon.exe was found but is neither Intel-signed nor pinned.</summary>
    Unverified,
    /// <summary>The user declined the elevation prompt.</summary>
    UacDeclined,
    /// <summary>The target exe is not running (checked before the UAC prompt).</summary>
    ProcessNotFound,
    /// <summary>PresentMon exited with an error before producing data (ETW session could not start).</summary>
    EtwFailed,
    /// <summary>PresentMon could not be launched (policy / blocked file / IO).</summary>
    StartFailed,
    /// <summary>PresentMon ran but produced no data (target never presented / exited at once).</summary>
    NoOutput,
    /// <summary>The CSV could not be read.</summary>
    OutputUnreadable,
    /// <summary>The stop helper was declined or failed; the capture keeps running until the game exits or the cap.</summary>
    StopFailed,
    /// <summary>Another capture is already running.</summary>
    Busy,
    /// <summary>The exe name is not a plain file name.</summary>
    InvalidTarget
}

public readonly record struct MonitorStartResult(bool Started, MonitorErrorCode Error, string Detail)
{
    public static MonitorStartResult Ok => new(true, MonitorErrorCode.None, "");
    public static MonitorStartResult Fail(MonitorErrorCode code, string detail) => new(false, code, detail);
}

/// <summary>
/// Facade over locator + session + stats: one capture at a time, never auto-started, state
/// machine Idle → Locating → Starting(UAC) → Capturing → Stopping → Idle | Error. Events fire
/// on worker threads; UI code marshals. Per-second summaries go to <see cref="SummaryUpdated"/>
/// and, as named <c>MetricSample</c>s, to every attached <see cref="IMetricSink"/>.
/// </summary>
public sealed class MonitorService : IDisposable
{
    public const int SummaryIntervalMs = 1000;

    private readonly Func<string?, PresentMonLocateResult> _locate;
    private readonly Func<string, IPresentMonSession> _sessionFactory;
    private readonly Func<string, bool> _isProcessRunning;
    private readonly object _gate = new();
    private readonly List<IMetricSink> _sinks = new();
    private readonly System.Threading.Timer _summaryTimer;

    private IPresentMonSession? _session;
    private MonitorState _state = MonitorState.Idle;
    private MonitorErrorCode _lastError = MonitorErrorCode.None;
    private string _lastErrorDetail = "";
    private MonitorErrorCode _pendingError = MonitorErrorCode.None;
    private string _pendingDetail = "";
    private string _target = "";
    private bool _disposed;

    /// <summary>Production wiring: real locator, real elevated session, process check via the kernel PID list.</summary>
    public MonitorService(double windowSeconds = 10)
        : this(new PresentMonLocator(), path => new PresentMonSession(path), null, windowSeconds) { }

    public MonitorService(PresentMonLocator locator, Func<string, IPresentMonSession> sessionFactory,
        Func<string, bool>? isProcessRunning = null, double windowSeconds = 10)
        : this(locator.Locate, sessionFactory, isProcessRunning, windowSeconds)
    {
        ArgumentNullException.ThrowIfNull(locator);
    }

    public MonitorService(Func<string?, PresentMonLocateResult> locate, Func<string, IPresentMonSession> sessionFactory,
        Func<string, bool>? isProcessRunning = null, double windowSeconds = 10)
    {
        _locate = locate ?? throw new ArgumentNullException(nameof(locate));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _isProcessRunning = isProcessRunning ?? (exe => ProcessSnapshot.IsRunning(ProcessNames.Key(exe)));
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
    /// <summary>Result of the last <see cref="Locate"/> / start attempt (for the settings UI).</summary>
    public PresentMonLocateResult? LastLocate { get; private set; }

    /// <summary>Optional: a user-configured PresentMon.exe path (file or folder). Null = default folders only.</summary>
    public string? UserConfiguredPath { get; set; }

    /// <summary>When true (default) the target exe must already be running; otherwise the UAC prompt is skipped and <see cref="MonitorErrorCode.ProcessNotFound"/> is returned.</summary>
    public bool RequireRunningTarget { get; set; } = true;

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

    /// <summary>Finds PresentMon without starting anything (settings page / pre-flight).</summary>
    public PresentMonLocateResult Locate()
    {
        var r = _locate(UserConfiguredPath);
        LastLocate = r;
        return r;
    }

    /// <summary>
    /// Explicit user action only. Synchronously locates and launches; the UAC prompt appears
    /// inside this call. Returns a failure result instead of throwing for every expected case.
    /// </summary>
    public MonitorStartResult StartCapture(string exeName)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is MonitorState.Locating or MonitorState.Starting or MonitorState.Capturing or MonitorState.Stopping)
                return MonitorStartResult.Fail(MonitorErrorCode.Busy, $"already capturing {_target}");
        }

        var exe = ProcessNames.ToExeName(exeName);
        if (!ProcessNames.IsPlainExeName(exe))
            return FailNow(MonitorErrorCode.InvalidTarget, $"'{exeName}' is not a plain exe name");

        SetState(MonitorState.Locating, MonitorErrorCode.None, "", target: exe);

        PresentMonLocateResult located;
        try { located = Locate(); }
        catch (Exception ex)
        {
            AppLog.Error(ex, "PresentMon locate");
            return FailNow(MonitorErrorCode.BinaryMissing, ex.Message);
        }
        if (located.Status == PresentMonStatus.Missing)
            return FailNow(MonitorErrorCode.BinaryMissing, located.Detail);
        if (located.Status == PresentMonStatus.Unverified)
            return FailNow(MonitorErrorCode.Unverified, located.Detail);

        if (RequireRunningTarget)
        {
            bool running;
            try { running = _isProcessRunning(exe); }
            catch (Exception ex) { AppLog.Warn(ex, "process check"); running = true; }   // unknown ≠ not running
            if (!running)
                return FailNow(MonitorErrorCode.ProcessNotFound, $"{exe} is not running");
        }

        IPresentMonSession session;
        try { session = _sessionFactory(located.Path!); }
        catch (Exception ex)
        {
            AppLog.Error(ex, "PresentMon session");
            return FailNow(MonitorErrorCode.StartFailed, ex.Message);
        }

        lock (_gate)
        {
            _session = session;
            _pendingError = MonitorErrorCode.None;
            _pendingDetail = "";
        }
        Stats.Clear();
        LastSummary = FrameStatsSummary.Empty;
        session.Frame += OnFrame;
        session.Error += OnSessionError;
        session.Exited += OnSessionExited;
        SetState(MonitorState.Starting, MonitorErrorCode.None, "");

        try
        {
            session.Start(PresentMonTarget.ForExe(exe));
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "PresentMon session start");
            DetachSession(session);
            return FailNow(MonitorErrorCode.StartFailed, ex.Message);
        }

        lock (_gate)
        {
            // Start() may have failed synchronously (UAC declined → Error + Exited already raised).
            if (_state == MonitorState.Error)
                return MonitorStartResult.Fail(_lastError, _lastErrorDetail);
            if (_state == MonitorState.Idle)
                return MonitorStartResult.Fail(MonitorErrorCode.NoOutput, "PresentMon exited immediately");
        }
        _summaryTimer.Change(SummaryIntervalMs, SummaryIntervalMs);
        return MonitorStartResult.Ok;
    }

    /// <summary>Explicit user action; the real session shows a second UAC prompt for the stop helper.</summary>
    public void StopCapture()
    {
        IPresentMonSession? session;
        lock (_gate)
        {
            if (_state is not (MonitorState.Starting or MonitorState.Capturing)) return;
            session = _session;
        }
        SetState(MonitorState.Stopping, MonitorErrorCode.None, "");
        try { session?.RequestStop(); }
        catch (Exception ex)
        {
            AppLog.Error(ex, "PresentMon stop");
            OnSessionError(PresentMonErrorCode.StopFailed, ex.Message);
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
        var summary = Stats.Snapshot();
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

    private void OnFrame(FrameRecord frame)
    {
        Stats.Add(frame);
        bool first;
        lock (_gate) first = _state == MonitorState.Starting;
        if (first) SetState(MonitorState.Capturing, MonitorErrorCode.None, "");
        try { FrameReceived?.Invoke(frame); } catch (Exception ex) { AppLog.Warn(ex, "Monitor frame handler"); }
    }

    private void OnSessionError(PresentMonErrorCode code, string detail)
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
        // NoOutput is advisory while the process is alive (game not started yet); every code
        // becomes the final reason only if the session then exits without data.
        lock (_gate) { _pendingError = mapped; _pendingDetail = detail; }
    }

    private void OnSessionExited(int? exitCode)
    {
        IPresentMonSession? session;
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
        if (session != null) DetachSession(session);
        if (pending != MonitorErrorCode.None && !(pending == MonitorErrorCode.NoOutput && hadFrames))
            SetState(MonitorState.Error, pending, pendingDetail);
        else if (!hadFrames && pending == MonitorErrorCode.None && State == MonitorState.Starting)
            SetState(MonitorState.Error, MonitorErrorCode.NoOutput, $"PresentMon exited (code {exitCode?.ToString() ?? "?"}) without data");
        else
            SetState(MonitorState.Idle, MonitorErrorCode.None, "");
    }

    private static MonitorErrorCode Map(PresentMonErrorCode code) => code switch
    {
        PresentMonErrorCode.UacDeclined => MonitorErrorCode.UacDeclined,
        PresentMonErrorCode.StartFailed => MonitorErrorCode.StartFailed,
        PresentMonErrorCode.ExitedWithError => MonitorErrorCode.EtwFailed,
        PresentMonErrorCode.NoOutput => MonitorErrorCode.NoOutput,
        PresentMonErrorCode.OutputUnreadable => MonitorErrorCode.OutputUnreadable,
        PresentMonErrorCode.StopFailed => MonitorErrorCode.StopFailed,
        _ => MonitorErrorCode.None
    };

    private MonitorStartResult FailNow(MonitorErrorCode code, string detail)
    {
        SetState(MonitorState.Error, code, detail);
        return MonitorStartResult.Fail(code, detail);
    }

    private void DetachSession(IPresentMonSession session)
    {
        session.Frame -= OnFrame;
        session.Error -= OnSessionError;
        session.Exited -= OnSessionExited;
        lock (_gate) if (ReferenceEquals(_session, session)) _session = null;
        try { session.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "PresentMon session dispose"); }
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
        IPresentMonSession? session;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            session = _session;
            _session = null;
        }
        _summaryTimer.Dispose();
        if (session != null)
        {
            // Visor is closing: we stop listening; the elevated capture ends on its own
            // (--terminate_on_proc_exit / --timed). No UAC prompt from Dispose, ever.
            session.Frame -= OnFrame;
            session.Error -= OnSessionError;
            session.Exited -= OnSessionExited;
            try { session.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "PresentMon session dispose"); }
        }
    }
}
