using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Visor.Services;

/// <summary>What PresentMon should capture: an exe name (PresentMon matches it case-insensitively) or a PID.</summary>
public sealed record PresentMonTarget
{
    /// <summary>Exe file name with extension ("game.exe"); null when <see cref="ProcessId"/> is used.</summary>
    public string? ExeName { get; init; }
    public int? ProcessId { get; init; }

    /// <summary>
    /// Hard cap for the capture; PresentMon terminates itself after it. Protects against a
    /// forgotten elevated session when the stop request (which needs a second UAC) is declined.
    /// </summary>
    public int SafetyCapSeconds { get; init; } = 3600;

    public static PresentMonTarget ForExe(string exeName) => new() { ExeName = ProcessNames.ToExeName(exeName) };
    public static PresentMonTarget ForPid(int pid) => new() { ProcessId = pid };

    public bool IsValid => ProcessId is > 0 || (ExeName != null && ProcessNames.IsPlainExeName(ExeName));

    public string Label => ProcessId is int pid ? $"pid {pid}" : ExeName ?? "?";
}

public enum PresentMonErrorCode
{
    None,
    /// <summary>The UAC prompt was declined (Win32 error 1223, ERROR_CANCELLED).</summary>
    UacDeclined,
    /// <summary>Process.Start failed for another reason (file blocked, policy, missing).</summary>
    StartFailed,
    /// <summary>PresentMon exited with a non-zero code before producing data (typically ETW session failure).</summary>
    ExitedWithError,
    /// <summary>PresentMon is running but wrote no CSV header within the timeout.</summary>
    NoOutput,
    /// <summary>The CSV file could not be opened / read.</summary>
    OutputUnreadable,
    /// <summary>The stop helper could not be launched (its UAC prompt was declined or it failed).</summary>
    StopFailed
}

/// <summary>Minimal session contract so the facade can be tested with a fake.</summary>
public interface IPresentMonSession : IDisposable
{
    bool IsRunning { get; }
    /// <summary>Starts the capture (shows a UAC prompt in the real implementation). Throws on misuse only; launch failures arrive via <see cref="Error"/>.</summary>
    void Start(PresentMonTarget target);
    /// <summary>Asks the capture to end. The real implementation needs a second elevation to do it.</summary>
    void RequestStop();
    event Action<FrameRecord>? Frame;
    event Action<PresentMonErrorCode, string>? Error;
    /// <summary>PresentMon's process ended (exit code, or null when unknown).</summary>
    event Action<int?>? Exited;
}

/// <summary>Command lines for PresentMon 2.x. Pure; see docs\MONITOR-PRESENTMON.md for the flag assumptions.</summary>
public static class PresentMonArgs
{
    public const string DefaultSessionName = "VisorPM";

    /// <summary>
    /// Capture command line. Output goes to a file (an elevated child's stdout is not reliably
    /// readable from a non-elevated parent) and the session is named so it can be stopped later.
    /// </summary>
    public static string BuildCapture(PresentMonTarget target, string outputCsvPath, string sessionName = DefaultSessionName)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsValid) throw new ArgumentException("Target needs a valid exe name or a PID.", nameof(target));
        if (string.IsNullOrWhiteSpace(outputCsvPath)) throw new ArgumentException("Output path required.", nameof(outputCsvPath));
        var sb = new StringBuilder();
        if (target.ProcessId is int pid) sb.Append("--process_id ").Append(pid);
        else sb.Append("--process_name ").Append(Quote(target.ExeName!));
        sb.Append(" --output_file ").Append(Quote(outputCsvPath));
        sb.Append(" --session_name ").Append(Quote(sessionName));
        sb.Append(" --stop_existing_session");
        sb.Append(" --terminate_on_proc_exit");
        sb.Append(" --no_console_stats");
        if (target.SafetyCapSeconds > 0)
            sb.Append(" --timed ").Append(target.SafetyCapSeconds).Append(" --terminate_after_timed");
        return sb.ToString();
    }

    /// <summary>Stop helper: stops the named ETW session (which makes the capturing instance exit) and quits.</summary>
    public static string BuildStop(string sessionName = DefaultSessionName) =>
        $"--terminate_existing_session --session_name {Quote(sessionName)} --no_console_stats";

    internal static string Quote(string s)
    {
        if (s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
        return "\"" + s.Replace("\"", "\\\"") + "\"";
    }
}

