using System.Net;
using System.Net.NetworkInformation;

namespace Visor.Services;

/// <summary>One sample of a continuous ping series. <see cref="RttMs"/> null = lost.</summary>
public sealed record PingSeriesSample(long Sequence, DateTime Utc, double? RttMs, ProbeErrorCode ErrorCode)
{
    public bool IsLost => RttMs == null;
}

/// <summary>Rolling statistics over a ping series window. Pure; see <see cref="Compute"/>.</summary>
public sealed record PingSeriesStats(
    int Sent,
    int Received,
    int Lost,
    double LossPercent,
    double? MinMs,
    double? P50Ms,
    double? P95Ms,
    double? MaxMs,
    double? AvgMs,
    double? JitterMs,
    double? LastMs,
    /// <summary>Lost samples among the most recent 10 (burst-loss indicator).</summary>
    int RecentLost)
{
    public static readonly PingSeriesStats Empty = new(0, 0, 0, 0, null, null, null, null, null, null, null, 0);

    /// <summary>Stats over samples in time order (null = lost). Jitter = mean |Δ| of consecutive replies.</summary>
    public static PingSeriesStats Compute(IReadOnlyList<double?> rtts)
    {
        if (rtts == null || rtts.Count == 0) return Empty;
        var s = NetworkStats.Summarize(rtts);
        var ok = rtts.Where(r => r is >= 0).Select(r => r!.Value).ToList();
        ok.Sort();
        double? last = rtts[^1];
        int recentLost = 0;
        for (int i = Math.Max(0, rtts.Count - 10); i < rtts.Count; i++)
            if (rtts[i] == null) recentLost++;
        return new PingSeriesStats(
            s.Sent, s.Received, s.Lost, s.LossPercent,
            s.MinMs,
            NetworkStats.PercentileSorted(ok, 50),
            NetworkStats.PercentileSorted(ok, 95),
            s.MaxMs, s.AvgMs, s.JitterMs, last, recentLost);
    }
}

/// <summary>
/// Continuous 1 Hz (configurable) ping to one host with a ring of the last N samples and rolling stats.
/// Starts only on <see cref="Start"/> (user click); <see cref="Stop"/> / <see cref="Dispose"/> end the loop.
/// Events fire on the thread pool — marshal to the UI thread in handlers.
/// </summary>
public sealed class PingMonitor : IDisposable
{
    public const int DefaultCapacity = 300;
    public const int DefaultIntervalMs = 1000;
    public const int DefaultTimeoutMs = 1000;

    private readonly IPinger _pinger;
    private readonly bool _ownsPinger;
    private readonly RingBuffer<PingSeriesSample> _ring;
    private readonly int _intervalMs;
    private readonly int _timeoutMs;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _seq;
    private bool _disposed;

    public IPAddress Target { get; }
    public string TargetLabel { get; }

    /// <summary>Fired after every sample is appended (thread pool).</summary>
    public event Action<PingSeriesSample>? SampleAdded;

    public PingMonitor(IPinger pinger, IPAddress target, string? label = null,
        int capacity = DefaultCapacity, int intervalMs = DefaultIntervalMs, int timeoutMs = DefaultTimeoutMs,
        bool ownsPinger = false)
    {
        _pinger = pinger ?? throw new ArgumentNullException(nameof(pinger));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        TargetLabel = string.IsNullOrWhiteSpace(label) ? target.ToString() : label!;
        _ring = new RingBuffer<PingSeriesSample>(Math.Clamp(capacity, 1, 10_000));
        _intervalMs = Math.Clamp(intervalMs, 1, 60_000);
        _timeoutMs = Math.Clamp(timeoutMs, 100, 10_000);
        _ownsPinger = ownsPinger;
    }

    /// <summary>Real-pinger factory. Timeout is capped at the interval so samples never overlap.</summary>
    public static PingMonitor Create(IPAddress target, string? label = null,
        int capacity = DefaultCapacity, int intervalMs = DefaultIntervalMs)
        => new(new SystemPinger(), target, label, capacity, intervalMs, Math.Min(DefaultTimeoutMs, intervalMs), ownsPinger: true);

    public int Capacity => _ring.Capacity;
    public int Count => _ring.Count;
    public long TotalSent => _ring.TotalAdded;
    public bool IsRunning { get { lock (_gate) return _loop is { IsCompleted: false }; } }

    public IReadOnlyList<PingSeriesSample> Snapshot() => _ring.ToArray();

    /// <summary>RTT window (oldest first; null = lost) for sparklines.</summary>
    public IReadOnlyList<double?> RttWindow() => _ring.ToArray().Select(s => s.RttMs).ToList();

    public PingSeriesStats Stats => PingSeriesStats.Compute(RttWindow());

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loop is { IsCompleted: false }) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
        }
    }

    /// <summary>Requests the loop to end; returns the task to await for completion (never faults).</summary>
    public Task Stop()
    {
        Task? loop;
        lock (_gate)
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            loop = _loop;
        }
        return loop ?? Task.CompletedTask;
    }

    public void Clear() => _ring.Clear();

    /// <summary>Sends exactly one probe and appends it (tests; also usable for a one-shot refresh).</summary>
    public async Task<PingSeriesSample> ProbeOnceAsync(CancellationToken ct)
    {
        PingSeriesSample sample;
        try
        {
            var reply = await _pinger.SendAsync(Target, _timeoutMs, 0, ct).ConfigureAwait(false);
            sample = reply.IsSuccess
                ? new PingSeriesSample(Interlocked.Increment(ref _seq), DateTime.UtcNow, Math.Max(0, reply.RttMs), ProbeErrorCode.None)
                : new PingSeriesSample(Interlocked.Increment(ref _seq), DateTime.UtcNow, null, NetworkProbeService.MapStatus(reply.Status));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            sample = new PingSeriesSample(Interlocked.Increment(ref _seq), DateTime.UtcNow, null, NetworkProbeService.MapException(ex));
        }
        _ring.Add(sample);
        try { SampleAdded?.Invoke(sample); } catch (Exception ex) { AppLog.Warn(ex, "PingMonitor.SampleAdded"); }
        return sample;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;
                await ProbeOnceAsync(ct).ConfigureAwait(false);
                int elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                int wait = _intervalMs - elapsed;
                if (wait > 0)
                    await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "PingMonitor loop");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts?.Cancel(); } catch { /* ignore */ }
        }
        // Never block the caller (often the UI thread): release the pinger once the loop has unwound.
        var loop = _loop ?? Task.CompletedTask;
        var cts = _cts;
        bool ownsPinger = _ownsPinger;
        var pinger = _pinger;
        loop.ContinueWith(_ =>
        {
            cts?.Dispose();
            if (ownsPinger)
            {
                try { pinger.Dispose(); } catch { /* ignore */ }
            }
        }, TaskScheduler.Default);
    }
}
