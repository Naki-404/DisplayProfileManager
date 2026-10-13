using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Visor.Services;

/// <summary>
/// Captures <see cref="HardwareInventory"/> once per session (WMI is slow: hundreds of ms) on the
/// thread pool; <see cref="RefreshAsync"/> re-captures on demand. Read-only everywhere: registry
/// reads, WMI queries, NvAPI queries — no elevation, no drivers. Every block fails independently
/// and reports an <see cref="UnavailableReason"/> instead of a guess. Pure mapping helpers are
/// internal for tests.
/// </summary>
public static class HardwareInventoryService
{
    private static readonly object Gate = new();
    private static HardwareInventory? _cached;
    private static Task<HardwareInventory>? _inflight;

    /// <summary>Last capture, or null before the first one completed.</summary>
    public static HardwareInventory? Cached
    {
        get { lock (Gate) return _cached; }
    }

    /// <summary>Returns the session capture, starting it on the thread pool if needed (concurrent callers share one run).</summary>
    public static Task<HardwareInventory> GetAsync()
    {
        lock (Gate)
        {
            if (_cached != null) return Task.FromResult(_cached);
            return _inflight ??= StartCaptureLocked();
        }
    }

    /// <summary>Forces a new capture (e.g. after a driver install); callers waiting on an in-flight run share it.</summary>
    public static Task<HardwareInventory> RefreshAsync()
    {
        lock (Gate)
        {
            return _inflight ??= StartCaptureLocked();
        }
    }

    private static Task<HardwareInventory> StartCaptureLocked()
    {
        return Task.Run(() =>
        {
            var inv = Capture();
            lock (Gate)
            {
                _cached = inv;
                _inflight = null;
            }
            return inv;
        });
    }

    /// <summary>Blocking capture — call off the UI thread. Never throws.</summary>
    public static HardwareInventory Capture()
    {
        var sw = Stopwatch.StartNew();
        var cpu = ReadCpu();
        var ram = ReadRam();
        var (gpus, gpusReason) = ReadGpus();
        var (storage, storageReason) = ReadStorage();
        var (board, boardReason) = ReadBoard();
        var (monitors, monitorsReason) = ReadMonitors();
        string? os = null;
        try { os = SystemInfoService.FormatWindowsLabel(Environment.OSVersion.Version); }
        catch (Exception ex) { AppLog.Warn(ex, "OS label"); }

        var inv = new HardwareInventory
        {
            Cpu = cpu,
            Ram = ram,
            Gpus = gpus,
            GpusReason = gpusReason,
            Storage = storage,
            StorageReason = storageReason,
            Board = board,
            BoardReason = boardReason,
            OsLabel = os,
            Monitors = monitors,
            MonitorsReason = monitorsReason,
            CapturedUtc = DateTime.UtcNow,
            CaptureDuration = sw.Elapsed
        };
        AppLog.Info($"Hardware inventory captured in {sw.ElapsedMilliseconds} ms: cpu={cpu.Name ?? "?"} ({cpu.PhysicalCores?.ToString() ?? "?"}C/{cpu.LogicalCores}T), " +
                    $"ram={ram.TotalGb:0.#} GB {ram.Type ?? "?"} x{ram.Modules.Count}, gpus={gpus.Count} ({gpusReason}), disks={storage.Count} ({storageReason}), monitors={monitors.Count} ({monitorsReason})");
        return inv;
    }

    // ---------------------------------------------------------------- pure mapping (tested)

    /// <summary>SMBIOS "Memory Device — Type" (SMBIOS 3.x table 7.18.2) → "DDR5" etc.; null for Unknown/Other/legacy.</summary>
    internal static string? MapSmbiosMemoryType(int smbiosType) => smbiosType switch
    {
        0x12 => "DDR",
        0x13 => "DDR2",
        0x14 => "DDR2 FB-DIMM",
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x20 => "HBM",
        0x21 => "HBM2",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        0x24 => "HBM3",
        _ => null
    };

