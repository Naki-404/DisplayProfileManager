using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native.GPU;

namespace Visor.Services;

/// <summary>Static NVIDIA facts read once through NvAPI (no driver date — NvAPI does not expose one).</summary>
internal sealed record NvidiaGpuFacts(string FullName, long VramBytes, string DriverVersion);

/// <summary>
/// NVIDIA GPU facts and live sensors through NvAPI (user-mode, no elevation, lifetime owned by
/// <see cref="NvApiHost"/>). Every read is wrapped: a missing driver / unsupported query becomes a
/// <see cref="Sensor{T}"/> with a reason, never a fake value. Dispose the <see cref="SensorService"/>
/// before <see cref="NvApiHost.Shutdown"/> at app exit — reads must not race the unload.
/// </summary>
internal static class NvidiaGpuReader
{
    /// <summary><c>NVIDIA.DriverVersion</c> is an integer like 58172 → "581.72".</summary>
    internal static string FormatDriverVersion(uint raw) => $"{raw / 100}.{raw % 100:00}";

    /// <summary>
    /// Maps NvAPI's current <c>PerformanceDecreaseReason</c> and <c>PerformanceLimit</c> bit sets to
    /// vendor-neutral <see cref="GpuThrottleFlags"/>. Raw bits (see <see cref="PerformanceDecreaseReason"/>,
    /// <see cref="PerformanceLimit"/>) so tests can feed them without the native library.
    /// </summary>
    internal static GpuThrottleFlags MapThrottle(uint decreaseReasonBits, uint activeLimitBits)
    {
        var f = GpuThrottleFlags.None;
        if ((decreaseReasonBits & (uint)PerformanceDecreaseReason.ThermalProtection) != 0) f |= GpuThrottleFlags.Thermal;
        if ((decreaseReasonBits & (uint)PerformanceDecreaseReason.PowerControl) != 0) f |= GpuThrottleFlags.Power;
        if ((decreaseReasonBits & (uint)PerformanceDecreaseReason.AC_BATT) != 0) f |= GpuThrottleFlags.Battery;
        if ((decreaseReasonBits & (uint)PerformanceDecreaseReason.ApiTriggered) != 0) f |= GpuThrottleFlags.Api;
        if ((decreaseReasonBits & (uint)PerformanceDecreaseReason.InsufficientPower) != 0) f |= GpuThrottleFlags.InsufficientPower;
        if ((activeLimitBits & (uint)PerformanceLimit.TemperatureLimit) != 0) f |= GpuThrottleFlags.Thermal;
        if ((activeLimitBits & (uint)PerformanceLimit.PowerLimit) != 0) f |= GpuThrottleFlags.Power;
        if ((activeLimitBits & (uint)PerformanceLimit.VoltageLimit) != 0) f |= GpuThrottleFlags.Voltage;
        if ((activeLimitBits & (uint)PerformanceLimit.NoLoadLimit) != 0) f |= GpuThrottleFlags.NoLoad;
        return f;
    }

    /// <summary>All NVIDIA GPUs, or an empty list with the reason (no driver / NvAPI failure).</summary>
    public static (IReadOnlyList<NvidiaGpuFacts> Gpus, UnavailableReason Reason) ReadFacts()
    {
        if (!NvApiHost.TryInitialize()) return (Array.Empty<NvidiaGpuFacts>(), UnavailableReason.NoDriver);
        try
        {
            string driver = "";
            try { driver = FormatDriverVersion(NVIDIA.DriverVersion); }
            catch (Exception ex) { AppLog.Info("NvAPI driver version: " + ex.Message); }

            var list = new List<NvidiaGpuFacts>();
            foreach (var gpu in PhysicalGPU.GetPhysicalGPUs())
            {
                string name;
                try { name = gpu.FullName ?? "NVIDIA GPU"; }
                catch { name = "NVIDIA GPU"; }
                long vram = 0;
                try { vram = (long)gpu.MemoryInformation.DedicatedVideoMemoryInkB * 1024L; }
                catch (Exception ex) { AppLog.Info("NvAPI VRAM: " + ex.Message); }
                list.Add(new NvidiaGpuFacts(name, vram, driver));
            }
            return (list, list.Count == 0 ? UnavailableReason.NoDevice : UnavailableReason.None);
        }
        catch (Exception ex)
        {
            AppLog.Info("NvAPI GPU facts failed: " + ex.Message);
            return (Array.Empty<NvidiaGpuFacts>(), UnavailableReason.QueryFailed);
        }
    }

