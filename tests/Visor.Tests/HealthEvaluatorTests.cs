using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class HealthEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    private static GpuSensorReading Gpu(int? tempC = 60, GpuThrottleFlags? throttle = GpuThrottleFlags.None,
        UnavailableReason missing = UnavailableReason.QueryFailed, string name = "GPU") => new()
    {
        Name = name,
        Vendor = GpuVendor.Nvidia,
        UtilizationPercent = Sensor.Of(50),
        TemperatureC = tempC is { } t ? Sensor.Of(t) : Sensor.Missing<int>(missing),
        Throttle = throttle is { } f ? Sensor.Of(f) : Sensor.Missing<GpuThrottleFlags>(missing),
        CoreClockMhz = Sensor.Of(2000),
        MemoryClockMhz = Sensor.Of(10000),
        VramUsedBytes = Sensor.Of(1L),
        PowerPercent = Sensor.Of(50.0)
    };

    private static SensorSnapshot Snap(params GpuSensorReading[] gpus) => new()
    {
        CpuTotalPercent = Sensor.Of(10.0),
        RamTotalBytes = 1000,
        RamUsedBytes = 500,
        RamUsedPercent = Sensor.Of(50.0),
        Gpus = gpus,
        Drives = [new DriveSensorReading { Name = "C:", TotalBytes = 1000, FreeBytes = 500 }]
    };

    private static CrashEventSummary NoCrashes() => CrashEventReader.Summarize([], Now, TimeSpan.FromDays(30));

    private static HardwareInventory Inventory(StorageHealth? health = StorageHealth.Healthy, UnavailableReason healthReason = UnavailableReason.None, DateTime? driverDate = null) => new()
    {
        Storage = [new StorageInventory { Model = "KINGSTON SNV2S1000G", Health = health, HealthReason = healthReason }],
        Gpus = [new GpuInventory { Name = "GPU", Vendor = GpuVendor.Nvidia, DriverDate = driverDate ?? Now.AddMonths(-1) }]
    };

    [Fact]
    public void Healthy_system_is_ok_with_cpu_temperature_skipped_not_unknown()
    {
        var r = HealthEvaluator.Evaluate(Inventory(), Snap(Gpu()), NoCrashes(), Now);
        Assert.Equal(HealthStatus.Ok, r.Overall);
        Assert.Single(r.Findings);
        Assert.Equal(HealthReasons.Ok, r.Findings[0].ReasonKey);
        Assert.Contains(r.Skipped, s => s.Component == "cpu.temperature" && s.ReasonKey == HealthReasons.NoDriver);
    }

    [Fact]
    public void Null_inputs_give_ok_with_nothing_evaluated()
    {
        var r = HealthEvaluator.Evaluate(null, null, null, Now);
        Assert.Equal(HealthStatus.Ok, r.Overall);
        Assert.Empty(r.Skipped);
    }

    [Theory]
    [InlineData(84, HealthStatus.Ok, HealthReasons.Ok)]
    [InlineData(85, HealthStatus.Warn, HealthReasons.GpuTempWarn)]
    [InlineData(94, HealthStatus.Warn, HealthReasons.GpuTempWarn)]
    [InlineData(95, HealthStatus.Bad, HealthReasons.GpuTempBad)]
    [InlineData(110, HealthStatus.Bad, HealthReasons.GpuTempBad)]
    public void Gpu_temperature_thresholds(int temp, HealthStatus expected, string key)
    {
        var r = HealthEvaluator.Evaluate(Inventory(), Snap(Gpu(temp, name: "RTX")), NoCrashes(), Now);
        Assert.Equal(expected, r.Overall);
        var f = Assert.Single(r.Findings);
        Assert.Equal(key, f.ReasonKey);
        if (expected != HealthStatus.Ok)
        {
            Assert.Equal("RTX", f.Subject);
            Assert.Equal(temp.ToString(), f.Detail);
        }
    }

    [Theory]
    [InlineData(GpuThrottleFlags.Thermal, HealthReasons.GpuThrottleThermal)]
    [InlineData(GpuThrottleFlags.Power, HealthReasons.GpuThrottlePower)]
    [InlineData(GpuThrottleFlags.InsufficientPower, HealthReasons.GpuThrottleInsufficientPower)]
    [InlineData(GpuThrottleFlags.Thermal | GpuThrottleFlags.Power, HealthReasons.GpuThrottleThermal)]
    [InlineData(GpuThrottleFlags.InsufficientPower | GpuThrottleFlags.Power, HealthReasons.GpuThrottleInsufficientPower)]
    public void Gpu_throttle_flags_warn(GpuThrottleFlags flags, string key)
    {
        var r = HealthEvaluator.Evaluate(Inventory(), Snap(Gpu(throttle: flags)), NoCrashes(), Now);
        Assert.Equal(HealthStatus.Warn, r.Overall);
        Assert.Contains(r.Findings, f => f.Component == "gpu.throttle" && f.ReasonKey == key);
    }

    [Theory]
    [InlineData(GpuThrottleFlags.Voltage)]
    [InlineData(GpuThrottleFlags.NoLoad)]
    [InlineData(GpuThrottleFlags.Battery)]
    [InlineData(GpuThrottleFlags.Api)]
    [InlineData(GpuThrottleFlags.Voltage | GpuThrottleFlags.NoLoad)]
    public void Informational_throttle_flags_do_not_warn(GpuThrottleFlags flags)
    {
        var r = HealthEvaluator.Evaluate(Inventory(), Snap(Gpu(throttle: flags)), NoCrashes(), Now);
        Assert.Equal(HealthStatus.Ok, r.Overall);
    }

    [Fact]
    public void Unsupported_vendor_gpu_is_skipped_not_unknown()
    {
        var amd = GpuSensorReading.Unsupported("Radeon", GpuVendor.Amd, UnavailableReason.UnsupportedVendor);
        var r = HealthEvaluator.Evaluate(Inventory(), Snap(amd), NoCrashes(), Now);
        Assert.Equal(HealthStatus.Ok, r.Overall);
        Assert.Contains(r.Skipped, s => s.Component == "gpu.temperature" && s.Subject == "Radeon" && s.ReasonKey == HealthReasons.UnsupportedVendor);
        Assert.Contains(r.Skipped, s => s.Component == "gpu.throttle" && s.ReasonKey == HealthReasons.UnsupportedVendor);
    }

    [Fact]
    public void Failed_gpu_sensor_is_unknown()
    {
        var r = HealthEvaluator.Evaluate(Inventory(), Snap(Gpu(tempC: null, missing: UnavailableReason.QueryFailed)), NoCrashes(), Now);
        Assert.Equal(HealthStatus.Unknown, r.Overall);
        Assert.Contains(r.Findings, f => f.Component == "gpu.temperature" && f.Status == HealthStatus.Unknown && f.ReasonKey == HealthReasons.Unknown);
    }

    [Fact]
    public void Not_sampled_yet_is_unknown_with_its_own_key()
    {
        var r = HealthEvaluator.Evaluate(Inventory(), Snap(Gpu(tempC: null, missing: UnavailableReason.NotSampledYet)), NoCrashes(), Now);
        Assert.Equal(HealthStatus.Unknown, r.Overall);
        Assert.Contains(r.Findings, f => f.ReasonKey == HealthReasons.NotSampledYet);
    }

    [Theory]
    [InlineData(89.9, HealthStatus.Ok)]
    [InlineData(90.0, HealthStatus.Warn)]
    [InlineData(99.0, HealthStatus.Warn)]
    public void Ram_threshold(double usedPct, HealthStatus expected)
    {
        var snap = Snap(Gpu()) with { RamUsedPercent = Sensor.Of(usedPct) };
        var r = HealthEvaluator.Evaluate(Inventory(), snap, NoCrashes(), Now);
        Assert.Equal(expected, r.Overall);
        if (expected == HealthStatus.Warn)
            Assert.Contains(r.Findings, f => f.ReasonKey == HealthReasons.RamHigh && f.Component == "ram.used");
    }

    [Fact]
    public void Ram_missing_is_unknown()
    {
        var snap = Snap(Gpu()) with { RamUsedPercent = Sensor.Missing<double>(UnavailableReason.QueryFailed) };
        var r = HealthEvaluator.Evaluate(Inventory(), snap, NoCrashes(), Now);
        Assert.Equal(HealthStatus.Unknown, r.Overall);
    }

    [Theory]
    [InlineData(100, HealthStatus.Ok, null)]
    [InlineData(10, HealthStatus.Ok, null)]
    [InlineData(9, HealthStatus.Warn, HealthReasons.DiskLowWarn)]
    [InlineData(5, HealthStatus.Warn, HealthReasons.DiskLowWarn)]
    [InlineData(4, HealthStatus.Bad, HealthReasons.DiskLowBad)]
    [InlineData(0, HealthStatus.Bad, HealthReasons.DiskLowBad)]
    public void Disk_free_thresholds(long free, HealthStatus expected, string? key)
    {
        var snap = Snap(Gpu()) with { Drives = [new DriveSensorReading { Name = "D:", TotalBytes = 100, FreeBytes = free }] };
        var r = HealthEvaluator.Evaluate(Inventory(), snap, NoCrashes(), Now);
        Assert.Equal(expected, r.Overall);
        if (key != null)
        {
            var f = Assert.Single(r.Findings);
            Assert.Equal(key, f.ReasonKey);
            Assert.Equal("D:", f.Subject);
        }
    }

    [Fact]
    public void Disk_with_zero_total_is_ignored()
    {
        var snap = Snap(Gpu()) with { Drives = [new DriveSensorReading { Name = "Z:", TotalBytes = 0, FreeBytes = 0 }] };
        Assert.Equal(HealthStatus.Ok, HealthEvaluator.Evaluate(Inventory(), snap, NoCrashes(), Now).Overall);
    }

    [Theory]
    [InlineData(StorageHealth.Healthy, HealthStatus.Ok, null)]
    [InlineData(StorageHealth.Warning, HealthStatus.Bad, HealthReasons.SmartWarning)]
    [InlineData(StorageHealth.Unhealthy, HealthStatus.Bad, HealthReasons.SmartUnhealthy)]
    [InlineData(StorageHealth.Unknown, HealthStatus.Unknown, HealthReasons.SmartUnknown)]
    public void Smart_health(StorageHealth health, HealthStatus expected, string? key)
    {
        var r = HealthEvaluator.Evaluate(Inventory(health), Snap(Gpu()), NoCrashes(), Now);
        Assert.Equal(expected, r.Overall);
        if (key != null)
            Assert.Contains(r.Findings, f => f.Component == "disk.smart" && f.ReasonKey == key && f.Subject == "KINGSTON SNV2S1000G");
    }

    [Fact]
    public void Smart_access_denied_is_unknown_but_not_supported_is_skipped()
    {
        var denied = HealthEvaluator.Evaluate(Inventory(null, UnavailableReason.AccessDenied), Snap(Gpu()), NoCrashes(), Now);
        Assert.Equal(HealthStatus.Unknown, denied.Overall);
        Assert.Contains(denied.Findings, f => f.Component == "disk.smart" && f.Status == HealthStatus.Unknown);

        var unsupported = HealthEvaluator.Evaluate(Inventory(null, UnavailableReason.NotSupported), Snap(Gpu()), NoCrashes(), Now);
        Assert.Equal(HealthStatus.Ok, unsupported.Overall);
        Assert.Contains(unsupported.Skipped, f => f.Component == "disk.smart");
    }

    [Fact]
    public void Old_driver_is_info_only()
    {
        var r = HealthEvaluator.Evaluate(Inventory(driverDate: Now.AddMonths(-13)), Snap(Gpu()), NoCrashes(), Now);
        Assert.Equal(HealthStatus.Info, r.Overall);
        var f = Assert.Single(r.Findings);
        Assert.Equal(HealthReasons.DriverOld, f.ReasonKey);
        Assert.Equal("gpu.driver", f.Component);
    }

    [Fact]
    public void Driver_age_boundary_is_twelve_months()
    {
        Assert.False(HealthEvaluator.IsDriverOld(Now.AddMonths(-12).AddDays(1), Now));
        Assert.True(HealthEvaluator.IsDriverOld(Now.AddMonths(-12), Now));
        Assert.False(HealthEvaluator.IsDriverOld(Now.AddDays(1), Now)); // future-dated packages are not "old"
        var r = HealthEvaluator.Evaluate(Inventory(driverDate: Now.AddMonths(-11)), Snap(Gpu()), NoCrashes(), Now);
        Assert.Equal(HealthStatus.Ok, r.Overall);
    }

    [Fact]
    public void Crash_events_map_to_findings()
    {
        var records = new[]
        {
            new CrashEventRecord(Now.AddDays(-1), "Microsoft-Windows-Kernel-Power", 41, 1),
            new CrashEventRecord(Now.AddDays(-2), "Microsoft-Windows-WHEA-Logger", 18, 1),
            new CrashEventRecord(Now.AddDays(-3), "Display", 4101, 3),
            new CrashEventRecord(Now.AddDays(-4), "nvlddmkm", 153, 2),
            new CrashEventRecord(Now.AddDays(-5), "Microsoft-Windows-WHEA-Logger", 17, 3)
        };
        var crashes = CrashEventReader.Summarize(records, Now, TimeSpan.FromDays(30));
        var r = HealthEvaluator.Evaluate(Inventory(), Snap(Gpu()), crashes, Now);

        Assert.Equal(HealthStatus.Warn, r.Overall);
        Assert.Contains(r.Findings, f => f.ReasonKey == HealthReasons.CrashUnexpectedShutdown && f.Status == HealthStatus.Warn && f.Detail == "1");
        Assert.Contains(r.Findings, f => f.ReasonKey == HealthReasons.CrashWheaFatal && f.Status == HealthStatus.Warn);
        Assert.Contains(r.Findings, f => f.ReasonKey == HealthReasons.CrashDisplayTdr && f.Status == HealthStatus.Warn);
        Assert.Contains(r.Findings, f => f.ReasonKey == HealthReasons.CrashDisplayDriver && f.Status == HealthStatus.Info);
        Assert.Contains(r.Findings, f => f.ReasonKey == HealthReasons.CrashWheaCorrected && f.Status == HealthStatus.Info && f.Detail == "1");
    }

    [Fact]
    public void Corrected_whea_flood_warns()
    {
        var records = Enumerable.Range(0, HealthEvaluator.WheaCorrectedWarnCount)
            .Select(i => new CrashEventRecord(Now.AddMinutes(-i), "Microsoft-Windows-WHEA-Logger", 17, 3));
        var crashes = CrashEventReader.Summarize(records, Now, TimeSpan.FromDays(30));
        var r = HealthEvaluator.Evaluate(Inventory(), Snap(Gpu()), crashes, Now);
        Assert.Equal(HealthStatus.Warn, r.Overall);
        Assert.Contains(r.Findings, f => f.ReasonKey == HealthReasons.CrashWheaCorrected && f.Status == HealthStatus.Warn);
    }

    [Fact]
    public void Unreadable_event_log_is_unknown()
    {
        var crashes = new CrashEventSummary { FromUtc = Now.AddDays(-30), ToUtc = Now, Reason = UnavailableReason.AccessDenied };
        var r = HealthEvaluator.Evaluate(Inventory(), Snap(Gpu()), crashes, Now);
        Assert.Equal(HealthStatus.Unknown, r.Overall);
        Assert.Contains(r.Findings, f => f.Component == "crash" && f.Status == HealthStatus.Unknown);
    }

    [Fact]
    public void Overall_is_the_worst_finding_and_findings_are_sorted_by_severity()
    {
        var snap = Snap(Gpu(90)) with   // Warn
        {
            RamUsedPercent = Sensor.Missing<double>(UnavailableReason.QueryFailed),   // Unknown
            Drives = [new DriveSensorReading { Name = "C:", TotalBytes = 100, FreeBytes = 2 }]   // Bad
        };
        var r = HealthEvaluator.Evaluate(Inventory(driverDate: Now.AddYears(-2)), snap, NoCrashes(), Now); // + Info
        Assert.Equal(HealthStatus.Bad, r.Overall);
        Assert.Equal(4, r.Findings.Count);
        Assert.Equal(HealthStatus.Bad, r.Findings[0].Status);
        Assert.Equal(HealthStatus.Warn, r.Findings[1].Status);
        Assert.Equal(HealthStatus.Unknown, r.Findings[2].Status);
        Assert.Equal(HealthStatus.Info, r.Findings[3].Status);
        Assert.DoesNotContain(r.Findings, f => f.ReasonKey == HealthReasons.Ok);
    }

    [Fact]
    public void Multiple_gpus_are_evaluated_independently()
    {
        var r = HealthEvaluator.Evaluate(Inventory(), Snap(Gpu(70, name: "A"), Gpu(96, name: "B")), NoCrashes(), Now);
        Assert.Equal(HealthStatus.Bad, r.Overall);
        var f = Assert.Single(r.Findings);
        Assert.Equal("B", f.Subject);
    }
}
