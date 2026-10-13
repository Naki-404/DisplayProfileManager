using System.Management;
using System.Net.NetworkInformation;

namespace Visor.Services;

/// <summary>Raw interface byte counters at a point in time.</summary>
public readonly record struct AdapterCounters(long RxBytes, long TxBytes, DateTime Utc);

/// <summary>Bytes per second between two counter reads.</summary>
public sealed record ThroughputSample(DateTime Utc, double RxBytesPerSec, double TxBytesPerSec)
{
    public double RxMbps => RxBytesPerSec * 8 / 1_000_000.0;
    public double TxMbps => TxBytesPerSec * 8 / 1_000_000.0;
}

/// <summary>
/// Per-adapter Rx/Tx throughput from <see cref="IPv4InterfaceStatistics"/> deltas with a ring for sparklines.
/// The counter source is injectable so tests never touch a NIC. A tick is a cheap read (~1 ms).
/// </summary>
public sealed class AdapterThroughputMonitor
{
    public const int DefaultCapacity = 120;

    private readonly Func<AdapterCounters?> _source;
    private readonly RingBuffer<ThroughputSample> _ring;
    private AdapterCounters? _prev;

    public AdapterThroughputMonitor(Func<AdapterCounters?> counterSource, int capacity = DefaultCapacity)
    {
        _source = counterSource ?? throw new ArgumentNullException(nameof(counterSource));
        _ring = new RingBuffer<ThroughputSample>(Math.Clamp(capacity, 2, 10_000));
    }

    /// <summary>Monitors the adapter with the given OS Id (from <see cref="NetworkAdapterInfo.Id"/>).</summary>
    public static AdapterThroughputMonitor ForAdapter(string adapterId, int capacity = DefaultCapacity)
        => new(() => ReadCounters(adapterId), capacity);

    public int Capacity => _ring.Capacity;
    public int Count => _ring.Count;
    public IReadOnlyList<ThroughputSample> Snapshot() => _ring.ToArray();
    public ThroughputSample? Last => _ring.Last;

    /// <summary>
    /// Reads the counters and appends a sample. Returns null on the first read, when the source
    /// has no data, when the counters went backwards (adapter reset / wrap) or when no time elapsed.
    /// </summary>
    public ThroughputSample? Tick()
    {
        AdapterCounters? cur;
        try { cur = _source(); }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Adapter counters");
            cur = null;
        }
        if (cur == null)
        {
            _prev = null;
            return null;
        }
        var sample = Delta(_prev, cur.Value);
        _prev = cur;
        if (sample != null) _ring.Add(sample);
        return sample;
    }

    public void Reset()
    {
        _prev = null;
        _ring.Clear();
    }

    /// <summary>Pure delta; null when there is no previous read, a counter decreased (wrap/reset) or Δt ≤ 0.</summary>
    public static ThroughputSample? Delta(AdapterCounters? prev, AdapterCounters cur)
    {
        if (prev == null) return null;
        double seconds = (cur.Utc - prev.Value.Utc).TotalSeconds;
        if (seconds <= 0) return null;
        long rx = cur.RxBytes - prev.Value.RxBytes;
        long tx = cur.TxBytes - prev.Value.TxBytes;
        if (rx < 0 || tx < 0) return null;
        return new ThroughputSample(cur.Utc, rx / seconds, tx / seconds);
    }

    /// <summary>Live counters for an adapter Id, or null when it is gone.</summary>
    public static AdapterCounters? ReadCounters(string adapterId)
    {
        if (string.IsNullOrEmpty(adapterId)) return null;
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Id, adapterId, StringComparison.OrdinalIgnoreCase));
            if (nic == null) return null;
            var st = nic.GetIPStatistics();
            return new AdapterCounters(st.BytesReceived, st.BytesSent, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "GetIPStatistics");
            return null;
        }
    }
}

/// <summary>Slow-path adapter facts (WMI) that the cached <see cref="NetworkInfoService"/> capture must not pay for.</summary>
public static class AdapterDetailsService
{
    /// <summary>
    /// Duplex of the adapter via <c>MSFT_NetAdapter.FullDuplex</c> (root\StandardCimv2; readable by standard
    /// users). Null when WMI is unavailable, the adapter is not found or the value is unknown (Wi-Fi).
    /// </summary>
    public static bool? QueryFullDuplex(int? interfaceIndex)
    {
        if (interfaceIndex is not int idx || idx <= 0) return null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\StandardCimv2",
                "SELECT FullDuplex, MediaConnectState FROM MSFT_NetAdapter WHERE InterfaceIndex = " + idx);
            using var results = searcher.Get();
            foreach (ManagementBaseObject row in results)
            {
                using (row)
                {
                    object? v = row["FullDuplex"];
                    if (v is bool b) return b;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "MSFT_NetAdapter duplex");
        }
        return null;
    }
}
