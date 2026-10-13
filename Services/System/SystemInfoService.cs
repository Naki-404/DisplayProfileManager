using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Visor.Services;

public sealed class SystemDiskInfo
{
    public required string Name { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
}

public sealed class SystemProcessRow
{
    public int Pid { get; init; }
    public required string Name { get; init; }
    public long WorkingSetBytes { get; init; }
    public double CpuPercent { get; init; }
}

/// <summary>Point-in-time PC snapshot for the System page. Never invents missing sensors.</summary>
public sealed class SystemInfoSnapshot
{
    public string? CpuName { get; init; }
    public int LogicalProcessors { get; init; }
    public long RamTotalBytes { get; init; }
    public long RamAvailableBytes { get; init; }
    public string? GpuLabel { get; init; }
    public string? WindowsVersion { get; init; }
    public string? PowerPlan { get; init; }
    public bool? GameModeEnabled { get; init; }
    public IReadOnlyList<SystemDiskInfo> Disks { get; init; } = Array.Empty<SystemDiskInfo>();
    public DateTime CapturedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// One process as seen by <c>NtQuerySystemInformation(SystemProcessInformation)</c> — no handle was opened.
/// <see cref="CpuTicks100ns"/> = kernel + user time; <see cref="CreateTime"/> disambiguates PID reuse.
/// </summary>
internal readonly record struct ProcessCpuSample(int Pid, string Name, long CreateTime, long CpuTicks100ns, long WorkingSetBytes);

/// <summary>
/// Read-only system facts + on-demand process sample. Failures surface as null / empty.
/// Safe to call from <c>Task.Run</c>: no WPF objects, no process handles, no registry writes.
/// </summary>
public static class SystemInfoService
{
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

    // -- NtQuerySystemInformation(SystemProcessInformation) --------------------------------------

    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    /// <summary>Leading fields of SYSTEM_PROCESS_INFORMATION (the tail is not read; NextEntryOffset walks the list).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_INFORMATION
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UNICODE_STRING ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public IntPtr UniqueProcessKey;
        public IntPtr PeakVirtualSize;
        public IntPtr VirtualSize;
        public uint PageFaultCount;
        public IntPtr PeakWorkingSetSize;
        public IntPtr WorkingSetSize;
    }

