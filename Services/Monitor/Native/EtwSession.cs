using System.Diagnostics;
using System.Runtime.InteropServices;
using static Visor.Services.FrameCapture.EtwNative;

namespace Visor.Services.FrameCapture;

/// <summary>
/// Private real-time ETW session "VisorFrameCapture" listening to the present-start events of
/// DXGI, D3D9 and DxgKrnl (<see cref="PresentProviders"/>). Only ever created inside the
/// elevated <c>Visor.exe --frame-capture</c> host — starting a real-time session needs
/// administrator rights (or Performance Log Users).
/// <para>
/// Lifecycle: stop a stale session with the same name → <c>StartTraceW</c> (QPC clock,
/// real-time mode, 1 s flush) → <c>EnableTraceEx2</c> per provider with an event-ID filter
/// (and a PID filter for the user-mode providers when there are ≤ 8 targets; each filter falls
/// back to a plainer enable when the OS refuses it) → <c>OpenTraceW</c> (real-time, event-record
/// callback, raw QPC timestamps) → <c>ProcessTrace</c> on a dedicated background thread.
/// <see cref="Stop"/> does <c>ControlTraceW(STOP)</c> + <c>CloseTrace</c> and joins the thread.
/// </para>
/// </summary>
public sealed class EtwSession : IPresentEventSource
{
    public const string DefaultSessionName = "VisorFrameCapture";
    public const uint BufferSizeKb = 64;
    public const uint FlushTimerSeconds = 1;

    private static readonly (long Lo, long Hi) DxgiKey = Key(PresentProviders.Dxgi);
    private static readonly (long Lo, long Hi) D3d9Key = Key(PresentProviders.D3d9);
    private static readonly (long Lo, long Hi) DxgKrnlKey = Key(PresentProviders.DxgKrnl);

    private readonly object _gate = new();
    private readonly string _sessionName;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr _props;
    private IntPtr _loggerName;
    private ulong _sessionHandle;
    private ulong _traceHandle = INVALID_PROCESSTRACE_HANDLE_64;
    private Thread? _thread;
    private EventRecordCallback? _callback;   // must outlive ProcessTrace: the native side holds only the function pointer
    private Action<PresentEvent>? _onEvent;
    private HashSet<int>? _pids;
    private bool _started;
    private bool _stopped;

    public EtwSession(string sessionName = DefaultSessionName)
    {
        if (string.IsNullOrWhiteSpace(sessionName) || sessionName.Length >= NameBufferChars)
            throw new ArgumentException("Invalid session name.", nameof(sessionName));
        _sessionName = sessionName;
    }

    public long QpcFrequency { get; private set; } = Stopwatch.Frequency;
    public Task Completion => _completion.Task;
    public string EnableSummary { get; private set; } = "";

