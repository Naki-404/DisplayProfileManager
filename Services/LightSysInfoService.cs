using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace DisplayProfileManager.Services;

/// <summary>Low-overhead PC snapshot for the status strip. Samples every few seconds.</summary>
public sealed class LightSysInfoService : IDisposable
{
    private readonly object _gate = new();
    private PerformanceCounter? _cpu;
    private readonly List<PerformanceCounter> _netRecv = new();
    private readonly List<PerformanceCounter> _netSent = new();
    private string _gpuName = "";
    private string _line = "…";
    private DateTime _lastSample = DateTime.MinValue;
    private DateTime _lastTempSample = DateTime.MinValue;
    private string? _cpuTemp;
    private string? _gpuTemp;
    private bool _disposed;

    public string CurrentLine
    {
        get { lock (_gate) return _line; }
    }

    public LightSysInfoService()
    {
        try
        {
            _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            _ = _cpu.NextValue();
        }
        catch { _cpu = null; }

        InitNetworkCounters();
        InitGpuName();
        Sample();
    }

    private void InitGpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("select Name from Win32_VideoController");
            foreach (ManagementObject obj in searcher.Get())
            {
                var n = obj["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(n)) continue;
                if (n.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)) continue;
                if (n.Contains("Remote", StringComparison.OrdinalIgnoreCase)) continue;
                _gpuName = Shorten(n, 22);
                break;
            }
        }
        catch { }
    }

    private void InitNetworkCounters()
    {
        try
        {
            var cat = new PerformanceCounterCategory("Network Interface");
            foreach (var name in cat.GetInstanceNames())
            {
                if (name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Contains("isatap", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var r = new PerformanceCounter("Network Interface", "Bytes Received/sec", name, true);
                    var s = new PerformanceCounter("Network Interface", "Bytes Sent/sec", name, true);
                    _ = r.NextValue();
                    _ = s.NextValue();
                    _netRecv.Add(r);
                    _netSent.Add(s);
                }
                catch { }
            }
        }
        catch { }
    }

    public string Sample()
    {
        if (_disposed) return CurrentLine;
        if ((DateTime.UtcNow - _lastSample).TotalSeconds < 2.5)
            return CurrentLine;

        _lastSample = DateTime.UtcNow;
        try
        {
            float cpu = 0;
            try { cpu = _cpu?.NextValue() ?? 0; } catch { }

            GetMemory(out double usedGb, out double totalGb);
            var disk = GetPrimaryFreeGb();
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            var (down, up) = SampleNetwork();
            MaybeRefreshTemps();

            var parts = new List<string>
            {
                $"CPU {cpu:0}%"
            };
            if (_cpuTemp != null) parts[^1] = $"CPU {cpu:0}% {_cpuTemp}";

            parts.Add($"RAM {usedGb:0.0}/{totalGb:0.#}G");

            if (!string.IsNullOrEmpty(_gpuName))
            {
                var gpu = _gpuName;
                if (_gpuTemp != null) gpu += $" {_gpuTemp}";
                parts.Add(gpu);
            }
            else if (_gpuTemp != null)
            {
                parts.Add($"GPU {_gpuTemp}");
            }

            if (down != null || up != null)
                parts.Add($"Net ↓{FormatRate(down ?? 0)} ↑{FormatRate(up ?? 0)}");

            if (disk != null) parts.Add($"SSD {disk.Value:0.#}G free");
            parts.Add($"Up {FormatUp(uptime)}");

            // Active adapters count as a light “health” signal
            try
            {
                int online = NetworkInterface.GetAllNetworkInterfaces()
                    .Count(n => n.OperationalStatus == OperationalStatus.Up
                                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel);
                if (online > 0) parts.Add($"{online} NIC");
            }
            catch { }

            var line = string.Join("  ·  ", parts);
            lock (_gate) _line = line;
            return line;
        }
        catch
        {
            lock (_gate) _line = "System status unavailable";
            return CurrentLine;
        }
    }

    private (float? down, float? up) SampleNetwork()
    {
        if (_netRecv.Count == 0) return (null, null);
        float d = 0, u = 0;
        for (int i = 0; i < _netRecv.Count; i++)
        {
            try { d += _netRecv[i].NextValue(); } catch { }
            try { u += _netSent[i].NextValue(); } catch { }
        }
        return (d, u);
    }

    private void MaybeRefreshTemps()
    {
        // Temps are expensive / often unavailable — at most every 30s
        if ((DateTime.UtcNow - _lastTempSample).TotalSeconds < 30) return;
        _lastTempSample = DateTime.UtcNow;

        try
        {
            // Thermal zone (often laptops only)
            using var searcher = new ManagementObjectSearcher(@"root\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["CurrentTemperature"] is uint tenths)
                {
                    // Kelvin * 10
                    double c = tenths / 10.0 - 273.15;
                    if (c is > 0 and < 125)
                    {
                        _cpuTemp = $"{c:0}°C";
                        break;
                    }
                }
            }
        }
        catch { /* desktop often denies */ }

        // Best-effort GPU temp via Win32 (rarely present without vendor tools)
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            // Not temperature — skip. Vendor-specific WMI is unreliable without drivers.
        }
        catch { }

        // AMD/NVIDIA expose nothing standard — leave _gpuTemp null unless we find something light
        _gpuTemp ??= TryReadGpuTempFromWmi();
    }

    private static string? TryReadGpuTempFromWmi()
    {
        // Some OEM boards expose thermal probes; try generic CIM_NumericSensor
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, CurrentReading FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            // Usually empty on consumer PCs
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2", "SELECT Name, CurrentReading FROM Win32_TemperatureProbe");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "";
                if (obj["CurrentReading"] is int reading && reading > 0)
                {
                    // Tenths of Kelvin in some implementations
                    double c = reading > 200 ? reading / 10.0 - 273.15 : reading;
                    if (c is > 20 and < 120 && name.Contains("GPU", StringComparison.OrdinalIgnoreCase))
                        return $"{c:0}°C";
                }
            }
        }
        catch { }

        return null;
    }

    private static string FormatRate(float bytesPerSec)
    {
        if (bytesPerSec < 1024) return $"{bytesPerSec:0}B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:0.#}K/s";
        return $"{bytesPerSec / (1024 * 1024):0.##}M/s";
    }

    private static void GetMemory(out double usedGb, out double totalGb)
    {
        usedGb = 0;
        totalGb = 0;
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                totalGb = status.TotalPhys / (1024.0 * 1024 * 1024);
                var avail = status.AvailPhys / (1024.0 * 1024 * 1024);
                usedGb = Math.Max(0, totalGb - avail);
            }
        }
        catch
        {
            usedGb = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024 * 1024);
            totalGb = usedGb;
        }
    }

    private static double? GetPrimaryFreeGb()
    {
        try
        {
            var sys = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var di = new DriveInfo(sys);
            if (!di.IsReady) return null;
            return di.AvailableFreeSpace / (1024.0 * 1024 * 1024);
        }
        catch { return null; }
    }

    private static string FormatUp(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{t.Minutes}m";
    }

    private static string Shorten(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        _disposed = true;
        _cpu?.Dispose();
        _cpu = null;
        foreach (var c in _netRecv) c.Dispose();
        foreach (var c in _netSent) c.Dispose();
        _netRecv.Clear();
        _netSent.Clear();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
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

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
