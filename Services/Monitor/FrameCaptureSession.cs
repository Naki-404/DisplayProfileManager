namespace Visor.Services;

/// <summary>What to capture: an exe name ("game.exe") and/or explicit PIDs.</summary>
public sealed record FrameCaptureTarget
{
    /// <summary>Exe file name with extension; null when only <see cref="ProcessIds"/> are used.</summary>
    public string? ExeName { get; init; }
    public IReadOnlyList<int> ProcessIds { get; init; } = Array.Empty<int>();

    /// <summary>
    /// Hard cap for the capture: the elevated host stops itself after it even if every other stop
    /// signal (stop byte, pipe, parent exit, target exit) were somehow missed. ≤ 3600 s.
    /// </summary>
    public int SafetyCapSeconds { get; init; } = 3600;

    public static FrameCaptureTarget ForExe(string exeName) => new() { ExeName = ProcessNames.ToExeName(exeName) };
    public static FrameCaptureTarget ForPid(int pid) => new() { ProcessIds = new[] { pid } };

    public bool IsValid =>
        (ProcessIds.Count > 0 && ProcessIds.All(p => p > 4)) || (ExeName != null && ProcessNames.IsPlainExeName(ExeName));

    public string Label => ExeName ?? (ProcessIds.Count > 0 ? "pid " + string.Join(",", ProcessIds) : "?");
}

public enum FrameCaptureErrorCode
{
    None,
    /// <summary>The UAC prompt was declined (Win32 error 1223, ERROR_CANCELLED).</summary>
    UacDeclined,
    /// <summary>The capture host could not be launched or never connected (policy, blocked file, refused request).</summary>
    StartFailed,
    /// <summary>The ETW session could not be started (detail carries the Win32 error).</summary>
    EtwFailed,
    /// <summary>None of the target processes was running when the host looked.</summary>
    ProcessNotFound,
    /// <summary>Capturing, but no frame arrived within the timeout (advisory while the target lives).</summary>
    NoOutput,
    /// <summary>The host's byte stream was malformed (bad hello / version / message).</summary>
    StreamUnreadable,
    /// <summary>Visor is not running as its own exe (dotnet host / test runner) — no elevated helper can be started.</summary>
    ElevationUnavailable,
    /// <summary>The stop request could not be delivered (kept for the facade's contract; the pipe implementation never raises it).</summary>
    StopFailed
}

/// <summary>Minimal session contract so the facade can be tested with a fake.</summary>
public interface IFrameCaptureSession : IDisposable
{
    bool IsRunning { get; }
    /// <summary>Starts the capture (one UAC prompt in the real implementation). Throws on misuse only; launch failures arrive via <see cref="Error"/>.</summary>
    void Start(FrameCaptureTarget target);
    /// <summary>Asks the capture to end. No elevation needed: the host listens for a stop byte on the pipe.</summary>
    void RequestStop();
    event Action<FrameRecord>? Frame;
    event Action<FrameCaptureErrorCode, string>? Error;
    /// <summary>The capture ended (host exit code, or null when unknown).</summary>
    event Action<int?>? Exited;
}
