namespace Visor.Services;

/// <summary>
/// Why a hardware value is missing. <see cref="None"/> means the value is present.
/// Visor never invents numbers: anything it cannot read is exposed as null plus one of these.
/// </summary>
public enum UnavailableReason
{
    /// <summary>The value is present.</summary>
    None = 0,
    /// <summary>A delta-based sensor needs two samples; the first one has no previous value yet.</summary>
    NotSampledYet,
    /// <summary>Reading this needs a kernel driver Visor deliberately does not ship (CPU / NVMe temperature).</summary>
    NoDriver,
    /// <summary>The vendor API that exposes this is not integrated (AMD / Intel GPU sensors).</summary>
    UnsupportedVendor,
    /// <summary>The OS / WMI provider refused the read for this (non-elevated) user.</summary>
    AccessDenied,
    /// <summary>The device or driver does not expose this value at all.</summary>
    NotSupported,
    /// <summary>The query threw or returned nothing usable (details in the log).</summary>
    QueryFailed,
    /// <summary>There is no such device (no NVIDIA GPU, no active network adapter, ...).</summary>
    NoDevice
}

/// <summary>A value that may be missing for a stated reason. <c>default</c> is "missing, QueryFailed".</summary>
public readonly record struct Sensor<T>(T? Value, UnavailableReason Reason) where T : struct
{
    public bool HasValue => Value.HasValue;

    /// <summary>The reason, never <see cref="UnavailableReason.None"/> when the value is missing.</summary>
    public UnavailableReason EffectiveReason =>
        Value.HasValue ? UnavailableReason.None
        : Reason == UnavailableReason.None ? UnavailableReason.QueryFailed : Reason;

    public static Sensor<T> Of(T value) => new(value, UnavailableReason.None);

    public static Sensor<T> Missing(UnavailableReason reason) =>
        new(null, reason == UnavailableReason.None ? UnavailableReason.QueryFailed : reason);

    public override string ToString() => Value.HasValue ? Value.Value.ToString() ?? "" : "n/a (" + EffectiveReason + ")";
}

public static class Sensor
{
    public static Sensor<T> Of<T>(T value) where T : struct => Sensor<T>.Of(value);
    public static Sensor<T> Missing<T>(UnavailableReason reason) where T : struct => Sensor<T>.Missing(reason);
}

public enum GpuVendor { Unknown = 0, Nvidia, Amd, Intel, Other }

public enum StorageBusType { Unknown = 0, Sata, Nvme, Usb, Sas, Scsi, Ata, Raid, SdCard, Virtual, Other }

public enum StorageMediaType { Unknown = 0, Hdd, Ssd, Scm }

/// <summary>Mirror of <c>MSFT_PhysicalDisk.HealthStatus</c> (0 Healthy, 1 Warning, 2 Unhealthy, 5 Unknown).</summary>
public enum StorageHealth { Healthy = 0, Warning = 1, Unhealthy = 2, Unknown = 5 }

/// <summary><see cref="Source"/> says which API produced the row ("registry", "wmi", "nvapi", "wmi+nvapi").</summary>
public sealed record CpuInventory
{
    public string? Name { get; init; }
    public int? PhysicalCores { get; init; }
    public int LogicalCores { get; init; }
    /// <summary>Nominal clock in MHz (registry <c>~MHz</c>, else <c>Win32_Processor.MaxClockSpeed</c>). Not the live clock.</summary>
    public int? BaseMhz { get; init; }
    public string Source { get; init; } = "";
}

public sealed record RamModuleInfo
{
    public long CapacityBytes { get; init; }
    /// <summary>"DDR5", "DDR4", "LPDDR5", ... from SMBIOS; null when the BIOS reports Unknown.</summary>
    public string? Type { get; init; }
    /// <summary>Configured speed in MT/s when the BIOS reports it, else the rated speed.</summary>
    public int? SpeedMts { get; init; }
    public string? Manufacturer { get; init; }
    public string? PartNumber { get; init; }
}

