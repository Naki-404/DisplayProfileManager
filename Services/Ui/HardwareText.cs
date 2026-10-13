using System.Globalization;

namespace Visor.Services;

/// <summary>
/// Pure text helpers for the hardware cards (My PC / Home): loc keys for reasons / flags / bus types and
/// number formatting. No WPF types — the LocTests cover the key mapping.
/// </summary>
public static class HardwareText
{
    public const string Missing = "—";

    /// <summary>Tooltip key explaining why a sensor value is missing.</summary>
    public static string ReasonKey(UnavailableReason reason) => reason switch
    {
        UnavailableReason.NotSampledYet => HealthReasons.NotSampledYet,
        UnavailableReason.NoDriver => HealthReasons.NoDriver,
        UnavailableReason.UnsupportedVendor => HealthReasons.UnsupportedVendor,
        UnavailableReason.AccessDenied => "hw.reason.accessDenied",
        UnavailableReason.NotSupported => "hw.reason.notSupported",
        UnavailableReason.NoDevice => "hw.reason.noDevice",
        UnavailableReason.None => HealthReasons.Ok,
        _ => "hw.reason.queryFailed"
    };

    public static string ReasonText(UnavailableReason reason) => Loc.T(ReasonKey(reason));

    /// <summary>Loc key of one throttle flag (single bit).</summary>
    public static string ThrottleFlagKey(GpuThrottleFlags flag) => flag switch
    {
        GpuThrottleFlags.Thermal => "hw.throttle.thermal",
        GpuThrottleFlags.Power => "hw.throttle.power",
        GpuThrottleFlags.Battery => "hw.throttle.battery",
        GpuThrottleFlags.Api => "hw.throttle.api",
        GpuThrottleFlags.InsufficientPower => "hw.throttle.insufficientPower",
        GpuThrottleFlags.Voltage => "hw.throttle.voltage",
        GpuThrottleFlags.NoLoad => "hw.throttle.noLoad",
        _ => "hw.gpu.throttle.na"
    };

    /// <summary>Flags that mean real throttling (raise a health warning) vs. informational limiter states.</summary>
    public const GpuThrottleFlags WarningFlags = GpuThrottleFlags.Thermal | GpuThrottleFlags.Power | GpuThrottleFlags.InsufficientPower;

    /// <summary>"temperature, power limit" — every set flag, localized, in enum order.</summary>
    public static string ThrottleList(GpuThrottleFlags flags)
    {
        var parts = new List<string>();
        foreach (var f in Enum.GetValues<GpuThrottleFlags>())
        {
            if (f == GpuThrottleFlags.None || (flags & f) == 0) continue;
            parts.Add(Loc.T(ThrottleFlagKey(f)));
        }
        return string.Join(", ", parts);
    }

    public static string BusKey(StorageBusType bus) => bus switch
    {
        StorageBusType.Nvme => "hw.bus.nvme",
        StorageBusType.Sata => "hw.bus.sata",
        StorageBusType.Usb => "hw.bus.usb",
        StorageBusType.Sas => "hw.bus.sas",
        StorageBusType.Scsi => "hw.bus.scsi",
        StorageBusType.Ata => "hw.bus.ata",
        StorageBusType.Raid => "hw.bus.raid",
        StorageBusType.SdCard => "hw.bus.sdCard",
        StorageBusType.Virtual => "hw.bus.virtual",
        StorageBusType.Other => "hw.bus.other",
        _ => ""
    };

    public static string MediaKey(StorageMediaType media) => media switch
    {
        StorageMediaType.Ssd => "hw.media.ssd",
        StorageMediaType.Hdd => "hw.media.hdd",
        StorageMediaType.Scm => "hw.media.scm",
        _ => ""
    };

    public static string SmartKey(StorageHealth? health) => health switch
    {
        StorageHealth.Healthy => "hw.smart.healthy",
        StorageHealth.Warning => "hw.smart.warning",
        StorageHealth.Unhealthy => "hw.smart.unhealthy",
        StorageHealth.Unknown => "hw.smart.unknown",
        _ => "hw.smart.na"
    };

