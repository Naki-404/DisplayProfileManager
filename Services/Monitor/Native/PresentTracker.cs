namespace Visor.Services.FrameCapture;

/// <summary>
/// Turns present-start events into <see cref="FrameRecord"/>s (pure, single-threaded, no clocks
/// other than the events' own QPC timestamps).
/// <para>
/// One frame = one present call of the game's main swap chain; <c>FrameTimeMs</c> is the
/// present-to-present delta of that stream. The same present is usually seen by several
/// providers (DXGI's Present_Start and DxgKrnl's present events, for example), so each event
/// is put on a <i>channel</i> — DXGI, D3D9, or one DxgKrnl event ID — and per process exactly
/// one channel is used: the most preferred one (<see cref="ChannelRank"/>) that has been seen
/// at least <see cref="MinEventsToSelect"/> times and is still active (event within
/// <see cref="ChannelStaleSeconds"/>). Within that channel every swap chain is its own stream;
/// across all target processes the stream with the highest recent present rate (decaying score,
/// half-life <see cref="ScoreHalfLifeSeconds"/>, switch only when another stream beats it by
/// <see cref="SwitchHysteresis"/>) is "the game" and is the only one that emits frames. On a
/// switch, an interval that overlaps the last emitted frame is skipped, so one present seen by two
/// providers is never counted twice (a switch costs at most one frame instead).
/// </para>
/// <para>
/// Deltas ≤ 0 (out-of-order) are dropped; deltas above <see cref="GapMs"/> are treated as a gap
/// (pause, alt-tab, loading screen): no frame is emitted and the stream restarts from the next
/// present. Everything that needs the full present lifecycle (displayed / dropped, display latency,
/// click-to-photon, GPU busy) is not available here and stays null.
/// </para>
/// </summary>
public sealed class PresentTracker
{
    public const int MinEventsToSelect = 3;
    public const double GapMs = 5000;
    public const double ChannelStaleSeconds = 1.0;
    public const double ScoreHalfLifeSeconds = 1.0;
    public const double SwitchHysteresis = 1.25;

    private readonly long _freq;
    private readonly HashSet<int>? _pids;
    private readonly Dictionary<int, ProcessState> _processes = new();
    private long? _originQpc;
    private long _lastEmitQpc;
    private Stream? _main;

    /// <param name="qpcFrequency">Ticks per second of the event timestamps.</param>
    /// <param name="allowedPids">Only these processes count; null / empty = every process.</param>
    public PresentTracker(long qpcFrequency, IEnumerable<int>? allowedPids = null)
    {
        if (qpcFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(qpcFrequency));
        _freq = qpcFrequency;
        var set = allowedPids != null ? new HashSet<int>(allowedPids) : null;
        _pids = set is { Count: > 0 } ? set : null;
    }

    /// <summary>Events accepted (after the PID filter and channel mapping).</summary>
    public long EventsSeen { get; private set; }
    /// <summary>Frames emitted.</summary>
    public long FramesEmitted { get; private set; }

    /// <summary>Process / source / swap chain of the stream currently treated as the game (null before the first selection).</summary>
    public (int ProcessId, PresentSource Source, ushort EventId, ulong SwapChain)? MainStream =>
        _main is { } m ? (m.Pid, m.Source, m.EventId, m.SwapChain) : null;

    /// <summary>
    /// Preference order of channels (lower wins): DXGI, D3D9, then DxgKrnl Present_Info,
    /// PresentHistoryDetailed_Start, PresentHistory_Start, Flip_Info, Blit_Info, FlipMultiPlaneOverlay_Info.
    /// −1 = not a channel we track.
    /// </summary>
    public static int ChannelRank(PresentSource source, ushort eventId) => source switch
    {
        PresentSource.Dxgi => eventId is PresentProviders.DxgiPresentStart or PresentProviders.DxgiPresentMpoStart ? 0 : -1,
        PresentSource.D3d9 => eventId == PresentProviders.D3d9PresentStart ? 1 : -1,
        PresentSource.DxgKrnl => eventId switch
        {
            PresentProviders.DxgkPresentInfo => 2,
            PresentProviders.DxgkPresentHistoryDetailedStart => 3,
            PresentProviders.DxgkPresentHistoryStart => 4,
            PresentProviders.DxgkFlipInfo => 5,
            PresentProviders.DxgkBlitInfo => 6,
            PresentProviders.DxgkFlipMpoInfo => 7,
            _ => -1
        },
        _ => -1
    };

    private const int ChannelCount = 8;

