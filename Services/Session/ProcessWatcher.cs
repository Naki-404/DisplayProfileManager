using System.Diagnostics;
using System.Management;

namespace Visor.Services;

/// <summary>
/// Watches only configured game exe names (name presence). Does not open game
/// process handles or inject — anti-cheat friendly process presence check.
/// Events carry the normalized key (<see cref="ProcessNames.Key"/>). "Stopped" is raised only
/// when no instance of that exe is left.
/// </summary>
public sealed class ProcessWatcher : IDisposable
{
    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private System.Threading.Timer? _pollTimer;
    private readonly HashSet<string> _watch = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private volatile bool _paused;
    private volatile bool _disposed;
    private int _polling;
    private int _pollRequested;
    private const int PollIntervalMs = 2000;

    public event Action<string>? ProcessStarted;
    public event Action<string>? ProcessStopped;

    public bool IsPaused
    {
        get => _paused;
        set => _paused = value;
    }

    public void SetWatchList(IEnumerable<string> processNames)
    {
        lock (_gate)
        {
            _watch.Clear();
            foreach (var raw in processNames)
            {
                var key = ProcessNames.Key(raw);
                if (key.Length > 0)
                    _watch.Add(key);
            }

            // Keep what Poll already knows for names still watched (no live re-query here — that
            // could swallow a start/stop inside the poll window). Newly added names that are
            // already running are not in _seen, so the next Poll raises ProcessStarted for them.
            _seen.IntersectWith(_watch);
        }
    }

    public void Start()
    {
        Stop();
        _disposed = false;
        try
        {
            _startWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
            _startWatcher.EventArrived += OnStart;
            _startWatcher.Start();

            _stopWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));
            _stopWatcher.EventArrived += OnStop;
            _stopWatcher.Start();

            AppLog.Info("Process watcher started (WMI + light poll).");
        }
        catch (Exception ex)
        {
            AppLog.Error($"WMI watcher unavailable ({ex.Message}), using poll only.");
        }

        // Always poll watch-list only (cheap GetProcessesByName) as reconcile / fallback.
        SeedSeen();
        _pollTimer = new System.Threading.Timer(_ => Poll(), null, 1500, PollIntervalMs);
    }

    public void Stop()
    {
        try { _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
        try { _pollTimer?.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Poll timer dispose"); }
        _pollTimer = null;
        try { _startWatcher?.Stop(); _startWatcher?.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "WMI start watcher stop"); }
        try { _stopWatcher?.Stop(); _stopWatcher?.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "WMI stop watcher stop"); }
        _startWatcher = null;
        _stopWatcher = null;
    }

    private void SeedSeen()
    {
        lock (_gate)
        {
            _seen.Clear();
            foreach (var key in _watch)
            {
                if (IsKeyRunning(key))
                    _seen.Add(key);
            }
        }
    }

    /// <summary>
    /// The only writer of <c>_seen</c>: compares running watched exes with the last poll and raises
    /// Started / Stopped. Non-reentrant; a request that arrives while polling runs one more pass.
    /// </summary>
    private void Poll()
    {
        if (_paused || _disposed) return;
        Interlocked.Exchange(ref _pollRequested, 1);
        if (Interlocked.Exchange(ref _polling, 1) == 1) return; // the running pass picks it up
        try
        {
            while (Interlocked.Exchange(ref _pollRequested, 0) == 1 && !_disposed)
                PollOnce();
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
        // A request that raced with the exit above.
        if (Volatile.Read(ref _pollRequested) == 1 && !_disposed)
            ThreadPool.QueueUserWorkItem(_ => Poll());
    }

    private void PollOnce()
    {
        try
        {
            List<string> watchCopy;
            lock (_gate) watchCopy = _watch.ToList();
            if (watchCopy.Count == 0) return;

            var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in watchCopy)
            {
                if (IsKeyRunning(key))
                    current.Add(key);
            }

            List<string> started;
            List<string> stopped;
            lock (_gate)
            {
                started = current.Where(k => _watch.Contains(k) && !_seen.Contains(k)).ToList();
                stopped = _seen.Where(k => !current.Contains(k)).ToList();
                foreach (var k in started) _seen.Add(k);
                foreach (var k in stopped) _seen.Remove(k);
            }

            foreach (var s in started) ProcessStarted?.Invoke(s);
            foreach (var s in stopped) ProcessStopped?.Invoke(s);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Poll error: {ex.Message}");
        }
    }

    /// <summary>WMI start event for a watched exe → poll now.</summary>
    private void OnStart(object sender, EventArrivedEventArgs e)
    {
        if (_paused || _disposed) return;
        if (IsWatched(e)) TriggerPoll(0);
    }

    /// <summary>
    /// WMI stop event for a watched exe → poll shortly (the exiting process can still be listed for
    /// a moment). Poll raises Stopped only when no instance of the exe is left.
    /// </summary>
    private void OnStop(object sender, EventArrivedEventArgs e)
    {
        if (_paused || _disposed) return;
        if (IsWatched(e)) TriggerPoll(300);
    }

    private bool IsWatched(EventArrivedEventArgs e)
    {
        var key = ProcessNames.Key(e.NewEvent["ProcessName"]?.ToString());
        if (key.Length == 0) return false;
        lock (_gate) return _watch.Contains(key);
    }

    private void TriggerPoll(int dueMs)
    {
        try { _pollTimer?.Change(dueMs, PollIntervalMs); }
        catch (ObjectDisposedException) { }
    }

    public static bool IsProcessRunning(string processName)
    {
        try
        {
            return IsKeyRunning(ProcessNames.Key(processName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Running exe names for UI discovery only — disposes Process handles immediately.</summary>
    public static IEnumerable<string> GetRunningExeNames()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(p.ProcessName))
                    set.Add(p.ProcessName + ".exe");
            }
            catch (InvalidOperationException) { /* exited */ }
            finally { p.Dispose(); }
        }
        return set;
    }

    /// <summary>Key is lower-case without ".exe" — GetProcessesByName is case-insensitive.</summary>
    private static bool IsKeyRunning(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var procs = Process.GetProcessesByName(key);
        try
        {
            return procs.Length > 0;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