    public uint Start(PresentEventFilter filter, Action<PresentEvent> onEvent)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(onEvent);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_started) throw new InvalidOperationException("Session already started.");
            _started = true;
        }
        _onEvent = onEvent;
        _pids = filter.ProcessIds.Count > 0 ? new HashSet<int>(filter.ProcessIds) : null;

        uint err = StartSession();
        if (err != ERROR_SUCCESS) { Stop(); return err; }

        err = EnableProviders(filter.ProcessIds);
        if (err != ERROR_SUCCESS) { Stop(); return err; }

        err = OpenConsumer();
        if (err != ERROR_SUCCESS) { Stop(); return err; }

        ulong handle = _traceHandle;
        _thread = new Thread(() =>
        {
            try { ProcessTrace(new[] { handle }, 1, IntPtr.Zero, IntPtr.Zero); }
            catch (Exception) { /* never let the consumer thread take the host down */ }
            finally { _completion.TrySetResult(); }
        })
        { IsBackground = true, Name = "Visor ETW frame consumer" };
        _thread.Start();
        return ERROR_SUCCESS;
    }

    private uint StartSession()
    {
        StopByName(_sessionName);   // a crashed previous host may have left the session running
        _props = AllocProperties(out _);
        uint err = StartTrace(out _sessionHandle, _sessionName, _props);
        if (err == ERROR_ALREADY_EXISTS)
        {
            StopByName(_sessionName);
            FreeProperties(ref _props);
            _props = AllocProperties(out _);
            err = StartTrace(out _sessionHandle, _sessionName, _props);
        }
        if (err != ERROR_SUCCESS) _sessionHandle = 0;
        return err;
    }

    private uint EnableProviders(IReadOnlyCollection<int> pids)
    {
        var userPids = pids.Count is > 0 and <= MAX_EVENT_FILTER_PID_COUNT ? pids : null;
        uint dxgi = EnableProvider(PresentProviders.Dxgi, PresentProviders.DxgiKeywords, PresentProviders.DxgiEventIds, userPids, out var m1);
        uint d3d9 = EnableProvider(PresentProviders.D3d9, PresentProviders.D3d9Keywords, PresentProviders.D3d9EventIds, userPids, out var m2);
        // Kernel provider: the PID filter does not apply to kernel-mode providers; the callback filters by EventHeader.ProcessId.
        uint dxgk = EnableProvider(PresentProviders.DxgKrnl, PresentProviders.DxgKrnlKeywords, PresentProviders.DxgKrnlEventIds, null, out var m3);
        EnableSummary = $"dxgi={m1} d3d9={m2} dxgkrnl={m3}";
        // DXGI covers nearly every modern game; without it (and without both fallbacks) there is nothing to measure.
        if (dxgi != ERROR_SUCCESS && d3d9 != ERROR_SUCCESS && dxgk != ERROR_SUCCESS) return dxgi;
        return ERROR_SUCCESS;
    }

    /// <summary>Tries event-ID (+PID) filtering first, then event-ID only, then a plain enable.</summary>
    private uint EnableProvider(Guid provider, ulong keywords, ushort[] ids, IReadOnlyCollection<int>? pids, out string mode)
    {
        uint err;
        if (pids != null)
        {
            err = EnableWithFilters(provider, keywords, ids, pids);
            if (err == ERROR_SUCCESS) { mode = "ids+pids"; return err; }
        }
        err = EnableWithFilters(provider, keywords, ids, null);
        if (err == ERROR_SUCCESS) { mode = "ids"; return err; }
        err = EnableWithFilters(provider, keywords, null, null);
        mode = err == ERROR_SUCCESS ? "unfiltered" : "failed:" + err;
        return err;
    }

    private uint EnableWithFilters(Guid provider, ulong keywords, ushort[]? ids, IReadOnlyCollection<int>? pids)
    {
        var allocations = new List<IntPtr>();
        IntPtr Alloc(int size)
        {
            var p = Marshal.AllocHGlobal(size);
            allocations.Add(p);
            Marshal.Copy(new byte[size], 0, p, size);
            return p;
        }
        try
        {
            var descriptors = new List<EVENT_FILTER_DESCRIPTOR>(2);
            if (ids is { Length: > 0 })
            {
                // EVENT_FILTER_EVENT_ID { BOOLEAN FilterIn; UCHAR Reserved; USHORT Count; USHORT Events[Count]; }
                int size = 4 + 2 * ids.Length;
                var p = Alloc(size);
                Marshal.WriteByte(p, 0, 1);
                Marshal.WriteInt16(p, 2, (short)ids.Length);
                for (int i = 0; i < ids.Length; i++) Marshal.WriteInt16(p, 4 + 2 * i, (short)ids[i]);
                descriptors.Add(new EVENT_FILTER_DESCRIPTOR { Ptr = (ulong)p.ToInt64(), Size = (uint)size, Type = EVENT_FILTER_TYPE_EVENT_ID });
            }
            if (pids is { Count: > 0 })
            {
                int size = 4 * pids.Count;
                var p = Alloc(size);
                int i = 0;
                foreach (int pid in pids) Marshal.WriteInt32(p, 4 * i++, pid);
                descriptors.Add(new EVENT_FILTER_DESCRIPTOR { Ptr = (ulong)p.ToInt64(), Size = (uint)size, Type = EVENT_FILTER_TYPE_PID });
            }

            IntPtr parameters = IntPtr.Zero;
            if (descriptors.Count > 0)
            {
                int descSize = Marshal.SizeOf<EVENT_FILTER_DESCRIPTOR>();
                var arr = Alloc(descSize * descriptors.Count);
                for (int i = 0; i < descriptors.Count; i++)
                    Marshal.StructureToPtr(descriptors[i], arr + i * descSize, false);
                var etp = new ENABLE_TRACE_PARAMETERS
                {
                    Version = ENABLE_TRACE_PARAMETERS_VERSION_2,
                    EnableFilterDesc = arr,
                    FilterDescCount = (uint)descriptors.Count
                };
                parameters = Alloc(Marshal.SizeOf<ENABLE_TRACE_PARAMETERS>());
                Marshal.StructureToPtr(etp, parameters, false);
            }
            var guid = provider;
            return EnableTraceEx2(_sessionHandle, ref guid, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_INFORMATION,
                keywords, 0, 0, parameters);
        }
        finally
        {
            foreach (var p in allocations) Marshal.FreeHGlobal(p);
        }
    }

    private uint OpenConsumer()
    {
        _callback = OnEventRecord;
        _loggerName = Marshal.StringToHGlobalUni(_sessionName);
        var logfile = new EVENT_TRACE_LOGFILEW
        {
            LoggerName = _loggerName,
            ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD | PROCESS_TRACE_MODE_RAW_TIMESTAMP,
            EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback)
        };
        ulong h = OpenTrace(ref logfile);
        if (IsInvalidProcessTraceHandle(h))
        {
            int last = Marshal.GetLastPInvokeError();
            return last != 0 ? (uint)last : ERROR_INVALID_PARAMETER;
        }
        _traceHandle = h;
        // With ClientContext = 1 the session clock is QPC; PerfFreq is its frequency (same as Stopwatch.Frequency).
        if (logfile.LogfileHeader.PerfFreq > 0) QpcFrequency = logfile.LogfileHeader.PerfFreq;
        return ERROR_SUCCESS;
    }

    private void OnEventRecord(IntPtr record)
    {
        // Runs on the ProcessTrace thread inside a native callback: nothing may escape.
        try
        {
            if (!TryDecode(record, out var ev)) return;
            var pids = _pids;
            if (pids != null && !pids.Contains(ev.ProcessId)) return;
            _onEvent?.Invoke(ev);
        }
        catch (Exception) { /* drop the event */ }
    }

    /// <summary>
    /// Decodes an EVENT_RECORD in place (fixed offsets, see <see cref="EtwNative"/>). Returns false
    /// for anything that is not one of our present events. The swap-chain pointer is the first
    /// payload field of DXGI Present_Start / PresentMultiplaneOverlay_Start and D3D9 Present_Start;
    /// its width follows the provider process's bitness (EVENT_HEADER_FLAG_32/64_BIT_HEADER).
    /// </summary>
    internal static bool TryDecode(IntPtr record, out PresentEvent ev)
    {
        ev = default;
        if (record == IntPtr.Zero) return false;
        long lo = Marshal.ReadInt64(record, RecordProviderId);
        long hi = Marshal.ReadInt64(record, RecordProviderId + 8);
        ushort id = (ushort)Marshal.ReadInt16(record, RecordEventId);
        PresentSource source;
        if (lo == DxgiKey.Lo && hi == DxgiKey.Hi)
            source = id is PresentProviders.DxgiPresentStart or PresentProviders.DxgiPresentMpoStart ? PresentSource.Dxgi : PresentSource.Unknown;
        else if (lo == D3d9Key.Lo && hi == D3d9Key.Hi)
            source = id == PresentProviders.D3d9PresentStart ? PresentSource.D3d9 : PresentSource.Unknown;
        else if (lo == DxgKrnlKey.Lo && hi == DxgKrnlKey.Hi)
            source = Array.IndexOf(PresentProviders.DxgKrnlEventIds, id) >= 0 ? PresentSource.DxgKrnl : PresentSource.Unknown;
        else
            return false;
        if (source == PresentSource.Unknown) return false;

        int pid = Marshal.ReadInt32(record, RecordProcessId);
        long qpc = Marshal.ReadInt64(record, RecordTimeStamp);
        ulong swapChain = 0;
        if (source != PresentSource.DxgKrnl)
        {
            ushort flags = (ushort)Marshal.ReadInt16(record, RecordHeaderFlags);
            int ptrSize = (flags & EVENT_HEADER_FLAG_32_BIT_HEADER) != 0 ? 4
                : (flags & EVENT_HEADER_FLAG_64_BIT_HEADER) != 0 ? 8
                : IntPtr.Size;
            int len = (ushort)Marshal.ReadInt16(record, RecordUserDataLength);
            IntPtr data = Marshal.ReadIntPtr(record, RecordUserData);
            if (data != IntPtr.Zero && len >= ptrSize)
                swapChain = ptrSize == 8 ? (ulong)Marshal.ReadInt64(data) : (uint)Marshal.ReadInt32(data);
        }
        ev = new PresentEvent(qpc, pid, source, id, swapChain);
        return true;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
        }
        try
        {
            if (_sessionHandle != 0 && _props != IntPtr.Zero)
                ControlTrace(_sessionHandle, null, _props, EVENT_TRACE_CONTROL_STOP);
            else if (_started)
                StopByName(_sessionName);
        }
        catch (Exception) { /* best effort */ }
        try
        {
            if (!IsInvalidProcessTraceHandle(_traceHandle)) CloseTrace(_traceHandle);
        }
        catch (Exception) { /* best effort */ }

        bool joined = _thread == null || _thread.Join(TimeSpan.FromSeconds(3));
        if (joined)
        {
            // Only free what the consumer could still touch once it is gone.
            FreeProperties(ref _props);
            if (_loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(_loggerName); _loggerName = IntPtr.Zero; }
        }
        _traceHandle = INVALID_PROCESSTRACE_HANDLE_64;
        _sessionHandle = 0;
        _completion.TrySetResult();
        GC.KeepAlive(_callback);
    }

    public void Dispose() => Stop();

    /// <summary>Stops a session by name (stale leftovers). Ignores "not found".</summary>
    internal static uint StopByName(string sessionName)
    {
        var p = AllocProperties(out _);
        try { return ControlTrace(0, sessionName, p, EVENT_TRACE_CONTROL_STOP); }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>EVENT_TRACE_PROPERTIES followed by room for the logger and log-file names.</summary>
    internal static IntPtr AllocProperties(out int size)
    {
        int structSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
        size = structSize + 2 * NameBufferChars * sizeof(char);
        var p = Marshal.AllocHGlobal(size);
        Marshal.Copy(new byte[size], 0, p, size);
        var props = BuildProperties(size);
        Marshal.StructureToPtr(props, p, false);
        return p;
    }

    /// <summary>The properties Visor starts its session with (pure; asserted by tests).</summary>
    internal static EVENT_TRACE_PROPERTIES BuildProperties(int totalSize) => new()
    {
        Wnode = new WNODE_HEADER
        {
            BufferSize = (uint)totalSize,
            Flags = WNODE_FLAG_TRACED_GUID,
            ClientContext = CLIENT_CONTEXT_QPC
        },
        BufferSize = BufferSizeKb,
        LogFileMode = EVENT_TRACE_REAL_TIME_MODE,
        FlushTimer = FlushTimerSeconds,
        LoggerNameOffset = (uint)Marshal.SizeOf<EVENT_TRACE_PROPERTIES>(),
        LogFileNameOffset = 0
    };

    private static void FreeProperties(ref IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        Marshal.FreeHGlobal(p);
        p = IntPtr.Zero;
    }

    private static (long, long) Key(Guid g)
    {
        var b = g.ToByteArray();   // in-memory GUID layout (Data1..3 little-endian), same as native
        return (BitConverter.ToInt64(b, 0), BitConverter.ToInt64(b, 8));
    }
}