    /// <summary>Handles are cheap to enumerate but not free — the sampler keeps them for one run.</summary>
    public static PhysicalGPU[] GetGpusOrEmpty()
    {
        if (!NvApiHost.TryInitialize()) return Array.Empty<PhysicalGPU>();
        try { return PhysicalGPU.GetPhysicalGPUs(); }
        catch (Exception ex)
        {
            AppLog.Info("NvAPI GPU enumeration failed: " + ex.Message);
            return Array.Empty<PhysicalGPU>();
        }
    }

    /// <summary>One live reading; each field fails independently.</summary>
    public static GpuSensorReading Read(PhysicalGPU gpu)
    {
        string name;
        try { name = gpu.FullName ?? "NVIDIA GPU"; }
        catch { name = "NVIDIA GPU"; }

        Sensor<int> util, temp, core, mem;
        Sensor<long> vramUsed;
        Sensor<double> power;
        Sensor<GpuThrottleFlags> throttle;

        try
        {
            // "busy % in the last 1 s" — the same number NVIDIA's own overlay shows.
            util = Sensor.Of(Math.Clamp(gpu.UsageInformation.GPU.Percentage, 0, 100));
        }
        catch (Exception ex) { util = Fail<int>(ex, "usage"); }

        try
        {
            // Prefer the GPU-core sensor; fall back to the first sensor the driver lists.
            GPUThermalSensor? pick = null;
            foreach (var s in gpu.ThermalInformation.ThermalSensors)
            {
                if (s.Target == ThermalSettingsTarget.GPU) { pick = s; break; }
                pick ??= s;
            }
            temp = pick == null ? Sensor.Missing<int>(UnavailableReason.NotSupported) : Sensor.Of(pick.CurrentTemperature);
        }
        catch (Exception ex) { temp = Fail<int>(ex, "thermal"); }

        try
        {
            var clocks = gpu.CurrentClockFrequencies;
            core = clocks.GraphicsClock.IsPresent
                ? Sensor.Of((int)(clocks.GraphicsClock.Frequency / 1000))   // NvAPI reports kHz
                : Sensor.Missing<int>(UnavailableReason.NotSupported);
            mem = clocks.MemoryClock.IsPresent
                ? Sensor.Of((int)(clocks.MemoryClock.Frequency / 1000))
                : Sensor.Missing<int>(UnavailableReason.NotSupported);
        }
        catch (Exception ex) { core = mem = Fail<int>(ex, "clocks"); }

        try
        {
            var pc = gpu.PerformanceControl;
            throttle = Sensor.Of(MapThrottle((uint)pc.CurrentPerformanceDecreaseReason, (uint)pc.CurrentActiveLimit));
        }
        catch (Exception ex) { throttle = Fail<GpuThrottleFlags>(ex, "performance control"); }

        try
        {
            var mi = gpu.MemoryInformation;
            long total = (long)mi.DedicatedVideoMemoryInkB * 1024L;
            long avail = (long)mi.CurrentAvailableDedicatedVideoMemoryInkB * 1024L;
            vramUsed = Sensor.Of(Math.Max(0, total - avail));
        }
        catch (Exception ex) { vramUsed = Fail<long>(ex, "memory"); }

        try
        {
            Sensor<double>? found = null;
            foreach (var e in gpu.PowerTopologyInformation.PowerTopologyEntries)
            {
                if (e.Domain == PowerTopologyDomain.GPU) { found = Sensor.Of((double)e.PowerUsageInPercent); break; }
                found ??= Sensor.Of((double)e.PowerUsageInPercent);
            }
            power = found ?? Sensor.Missing<double>(UnavailableReason.NotSupported);
        }
        catch (Exception ex) { power = Fail<double>(ex, "power topology"); }

        return new GpuSensorReading
        {
            Name = name,
            Vendor = GpuVendor.Nvidia,
            UtilizationPercent = util,
            TemperatureC = temp,
            CoreClockMhz = core,
            MemoryClockMhz = mem,
            Throttle = throttle,
            VramUsedBytes = vramUsed,
            PowerPercent = power
        };
    }

    private static int _failuresLogged;

    private static Sensor<T> Fail<T>(Exception ex, string what) where T : struct
    {
        // Unsupported queries throw NVIDIAApiException(NotSupported) on every tick — log the first few only.
        if (Interlocked.Increment(ref _failuresLogged) <= 8)
            AppLog.Info($"NvAPI {what} read failed: {ex.Message}");
        bool notSupported = ex.Message.Contains("NotSupported", StringComparison.OrdinalIgnoreCase)
                            || ex.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase);
        return Sensor.Missing<T>(notSupported ? UnavailableReason.NotSupported : UnavailableReason.QueryFailed);
    }
}
