using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NvAPIWrapper.GPU;

namespace Visor.Services;

/// <summary>One sampling run: owns the per-run state (previous counters, disk handles, GPU handles).</summary>
internal interface ISensorSampler : IDisposable
{
    SensorSnapshot Sample();
}

/// <summary>
/// 1 Hz live sensors on a dedicated background thread that runs only while someone holds a
/// <see cref="Subscribe"/> token (ref-counted). <see cref="SnapshotReady"/> is raised on the sampler
/// thread — consumers marshal to their dispatcher themselves (no WPF here). Dispose this before
/// <see cref="NvApiHost.Shutdown"/> at app exit.
/// </summary>
public sealed class SensorService : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Func<ISensorSampler> _samplerFactory;
    private readonly TimeSpan _interval;
    private int _subscribers;
    private int _generation;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private SensorSnapshot? _latest;

    public SensorService() : this(() => new LiveSensorSampler(), DefaultInterval) { }

    internal SensorService(Func<ISensorSampler> samplerFactory, TimeSpan interval)
    {
        _samplerFactory = samplerFactory;
        _interval = interval <= TimeSpan.Zero ? DefaultInterval : interval;
    }

    /// <summary>Raised on the sampler thread after every pass (handler exceptions are logged, never propagated).</summary>
    public event EventHandler<SensorSnapshot>? SnapshotReady;

    /// <summary>Most recent snapshot (null before the first pass / after the sampler stopped and was reset).</summary>
    public SensorSnapshot? Latest
    {
        get { lock (_gate) return _latest; }
    }

    public bool IsRunning
    {
        get { lock (_gate) return _thread != null; }
    }

    public int SubscriberCount
    {
        get { lock (_gate) return _subscribers; }
    }

    /// <summary>Starts sampling on the first subscriber; the sampler stops when the last token is disposed.</summary>
    public IDisposable Subscribe()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscribers++;
            if (_subscribers == 1) StartLocked();
        }
        return new Token(this);
    }

    private void Unsubscribe()
    {
        lock (_gate)
        {
            if (_subscribers == 0) return;
            _subscribers--;
            if (_subscribers == 0) StopLocked();
        }
    }

    private void StartLocked()
    {
        var cts = new CancellationTokenSource();
        int gen = ++_generation;
        var thread = new Thread(() => Run(gen, cts.Token))
        {
            IsBackground = true,
            Name = "Visor.Sensors",
            Priority = ThreadPriority.BelowNormal
        };
        _cts = cts;
        _thread = thread;
        thread.Start();
    }

    private void StopLocked()
    {
        // Signal and let the thread unwind on its own; a new subscriber starts a fresh generation.
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _cts = null;
        _thread = null;
        _latest = null;
    }

    private void Run(int generation, CancellationToken token)
    {
        ISensorSampler? sampler = null;
        try
        {
            sampler = _samplerFactory();
            var sw = new Stopwatch();
            while (!token.IsCancellationRequested)
            {
                sw.Restart();
                SensorSnapshot? snap = null;
                try { snap = sampler.Sample(); }
                catch (Exception ex) { AppLog.Warn(ex, "Sensor sample"); }

                if (snap != null && !token.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        if (_generation == generation) _latest = snap;
                    }
                    try { SnapshotReady?.Invoke(this, snap); }
                    catch (Exception ex) { AppLog.Warn(ex, "Sensor subscriber"); }
                }

                var wait = _interval - sw.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    try { if (token.WaitHandle.WaitOne(wait)) break; }
                    catch (ObjectDisposedException) { break; }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Sensor thread");
        }
        finally
        {
            try { sampler?.Dispose(); }
            catch (Exception ex) { AppLog.Warn(ex, "Sensor sampler dispose"); }
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            thread = _thread;
            _subscribers = 0;
            StopLocked();
        }
        if (thread != null && thread != Thread.CurrentThread)
        {
            try { thread.Join(TimeSpan.FromSeconds(2)); } catch { /* exiting */ }
        }
    }

    private sealed class Token(SensorService owner) : IDisposable
    {
        private SensorService? _owner = owner;

        public void Dispose()
        {
            var o = Interlocked.Exchange(ref _owner, null);
            o?.Unsubscribe();
        }
    }
}