    /// <summary>"Model · NVMe · SSD · 931 GB" (empty parts skipped).</summary>
    public static string DiskLine(StorageInventory disk)
    {
        var parts = new List<string>(3);
        var bus = BusKey(disk.Bus);
        if (bus.Length > 0) parts.Add(Loc.T(bus));
        var media = MediaKey(disk.Media);
        if (media.Length > 0) parts.Add(Loc.T(media));
        if (disk.SizeBytes > 0) parts.Add(SystemInfoService.FormatBytes(disk.SizeBytes));
        return string.Join(" · ", parts);
    }

    /// <summary>Localized finding: "NVIDIA GeForce RTX 4070: GPU is running hot (87 °C)".</summary>
    public static string FindingText(HealthFinding f)
    {
        string reason = f.Detail == null ? Loc.T(f.ReasonKey) : Loc.Tf(f.ReasonKey, f.Detail);
        return string.IsNullOrWhiteSpace(f.Subject) ? reason : f.Subject + ": " + reason;
    }

    /// <summary>Bytes per second → "12.3 MB" (caller appends "/s" through the loc string).</summary>
    public static string FormatRate(double bytesPerSec) =>
        SystemInfoService.FormatBytes((long)Math.Max(0, Math.Round(bytesPerSec)));

    public static string FormatGb(long bytes) =>
        (bytes / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.CurrentCulture) + " GB";

    public static string FormatPercent(double value) =>
        Math.Round(value).ToString(CultureInfo.CurrentCulture);

    public static string FormatUptime(TimeSpan up)
    {
        if (up < TimeSpan.Zero) up = TimeSpan.Zero;
        return up.TotalDays >= 1
            ? Loc.Tf("hw.uptime.days", (int)up.TotalDays, up.Hours)
            : Loc.Tf("hw.uptime.hours", up.Hours, up.Minutes);
    }

    /// <summary>Local short time of a UTC instant ("14:07").</summary>
    public static string LocalTime(DateTime utc) =>
        utc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    public static string LocalDate(DateTime utc) =>
        utc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);

    /// <summary>Highest-severity finding status → dot kind; Unknown and Info are "unknown" (grey), never fake green.</summary>
    public static bool IsAttention(HealthStatus s) => s is HealthStatus.Warn or HealthStatus.Bad;

    /// <summary>GPU temperature verdict for the Home welcome card: hot = any GPU ≥ warn threshold.</summary>
    public static bool? TemperaturesHot(SensorSnapshot? snap)
    {
        if (snap == null) return null;
        bool any = false;
        foreach (var g in snap.Gpus)
        {
            if (!g.TemperatureC.HasValue) continue;
            any = true;
            if (g.TemperatureC.Value!.Value >= HealthEvaluator.GpuTempWarnC) return true;
        }
        return any ? false : null;
    }

    /// <summary>Any GPU driver older than the threshold; null when no driver date is known.</summary>
    public static bool? DriversOld(HardwareInventory? inv, DateTime nowUtc)
    {
        if (inv == null) return null;
        bool any = false;
        foreach (var g in inv.Gpus)
        {
            if (g.DriverDate is not { } d) continue;
            any = true;
            if (HealthEvaluator.IsDriverOld(d, nowUtc)) return true;
        }
        return any ? false : null;
    }

    /// <summary>The volume to feature on Home: the system drive when present, else the fullest one.</summary>
    public static DriveSensorReading? FeaturedDrive(IReadOnlyList<DriveSensorReading> drives)
    {
        if (drives.Count == 0) return null;
        string sys = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
        foreach (var d in drives)
            if (string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase)) return d;
        DriveSensorReading? best = null;
        foreach (var d in drives)
            if (d.TotalBytes > 0 && (best == null || d.FreePercent < best.FreePercent)) best = d;
        return best ?? drives[0];
    }
}