    /// <summary>Legacy <c>Win32_PhysicalMemory.MemoryType</c> (pre-SMBIOS-3 BIOSes report it; 0 = unknown).</summary>
    internal static string? MapLegacyMemoryType(int memoryType) => memoryType switch
    {
        20 => "DDR",
        21 => "DDR2",
        22 => "DDR2 FB-DIMM",
        24 => "DDR3",
        26 => "DDR4",
        _ => null
    };

    /// <summary><c>MSFT_PhysicalDisk.BusType</c> (Storage Management API).</summary>
    internal static StorageBusType MapStorageBus(int busType) => busType switch
    {
        1 => StorageBusType.Scsi,
        3 => StorageBusType.Ata,
        7 => StorageBusType.Usb,
        8 => StorageBusType.Raid,
        10 => StorageBusType.Sas,
        11 => StorageBusType.Sata,
        12 => StorageBusType.SdCard,
        13 => StorageBusType.SdCard,
        14 => StorageBusType.Virtual,
        15 => StorageBusType.Virtual,
        16 => StorageBusType.Virtual,
        17 => StorageBusType.Nvme,
        0 => StorageBusType.Unknown,
        _ => StorageBusType.Other
    };

    /// <summary><c>MSFT_PhysicalDisk.MediaType</c>: 0 Unspecified, 3 HDD, 4 SSD, 5 SCM.</summary>
    internal static StorageMediaType MapStorageMedia(int mediaType) => mediaType switch
    {
        3 => StorageMediaType.Hdd,
        4 => StorageMediaType.Ssd,
        5 => StorageMediaType.Scm,
        _ => StorageMediaType.Unknown
    };

    /// <summary><c>MSFT_PhysicalDisk.HealthStatus</c>: 0 Healthy, 1 Warning, 2 Unhealthy, 5 Unknown.</summary>
    internal static StorageHealth MapStorageHealth(int healthStatus) => healthStatus switch
    {
        0 => StorageHealth.Healthy,
        1 => StorageHealth.Warning,
        2 => StorageHealth.Unhealthy,
        _ => StorageHealth.Unknown
    };

    /// <summary>
    /// Fallback bus from <c>Win32_DiskDrive</c>: <c>InterfaceType</c> is coarse ("SCSI" for every NVMe disk),
    /// so the PnP id is checked first (VEN_NVME → NVMe, USBSTOR → USB).
    /// </summary>
    internal static StorageBusType BusFromDiskDrive(string? interfaceType, string? pnpDeviceId)
    {
        var pnp = pnpDeviceId ?? "";
        if (pnp.Contains("NVME", StringComparison.OrdinalIgnoreCase)) return StorageBusType.Nvme;
        if (pnp.StartsWith("USBSTOR", StringComparison.OrdinalIgnoreCase)) return StorageBusType.Usb;
        return (interfaceType ?? "").ToUpperInvariant() switch
        {
            "USB" => StorageBusType.Usb,
            "IDE" => StorageBusType.Sata,
            "SCSI" => StorageBusType.Scsi,
            "" => StorageBusType.Unknown,
            _ => StorageBusType.Other
        };
    }

    /// <summary>PCI vendor id from a PnP device id ("PCI\VEN_10DE&amp;DEV_2786...").</summary>
    internal static GpuVendor VendorFromPnpId(string? pnpDeviceId, string? name = null)
    {
        var pnp = pnpDeviceId ?? "";
        int i = pnp.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
        if (i >= 0 && i + 8 <= pnp.Length)
        {
            switch (pnp.Substring(i + 4, 4).ToUpperInvariant())
            {
                case "10DE": return GpuVendor.Nvidia;
                case "1002":
                case "1022": return GpuVendor.Amd;
                case "8086": return GpuVendor.Intel;
            }
        }
        var n = (name ?? "").ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro")) return GpuVendor.Nvidia;
        if (n.Contains("radeon") || n.Contains("amd")) return GpuVendor.Amd;
        if (n.Contains("intel")) return GpuVendor.Intel;
        return string.IsNullOrEmpty(pnp) && string.IsNullOrEmpty(n) ? GpuVendor.Unknown : GpuVendor.Other;
    }

