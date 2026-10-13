namespace Visor.Services;

/// <summary>Ordered by severity: the overall status is the highest one among the findings.</summary>
public enum HealthStatus
{
    Ok = 0,
    /// <summary>Advisory, nothing wrong (e.g. an old driver — "consider updating", never a download).</summary>
    Info = 1,
    /// <summary>A check could not be evaluated (sensor failed / not sampled yet). Intentionally skipped sensors do not count.</summary>
    Unknown = 2,
    Warn = 3,
    Bad = 4
}

/// <summary>
/// One evaluated check. <see cref="ReasonKey"/> is a stable, localizable key (see <see cref="HealthReasons"/>);
/// <see cref="Subject"/> names the device ("C:", "NVIDIA GeForce RTX 4070"); <see cref="Detail"/> is the raw
/// measurement for the UI to format ("87", "4.2").
/// </summary>
public sealed record HealthFinding(string Component, string? Subject, HealthStatus Status, string ReasonKey, string? Detail = null);

public sealed record HealthReport
{
    public HealthStatus Overall { get; init; }
    public IReadOnlyList<HealthFinding> Findings { get; init; } = Array.Empty<HealthFinding>();
    /// <summary>Checks intentionally not evaluated on this machine (no driver / unsupported vendor) with the reason key.</summary>
    public IReadOnlyList<HealthFinding> Skipped { get; init; } = Array.Empty<HealthFinding>();
    public DateTime EvaluatedUtc { get; init; }
}

/// <summary>Localizable reason keys produced by <see cref="HealthEvaluator"/>.</summary>
public static class HealthReasons
{
    public const string Ok = "health.ok";
    public const string GpuTempWarn = "health.gpu.tempWarn";
    public const string GpuTempBad = "health.gpu.tempBad";
    public const string GpuThrottleThermal = "health.gpu.throttleThermal";
    public const string GpuThrottlePower = "health.gpu.throttlePower";
    public const string GpuThrottleInsufficientPower = "health.gpu.throttleInsufficientPower";
    public const string RamHigh = "health.ram.high";
    public const string DiskLowWarn = "health.disk.lowWarn";
    public const string DiskLowBad = "health.disk.lowBad";
    public const string SmartWarning = "health.smart.warning";
    public const string SmartUnhealthy = "health.smart.unhealthy";
    public const string SmartUnknown = "health.smart.unknown";
    public const string DriverOld = "health.driver.old";
    public const string CrashUnexpectedShutdown = "health.crash.unexpectedShutdown";
    public const string CrashWheaFatal = "health.crash.wheaFatal";
    public const string CrashWheaCorrected = "health.crash.wheaCorrected";
    public const string CrashDisplayTdr = "health.crash.displayTdr";
    public const string CrashDisplayDriver = "health.crash.displayDriver";
    public const string Unknown = "health.unknown";
    public const string NotSampledYet = "health.notSampledYet";
    public const string NoDriver = "health.skipped.noDriver";
    public const string UnsupportedVendor = "health.skipped.unsupportedVendor";
}

/// <summary>
/// Pure status derivation from inventory + live snapshot + crash summary. Thresholds are deliberately
/// conservative and documented in docs/HARDWARE-SENSORS.md; nothing here reads hardware or time.
/// </summary>
public static class HealthEvaluator
{
    public const int GpuTempWarnC = 85;
    public const int GpuTempBadC = 95;
    public const double RamUsedWarnPercent = 90;
    public const double DiskFreeWarnPercent = 10;
    public const double DiskFreeBadPercent = 5;
    public const int DriverOldMonths = 12;
    /// <summary>Corrected WHEA errors are survivable; only a flood of them is worth a warning.</summary>
    public const int WheaCorrectedWarnCount = 100;

    private const GpuThrottleFlags ThrottleWorthWarning =
        GpuThrottleFlags.Thermal | GpuThrottleFlags.Power | GpuThrottleFlags.InsufficientPower;

