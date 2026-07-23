using System.Diagnostics;
using System.Management;

namespace DisplayProfileManager.Services;

/// <summary>
/// Watches only configured game exe names (name presence). Does not open game
/// process handles or inject — anti-cheat friendly process presence check.
/// </summary>
public sealed class ProcessWatcher : IDisposable
{
    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private System.Threading.Timer? _pollTimer;
    private readonly HashSet<string> _watch = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _paused;

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
            var previous = new HashSet<string>(_watch, StringComparer.OrdinalIgnoreCase);
            _watch.Clear();
            foreach (var raw in processNames)
            {
                var n = Normalize(raw);
                if (!string.IsNullOrWhiteSpace(n))
                    _watch.Add(n);
            }

            // Reseed _seen only for names that were already watched.
            // Newly added names that are already running stay out of _seen so Poll
            // fires ProcessStarted (autosave watch-list refresh).
            _seen.Clear();
            foreach (var name in _watch)
            {
                if (!IsBareRunning(Bare(name))) continue;
                if (previous.Contains(name))
                    _seen.Add(name);
            }
        }
    }

    public void Start()
    {
        Stop();
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
        _pollTimer = new System.Threading.Timer(_ => Poll(), null, 1500, 2000);
    }

    public void Stop()
    {
        try { _startWatcher?.Stop(); _startWatcher?.Dispose(); } catch { }
        try { _stopWatcher?.Stop(); _stopWatcher?.Dispose(); } catch { }
        try { _pollTimer?.Dispose(); } catch { }
        _startWatcher = null;
        _stopWatcher = null;
        _pollTimer = null;
    }

    private void SeedSeen()
    {
        lock (_gate)
        {
            _seen.Clear();
            foreach (var name in _watch)
            {
                if (IsBareRunning(Bare(name)))
                    _seen.Add(name);
            }
        }
    }

    private void Poll()
    {
        if (_paused) return;
        try
        {
            List<string> watchCopy;
            lock (_gate) watchCopy = _watch.ToList();
            if (watchCopy.Count == 0) return;

            var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in watchCopy)
            {
                if (IsBareRunning(Bare(name)))
                    current.Add(name);
            }

            List<string> started;
            List<string> stopped;
            lock (_gate)
            {
                started = current.Except(_seen, StringComparer.OrdinalIgnoreCase).ToList();
                stopped = _seen.Except(current, StringComparer.OrdinalIgnoreCase).ToList();
                _seen.Clear();
                foreach (var c in current) _seen.Add(c);
            }

            foreach (var s in started) ProcessStarted?.Invoke(s);
            foreach (var s in stopped) ProcessStopped?.Invoke(s);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Poll error: {ex.Message}");
        }
    }

    private void OnStart(object sender, EventArrivedEventArgs e)
    {
        if (_paused) return;
        var name = e.NewEvent["ProcessName"]?.ToString();
        if (string.IsNullOrWhiteSpace(name)) return;
        var norm = Normalize(name);
        lock (_gate)
        {
            if (!_watch.Contains(norm)) return;
            if (!_seen.Add(norm)) return;
        }
        ProcessStarted?.Invoke(norm);
    }

    private void OnStop(object sender, EventArrivedEventArgs e)
    {
        if (_paused) return;
        var name = e.NewEvent["ProcessName"]?.ToString();
        if (string.IsNullOrWhiteSpace(name)) return;
        var norm = Normalize(name);
        lock (_gate)
        {
            if (!_watch.Contains(norm)) return;
            if (!_seen.Remove(norm)) return;
        }
        ProcessStopped?.Invoke(norm);
    }

    public static bool IsProcessRunning(string processName)
    {
        try
        {
            return IsBareRunning(Bare(Normalize(processName)));
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
            catch { }
            finally { p.Dispose(); }
        }
        return set;
    }

    private static bool IsBareRunning(string bare)
    {
        if (string.IsNullOrWhiteSpace(bare)) return false;
        var procs = Process.GetProcessesByName(bare);
        try
        {
            return procs.Length > 0;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    private static string Normalize(string name)
    {
        name = name.Trim();
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name += ".exe";
        return name.ToLowerInvariant();
    }

    private static string Bare(string normalizedExe) =>
        normalizedExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalizedExe[..^4]
            : normalizedExe;

    public void Dispose() => Stop();
}