    public static SystemInfoSnapshot Capture()
    {
        GetMemory(out long total, out long avail);
        return new SystemInfoSnapshot
        {
            CpuName = ReadCpuName(),
            LogicalProcessors = Environment.ProcessorCount,
            RamTotalBytes = total,
            RamAvailableBytes = avail,
            GpuLabel = GpuVendorDetect.DriverLabel,
            WindowsVersion = ReadWindowsVersion(),
            PowerPlan = PowerPlanSafeAction.ReadAliasOrGuid(),
            GameModeEnabled = GameModeSetting.Read(),
            Disks = ReadDisks(),
            CapturedUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Top processes by CPU then working set from two handle-free system snapshots
    /// <paramref name="sampleMs"/> apart. Never opens a process handle (anti-cheat friendly).
    /// </summary>
    public static IReadOnlyList<SystemProcessRow> SampleTopProcesses(int take = 12, int sampleMs = 200)
    {
        if (take <= 0) return Array.Empty<SystemProcessRow>();
        sampleMs = Math.Clamp(sampleMs, 50, 1000);

        var first = SnapshotProcesses();
        if (first.Count == 0) return Array.Empty<SystemProcessRow>();
        var sw = Stopwatch.StartNew();
        Thread.Sleep(sampleMs);
        var second = SnapshotProcesses();
        long elapsedTicks = sw.Elapsed.Ticks; // 100 ns units, same as kernel/user times
        return ComputeTopProcesses(first, second, elapsedTicks, Environment.ProcessorCount, take);
    }

    /// <summary>Pure CPU% math over two snapshots (tested without live processes).</summary>
    internal static IReadOnlyList<SystemProcessRow> ComputeTopProcesses(
        IReadOnlyList<ProcessCpuSample> first, IReadOnlyList<ProcessCpuSample> second,
        long elapsedTicks100ns, int cores, int take)
    {
        if (take <= 0 || second == null || second.Count == 0) return Array.Empty<SystemProcessRow>();
        cores = Math.Max(1, cores);
        var before = new Dictionary<(int Pid, long Create), long>(first?.Count ?? 0);
        if (first != null)
            foreach (var s in first) before[(s.Pid, s.CreateTime)] = s.CpuTicks100ns;

        double window = Math.Max(1, (double)elapsedTicks100ns) * cores;
        var rows = new List<SystemProcessRow>(second.Count);
        foreach (var s in second)
        {
            if (s.Pid == 0) continue; // Idle
            double pct = 0;
            if (before.TryGetValue((s.Pid, s.CreateTime), out long prev))
            {
                long delta = s.CpuTicks100ns - prev;
                if (delta > 0) pct = Math.Clamp(delta / window * 100.0, 0, 100);
            }
            rows.Add(new SystemProcessRow
            {
                Pid = s.Pid,
                Name = string.IsNullOrWhiteSpace(s.Name) ? "#" + s.Pid : s.Name,
                WorkingSetBytes = s.WorkingSetBytes,
                CpuPercent = pct
            });
        }
        return rows
            .OrderByDescending(r => r.CpuPercent)
            .ThenByDescending(r => r.WorkingSetBytes)
            .Take(take)
            .ToList();
    }

    /// <summary>One pass over the system process list without opening handles. Empty on failure.</summary>
    internal static IReadOnlyList<ProcessCpuSample> SnapshotProcesses()
    {
        int size = 256 * 1024;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                buffer = Marshal.AllocHGlobal(size);
                int status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out int needed);
                if (status == StatusInfoLengthMismatch)
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                    size = Math.Max(size * 2, needed + 64 * 1024);
                    continue;
                }
                if (status != 0)
                {
                    AppLog.Warn("NtQuerySystemInformation(5) status 0x" + status.ToString("X8"));
                    return Array.Empty<ProcessCpuSample>();
                }
                return ParseProcessList(buffer, size);
            }
            return Array.Empty<ProcessCpuSample>();
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Process snapshot");
            return Array.Empty<ProcessCpuSample>();
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<ProcessCpuSample> ParseProcessList(IntPtr buffer, int size)
    {
        var list = new List<ProcessCpuSample>(256);
        int entrySize = Marshal.SizeOf<SYSTEM_PROCESS_INFORMATION>();
        long offset = 0;
        while (offset + entrySize <= size)
        {
            IntPtr p = buffer + (nint)offset;
            var info = Marshal.PtrToStructure<SYSTEM_PROCESS_INFORMATION>(p);
            int pid = unchecked((int)info.UniqueProcessId.ToInt64());
            string name = "";
            if (info.ImageName.Buffer != IntPtr.Zero && info.ImageName.Length > 0)
            {
                try { name = Marshal.PtrToStringUni(info.ImageName.Buffer, info.ImageName.Length / 2) ?? ""; }
                catch { name = ""; }
            }
            if (name.Length == 0)
                name = pid == 0 ? "Idle" : pid == 4 ? "System" : "";
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            list.Add(new ProcessCpuSample(pid, name, info.CreateTime,
                info.KernelTime + info.UserTime, (long)info.WorkingSetSize));
            if (info.NextEntryOffset == 0) break;
            offset += info.NextEntryOffset;
        }
        return list;
    }

    private static void GetMemory(out long total, out long avail)
    {
        total = avail = 0;
        try
        {
            var st = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref st))
            {
                total = (long)st.TotalPhys;
                avail = (long)st.AvailPhys;
            }
        }
        catch (Exception ex) { AppLog.Warn(ex, "GlobalMemoryStatusEx"); }
    }

    private static string? ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString") as string;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch (Exception ex) { AppLog.Warn(ex, "CPU name registry"); }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
        }
        catch (Exception ex) { AppLog.Warn(ex, "CPU name WMI"); }
        return null;
    }

    /// <summary>Registry-free: .NET 5+ reports the real OS version; build ≥ 22000 is Windows 11.</summary>
    private static string? ReadWindowsVersion()
    {
        try
        {
            return FormatWindowsLabel(Environment.OSVersion.Version);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Windows version");
            return null;
        }
    }

    /// <summary>"Windows 11 (build 22631)" / "Windows 10 (build 19045)" / "Windows 6.3 (build 9600)".</summary>
    internal static string FormatWindowsLabel(Version v)
    {
        ArgumentNullException.ThrowIfNull(v);
        string product = v.Major == 10 && v.Build >= 22000 ? "Windows 11"
            : v.Major == 10 ? "Windows 10"
            : $"Windows {v.Major}.{v.Minor}";
        return $"{product} (build {Math.Max(0, v.Build)})";
    }

    private static List<SystemDiskInfo> ReadDisks()
    {
        var list = new List<SystemDiskInfo>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    // DriveType is cheap and never blocks; IsReady on a disconnected network / optical
                    // drive can hang for seconds — so the type check must come first.
                    if (d.DriveType != DriveType.Fixed) continue;
                    if (!d.IsReady) continue;
                    list.Add(new SystemDiskInfo
                    {
                        Name = d.Name.TrimEnd('\\'),
                        TotalBytes = d.TotalSize,
                        FreeBytes = d.AvailableFreeSpace
                    });
                }
                catch { /* skip locked volumes */ }
            }
        }
        catch (Exception ex) { AppLog.Warn(ex, "Disk list"); }
        return list;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{(long)v} {units[u]}" : $"{v:0.#} {units[u]}";
    }
}
