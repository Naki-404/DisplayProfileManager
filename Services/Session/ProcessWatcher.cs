using System.Management;

namespace Visor.Services;

/// <summary>
/// Watches only configured game exe names (name presence). Does not open game
/// process handles or inject — anti-cheat friendly process presence check.
/// Events carry the normalized key (<see cref="ProcessNames.Key"/>). "Stopped" is raised only
/// when no instance of that exe is left.
/// </summary>
/// <remarks>
/// Cost model: one <see cref="ProcessSnapshot"/> scan per poll (~0.1 ms for 300 processes, no
/// Process objects, no handles, no per-process strings).
/// WMI start/stop events (when available — they need rights many users lack) trigger an
/// immediate poll; the timer is a reconcile fallback: 2 s while poll-only or a watched game runs,
/// 5 s when WMI is delivering events and nothing is running, stopped when nothing is watched.
/// The timer is only ever (re)armed under <c>_gate</c> together with the state it depends on, so
/// a <see cref="Rearm"/> can never overwrite a poll request with a longer delay or disarm the
/// timer while the watch list is non-empty.
/// </remarks>
public sealed class ProcessWatcher : IDisposable
{
    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private System.Threading.Timer? _pollTimer;
    private readonly HashSet<string> _watch = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    // Scratch for the (non-reentrant) poll loop — reused, never allocated per tick.
    private readonly HashSet<string> _current = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _started = new();
    private readonly List<string> _stopped = new();
    private readonly object _gate = new();
    private volatile bool _paused;
    private volatile bool _disposed;
    private volatile bool _wmiActive;
    private int _polling;
    private int _pollRequested;
    private int _startGeneration;
    private const int FirstPollDelayMs = 1500;
    private const int PollIntervalMs = 2000;
    private const int IdlePollIntervalMs = 5000;
    /// <summary>Win32_ProcessStartTrace / StopTrace may carry only the first 15 characters of the image name.</summary>
    internal const int WmiNameTruncationLength = 15;

    public event Action<string>? ProcessStarted;
    public event Action<string>? ProcessStopped;

    /// <summary>How a WMI event's process name relates to the watch list.</summary>
    internal enum WatchMatch
    {
        None,
        /// <summary>The (normalized) name is a watched key.</summary>
        Exact,
        /// <summary>A 15-character (truncated) name that is a prefix of a longer watched key — poll to find out.</summary>
        Prefix
    }

    public bool IsPaused
    {
        get => _paused;
        set
        {
            bool wasPaused = _paused;
            _paused = value;
            // Resume via Poll() protocol (not TriggerPoll alone) so a concurrent Rearm cannot
            // overwrite a due-0 with IdlePollIntervalMs and delay applying in-pause starts.
            if (wasPaused && !value && !_disposed)
            {
                Interlocked.Exchange(ref _pollRequested, 1);
                ThreadPool.QueueUserWorkItem(_ => Poll());
            }
        }
    }

    public void SetWatchList(IEnumerable<string> processNames)
    {
        bool changed;
        lock (_gate)
        {
            var incoming = NormalizeWatchList(processNames);
            changed = !_watch.SetEquals(incoming);
            if (changed)
            {
                _watch.Clear();
                _watch.UnionWith(incoming);
            }

            // Keep what Poll already knows for names still watched (no live re-query here — that
            // could swallow a start/stop inside the poll window). Newly added names that are
            // already running are not in _seen, so the next Poll raises ProcessStarted for them.
            _seen.IntersectWith(_watch);
        }

        // Config change: reconcile right away instead of waiting for the next tick. This also
        // (re)arms the timer when the list just became non-empty.
        if (changed) TriggerPoll(0);
    }

    /// <summary>Watched keys (snapshot) — diagnostics / tests.</summary>
    internal string[] WatchedKeys
    {
        get { lock (_gate) return _watch.ToArray(); }
    }

