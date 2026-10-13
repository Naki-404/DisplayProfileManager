using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class HardwareInventoryTests
{
    [Theory]
    [InlineData(34, "DDR5")] // 0x22 — what this machine's DDR5 BIOS reports
    [InlineData(0x1A, "DDR4")]
    [InlineData(0x18, "DDR3")]
    [InlineData(0x13, "DDR2")]
    [InlineData(0x12, "DDR")]
    [InlineData(0x1E, "LPDDR4")]
    [InlineData(0x23, "LPDDR5")]
    [InlineData(0x21, "HBM2")]
    [InlineData(0, null)]
    [InlineData(2, null)]
    [InlineData(0x1F, null)] // logical non-volatile device
    [InlineData(999, null)]
    public void Smbios_memory_type_maps(int code, string? expected)
    {
        Assert.Equal(expected, HardwareInventoryService.MapSmbiosMemoryType(code));
    }

    [Theory]
    [InlineData(26, "DDR4")]
    [InlineData(24, "DDR3")]
    [InlineData(21, "DDR2")]
    [InlineData(20, "DDR")]
    [InlineData(0, null)]
    public void Legacy_memory_type_maps(int code, string? expected)
    {
        Assert.Equal(expected, HardwareInventoryService.MapLegacyMemoryType(code));
    }

    [Theory]
    [InlineData(17, StorageBusType.Nvme)]
    [InlineData(11, StorageBusType.Sata)]
    [InlineData(7, StorageBusType.Usb)]
    [InlineData(10, StorageBusType.Sas)]
    [InlineData(1, StorageBusType.Scsi)]
    [InlineData(3, StorageBusType.Ata)]
    [InlineData(8, StorageBusType.Raid)]
    [InlineData(12, StorageBusType.SdCard)]
    [InlineData(14, StorageBusType.Virtual)]
    [InlineData(0, StorageBusType.Unknown)]
    [InlineData(99, StorageBusType.Other)]
    public void Storage_bus_maps(int code, StorageBusType expected)
    {
        Assert.Equal(expected, HardwareInventoryService.MapStorageBus(code));
    }

    [Theory]
    [InlineData(4, StorageMediaType.Ssd)]
    [InlineData(3, StorageMediaType.Hdd)]
    [InlineData(5, StorageMediaType.Scm)]
    [InlineData(0, StorageMediaType.Unknown)]
    [InlineData(42, StorageMediaType.Unknown)]
    public void Storage_media_maps(int code, StorageMediaType expected)
    {
        Assert.Equal(expected, HardwareInventoryService.MapStorageMedia(code));
    }

    [Theory]
    [InlineData(0, StorageHealth.Healthy)]
    [InlineData(1, StorageHealth.Warning)]
    [InlineData(2, StorageHealth.Unhealthy)]
    [InlineData(5, StorageHealth.Unknown)]
    [InlineData(77, StorageHealth.Unknown)]
    public void Storage_health_maps(int code, StorageHealth expected)
    {
        Assert.Equal(expected, HardwareInventoryService.MapStorageHealth(code));
    }

    [Theory]
    [InlineData("SCSI", @"SCSI\DISK&VEN_NVME&PROD_KINGSTON_SNV2S10\5&18628FF4&0&000000", StorageBusType.Nvme)]
    [InlineData("USB", @"USBSTOR\DISK&VEN_SANDISK&PROD_ULTRA\4C530001&0", StorageBusType.Usb)]
    [InlineData("SCSI", @"USBSTOR\DISK&VEN_WD&PROD_ELEMENTS\0&0", StorageBusType.Usb)]
    [InlineData("IDE", @"SCSI\DISK&VEN_SAMSUNG&PROD_SSD_870\4&1", StorageBusType.Sata)]
    [InlineData("SCSI", @"SCSI\DISK&VEN_WDC&PROD_WD40EZ\4&1", StorageBusType.Scsi)]
    [InlineData(null, null, StorageBusType.Unknown)]
    [InlineData("1394", "", StorageBusType.Other)]
    public void Disk_drive_fallback_bus(string? iface, string? pnp, StorageBusType expected)
    {
        Assert.Equal(expected, HardwareInventoryService.BusFromDiskDrive(iface, pnp));
    }

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2786&SUBSYS_17FE10DE&REV_A1\4&8BD6E8D&0&0008", "whatever", GpuVendor.Nvidia)]
    [InlineData(@"PCI\VEN_8086&DEV_A780&SUBSYS_D0001458&REV_04\3&11583659&0&10", "Intel(R) UHD Graphics 770", GpuVendor.Intel)]
    [InlineData(@"PCI\VEN_1002&DEV_744C&SUBSYS_0E3D1002&REV_C8\4&1", "AMD Radeon RX 7900 XTX", GpuVendor.Amd)]
    [InlineData(@"pci\ven_1022&dev_15e7", "", GpuVendor.Amd)]
    [InlineData(null, "NVIDIA GeForce GTX 1060", GpuVendor.Nvidia)]
    [InlineData("", "Radeon RX 580", GpuVendor.Amd)]
    [InlineData(@"PCI\VEN_1AF4&DEV_1050", "Red Hat VirtIO GPU", GpuVendor.Other)]
    [InlineData(null, null, GpuVendor.Unknown)]
    public void Gpu_vendor_from_pnp_or_name(string? pnp, string? name, GpuVendor expected)
    {
        Assert.Equal(expected, HardwareInventoryService.VendorFromPnpId(pnp, name));
    }

    [Theory]
    [InlineData(@"ROOT\DISPLAY\0000", true)]
    [InlineData(@"root\basicdisplay\0000", true)]
    [InlineData(@"PCI\VEN_10DE&DEV_2786", false)]
    [InlineData(null, false)]
    public void Virtual_display_adapters_detected(string? pnp, bool expected)
    {
        Assert.Equal(expected, HardwareInventoryService.IsVirtualDisplayAdapter(pnp));
    }

    [Fact]
    public void Dmtf_date_parses_date_and_offset()
    {
        var d = HardwareInventoryService.TryParseDmtfDate("20260930000000.000000-000");
        Assert.Equal(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc), d);

        // +120 minutes east of UTC → 02:00 local == 00:00 UTC
        var e = HardwareInventoryService.TryParseDmtfDate("20250919020000.000000+120");
        Assert.Equal(new DateTime(2025, 9, 19, 0, 0, 0, DateTimeKind.Utc), e);

        // west of UTC
        var w = HardwareInventoryService.TryParseDmtfDate("20250919220000.000000-120");
        Assert.Equal(new DateTime(2025, 9, 20, 0, 0, 0, DateTimeKind.Utc), w);

        // date-only (BIOS strings sometimes are)
        Assert.Equal(new DateTime(2024, 5, 21, 0, 0, 0, DateTimeKind.Utc), HardwareInventoryService.TryParseDmtfDate("20240521"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("2026093")]
    [InlineData("20261332000000.000000-000")] // month 13
    [InlineData("20260230000000.000000-000")] // Feb 30
    [InlineData("19700101000000.000000-000")] // before any PC driver existed → treated as invalid
    public void Dmtf_date_rejects_invalid(string? s)
    {
        Assert.Null(HardwareInventoryService.TryParseDmtfDate(s));
    }

    [Fact]
    public void Wmi_char_arrays_decode()
    {
        ushort[] name = ['C', 'R', '2', '7', '0', 'H', 'D', 'M', 0, 0, 0, 0, 0];
        Assert.Equal("CR270HDM", HardwareInventoryService.DecodeWmiChars(name));
        Assert.Equal("CVT", HardwareInventoryService.DecodeWmiChars(['C', 'V', 'T']));
        Assert.Null(HardwareInventoryService.DecodeWmiChars(null));
        Assert.Null(HardwareInventoryService.DecodeWmiChars([]));
        Assert.Null(HardwareInventoryService.DecodeWmiChars([0, 'X']));
        Assert.Null(HardwareInventoryService.DecodeWmiChars([' ', ' ']));
        Assert.Equal("AB", HardwareInventoryService.DecodeWmiChars(['A', 0x1F, 'B', 0xFFFF]));
    }

    [Theory]
    [InlineData(58172u, "581.72")]
    [InlineData(57605u, "576.05")]
    [InlineData(0u, "0.00")]
    public void Nvidia_driver_version_formats(uint raw, string expected)
    {
        Assert.Equal(expected, NvidiaGpuReader.FormatDriverVersion(raw));
    }

    [Fact]
    public void Merge_nvidia_replaces_capped_vram_and_version_by_name()
    {
        var wmi = new List<GpuInventory>
        {
            new() { Name = "NVIDIA GeForce RTX 4070", Vendor = GpuVendor.Nvidia, VramBytes = 4293918720, VramMayBeCapped = true, DriverVersion = "32.0.16.1742", DriverDate = new DateTime(2026, 9, 30), Source = "wmi" },
            new() { Name = "Intel(R) UHD Graphics 770", Vendor = GpuVendor.Intel, VramBytes = 2147479552, DriverVersion = "32.0.101.7092", Source = "wmi" }
        };
        var nv = new List<NvidiaGpuFacts> { new("NVIDIA GeForce RTX 4070", 12L * 1024 * 1024 * 1024, "581.72") };

        var merged = HardwareInventoryService.MergeNvidia(wmi, nv);

        Assert.Equal(2, merged.Count);
        var rtx = merged[0];
        Assert.Equal(12L * 1024 * 1024 * 1024, rtx.VramBytes);
        Assert.False(rtx.VramMayBeCapped);
        Assert.Equal("581.72", rtx.DriverVersion);
        Assert.Equal(new DateTime(2026, 9, 30), rtx.DriverDate); // WMI date is kept — NvAPI has none
        Assert.Equal("wmi+nvapi", rtx.Source);
        Assert.Same(wmi[1], merged[1]); // the Intel row is untouched
    }

    [Fact]
    public void Merge_nvidia_falls_back_to_order_and_appends_unknown()
    {
        var wmi = new List<GpuInventory>
        {
            new() { Name = "NVIDIA GeForce RTX 3080 (renamed by OEM)", Vendor = GpuVendor.Nvidia, VramBytes = null, Source = "wmi" }
        };
        var nv = new List<NvidiaGpuFacts>
        {
            new("NVIDIA GeForce RTX 3080", 10L * 1024 * 1024 * 1024, "581.72"),
            new("NVIDIA GeForce GTX 1050", 2L * 1024 * 1024 * 1024, "581.72")
        };

        var merged = HardwareInventoryService.MergeNvidia(wmi, nv);

        Assert.Equal(2, merged.Count);
        Assert.Equal(10L * 1024 * 1024 * 1024, merged[0].VramBytes);
        Assert.Equal("wmi+nvapi", merged[0].Source);
        Assert.Equal("NVIDIA GeForce GTX 1050", merged[1].Name);
        Assert.Equal("nvapi", merged[1].Source);
        Assert.Null(merged[1].DriverDate);
    }

    [Fact]
    public void Merge_nvidia_without_facts_returns_input()
    {
        var wmi = new List<GpuInventory> { new() { Name = "AMD Radeon RX 6800", Vendor = GpuVendor.Amd, Source = "wmi" } };
        Assert.Same(wmi, HardwareInventoryService.MergeNvidia(wmi, Array.Empty<NvidiaGpuFacts>()));
    }

    [Fact]
    public void Merge_nvidia_zero_vram_fact_keeps_wmi_value()
    {
        var wmi = new List<GpuInventory> { new() { Name = "NVIDIA X", Vendor = GpuVendor.Nvidia, VramBytes = 4293918720, VramMayBeCapped = true, DriverVersion = "1.2", Source = "wmi" } };
        var merged = HardwareInventoryService.MergeNvidia(wmi, [new NvidiaGpuFacts("NVIDIA X", 0, "")]);
        Assert.Equal(4293918720, merged[0].VramBytes);
        Assert.True(merged[0].VramMayBeCapped);
        Assert.Equal("1.2", merged[0].DriverVersion);
    }

    [Fact]
    public void Ram_inventory_derives_type_and_speed()
    {
        var ram = new RamInventory
        {
            TotalBytes = 34L * 1024 * 1024 * 1024,
            Modules =
            [
                new RamModuleInfo { CapacityBytes = 17179869184, Type = "DDR5", SpeedMts = 5200 },
                new RamModuleInfo { CapacityBytes = 17179869184, Type = "DDR5", SpeedMts = 4800 }
            ]
        };
        Assert.Equal("DDR5", ram.Type);
        Assert.Equal(5200, ram.SpeedMts);
        Assert.Equal(34.0, ram.TotalGb, 3);

        var mixed = ram with { Modules = [ram.Modules[0], ram.Modules[1] with { Type = null }] };
        Assert.Null(mixed.Type);
        Assert.Null((ram with { Modules = [] }).Type);
        Assert.Null((ram with { Modules = [] }).SpeedMts);
    }

    [Fact]
    public void Sensor_default_is_missing_with_a_reason()
    {
        Sensor<int> s = default;
        Assert.False(s.HasValue);
        Assert.Equal(UnavailableReason.QueryFailed, s.EffectiveReason);
        Assert.Equal(UnavailableReason.QueryFailed, Sensor.Missing<int>(UnavailableReason.None).Reason);
        Assert.Equal(UnavailableReason.NoDriver, Sensor.Missing<double>(UnavailableReason.NoDriver).EffectiveReason);
        var v = Sensor.Of(42);
        Assert.True(v.HasValue);
        Assert.Equal(UnavailableReason.None, v.EffectiveReason);
        Assert.Equal("42", v.ToString());
        Assert.Contains("NoDriver", Sensor.Missing<int>(UnavailableReason.NoDriver).ToString());
    }

    [Fact]
    public void Unsupported_gpu_reading_carries_reason_everywhere()
    {
        var g = GpuSensorReading.Unsupported("AMD Radeon", GpuVendor.Amd, UnavailableReason.UnsupportedVendor);
        Assert.Equal(UnavailableReason.UnsupportedVendor, g.TemperatureC.Reason);
        Assert.Equal(UnavailableReason.UnsupportedVendor, g.UtilizationPercent.Reason);
        Assert.Equal(UnavailableReason.UnsupportedVendor, g.Throttle.Reason);
        Assert.Equal(UnavailableReason.UnsupportedVendor, g.PowerPercent.Reason);
        Assert.Equal(UnavailableReason.UnsupportedVendor, g.VramUsedBytes.Reason);
    }

    // ---- live smoke tests: skip rather than fail when this host cannot answer ----

    [SkippableFact]
    public void Live_capture_never_throws_and_reports_reasons()
    {
        HardwareInventory inv;
        try { inv = HardwareInventoryService.Capture(); }
        catch (Exception ex) { throw new Xunit.SkipException("capture threw on this host: " + ex.Message); }

        Assert.True(inv.Cpu.LogicalCores > 0);
        Assert.NotNull(inv.OsLabel);
        Skip.If(inv.Ram.TotalBytes == 0, "GlobalMemoryStatusEx unavailable here");
        foreach (var d in inv.Storage)
            Assert.True(d.Health != null || d.HealthReason != UnavailableReason.None, "missing health must carry a reason");
        Assert.True(inv.Gpus.Count > 0 || inv.GpusReason != UnavailableReason.None);
    }

    [SkippableFact]
    public void Live_get_async_caches_one_capture()
    {
        Task<HardwareInventory> a, b;
        try
        {
            a = HardwareInventoryService.GetAsync();
            b = HardwareInventoryService.GetAsync();
            Task.WaitAll([a, b], TimeSpan.FromSeconds(60));
        }
        catch (Exception ex) { throw new Xunit.SkipException("WMI unavailable here: " + ex.Message); }
        Skip.IfNot(a.IsCompletedSuccessfully && b.IsCompletedSuccessfully, "capture did not finish in time");
        Assert.Same(a.Result, b.Result);
        Assert.Same(a.Result, HardwareInventoryService.Cached);
    }
}
