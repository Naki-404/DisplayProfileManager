# Hardware inventory & live sensors

Backend for *My PC* / *Home* (plan §4 "Этап 2"). Everything here runs as a standard user:
**no elevation, no kernel driver, no vendor service**. If a value cannot be read it is exposed as
`null` with an `UnavailableReason` — never estimated, never faked.

Code: `Services\Hardware\` — `HardwareInventory` (models), `HardwareInventoryService`,
`SensorService` / `LiveSensorSampler`, `NvidiaGpuReader`, `HealthEvaluator`, `CrashEventReader`.
Tests: `tests\Visor.Tests\{HardwareInventory,SensorMath,HealthEvaluator,CrashEventReader}Tests.cs`.

## Inventory (once per session, `HardwareInventoryService.GetAsync()`)

| Block | Source | Notes |
|---|---|---|
| CPU name, base MHz | Registry `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0` (read-only) | `~MHz` is the nominal clock, not live |
| CPU physical cores | WMI `Win32_Processor.NumberOfCores` | logical count from `Environment.ProcessorCount` |
| RAM total | `GlobalMemoryStatusEx` | usable physical RAM |
| RAM modules (type / MT/s / vendor) | WMI `Win32_PhysicalMemory` | `SMBIOSMemoryType` → DDR4/DDR5/LPDDR…, legacy `MemoryType` fallback; `ConfiguredClockSpeed` preferred over rated `Speed` |
| GPUs | WMI `Win32_VideoController` + NvAPI for NVIDIA | `AdapterRAM` is a 32-bit WMI field (caps at 4 GB → `VramMayBeCapped`); NvAPI replaces it with the real size and the "581.72" driver version. Driver **date** only exists in WMI. Software adapters under `ROOT\` (Virtual Desktop, RDP) are dropped |
| Storage | WMI `root\Microsoft\Windows\Storage` → `MSFT_PhysicalDisk` | bus (NVMe/SATA/USB/…), media (SSD/HDD), size and **`HealthStatus`** (SMART-derived). Verified readable at Medium integrity (standard user). Fallback `Win32_DiskDrive` gives bus/size only, health = `null` + reason |
| Board / BIOS | WMI `Win32_BaseBoard`, `Win32_BIOS` | BIOS release date parsed from DMTF |
| OS | `SystemInfoService.FormatWindowsLabel` | |
| Monitors | WMI `root\wmi` → `WmiMonitorID` | EDID name / vendor code / year; `NotSupported` on RDP sessions |

Cost: ~600 ms cold, ~100 ms warm (WMI). Runs on the thread pool; concurrent callers share one capture.

## Live sensors (`SensorService`, 1 Hz, only while subscribed)

`Subscribe()` is ref-counted; the sampler thread exists only between the first subscription and the
last dispose. `SnapshotReady` fires on the sampler thread — the UI marshals it. Dispose the service
**before** `NvApiHost.Shutdown()` at exit.

| Sensor | Source | Admin? | Missing when |
|---|---|---|---|
| CPU total % | `GetSystemTimes` delta (idle/kernel/user) | no | first tick → `NotSampledYet` |
| CPU temperature | **not read** | — | always `NoDriver` (see below) |
| RAM used / total / % | `GlobalMemoryStatusEx` | no | — |
| GPU util %, temp °C, core/mem MHz, VRAM used, power % of limit, throttle flags | NvAPI `PhysicalGPU.UsageInformation`, `ThermalInformation` (GPU-target sensor), `CurrentClockFrequencies` (kHz→MHz), `MemoryInformation`, `PowerTopologyInformation`, `PerformanceControl.CurrentPerformanceDecreaseReason` + `CurrentActiveLimit` | no | no NVIDIA driver → `NoDriver`; AMD / Intel → `UnsupportedVendor` (ADL overdrive is not integrated — `AmdAdlColor` only has colour calls); a single unsupported query → `NotSupported` for that field only |
| Disk free / total per volume | `DriveInfo` (fixed, ready drives) | no | — |
| Disk read / write B/s per volume | `IOCTL_DISK_PERFORMANCE` on `\\.\C:` opened with **desiredAccess = 0** | no — verified at Medium integrity on this machine (the IOCTL is `FILE_ANY_ACCESS`) | open/IOCTL fails → `AccessDenied` / `NotSupported`, logged once |
| Network B/s | `NetworkInterface.GetIPStatistics()` delta on the adapter that owns the IPv4 default gateway (fastest wins; re-picked every 15 s) | no | no adapter → `NoDevice`; 32-bit counter wrap is handled, a 64-bit counter going backwards is treated as a reset |

Throttle flags are vendor-neutral `GpuThrottleFlags`: `Thermal`, `Power`, `InsufficientPower`
(these three raise a health warning), `Battery`, `Api`, `Voltage`, `NoLoad` (informational — being
voltage-limited at max boost or idle is normal).

## Health (`HealthEvaluator.Evaluate`, pure)

| Check | Warn | Bad | Note |
|---|---|---|---|
| GPU temperature | ≥ 85 °C | ≥ 95 °C | |
| GPU throttling | Thermal / Power / InsufficientPower flag | — | |
| RAM used | ≥ 90 % | — | |
| Disk free | < 10 % | < 5 % | per volume |
| SMART (`HealthStatus`) | — | Warning / Unhealthy | `Unknown` → status Unknown |
| GPU driver date | — | — | older than 12 months → **Info** "consider updating" (never a download) |
| Crashes (30 days) | Kernel-Power 41, WHEA 18, Display 4101 (≥ 1) ; WHEA 17/19 ≥ 100 | — | nvlddmkm/amdkmdag error-level events and < 100 corrected WHEA → Info |

Overall = worst finding (`Bad > Warn > Unknown > Info > Ok`). Sensors that are *intentionally* not
read (`NoDriver`, `UnsupportedVendor`, `NotSupported`, `NoDevice`) go to `Report.Skipped` and do not
make the overall status Unknown; sensors that *failed* (`QueryFailed`, `AccessDenied`,
`NotSampledYet`) do. Reason keys live in `HealthReasons` for localisation.

## Crash events (`CrashEventReader.Read`)

`EventLogReader` on the **System** log (readable by standard users; the Security log is never
opened), XPath-filtered to the providers above and the last 30 days, newest first, capped at
20 000 records. Counts + last timestamp per kind. Classification and summarisation are pure and
tested with injected records.

## Intentionally not shown

* **CPU temperature / per-core clocks / Vcore** — need MSR or EC access, i.e. a kernel driver
  (what LibreHardwareMonitor / HWiNFO install). Owner rule: no drivers, no elevation. Shown as
  `null` + `NoDriver`, never estimated from load.
* **NVMe / SATA drive temperature** — `IOCTL_STORAGE_QUERY_PROPERTY(StorageDeviceTemperatureProperty)`
  and SMART attribute reads need a handle with read access to the physical drive → admin. Only the
  provider-level `HealthStatus` is read.
* **GPU power in watts, fan RPM** — NvAPI only publishes power as % of the limit publicly; fan RPM
  via the cooler API is unreliable across generations. Percent is shown; watts/RPM are not.
* **AMD / Intel GPU sensors** — would need ADL overdrive / IGCL integration; marked
  `UnsupportedVendor` for now.
* **Per-process GPU usage** — would require D3DKMT handle enumeration; out of scope.

## Threading

`HardwareInventoryService` — static cache behind a lock, capture on the thread pool.
`SensorService` — one background thread (`Visor.Sensors`, BelowNormal), `CancellationToken` per run,
the previous run's thread unwinds on its own; `Latest` is cleared when the last subscriber leaves.
No WPF types anywhere in `Services\Hardware`.