    /// <summary>Feeds one event; returns true with a frame when it completes a frame of the main stream.</summary>
    public bool TryFeed(in PresentEvent ev, out FrameRecord frame)
    {
        frame = default;
        if (_pids != null && !_pids.Contains(ev.ProcessId)) return false;
        int rank = ChannelRank(ev.Source, ev.EventId);
        if (rank < 0) return false;
        EventsSeen++;
        _originQpc ??= ev.Qpc;

        if (!_processes.TryGetValue(ev.ProcessId, out var proc))
            _processes[ev.ProcessId] = proc = new ProcessState();
        var channel = proc.Channels[rank] ??= new Channel();
        channel.Count++;
        if (ev.Qpc > channel.LastQpc) channel.LastQpc = ev.Qpc;

        // DxgKrnl events carry no swap chain → one stream per process and event ID.
        ulong swap = ev.Source == PresentSource.DxgKrnl ? 0 : ev.SwapChain;
        if (!channel.Streams.TryGetValue(swap, out var stream))
            channel.Streams[swap] = stream = new Stream(ev.ProcessId, ev.Source, ev.EventId, rank, swap);

        long prev = stream.LastQpc;
        bool ordered = prev == 0 || ev.Qpc > prev;
        if (ordered) stream.LastQpc = ev.Qpc;
        stream.Score = DecayedScore(stream, ev.Qpc) + 1;
        stream.ScoreQpc = Math.Max(stream.ScoreQpc, ev.Qpc);

        if (SelectedRank(proc, ev.Qpc) != rank) return false;

        if (!ReferenceEquals(_main, stream))
        {
            if (_main == null || !IsEligible(_main, ev.Qpc) || stream.Score > DecayedScore(_main, ev.Qpc) * SwitchHysteresis)
                _main = stream;
            else
                return false;
        }

        if (prev == 0 || !ordered) return false;
        // Right after a switch the new stream's interval may overlap the frame the previous main
        // stream just reported (same present seen by two providers) — skip it instead of counting twice.
        if (prev < _lastEmitQpc) return false;
        double deltaMs = (ev.Qpc - prev) * 1000.0 / _freq;
        if (deltaMs <= 0 || deltaMs > GapMs) return false;

        double time = (ev.Qpc - _originQpc.Value) / (double)_freq;
        frame = new FrameRecord(time, deltaMs, null, null, null, null, false) { ProcessId = ev.ProcessId };
        _lastEmitQpc = ev.Qpc;
        FramesEmitted++;
        return true;
    }

    /// <summary>Lowest-rank channel of the process with enough events that is still active; −1 when none.</summary>
    private int SelectedRank(ProcessState proc, long nowQpc)
    {
        long stale = (long)(ChannelStaleSeconds * _freq);
        for (int r = 0; r < ChannelCount; r++)
        {
            var c = proc.Channels[r];
            if (c == null || c.Count < MinEventsToSelect) continue;
            if (nowQpc - c.LastQpc > stale) continue;
            return r;
        }
        return -1;
    }

    /// <summary>The current main stream may keep emitting only while its channel is still the selected one of its process.</summary>
    private bool IsEligible(Stream s, long nowQpc) =>
        _processes.TryGetValue(s.Pid, out var proc) && SelectedRank(proc, nowQpc) == s.Rank
        && nowQpc - s.LastQpc <= (long)(ChannelStaleSeconds * _freq);

    private double DecayedScore(Stream s, long nowQpc)
    {
        if (s.ScoreQpc == 0 || nowQpc <= s.ScoreQpc) return s.Score;
        double dt = (nowQpc - s.ScoreQpc) / (double)_freq;
        return s.Score * Math.Pow(0.5, dt / ScoreHalfLifeSeconds);
    }

    private sealed class ProcessState
    {
        public readonly Channel?[] Channels = new Channel?[ChannelCount];
    }

    private sealed class Channel
    {
        public long Count;
        public long LastQpc;
        public readonly Dictionary<ulong, Stream> Streams = new();
    }

    private sealed class Stream
    {
        public Stream(int pid, PresentSource source, ushort eventId, int rank, ulong swapChain)
        {
            Pid = pid; Source = source; EventId = eventId; Rank = rank; SwapChain = swapChain;
        }
        public readonly int Pid;
        public readonly PresentSource Source;
        public readonly ushort EventId;
        public readonly int Rank;
        public readonly ulong SwapChain;
        public long LastQpc;
        public double Score;
        public long ScoreQpc;
    }
}
