namespace Visor.Services;

/// <summary>
/// One frame of the captured game. All durations are milliseconds. The built-in capture
/// (<c>Services\Monitor\Native</c>) measures present-to-present time only; the optional values
/// (GPU / CPU busy, display latency, click-to-photon, dropped) need tracking of the whole present
/// lifecycle (DWM, flip queue, vsync) and are null / false — every consumer treats null as "not measured".
/// </summary>
/// <param name="Time">Seconds since the capture started (first present seen).</param>
/// <param name="FrameTimeMs">Time between this present and the previous one of the same swap chain.</param>
/// <param name="GpuBusyMs">GPU execution time for the frame (not measured by the built-in capture).</param>
/// <param name="CpuBusyMs">CPU time spent producing the frame (not measured by the built-in capture).</param>
/// <param name="DisplayLatencyMs">Present-to-scanout latency (not measured by the built-in capture).</param>
/// <param name="ClickToPhotonMs">Input-to-photon latency (not measured by the built-in capture).</param>
/// <param name="Dropped">The frame was never displayed (not measured by the built-in capture).</param>
public readonly record struct FrameRecord(
    double Time,
    double FrameTimeMs,
    double? GpuBusyMs,
    double? CpuBusyMs,
    double? DisplayLatencyMs,
    double? ClickToPhotonMs,
    bool Dropped)
{
    /// <summary>Process the frame belongs to, when known.</summary>
    public string? Application { get; init; }

    /// <summary>PID of the presenting process, or 0 when unknown.</summary>
    public int ProcessId { get; init; }

    /// <summary>Instantaneous FPS for this frame (0 when the frame time is not positive).</summary>
    public double Fps => FrameTimeMs > 0 ? 1000.0 / FrameTimeMs : 0;
}