    /// <summary>Normalizes configured names to unique keys (<see cref="ProcessNames.Key"/>), skipping blanks.</summary>
    internal static HashSet<string> NormalizeWatchList(IEnumerable<string> processNames)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in processNames)
        {
            var key = ProcessNames.Key(raw);
            if (key.Length > 0)
                keys.Add(key);
        }
        return keys;
    }

    /// <summary>
    /// Started = in <paramref name="current"/> but not in <paramref name="seen"/>; stopped = the reverse.
    /// Output lists are cleared first.
    /// </summary>
    internal static void Diff(HashSet<string> seen, HashSet<string> current, List<string> started, List<string> stopped)
    {
        started.Clear();
        stopped.Clear();
        foreach (var k in current)
            if (!seen.Contains(k)) started.Add(k);
        foreach (var k in seen)
            if (!current.Contains(k)) stopped.Add(k);
    }

    /// <summary>
    /// Classifies a process name from a WMI event against the watched keys. A name of exactly
    /// 15 characters may be truncated (the kernel image name limit): it matches as
    /// <see cref="WatchMatch.Prefix"/> when some longer watched key starts with it (a partial
    /// extension such as "." / ".e" / ".ex" is ignored).
    /// </summary>
    internal static WatchMatch Match(string? processName, IReadOnlyCollection<string> watchedKeys)
    {
        if (string.IsNullOrWhiteSpace(processName) || watchedKeys.Count == 0) return WatchMatch.None;
        var raw = processName.Trim();
        var key = ProcessNames.Key(raw);
        if (key.Length == 0) return WatchMatch.None;
        if (watchedKeys.Contains(key)) return WatchMatch.Exact;
        if (raw.Length != WmiNameTruncationLength) return WatchMatch.None;

        // Candidates: the lower-cased name as is, and without a trailing partial ".exe".
        var candidate = raw.ToLowerInvariant();
        if (IsPrefixOfAny(candidate, watchedKeys)) return WatchMatch.Prefix;
        int dot = candidate.LastIndexOf('.');
        if (dot > 0 && candidate.Length - dot <= 3
            && ".exe".StartsWith(candidate[dot..], StringComparison.Ordinal)
            && IsPrefixOfAny(candidate[..dot], watchedKeys))
            return WatchMatch.Prefix;
        return WatchMatch.None;
    }

    private static bool IsPrefixOfAny(string prefix, IEnumerable<string> keys)
    {
        if (prefix.Length == 0) return false;
        foreach (var k in keys)
        {
            // Equal length counts too: "abcdefghijkl.ex" is "abcdefghijkl.exe" cut at 15 characters.
            if (k.Length >= prefix.Length && k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public void Start()
    {
        Stop();
        _disposed = false;
        int generation = Interlocked.Increment(ref _startGeneration);

        SeedSeen();
        lock (_gate)
        {
            _pollTimer = new System.Threading.Timer(_ => Poll(), null, FirstPollDelayMs, Timeout.Infinite);
        }

        // WMI connect is slow (COM, hundreds of ms) — never on the UI thread. The poll already
        // covers the gap; a Stop() that races this start discards the watchers (generation check).
        ThreadPool.QueueUserWorkItem(_ => StartWmi(generation));
    }

    private void StartWmi(int generation)
    {
        ManagementEventWatcher? start = null;
        ManagementEventWatcher? stop = null;
        try
        {
            start = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
            start.EventArrived += OnStart;
            start.Start();

            stop = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));
            stop.EventArrived += OnStop;
            stop.Start();
        }
        catch (Exception ex)
        {
            DisposeWatcher(start, OnStart);
            DisposeWatcher(stop, OnStop);
            AppLog.Info($"WMI process events unavailable ({ex.Message}) — polling every {PollIntervalMs / 1000} s.");
            return;
        }

        bool adopted;
        lock (_gate)
        {
            adopted = generation == _startGeneration && !_disposed;
            if (adopted)
            {
                _startWatcher = start;
                _stopWatcher = stop;
                _wmiActive = true;
            }
        }

        if (!adopted)
        {
            DisposeWatcher(start, OnStart);
            DisposeWatcher(stop, OnStop);
            return;
        }
        AppLog.Info("Process watcher started (WMI events + light reconcile poll).");
    }

    public void Stop()
    {
        ManagementEventWatcher? start;
        ManagementEventWatcher? stop;
        System.Threading.Timer? timer;
        lock (_gate)
        {
            Interlocked.Increment(ref _startGeneration); // a WMI start still in flight is discarded
            start = _startWatcher;
            stop = _stopWatcher;
            _startWatcher = null;
            _stopWatcher = null;
            _wmiActive = false;
            timer = _pollTimer;
            _pollTimer = null;
        }

        try { timer?.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
        try { timer?.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Poll timer dispose"); }
        DisposeWatcher(start, OnStart);
        DisposeWatcher(stop, OnStop);
    }

    private static void DisposeWatcher(ManagementEventWatcher? w, EventArrivedEventHandler handler)
    {
        if (w == null) return;
        try { w.EventArrived -= handler; } catch (Exception ex) { AppLog.Warn(ex, "WMI watcher unsubscribe"); }
        try { w.Stop(); } catch (Exception ex) { AppLog.Warn(ex, "WMI watcher stop"); }
        try { w.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "WMI watcher dispose"); }
    }

    private void SeedSeen()
    {
        lock (_gate)
        {
            _seen.Clear();
            ProcessSnapshot.RunningKeys(_watch, _seen);
        }
    }

    /// <summary>
    /// The only writer of <c>_seen</c>: compares running watched exes with the last poll and raises
    /// Started / Stopped. Non-reentrant; a request that arrives while polling runs one more pass.
    /// </summary>
    private void Poll()
    {
        // While paused we still reconcile so active sessions can restore on exit;
        // ProcessStarted is suppressed inside PollOnce.
        if (_disposed) { Rearm(); return; }
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
        else
            Rearm();
    }

    private void PollOnce()
    {
        try
        {
            bool raiseStarts;
            lock (_gate)
            {
                if (_watch.Count == 0) return;

                _current.Clear();
                if (!ProcessSnapshot.RunningKeys(_watch, _current))
                    return; // process list unreadable right now — keep the last known state

                Diff(_seen, _current, _started, _stopped);

                if (_paused)
                {
                    // Exit restores only. Leave new starts out of _seen so Resume can apply them.
                    foreach (var k in _stopped) _seen.Remove(k);
                    _started.Clear();
                    raiseStarts = false;
                }
                else
                {
                    foreach (var k in _started) _seen.Add(k);
                    foreach (var k in _stopped) _seen.Remove(k);
                    raiseStarts = true;
                }
            }

            // Only the poll loop touches _started / _stopped; handlers run outside the gate.
            if (raiseStarts)
            {
                foreach (var s in _started) ProcessStarted?.Invoke(s);
            }
            foreach (var s in _stopped) ProcessStopped?.Invoke(s);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Process poll");
        }
    }

    /// <summary>
    /// Schedules the next reconcile tick (one-shot; the interval adapts to the state). Computed
    /// and armed under <c>_gate</c>: a poll requested meanwhile wins (due 0), and the timer is
    /// disarmed only while nothing is watched.
    /// </summary>
    private void Rearm()
    {
        lock (_gate)
        {
            int due;
            if (_disposed) due = Timeout.Infinite;                             // Stop() disposes the timer
            else if (Volatile.Read(ref _pollRequested) == 1) due = 0;         // request raced with the poll
            else if (_watch.Count == 0) due = Timeout.Infinite;                // nothing to look for
            else if (_wmiActive && _seen.Count == 0) due = IdlePollIntervalMs; // events do the work
            else due = PollIntervalMs;
            ArmLocked(due);
        }
    }

    private void ArmLocked(int dueMs)
    {
        try { _pollTimer?.Change(dueMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>WMI start event for a watched exe → poll now (starts ignored while paused).</summary>
    private void OnStart(object sender, EventArrivedEventArgs e)
    {
        if (_disposed) return;
        // A prefix (truncated-name) match only asks for a poll — Started is raised by the poll alone.
        if (IsWatched(e) != WatchMatch.None) TriggerPoll(0);
    }

    /// <summary>
    /// WMI stop event for a watched exe → poll shortly (the exiting process can still be listed for
    /// a moment). Poll raises Stopped only when no instance of the exe is left. Still fires while
    /// paused so active sessions can restore.
    /// </summary>
    private void OnStop(object sender, EventArrivedEventArgs e)
    {
        if (_disposed) return;
        if (IsWatched(e) != WatchMatch.None) TriggerPoll(300);
    }

    private WatchMatch IsWatched(EventArrivedEventArgs e)
    {
        string? name;
        try { name = e.NewEvent["ProcessName"]?.ToString(); }
        catch (ManagementException) { return WatchMatch.None; }
        lock (_gate) return Match(name, _watch);
    }

    /// <summary>
    /// Asks for a poll after <paramref name="dueMs"/>. The request flag is set first so a
    /// concurrent <see cref="Rearm"/> picks due 0 instead of the idle interval.
    /// </summary>
    private void TriggerPoll(int dueMs)
    {
        Interlocked.Exchange(ref _pollRequested, 1);
        lock (_gate) ArmLocked(dueMs);
    }

    /// <summary>One process-list snapshot, no handles opened.</summary>
    public static bool IsProcessRunning(string processName)
    {
        try
        {
            return ProcessSnapshot.IsRunning(ProcessNames.Key(processName));
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "IsProcessRunning");
            return false;
        }
    }

    /// <summary>Keys (ProcessNames.Key form) of <paramref name="processNames"/> that are running — one snapshot.</summary>
    public static HashSet<string> RunningKeys(IEnumerable<string> processNames)
    {
        var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in processNames)
        {
            var key = ProcessNames.Key(n);
            if (key.Length > 0) want.Add(key);
        }
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { ProcessSnapshot.RunningKeys(want, running); }
        catch (Exception ex) { AppLog.Warn(ex, "RunningKeys"); }
        return running;
    }

    /// <summary>Running exe names for UI discovery only.</summary>
    public static IEnumerable<string> GetRunningExeNames() => ProcessSnapshot.AllExeNames();

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