/// <summary>Process-launch seam (the real one calls Process.Start with the "runas" verb).</summary>
internal interface IProcessLauncher
{
    /// <summary>Throws <see cref="Win32Exception"/> (1223 = UAC declined) or other exceptions on failure.</summary>
    IProcessHandle Launch(ProcessStartInfo psi);
}

internal interface IProcessHandle : IDisposable
{
    bool HasExited { get; }
    int? ExitCode { get; }
    /// <summary>Raised once when the process exits (any thread).</summary>
    event Action? Exited;
}

internal sealed class ShellProcessLauncher : IProcessLauncher
{
    public IProcessHandle Launch(ProcessStartInfo psi)
    {
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        return new Handle(p);
    }

    private sealed class Handle : IProcessHandle
    {
        private readonly Process _p;
        public Handle(Process p)
        {
            _p = p;
            try
            {
                _p.EnableRaisingEvents = true;
                _p.Exited += (_, _) => Exited?.Invoke();
                if (_p.HasExited) Exited?.Invoke();
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                AppLog.Warn(ex, "PresentMon exit tracking");
            }
        }
        public bool HasExited { get { try { return _p.HasExited; } catch { return true; } } }
        public int? ExitCode { get { try { return _p.HasExited ? _p.ExitCode : null; } catch { return null; } } }
        public event Action? Exited;
        public void Dispose() => _p.Dispose();
    }
}

/// <summary>
/// Tails a CSV that another process appends to: opens with ReadWrite+Delete sharing, remembers
/// the offset, and returns only complete lines (a trailing partial line waits for the next poll).
/// </summary>
internal sealed class CsvFileTail : IDisposable
{
    private readonly string _path;
    private FileStream? _fs;
    private readonly StringBuilder _partial = new();
    private readonly byte[] _buffer = new byte[64 * 1024];
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly char[] _chars = new char[64 * 1024];

    public CsvFileTail(string path) => _path = path;

    public long BytesRead { get; private set; }

    /// <summary>Opens the file if it now exists. Returns false while it does not.</summary>
    public bool TryOpen()
    {
        if (_fs != null) return true;
        if (!File.Exists(_path)) return false;
        _fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        return true;
    }

    /// <summary>Reads whatever was appended since the last call and appends complete lines to <paramref name="lines"/>.</summary>
    public int ReadNewLines(List<string> lines)
    {
        if (_fs == null && !TryOpen()) return 0;
        int added = 0;
        while (true)
        {
            int n = _fs!.Read(_buffer, 0, _buffer.Length);
            if (n <= 0) break;
            BytesRead += n;
            int chars = _decoder.GetChars(_buffer, 0, n, _chars, 0);
            for (int i = 0; i < chars; i++)
            {
                char ch = _chars[i];
                if (ch == '\n')
                {
                    lines.Add(_partial.ToString().TrimEnd('\r'));
                    _partial.Clear();
                    added++;
                }
                else _partial.Append(ch);
            }
        }
        return added;
    }

    /// <summary>Flushes a trailing line without a newline (call when the writer has exited).</summary>
    public bool TakePartial(out string line)
    {
        line = _partial.ToString().TrimEnd('\r');
        _partial.Clear();
        return line.Length > 0;
    }

    public void Dispose()
    {
        _fs?.Dispose();
        _fs = null;
    }
}