/// <summary>
/// The real sampler. Every source is user-mode and admin-free: <c>GetSystemTimes</c> (CPU),
/// <c>GlobalMemoryStatusEx</c> (RAM), NvAPI (NVIDIA GPUs), <c>IOCTL_DISK_PERFORMANCE</c> on a
/// zero-access volume handle (disk throughput), <c>NetworkInterface.GetIPStatistics</c> (network).
/// </summary>
internal sealed class LiveSensorSampler : ISensorSampler
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, int inBufferSize,
        byte[] outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    /// <summary>CTL_CODE(IOCTL_DISK_BASE, 0x0008, METHOD_BUFFERED, FILE_ANY_ACCESS) — works on a 0-access handle.</summary>
    private const uint IoctlDiskPerformance = 0x00070020;
    private const int DiskPerformanceSize = 88;
    private const int AdapterRefreshEvery = 15;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastSampleSeconds = -1;
    private (long Idle, long Kernel, long User)? _cpuPrev;
    private PhysicalGPU[]? _nvGpus;
    private IReadOnlyList<GpuInventory>? _gpuInventory;
    private readonly Dictionary<string, DriveState> _drives = new(StringComparer.OrdinalIgnoreCase);
    private NetworkInterface? _adapter;
    private int _adapterAge = AdapterRefreshEvery;
    private (long Rx, long Tx)? _netPrev;
    private string? _netPrevId;
    private bool _diskPerfLogged;

    private sealed class DriveState : IDisposable
    {
        public SafeFileHandle? Handle;
        public bool Unsupported;
        public (long Read, long Write)? Prev;
        public void Dispose() { Handle?.Dispose(); Handle = null; }
    }

    public SensorSnapshot Sample()
    {
        var sw = Stopwatch.StartNew();
        double now = _clock.Elapsed.TotalSeconds;
        double seconds = _lastSampleSeconds < 0 ? 0 : now - _lastSampleSeconds;
        _lastSampleSeconds = now;

        var cpu = SampleCpu();
        var (ramTotal, ramAvail) = HardwareInventoryService.ReadMemoryStatus();
        long ramUsed = Math.Max(0, ramTotal - ramAvail);
        var ramPct = SensorMath.UsedPercent(ramUsed, ramTotal);

        var snap = new SensorSnapshot
        {
            CapturedUtc = DateTime.UtcNow,
            CpuTotalPercent = cpu,
            RamTotalBytes = ramTotal,
            RamUsedBytes = ramUsed,
            RamUsedPercent = ramPct == null ? Sensor.Missing<double>(UnavailableReason.QueryFailed) : Sensor.Of(ramPct.Value),
            Gpus = SampleGpus(),
            Drives = SampleDrives(seconds),
            Network = SampleNetwork(seconds),
            SampleDuration = sw.Elapsed
        };
        return snap;
    }

    private Sensor<double> SampleCpu()
    {
        try
        {
            if (!GetSystemTimes(out long idle, out long kernel, out long user))
            {
                AppLog.Warn("GetSystemTimes failed: " + Marshal.GetLastWin32Error());
                return Sensor.Missing<double>(UnavailableReason.QueryFailed);
            }
            var prev = _cpuPrev;
            _cpuPrev = (idle, kernel, user);
            if (prev == null) return Sensor.Missing<double>(UnavailableReason.NotSampledYet);
            var pct = SensorMath.CpuPercent(prev.Value.Idle, prev.Value.Kernel, prev.Value.User, idle, kernel, user);
            return pct == null ? Sensor.Missing<double>(UnavailableReason.NotSampledYet) : Sensor.Of(pct.Value);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "GetSystemTimes");
            return Sensor.Missing<double>(UnavailableReason.QueryFailed);
        }
    }

    private IReadOnlyList<GpuSensorReading> SampleGpus()
    {
        var list = new List<GpuSensorReading>(2);
        _nvGpus ??= NvidiaGpuReader.GetGpusOrEmpty();
        foreach (var gpu in _nvGpus)
        {
            try { list.Add(NvidiaGpuReader.Read(gpu)); }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "NvAPI GPU read");
                list.Add(GpuSensorReading.Unsupported("NVIDIA GPU", GpuVendor.Nvidia, UnavailableReason.QueryFailed));
            }
        }

        // Non-NVIDIA adapters from the (cheap, cached) inventory: present, but without live sensors.
        _gpuInventory ??= HardwareInventoryService.Cached?.Gpus;
        if (_gpuInventory != null)
        {
            foreach (var g in _gpuInventory)
            {
                if (g.Vendor == GpuVendor.Nvidia) continue;
                list.Add(GpuSensorReading.Unsupported(g.Name, g.Vendor, UnavailableReason.UnsupportedVendor));
            }
        }
        return list;
    }

    private IReadOnlyList<DriveSensorReading> SampleDrives(double seconds)
    {
        var list = new List<DriveSensorReading>(4);
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Drive list");
            return list;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in drives)
        {
            try
            {
                if (d.DriveType != DriveType.Fixed) continue;   // type check first: IsReady can block on network/optical
                if (!d.IsReady) continue;
                string name = d.Name.TrimEnd('\\');
                seen.Add(name);
                if (!_drives.TryGetValue(name, out var state))
                    _drives[name] = state = new DriveState();

                var (read, write) = ReadDiskPerf(name, state, seconds);
                list.Add(new DriveSensorReading
                {
                    Name = name,
                    TotalBytes = d.TotalSize,
                    FreeBytes = d.AvailableFreeSpace,
                    ReadBytesPerSec = read,
                    WriteBytesPerSec = write
                });
            }
            catch (Exception ex)
            {
                AppLog.Info("Drive sample " + d.Name + ": " + ex.Message);
            }
        }

        // Unplugged volumes: drop their handle.
        foreach (var gone in _drives.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _drives[gone].Dispose();
            _drives.Remove(gone);
        }
        return list;
    }

    private (Sensor<double> Read, Sensor<double> Write) ReadDiskPerf(string drive, DriveState state, double seconds)
    {
        if (state.Unsupported)
            return (Sensor.Missing<double>(UnavailableReason.NotSupported), Sensor.Missing<double>(UnavailableReason.NotSupported));
        try
        {
            if (state.Handle == null || state.Handle.IsInvalid)
            {
                // desiredAccess = 0: a standard user may open the volume for "query only" — no read/write rights needed.
                var h = CreateFileW(@"\\.\" + drive, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (h.IsInvalid)
                {
                    int err = Marshal.GetLastWin32Error();
                    h.Dispose();
                    state.Unsupported = true;
                    LogDiskPerfOnce($"open {drive} failed: error {err}");
                    var reason = err == 5 ? UnavailableReason.AccessDenied : UnavailableReason.NotSupported;
                    return (Sensor.Missing<double>(reason), Sensor.Missing<double>(reason));
                }
                state.Handle = h;
            }

            var buf = new byte[DiskPerformanceSize];
            if (!DeviceIoControl(state.Handle, IoctlDiskPerformance, IntPtr.Zero, 0, buf, buf.Length, out int got, IntPtr.Zero) || got < 16)
            {
                int err = Marshal.GetLastWin32Error();
                state.Unsupported = true;
                LogDiskPerfOnce($"IOCTL_DISK_PERFORMANCE {drive} failed: error {err}");
                var reason = err == 5 ? UnavailableReason.AccessDenied : UnavailableReason.NotSupported;
                return (Sensor.Missing<double>(reason), Sensor.Missing<double>(reason));
            }
            long bytesRead = BitConverter.ToInt64(buf, 0);
            long bytesWritten = BitConverter.ToInt64(buf, 8);
            var prev = state.Prev;
            state.Prev = (bytesRead, bytesWritten);
            return (SensorMath.Rate(prev?.Read, bytesRead, seconds), SensorMath.Rate(prev?.Write, bytesWritten, seconds));
        }
        catch (Exception ex)
        {
            state.Unsupported = true;
            LogDiskPerfOnce($"{drive}: {ex.Message}");
            return (Sensor.Missing<double>(UnavailableReason.QueryFailed), Sensor.Missing<double>(UnavailableReason.QueryFailed));
        }
    }

    private void LogDiskPerfOnce(string msg)
    {
        if (_diskPerfLogged) return;
        _diskPerfLogged = true;
        AppLog.Info("Disk throughput unavailable (" + msg + ")");
    }

    private NetworkSensorReading SampleNetwork(double seconds)
    {
        try
        {
            if (++_adapterAge >= AdapterRefreshEvery)
            {
                _adapterAge = 0;
                _adapter = PickPrimaryAdapter();
            }
            if (_adapter == null)
            {
                _netPrev = null;
                _netPrevId = null;
                return new NetworkSensorReading
                {
                    ReceiveBytesPerSec = Sensor.Missing<double>(UnavailableReason.NoDevice),
                    SendBytesPerSec = Sensor.Missing<double>(UnavailableReason.NoDevice)
                };
            }
            var st = _adapter.GetIPStatistics();
            long rx = st.BytesReceived, tx = st.BytesSent;
            var prev = _netPrevId == _adapter.Id ? _netPrev : null;
            _netPrev = (rx, tx);
            _netPrevId = _adapter.Id;
            return new NetworkSensorReading
            {
                AdapterName = _adapter.Name,
                ReceiveBytesPerSec = SensorMath.Rate(prev?.Rx, rx, seconds),
                SendBytesPerSec = SensorMath.Rate(prev?.Tx, tx, seconds)
            };
        }
        catch (Exception ex)
        {
            AppLog.Info("Network sample: " + ex.Message);
            _adapter = null;
            return new NetworkSensorReading
            {
                ReceiveBytesPerSec = Sensor.Missing<double>(UnavailableReason.QueryFailed),
                SendBytesPerSec = Sensor.Missing<double>(UnavailableReason.QueryFailed)
            };
        }
    }

    /// <summary>The up, non-virtual adapter that owns an IPv4 default gateway; fastest wins on a tie.</summary>
    private static NetworkInterface? PickPrimaryAdapter()
    {
        NetworkInterface? best = null;
        long bestSpeed = -1;
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = n.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue;
                long speed = n.Speed;
                if (best == null || speed > bestSpeed) { best = n; bestSpeed = speed; }
            }
            catch { /* adapter vanished mid-enumeration */ }
        }
        return best;
    }

    public void Dispose()
    {
        foreach (var s in _drives.Values) s.Dispose();
        _drives.Clear();
        _nvGpus = null;
    }
}