    /// <summary>Software display adapters (Virtual Desktop, RDP, Parsec) live under ROOT\ — not hardware.</summary>
    internal static bool IsVirtualDisplayAdapter(string? pnpDeviceId) =>
        (pnpDeviceId ?? "").StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// DMTF datetime ("20260930000000.000000-000" / "20250919000000.000000+000") → UTC. Only the date part
    /// is reliable for driver / BIOS dates (vendors store midnight); the offset is applied when present.
    /// </summary>
    internal static DateTime? TryParseDmtfDate(string? dmtf)
    {
        if (string.IsNullOrWhiteSpace(dmtf) || dmtf.Length < 8) return null;
        if (!int.TryParse(dmtf.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int y) ||
            !int.TryParse(dmtf.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int mo) ||
            !int.TryParse(dmtf.AsSpan(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int d))
            return null;
        int h = 0, mi = 0, s = 0;
        if (dmtf.Length >= 14)
        {
            int.TryParse(dmtf.AsSpan(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out h);
            int.TryParse(dmtf.AsSpan(10, 2), NumberStyles.None, CultureInfo.InvariantCulture, out mi);
            int.TryParse(dmtf.AsSpan(12, 2), NumberStyles.None, CultureInfo.InvariantCulture, out s);
        }
        if (y < 1980 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || s > 59)
            return null;
        var local = new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc);
        int plus = dmtf.LastIndexOfAny(['+', '-']);
        if (plus > 14 && int.TryParse(dmtf.AsSpan(plus + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int offMin) && offMin <= 24 * 60)
        {
            // DMTF offset is "minutes east of UTC": local = utc + off → utc = local - off.
            local = dmtf[plus] == '+' ? local.AddMinutes(-offMin) : local.AddMinutes(offMin);
        }
        return local;
    }

    /// <summary>Decodes the UInt16[] strings WMI's <c>WmiMonitorID</c> uses (NUL-terminated, EDID padding stripped).</summary>
    internal static string? DecodeWmiChars(ushort[]? chars)
    {
        if (chars == null || chars.Length == 0) return null;
        var sb = new System.Text.StringBuilder(chars.Length);
        foreach (var c in chars)
        {
            if (c == 0) break;
            if (c < 32 || c > 126) continue;
            sb.Append((char)c);
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// Replaces WMI's capped VRAM / PnP driver version on NVIDIA rows with NvAPI's real values: matched by
    /// name first, otherwise by order among NVIDIA rows. Facts for GPUs WMI did not list are appended.
    /// </summary>
    internal static IReadOnlyList<GpuInventory> MergeNvidia(IReadOnlyList<GpuInventory> wmi, IReadOnlyList<NvidiaGpuFacts> nv)
    {
        if (nv.Count == 0) return wmi;
        var result = new List<GpuInventory>(wmi.Count + nv.Count);
        var unmatched = new List<NvidiaGpuFacts>(nv);
        var nvidiaRows = new List<int>();
        for (int i = 0; i < wmi.Count; i++)
        {
            result.Add(wmi[i]);
            if (wmi[i].Vendor == GpuVendor.Nvidia) nvidiaRows.Add(i);
        }

        // Pass 1: exact name match.
        foreach (int i in nvidiaRows)
        {
            int hit = unmatched.FindIndex(f => string.Equals(f.FullName.Trim(), wmi[i].Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (hit < 0) continue;
            result[i] = Enrich(wmi[i], unmatched[hit]);
            unmatched.RemoveAt(hit);
        }
        // Pass 2: leftover NVIDIA rows take leftover facts in order.
        foreach (int i in nvidiaRows)
        {
            if (unmatched.Count == 0) break;
            if (result[i].Source.Contains("nvapi", StringComparison.Ordinal)) continue;
            result[i] = Enrich(wmi[i], unmatched[0]);
            unmatched.RemoveAt(0);
        }
        foreach (var f in unmatched)
        {
            result.Add(new GpuInventory
            {
                Name = f.FullName,
                Vendor = GpuVendor.Nvidia,
                VramBytes = f.VramBytes > 0 ? f.VramBytes : null,
                DriverVersion = string.IsNullOrEmpty(f.DriverVersion) ? null : f.DriverVersion,
                Source = "nvapi"
            });
        }
        return result;

        static GpuInventory Enrich(GpuInventory row, NvidiaGpuFacts f) => row with
        {
            VramBytes = f.VramBytes > 0 ? f.VramBytes : row.VramBytes,
            VramMayBeCapped = f.VramBytes > 0 ? false : row.VramMayBeCapped,
            DriverVersion = string.IsNullOrEmpty(f.DriverVersion) ? row.DriverVersion : f.DriverVersion,
            Source = row.Source + "+nvapi"
        };
    }

    // ---------------------------------------------------------------- readers

    private static CpuInventory ReadCpu()
    {
        string? name = null;
        int? baseMhz = null;
        var source = new List<string>(2);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key != null)
            {
                name = (key.GetValue("ProcessorNameString") as string)?.Trim();
                if (key.GetValue("~MHz") is int mhz && mhz > 0) baseMhz = mhz;
                if (name != null || baseMhz != null) source.Add("registry");
            }
        }
        catch (Exception ex) { AppLog.Warn(ex, "CPU registry"); }

        int? physical = null;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, MaxClockSpeed FROM Win32_Processor");
            using var results = searcher.Get();
            int cores = 0;
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    cores += ToInt(obj["NumberOfCores"]);
                    name ??= obj["Name"]?.ToString()?.Trim();
                    if (baseMhz == null && ToInt(obj["MaxClockSpeed"]) is > 0 and var max) baseMhz = max;
                }
            }
            if (cores > 0) physical = cores;
            source.Add("wmi");
        }
        catch (Exception ex) { AppLog.Warn(ex, "CPU WMI"); }

        return new CpuInventory
        {
            Name = string.IsNullOrWhiteSpace(name) ? null : name,
            PhysicalCores = physical,
            LogicalCores = Environment.ProcessorCount,
            BaseMhz = baseMhz,
            Source = string.Join("+", source)
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    /// <summary>(total, available) physical bytes, or (0, 0) on failure. Also used by the sampler.</summary>
    internal static (long Total, long Available) ReadMemoryStatus()
    {
        try
        {
            var st = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref st)) return ((long)st.TotalPhys, (long)st.AvailPhys);
        }
        catch (Exception ex) { AppLog.Warn(ex, "GlobalMemoryStatusEx"); }
        return (0, 0);
    }

    private static RamInventory ReadRam()
    {
        var (total, _) = ReadMemoryStatus();
        var modules = new List<RamModuleInfo>();
        var reason = UnavailableReason.None;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Capacity, SMBIOSMemoryType, MemoryType, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber FROM Win32_PhysicalMemory");
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    int configured = ToInt(obj["ConfiguredClockSpeed"]);
                    int rated = ToInt(obj["Speed"]);
                    modules.Add(new RamModuleInfo
                    {
                        CapacityBytes = ToLong(obj["Capacity"]),
                        Type = MapSmbiosMemoryType(ToInt(obj["SMBIOSMemoryType"])) ?? MapLegacyMemoryType(ToInt(obj["MemoryType"])),
                        SpeedMts = configured > 0 ? configured : rated > 0 ? rated : null,
                        Manufacturer = Clean(obj["Manufacturer"]),
                        PartNumber = Clean(obj["PartNumber"])
                    });
                }
            }
            if (modules.Count == 0) reason = UnavailableReason.NoDevice;
        }
        catch (UnauthorizedAccessException ex) { reason = UnavailableReason.AccessDenied; AppLog.Warn(ex, "RAM modules WMI"); }
        catch (ManagementException ex) { reason = ex.ErrorCode == ManagementStatus.AccessDenied ? UnavailableReason.AccessDenied : UnavailableReason.QueryFailed; AppLog.Warn(ex, "RAM modules WMI"); }
        catch (Exception ex) { reason = UnavailableReason.QueryFailed; AppLog.Warn(ex, "RAM modules WMI"); }

        return new RamInventory { TotalBytes = total, Modules = modules, ModulesReason = reason };
    }

    private static (IReadOnlyList<GpuInventory>, UnavailableReason) ReadGpus()
    {
        var wmi = new List<GpuInventory>();
        var reason = UnavailableReason.None;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController");
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    var pnp = obj["PNPDeviceID"]?.ToString();
                    if (IsVirtualDisplayAdapter(pnp)) continue;
                    var name = Clean(obj["Name"]) ?? "GPU";
                    long vram = ToLong(obj["AdapterRAM"]);
                    wmi.Add(new GpuInventory
                    {
                        Name = name,
                        Vendor = VendorFromPnpId(pnp, name),
                        VramBytes = vram > 0 ? vram : null,
                        VramMayBeCapped = vram >= 4L * 1024 * 1024 * 1024 - 64 * 1024 * 1024, // 32-bit field: ≥ ~4 GB means "at least this"
                        DriverVersion = Clean(obj["DriverVersion"]),
                        DriverDate = TryParseDmtfDate(obj["DriverDate"]?.ToString()),
                        PnpDeviceId = pnp,
                        Source = "wmi"
                    });
                }
            }
        }
        catch (Exception ex) { reason = UnavailableReason.QueryFailed; AppLog.Warn(ex, "GPU WMI"); }

        var (nv, nvReason) = NvidiaGpuReader.ReadFacts();
        if (nvReason is not (UnavailableReason.None or UnavailableReason.NoDriver or UnavailableReason.NoDevice))
            AppLog.Info("NVIDIA facts unavailable: " + nvReason);
        var merged = MergeNvidia(wmi, nv);
        if (merged.Count == 0 && reason == UnavailableReason.None) reason = UnavailableReason.NoDevice;
        return (merged, reason);
    }

    private static (IReadOnlyList<StorageInventory>, UnavailableReason) ReadStorage()
    {
        // Preferred: Storage Management provider — bus/media/SMART health, readable by standard users.
        var list = new List<StorageInventory>();
        UnavailableReason smartReason;
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT DeviceId, FriendlyName, Model, BusType, MediaType, HealthStatus, Size FROM MSFT_PhysicalDisk"));
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    int? id = int.TryParse(obj["DeviceId"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
                    var health = obj["HealthStatus"];
                    list.Add(new StorageInventory
                    {
                        DeviceId = id,
                        Model = Clean(obj["Model"]) ?? Clean(obj["FriendlyName"]) ?? "Disk " + (id?.ToString() ?? "?"),
                        Bus = MapStorageBus(ToInt(obj["BusType"])),
                        Media = MapStorageMedia(ToInt(obj["MediaType"])),
                        SizeBytes = ToLong(obj["Size"]),
                        Health = health == null ? null : MapStorageHealth(ToInt(health)),
                        HealthReason = health == null ? UnavailableReason.NotSupported : UnavailableReason.None
                    });
                }
            }
            if (list.Count > 0) return (list.OrderBy(d => d.DeviceId ?? int.MaxValue).ToList(), UnavailableReason.None);
            smartReason = UnavailableReason.NoDevice;
            AppLog.Info("MSFT_PhysicalDisk returned no rows; falling back to Win32_DiskDrive.");
        }
        catch (UnauthorizedAccessException ex) { smartReason = UnavailableReason.AccessDenied; AppLog.Warn(ex, "MSFT_PhysicalDisk"); }
        catch (ManagementException ex)
        {
            smartReason = ex.ErrorCode == ManagementStatus.AccessDenied ? UnavailableReason.AccessDenied : UnavailableReason.QueryFailed;
            AppLog.Warn(ex, "MSFT_PhysicalDisk");
        }
        catch (Exception ex) { smartReason = UnavailableReason.QueryFailed; AppLog.Warn(ex, "MSFT_PhysicalDisk"); }

        // Fallback: Win32_DiskDrive — no health, coarse bus, no SSD/HDD distinction.
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Index, Model, InterfaceType, Size, PNPDeviceID FROM Win32_DiskDrive");
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    list.Add(new StorageInventory
                    {
                        DeviceId = ToInt(obj["Index"]),
                        Model = Clean(obj["Model"]) ?? "Disk",
                        Bus = BusFromDiskDrive(obj["InterfaceType"]?.ToString(), obj["PNPDeviceID"]?.ToString()),
                        Media = StorageMediaType.Unknown,
                        SizeBytes = ToLong(obj["Size"]),
                        Health = null,
                        HealthReason = smartReason
                    });
                }
            }
            return (list.OrderBy(d => d.DeviceId ?? int.MaxValue).ToList(), list.Count == 0 ? UnavailableReason.NoDevice : UnavailableReason.None);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Win32_DiskDrive");
            return (Array.Empty<StorageInventory>(), UnavailableReason.QueryFailed);
        }
    }

    private static (BoardInventory?, UnavailableReason) ReadBoard()
    {
        string? man = null, prod = null, biosVendor = null, biosVer = null;
        DateTime? biosDate = null;
        bool any = false;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    man = Clean(obj["Manufacturer"]);
                    prod = Clean(obj["Product"]);
                    any = true;
                    break;
                }
            }
        }
        catch (Exception ex) { AppLog.Warn(ex, "Win32_BaseBoard"); }
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    biosVendor = Clean(obj["Manufacturer"]);
                    biosVer = Clean(obj["SMBIOSBIOSVersion"]);
                    biosDate = TryParseDmtfDate(obj["ReleaseDate"]?.ToString());
                    any = true;
                    break;
                }
            }
        }
        catch (Exception ex) { AppLog.Warn(ex, "Win32_BIOS"); }
        if (!any) return (null, UnavailableReason.QueryFailed);
        return (new BoardInventory
        {
            Manufacturer = man,
            Product = prod,
            BiosVendor = biosVendor,
            BiosVersion = biosVer,
            BiosDate = biosDate
        }, UnavailableReason.None);
    }

    private static (IReadOnlyList<MonitorInventory>, UnavailableReason) ReadMonitors()
    {
        var list = new List<MonitorInventory>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\wmi");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT UserFriendlyName, ManufacturerName, YearOfManufacture FROM WmiMonitorID"));
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    int year = ToInt(obj["YearOfManufacture"]);
                    list.Add(new MonitorInventory
                    {
                        Name = DecodeWmiChars(obj["UserFriendlyName"] as ushort[]),
                        ManufacturerCode = DecodeWmiChars(obj["ManufacturerName"] as ushort[]),
                        YearOfManufacture = year >= 1990 && year <= 2100 ? year : null
                    });
                }
            }
            return (list, list.Count == 0 ? UnavailableReason.NoDevice : UnavailableReason.None);
        }
        catch (ManagementException ex)
        {
            // "Not supported" is what a session without EDID monitors (RDP, some VMs) returns.
            AppLog.Info("WmiMonitorID: " + ex.Message);
            return (list, ex.ErrorCode == ManagementStatus.AccessDenied ? UnavailableReason.AccessDenied
                : ex.ErrorCode == ManagementStatus.NotSupported ? UnavailableReason.NotSupported : UnavailableReason.QueryFailed);
        }
        catch (Exception ex)
        {
            AppLog.Info("WmiMonitorID: " + ex.Message);
            return (list, UnavailableReason.QueryFailed);
        }
    }

    private static string? Clean(object? v)
    {
        var s = v?.ToString()?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static int ToInt(object? v)
    {
        try { return v == null ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static long ToLong(object? v)
    {
        try { return v == null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }
}