/// <summary>
/// One PresentMon capture: launches <c>PresentMon.exe</c> elevated (UAC prompt), tails its CSV
/// output file every <see cref="PollIntervalMs"/> and raises a <see cref="Frame"/> per row.
/// Stopping needs a second elevated launch of the stop helper (PresentMon has no IPC and a
/// non-elevated parent cannot terminate an elevated child); the <c>--timed</c> cap and
/// <c>--terminate_on_proc_exit</c> make sure a capture never outlives the game by much even
/// when that second prompt is declined.
/// </summary>
public sealed class PresentMonSession : IPresentMonSession
{
    public const int PollIntervalMs = 250;
    /// <summary>No CSV header after this long → <see cref="PresentMonErrorCode.NoOutput"/> (the game may simply not be running yet).</summary>
    public static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(60);

    private readonly string _exePath;
    private readonly IProcessLauncher _launcher;
    private readonly string _workDir;
    private readonly string _sessionName;
    private readonly object _gate = new();
    private PresentMonCsvParser _parser = new();

    private IProcessHandle? _process;
    private CsvFileTail? _tail;
    private System.Threading.Timer? _poll;
    private string? _csvPath;
    private DateTime _startedUtc;
    private bool _running;
    private bool _stopRequested;
    private bool _finished;
    private bool _pollBusy;
    private bool _disposed;

    public PresentMonSession(string exePath, string? workDir = null)
        : this(exePath, new ShellProcessLauncher(), workDir, PresentMonArgs.DefaultSessionName) { }

    internal PresentMonSession(string exePath, IProcessLauncher launcher, string? workDir, string sessionName)
    {
        _exePath = exePath ?? throw new ArgumentNullException(nameof(exePath));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _workDir = workDir ?? Path.Combine(Path.GetTempPath(), "Visor", "presentmon");
        _sessionName = string.IsNullOrWhiteSpace(sessionName) ? PresentMonArgs.DefaultSessionName : sessionName;
    }

    public bool IsRunning { get { lock (_gate) return _running; } }

    /// <summary>CSV file of the current / last capture (for diagnostics).</summary>
    public string? OutputPath { get { lock (_gate) return _csvPath; } }

    public PresentMonTarget? Target { get; private set; }

    public event Action<FrameRecord>? Frame;
    public event Action<PresentMonErrorCode, string>? Error;
    public event Action<int?>? Exited;

