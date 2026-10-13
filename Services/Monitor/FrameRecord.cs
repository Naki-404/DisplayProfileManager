namespace Visor.Services;

/// <summary>
/// One presented (or dropped) frame as reported by PresentMon. All durations are milliseconds;
/// optional values are null when the CSV has no such column or the cell is "NA".
/// </summary>
/// <param name="Time">Seconds since the capture started (PresentMon <c>CPUStartTime</c> / <c>TimeInSeconds</c>).</param>
/// <param name="FrameTimeMs">Time between this present and the previous one (<c>FrameTime</c> / <c>msBetweenPresents</c>).</param>
/// <param name="GpuBusyMs">GPU execution time for the frame (<c>GPUBusy</c> / <c>msGPUActive</c>).</param>
/// <param name="CpuBusyMs">CPU time spent producing the frame (<c>CPUBusy</c>; 1.x has no equivalent).</param>
/// <param name="DisplayLatencyMs">Present-to-scanout latency (<c>DisplayLatency</c> / <c>msUntilDisplayed</c>).</param>
/// <param name="ClickToPhotonMs">Input-to-photon latency when PresentMon could attribute input (<c>ClickToPhotonLatency</c>).</param>
/// <param name="Dropped">The frame was never displayed.</param>
public readonly record struct FrameRecord(
    double Time,
    double FrameTimeMs,
    double? GpuBusyMs,
    double? CpuBusyMs,
    double? DisplayLatencyMs,
    double? ClickToPhotonMs,
    bool Dropped)
{
    /// <summary>Process the frame belongs to (PresentMon <c>Application</c> column), when known.</summary>
    public string? Application { get; init; }

    /// <summary>PID of the presenting process, or 0 when the column is absent.</summary>
    public int ProcessId { get; init; }

    /// <summary>Instantaneous FPS for this frame (0 when the frame time is not positive).</summary>
    public double Fps => FrameTimeMs > 0 ? 1000.0 / FrameTimeMs : 0;
}