public sealed record RamInventory
{
    /// <summary>From <c>GlobalMemoryStatusEx</c> (usable physical RAM) — always available.</summary>
    public long TotalBytes { get; init; }
    public double TotalGb => TotalBytes / (1024.0 * 1024 * 1024);
    public IReadOnlyList<RamModuleInfo> Modules { get; init; } = Array.Empty<RamModuleInfo>();
    /// <summary><see cref="UnavailableReason.None"/> when <see cref="Modules"/> came from WMI <c>Win32_PhysicalMemory</c>.</summary>
    public UnavailableReason ModulesReason { get; init; }
    /// <summary>Common module type ("DDR5"), or null when modules disagree / are unknown.</summary>
    public string? Type => Modules.Count == 0 ? null
        : Modules.Select(m => m.Type).Distinct().Count() == 1 ? Modules[0].Type : null;
    /// <summary>Highest configured speed among modules (MT/s).</summary>
    public int? SpeedMts => Modules.Count == 0 ? null : Modules.Max(m => m.SpeedMts);
}

public sealed record GpuInventory
{
    public string Name { get; init; } = "";
    public GpuVendor Vendor { get; init; }
    /// <summary>Dedicated VRAM. NvAPI reports the true size; the WMI fallback (<c>AdapterRAM</c>) is a 32-bit field capped at 4 GB.</summary>
    public long? VramBytes { get; init; }
    /// <summary>True when <see cref="VramBytes"/> came from the WMI 32-bit field and may be capped at 4 GB.</summary>
    public bool VramMayBeCapped { get; init; }
    /// <summary>Vendor-style version ("581.72" for NVIDIA via NvAPI) or the PnP driver version from WMI ("32.0.16.1742").</summary>
    public string? DriverVersion { get; init; }
    /// <summary>Driver package date from WMI (<c>Win32_VideoController.DriverDate</c>, UTC date part).</summary>
    public DateTime? DriverDate { get; init; }
    public string? PnpDeviceId { get; init; }
    public string Source { get; init; } = "";
}

public sealed record StorageInventory
{
    /// <summary><c>MSFT_PhysicalDisk.DeviceId</c> / <c>Win32_DiskDrive.Index</c> (same number space: PhysicalDriveN).</summary>
    public int? DeviceId { get; init; }
    public string Model { get; init; } = "";
    public StorageBusType Bus { get; init; }
    public StorageMediaType Media { get; init; }
    public long SizeBytes { get; init; }
    /// <summary>SMART-derived status from the Storage Management provider; null when it could not be read.</summary>
    public StorageHealth? Health { get; init; }
    public UnavailableReason HealthReason { get; init; }
}

public sealed record BoardInventory
{
    public string? Manufacturer { get; init; }
    public string? Product { get; init; }
    public string? BiosVendor { get; init; }
    public string? BiosVersion { get; init; }
    public DateTime? BiosDate { get; init; }
}

public sealed record MonitorInventory
{
    /// <summary>EDID friendly name ("CR270HDM"); null when the EDID carries none.</summary>
    public string? Name { get; init; }
    /// <summary>Three-letter PnP vendor code from the EDID ("CVT", "DEL", "SAM").</summary>
    public string? ManufacturerCode { get; init; }
    public int? YearOfManufacture { get; init; }
}

/// <summary>
/// Immutable hardware inventory, captured once per app session by <see cref="HardwareInventoryService"/>
/// (refresh on demand). Static facts only — live values live in <see cref="SensorSnapshot"/>.
/// </summary>
public sealed record HardwareInventory
{
    public CpuInventory Cpu { get; init; } = new();
    public RamInventory Ram { get; init; } = new();
    public IReadOnlyList<GpuInventory> Gpus { get; init; } = Array.Empty<GpuInventory>();
    public UnavailableReason GpusReason { get; init; }
    public IReadOnlyList<StorageInventory> Storage { get; init; } = Array.Empty<StorageInventory>();
    public UnavailableReason StorageReason { get; init; }
    public BoardInventory? Board { get; init; }
    public UnavailableReason BoardReason { get; init; }
    /// <summary>"Windows 11 (build 26300)" — see <c>SystemInfoService.FormatWindowsLabel</c>.</summary>
    public string? OsLabel { get; init; }
    public IReadOnlyList<MonitorInventory> Monitors { get; init; } = Array.Empty<MonitorInventory>();
    public UnavailableReason MonitorsReason { get; init; }
    public DateTime CapturedUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Wall-clock cost of the capture (WMI dominates) — for the log, not the UI.</summary>
    public TimeSpan CaptureDuration { get; init; }
}