    public void Start(PresentMonTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsValid) throw new ArgumentException("Invalid PresentMon target.", nameof(target));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) throw new InvalidOperationException("A capture is already running.");
            _running = true;
            _stopRequested = false;
            _finished = false;
            _parser = new PresentMonCsvParser();
            Target = target;
            Directory.CreateDirectory(_workDir);
            _csvPath = Path.Combine(_workDir, $"capture-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.csv");
            TryDelete(_csvPath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            Arguments = PresentMonArgs.BuildCapture(target, _csvPath!, _sessionName),
            UseShellExecute = true,       // required for the "runas" verb
            Verb = "runas",               // UAC prompt — only ever on an explicit user action
            WorkingDirectory = _workDir,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        };

        IProcessHandle handle;
        try
        {
            AppLog.Info($"PresentMon start (elevated): {target.Label} → {Path.GetFileName(_csvPath)}");
            handle = _launcher.Launch(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Finish(PresentMonErrorCode.UacDeclined, "UAC prompt declined", exitCode: null);
            return;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error(ex, "PresentMon start");
            Finish(PresentMonErrorCode.StartFailed, ex.Message, exitCode: null);
            return;
        }

        lock (_gate)
        {
            _process = handle;
            _tail = new CsvFileTail(_csvPath!);
            _startedUtc = DateTime.UtcNow;
            handle.Exited += OnProcessExited;
            _poll = new System.Threading.Timer(_ => Poll(), null, PollIntervalMs, PollIntervalMs);
        }
        if (handle.HasExited) OnProcessExited();
    }

    public void RequestStop()
    {
        lock (_gate)
        {
            if (!_running || _stopRequested) return;
            _stopRequested = true;
        }
        // Second elevation: the only way a non-elevated Visor can end an elevated PresentMon.
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            Arguments = PresentMonArgs.BuildStop(_sessionName),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = _workDir,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        };
        try
        {
            AppLog.Info("PresentMon stop helper (elevated)");
            using var stopper = _launcher.Launch(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            lock (_gate) _stopRequested = false;   // allow another attempt
            Error?.Invoke(PresentMonErrorCode.StopFailed, "UAC prompt for the stop helper declined; capture continues until the game exits or the safety cap");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            lock (_gate) _stopRequested = false;
            AppLog.Error(ex, "PresentMon stop");
            Error?.Invoke(PresentMonErrorCode.StopFailed, ex.Message);
        }
    }

    /// <summary>One tail pass; exposed for tests (the timer calls it every 250 ms).</summary>
    internal void Poll()
    {
        CsvFileTail? tail;
        lock (_gate)
        {
            if (!_running || _finished || _pollBusy || _tail == null) return;
            _pollBusy = true;
            tail = _tail;
        }
        try
        {
            var lines = new List<string>();
            try
            {
                tail.ReadNewLines(lines);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn(ex, "PresentMon tail");
                Finish(PresentMonErrorCode.OutputUnreadable, ex.Message, exitCode: null);
                return;
            }
            Dispatch(lines);

            if (!_parser.HasHeader && DateTime.UtcNow - _startedUtc > HeaderTimeout)
            {
                bool exited = _process?.HasExited ?? false;
                if (!exited)
                    Error?.Invoke(PresentMonErrorCode.NoOutput, $"no CSV header after {HeaderTimeout.TotalSeconds:F0} s (is {Target?.Label} running?)");
                _startedUtc = DateTime.UtcNow;   // report once per timeout period, keep tailing
            }
        }
        finally
        {
            lock (_gate) _pollBusy = false;
        }
    }

    private void Dispatch(List<string> lines)
    {
        foreach (var line in lines)
        {
            if (_parser.Feed(line, out var rec))
            {
                try { Frame?.Invoke(rec); }
                catch (Exception ex) { AppLog.Warn(ex, "PresentMon frame handler"); }
            }
        }
    }

    private void OnProcessExited()
    {
        int? code;
        lock (_gate)
        {
            if (_finished) return;
            code = _process?.ExitCode;
        }
        // Drain what the process wrote before it went away.
        try
        {
            var lines = new List<string>();
            CsvFileTail? tail;
            lock (_gate) tail = _tail;
            if (tail != null)
            {
                tail.ReadNewLines(lines);
                if (tail.TakePartial(out var last)) lines.Add(last);
            }
            Dispatch(lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn(ex, "PresentMon final drain"); }

        bool gotData = _parser.HasHeader;
        if (code is int c && c != 0 && !gotData)
            Finish(PresentMonErrorCode.ExitedWithError, $"PresentMon exited with code {c} before producing data", code);
        else
            Finish(PresentMonErrorCode.None, "", code);
    }

    private void Finish(PresentMonErrorCode error, string detail, int? exitCode)
    {
        IProcessHandle? proc;
        CsvFileTail? tail;
        System.Threading.Timer? poll;
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
            _running = false;
            proc = _process; _process = null;
            tail = _tail; _tail = null;
            poll = _poll; _poll = null;
        }
        poll?.Dispose();
        tail?.Dispose();
        if (proc != null) { proc.Exited -= OnProcessExited; proc.Dispose(); }
        if (error != PresentMonErrorCode.None)
        {
            AppLog.Warn($"PresentMon: {error} — {detail}");
            try { Error?.Invoke(error, detail); } catch (Exception ex) { AppLog.Warn(ex, "PresentMon error handler"); }
        }
        else AppLog.Info($"PresentMon exited (code {exitCode?.ToString() ?? "?"}), {_parser.AcceptedRows} frames");
        try { Exited?.Invoke(exitCode); } catch (Exception ex) { AppLog.Warn(ex, "PresentMon exited handler"); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Does NOT stop the elevated process (cannot without UAC); it ends by itself via
        // --terminate_on_proc_exit / --timed. We only stop listening.
        Finish(PresentMonErrorCode.None, "", exitCode: null);
    }
}
