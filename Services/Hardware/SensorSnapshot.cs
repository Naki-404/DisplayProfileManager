namespace Visor.Services;

/// <summary>Vendor-neutral GPU clock/power limiter state (NVIDIA: PerformanceDecreaseReason + PerformanceLimit).</summary>
[Flags]
public enum GpuThrottleFlags
{
    None = 0,
    /// <summary>Clocks reduced to stay under the temperature limit.</summary>
    Thermal = 1,
    /// <summary>Clocks capped by the board power limit (normal under full load on power-limited cards).</summary>
    Power = 2,
    /// <summary>Laptop on battery.</summary>
    Battery = 4,
    /// <summary>A tool (NvAPI client) asked for a lower state.</summary>
    Api = 8,
    /// <summary>PCIe power connector missing / insufficient supply.</summary>
    InsufficientPower = 16,
    /// <summary>At the voltage ceiling (the usual state at maximum boost — informational).</summary>
    Voltage = 32,
    /// <summary>Idle: nothing to do (informational).</summary>
    NoLoad = 64
}

public sealed record GpuSensorReading
{
    public string Name { get; init; } = "";
    public GpuVendor Vendor { get; init; }
    public Sensor<int> UtilizationPercent { get; init; }
    public Sensor<int> TemperatureC { get; init; }
    public Sensor<int> CoreClockMhz { get; init; }
    public Sensor<int> MemoryClockMhz { get; init; }
    public Sensor<GpuThrottleFlags> Throttle { get; init; }
    public Sensor<long> VramUsedBytes { get; init; }
    /// <summary>Board power as % of the limit (NvAPI power topology); not watts — NvAPI does not publish watts.</summary>
    public Sensor<double> PowerPercent { get; init; }

    /// <summary>A GPU whose sensors are not integrated (AMD / Intel): every field carries the same reason.</summary>
    public static GpuSensorReading Unsupported(string name, GpuVendor vendor, UnavailableReason reason) => new()
    {
        Name = name,
        Vendor = vendor,
        UtilizationPercent = Sensor.Missing<int>(reason),
        TemperatureC = Sensor.Missing<int>(reason),
        CoreClockMhz = Sensor.Missing<int>(reason),
        MemoryClockMhz = Sensor.Missing<int>(reason),
        Throttle = Sensor.Missing<GpuThrottleFlags>(reason),
        VramUsedBytes = Sensor.Missing<long>(reason),
        PowerPercent = Sensor.Missing<double>(reason)
    };
}

public sealed record DriveSensorReading
{
    /// <summary>"C:" — the logical drive (free space is per volume; throughput is per volume too).</summary>
    public string Name { get; init; } = "";
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public double FreePercent => TotalBytes <= 0 ? 0 : Math.Clamp(FreeBytes * 100.0 / TotalBytes, 0, 100);
    public Sensor<double> ReadBytesPerSec { get; init; }
    public Sensor<double> WriteBytesPerSec { get; init; }
}

public sealed record NetworkSensorReading
{
    /// <summary>The adapter that owns the default route (null when none is up).</summary>
    public string? AdapterName { get; init; }
    public Sensor<double> ReceiveBytesPerSec { get; init; }
    public Sensor<double> SendBytesPerSec { get; init; }
}

/// <summary>One 1 Hz reading from <see cref="SensorService"/>. Missing values carry a reason, never a guess.</summary>
public sealed record SensorSnapshot
{
    public DateTime CapturedUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Whole-system CPU busy % from <c>GetSystemTimes</c> deltas (first sample: NotSampledYet).</summary>
    public Sensor<double> CpuTotalPercent { get; init; }
    /// <summary>Always missing: CPU temperature needs a kernel driver (MSR / EC access) that Visor does not ship.</summary>
    public Sensor<double> CpuTemperatureC { get; init; } = Sensor.Missing<double>(UnavailableReason.NoDriver);
    public long RamTotalBytes { get; init; }
    public long RamUsedBytes { get; init; }
    public Sensor<double> RamUsedPercent { get; init; }
    public IReadOnlyList<GpuSensorReading> Gpus { get; init; } = Array.Empty<GpuSensorReading>();
    public IReadOnlyList<DriveSensorReading> Drives { get; init; } = Array.Empty<DriveSensorReading>();
    public NetworkSensorReading Network { get; init; } = new();
    /// <summary>How long the sampling pass took — stays well under the 1 s interval on a healthy box.</summary>
    public TimeSpan SampleDuration { get; init; }
}

/// <summary>Pure delta / percentage math shared by the sampler (tested without hardware).</summary>
public static class SensorMath
{
    /// <summary>
    /// CPU busy % between two <c>GetSystemTimes</c> readings (100 ns units; kernel time includes idle).
    /// Null when the window is empty or the counters went backwards.
    /// </summary>
    public static double? CpuPercent(long idle0, long kernel0, long user0, long idle1, long kernel1, long user1)
    {
        long total = (kernel1 - kernel0) + (user1 - user0);
        long idle = idle1 - idle0;
        if (total <= 0 || idle < 0) return null;
        return Math.Clamp((total - idle) * 100.0 / total, 0, 100);
    }

    /// <summary>
    /// Counter delta per second. A counter that went backwards is treated as a 32-bit wrap when the
    /// previous value fits in 32 bits (NIC / IOCTL counters), otherwise as a reset (null).
    /// </summary>
    public static double? RatePerSecond(long previous, long current, double seconds)
    {
        if (seconds <= 0 || previous < 0 || current < 0) return null;
        long delta;
        if (current >= previous)
            delta = current - previous;
        else if (previous <= uint.MaxValue)
            delta = (long)uint.MaxValue - previous + current + 1;
        else
            return null;
        return delta / seconds;
    }

    public static double? UsedPercent(long used, long total)
    {
        if (total <= 0 || used < 0) return null;
        return Math.Clamp(used * 100.0 / total, 0, 100);
    }

    public static Sensor<double> Rate(long? previous, long current, double seconds)
    {
        if (previous == null) return Sensor.Missing<double>(UnavailableReason.NotSampledYet);
        var r = RatePerSecond(previous.Value, current, seconds);
        return r == null ? Sensor.Missing<double>(UnavailableReason.NotSampledYet) : Sensor.Of(r.Value);
    }
}