    public static HealthReport Evaluate(HardwareInventory? inventory, SensorSnapshot? snapshot, CrashEventSummary? crashes, DateTime nowUtc)
    {
        var findings = new List<HealthFinding>();
        var skipped = new List<HealthFinding>();

        if (snapshot != null)
        {
            EvaluateGpus(snapshot, findings, skipped);
            EvaluateRam(snapshot, findings);
            EvaluateDrives(snapshot, findings);
            if (!snapshot.CpuTemperatureC.HasValue)
                skipped.Add(new HealthFinding("cpu.temperature", null, HealthStatus.Unknown, ReasonFor(snapshot.CpuTemperatureC.EffectiveReason)));
        }
        if (inventory != null)
        {
            EvaluateStorageHealth(inventory, findings, skipped);
            EvaluateDrivers(inventory, nowUtc, findings);
        }
        if (crashes != null)
            EvaluateCrashes(crashes, findings);

        var overall = findings.Count == 0 ? HealthStatus.Ok : findings.Max(f => f.Status);
        if (findings.Count == 0)
            findings.Add(new HealthFinding("overall", null, HealthStatus.Ok, HealthReasons.Ok));
        return new HealthReport
        {
            Overall = overall,
            Findings = findings.OrderByDescending(f => f.Status).ToList(),
            Skipped = skipped,
            EvaluatedUtc = nowUtc
        };
    }

    /// <summary>True when the driver package is older than <see cref="DriverOldMonths"/> months.</summary>
    public static bool IsDriverOld(DateTime driverDate, DateTime nowUtc) => driverDate <= nowUtc.AddMonths(-DriverOldMonths);

    private static void EvaluateGpus(SensorSnapshot s, List<HealthFinding> findings, List<HealthFinding> skipped)
    {
        foreach (var g in s.Gpus)
        {
            if (g.TemperatureC.HasValue)
            {
                int t = g.TemperatureC.Value!.Value;
                if (t >= GpuTempBadC) findings.Add(new HealthFinding("gpu.temperature", g.Name, HealthStatus.Bad, HealthReasons.GpuTempBad, t.ToString()));
                else if (t >= GpuTempWarnC) findings.Add(new HealthFinding("gpu.temperature", g.Name, HealthStatus.Warn, HealthReasons.GpuTempWarn, t.ToString()));
            }
            else
            {
                AddMissing("gpu.temperature", g.Name, g.TemperatureC.EffectiveReason, findings, skipped);
            }

            if (g.Throttle.HasValue)
            {
                var f = g.Throttle.Value!.Value & ThrottleWorthWarning;
                if ((f & GpuThrottleFlags.Thermal) != 0)
                    findings.Add(new HealthFinding("gpu.throttle", g.Name, HealthStatus.Warn, HealthReasons.GpuThrottleThermal, f.ToString()));
                else if ((f & GpuThrottleFlags.InsufficientPower) != 0)
                    findings.Add(new HealthFinding("gpu.throttle", g.Name, HealthStatus.Warn, HealthReasons.GpuThrottleInsufficientPower, f.ToString()));
                else if ((f & GpuThrottleFlags.Power) != 0)
                    findings.Add(new HealthFinding("gpu.throttle", g.Name, HealthStatus.Warn, HealthReasons.GpuThrottlePower, f.ToString()));
            }
            else
            {
                AddMissing("gpu.throttle", g.Name, g.Throttle.EffectiveReason, findings, skipped);
            }
        }
    }

    private static void EvaluateRam(SensorSnapshot s, List<HealthFinding> findings)
    {
        if (s.RamUsedPercent.HasValue)
        {
            double p = s.RamUsedPercent.Value!.Value;
            if (p >= RamUsedWarnPercent)
                findings.Add(new HealthFinding("ram.used", null, HealthStatus.Warn, HealthReasons.RamHigh, p.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
        }
        else
        {
            findings.Add(new HealthFinding("ram.used", null, HealthStatus.Unknown, ReasonFor(s.RamUsedPercent.EffectiveReason)));
        }
    }

    private static void EvaluateDrives(SensorSnapshot s, List<HealthFinding> findings)
    {
        foreach (var d in s.Drives)
        {
            if (d.TotalBytes <= 0) continue;
            double free = d.FreePercent;
            string detail = free.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            if (free < DiskFreeBadPercent)
                findings.Add(new HealthFinding("disk.free", d.Name, HealthStatus.Bad, HealthReasons.DiskLowBad, detail));
            else if (free < DiskFreeWarnPercent)
                findings.Add(new HealthFinding("disk.free", d.Name, HealthStatus.Warn, HealthReasons.DiskLowWarn, detail));
        }
    }

    private static void EvaluateStorageHealth(HardwareInventory inv, List<HealthFinding> findings, List<HealthFinding> skipped)
    {
        foreach (var disk in inv.Storage)
        {
            switch (disk.Health)
            {
                case StorageHealth.Healthy:
                    break;
                case StorageHealth.Warning:
                    findings.Add(new HealthFinding("disk.smart", disk.Model, HealthStatus.Bad, HealthReasons.SmartWarning));
                    break;
                case StorageHealth.Unhealthy:
                    findings.Add(new HealthFinding("disk.smart", disk.Model, HealthStatus.Bad, HealthReasons.SmartUnhealthy));
                    break;
                case StorageHealth.Unknown:
                    findings.Add(new HealthFinding("disk.smart", disk.Model, HealthStatus.Unknown, HealthReasons.SmartUnknown));
                    break;
                case null:
                    AddMissing("disk.smart", disk.Model, disk.HealthReason, findings, skipped);
                    break;
            }
        }
    }

    private static void EvaluateDrivers(HardwareInventory inv, DateTime nowUtc, List<HealthFinding> findings)
    {
        foreach (var g in inv.Gpus)
        {
            if (g.DriverDate is { } date && IsDriverOld(date, nowUtc))
                findings.Add(new HealthFinding("gpu.driver", g.Name, HealthStatus.Info, HealthReasons.DriverOld, date.ToString("yyyy-MM-dd")));
        }
    }

    private static void EvaluateCrashes(CrashEventSummary c, List<HealthFinding> findings)
    {
        if (c.Reason != UnavailableReason.None)
        {
            findings.Add(new HealthFinding("crash", null, HealthStatus.Unknown, ReasonFor(c.Reason)));
            return;
        }
        Add(CrashEventKind.UnexpectedShutdown, HealthStatus.Warn, HealthReasons.CrashUnexpectedShutdown, 1);
        Add(CrashEventKind.WheaFatal, HealthStatus.Warn, HealthReasons.CrashWheaFatal, 1);
        Add(CrashEventKind.DisplayTdr, HealthStatus.Warn, HealthReasons.CrashDisplayTdr, 1);
        Add(CrashEventKind.DisplayDriverError, HealthStatus.Info, HealthReasons.CrashDisplayDriver, 1);
        int corrected = c.Count(CrashEventKind.WheaCorrected);
        if (corrected >= WheaCorrectedWarnCount)
            findings.Add(new HealthFinding("crash", null, HealthStatus.Warn, HealthReasons.CrashWheaCorrected, corrected.ToString()));
        else if (corrected > 0)
            findings.Add(new HealthFinding("crash", null, HealthStatus.Info, HealthReasons.CrashWheaCorrected, corrected.ToString()));

        void Add(CrashEventKind kind, HealthStatus status, string key, int threshold)
        {
            int n = c.Count(kind);
            if (n >= threshold)
                findings.Add(new HealthFinding("crash", null, status, key, n.ToString()));
        }
    }

    /// <summary>Intentionally unavailable sensors go to <paramref name="skipped"/>; failures become Unknown findings.</summary>
    private static void AddMissing(string component, string? subject, UnavailableReason reason, List<HealthFinding> findings, List<HealthFinding> skipped)
    {
        switch (reason)
        {
            case UnavailableReason.NoDriver:
            case UnavailableReason.UnsupportedVendor:
            case UnavailableReason.NotSupported:
            case UnavailableReason.NoDevice:
                skipped.Add(new HealthFinding(component, subject, HealthStatus.Unknown, ReasonFor(reason)));
                break;
            default:
                findings.Add(new HealthFinding(component, subject, HealthStatus.Unknown, ReasonFor(reason)));
                break;
        }
    }

    internal static string ReasonFor(UnavailableReason reason) => reason switch
    {
        UnavailableReason.NoDriver => HealthReasons.NoDriver,
        UnavailableReason.UnsupportedVendor => HealthReasons.UnsupportedVendor,
        UnavailableReason.NotSampledYet => HealthReasons.NotSampledYet,
        _ => HealthReasons.Unknown
    };
}
