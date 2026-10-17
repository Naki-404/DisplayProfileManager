using System.Runtime.InteropServices;

namespace Visor.Services.FrameCapture;

// Raw advapi32 ETW interop for the frame-capture host. No TraceEvent / NuGet: the handful of
// structures below mirror evntrace.h / evntcons.h / evntprov.h. Every structure is blittable
// (no strings, no arrays — fixed-size character buffers are opaque sized structs) so it can be
// pinned and passed as-is, and FrameCaptureEtwLayoutTests asserts Marshal.SizeOf / OffsetOf
// against the native x64 values written next to each field. Offsets are identical on ARM64.

/// <summary>WNODE_HEADER (wmistr.h), 48 bytes on x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WNODE_HEADER
{
    public uint BufferSize;          // 0  total size of EVENT_TRACE_PROPERTIES + name buffers
    public uint ProviderId;          // 4
    public ulong HistoricalContext;  // 8  union { HistoricalContext; Version/Linkage }
    public long TimeStamp;           // 16 union { KernelHandle; TimeStamp }
    public Guid Guid;                // 24 session GUID (zero → ETW assigns one for a private real-time session)
    public uint ClientContext;       // 40 clock: 1 = QueryPerformanceCounter
    public uint Flags;               // 44 WNODE_FLAG_TRACED_GUID
}

/// <summary>EVENT_TRACE_PROPERTIES (evntrace.h), 120 bytes on x64; the logger / log-file name buffers follow it in the same allocation.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_TRACE_PROPERTIES
{
    public WNODE_HEADER Wnode;       // 0
    public uint BufferSize;          // 48 KB per buffer
    public uint MinimumBuffers;      // 52
    public uint MaximumBuffers;      // 56
    public uint MaximumFileSize;     // 60
    public uint LogFileMode;         // 64 EVENT_TRACE_REAL_TIME_MODE
    public uint FlushTimer;          // 68 seconds
    public uint EnableFlags;         // 72 kernel-logger flags only (0 here)
    public int AgeLimit;             // 76 union { AgeLimit; FlushThreshold }
    public uint NumberOfBuffers;     // 80
    public uint FreeBuffers;         // 84
    public uint EventsLost;          // 88
    public uint BuffersWritten;      // 92
    public uint LogBuffersLost;      // 96
    public uint RealTimeBuffersLost; // 100
    public IntPtr LoggerThreadId;    // 104 HANDLE
    public uint LogFileNameOffset;   // 112
    public uint LoggerNameOffset;    // 116
}

/// <summary>EVENT_DESCRIPTOR (evntprov.h), 16 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_DESCRIPTOR
{
    public ushort Id;      // 0
    public byte Version;   // 2
    public byte Channel;   // 3
    public byte Level;     // 4
    public byte Opcode;    // 5
    public ushort Task;    // 6
    public ulong Keyword;  // 8
}

/// <summary>EVENT_HEADER (evntcons.h), 80 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_HEADER
{
    public ushort Size;                      // 0
    public ushort HeaderType;                // 2
    public ushort Flags;                     // 4  EVENT_HEADER_FLAG_32_BIT_HEADER / _64_BIT_HEADER = pointer size of the provider
    public ushort EventProperty;             // 6
    public uint ThreadId;                    // 8
    public uint ProcessId;                   // 12 process that logged the event (the presenting app for our events)
    public long TimeStamp;                   // 16 raw QPC with PROCESS_TRACE_MODE_RAW_TIMESTAMP + ClientContext 1
    public Guid ProviderId;                  // 24
    public EVENT_DESCRIPTOR EventDescriptor; // 40 (Id at 40)
    public ulong ProcessorTime;              // 56 union { KernelTime/UserTime; ProcessorTime }
    public Guid ActivityId;                  // 64
}

/// <summary>ETW_BUFFER_CONTEXT (evntcons.h), 4 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ETW_BUFFER_CONTEXT
{
    public ushort ProcessorIndex; // 0 union { { ProcessorNumber; Alignment }; ProcessorIndex }
    public ushort LoggerId;       // 2
}

/// <summary>EVENT_RECORD (evntcons.h), 112 bytes on x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_RECORD
{
    public EVENT_HEADER EventHeader;          // 0
    public ETW_BUFFER_CONTEXT BufferContext;  // 80
    public ushort ExtendedDataCount;          // 84
    public ushort UserDataLength;             // 86
    public IntPtr ExtendedData;               // 88
    public IntPtr UserData;                   // 96 payload
    public IntPtr UserContext;                // 104 EVENT_TRACE_LOGFILEW.Context
}

/// <summary>EVENT_TRACE_HEADER (evntrace.h), 48 bytes (only as part of EVENT_TRACE).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_TRACE_HEADER
{
    public ushort Size;            // 0
    public ushort FieldTypeFlags;  // 2
    public uint Version;           // 4
    public uint ThreadId;          // 8
    public uint ProcessId;         // 12
    public long TimeStamp;         // 16
    public Guid Guid;              // 24 union { Guid; GuidPtr }
    public ulong ProcessorTime;    // 40
}

/// <summary>EVENT_TRACE (evntrace.h), 88 bytes on x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_TRACE
{
    public EVENT_TRACE_HEADER Header; // 0
    public uint InstanceId;           // 48
    public uint ParentInstanceId;     // 52
    public Guid ParentGuid;           // 56
    public IntPtr MofData;            // 72
    public uint MofLength;            // 80
    public uint ClientContext;        // 84 union { ClientContext; BufferContext }
}

/// <summary>WCHAR[32] — opaque, 64 bytes, 2-byte aligned.</summary>
[StructLayout(LayoutKind.Sequential, Size = 64)]
internal struct WCHAR32
{
    private ushort _first;
}

/// <summary>SYSTEMTIME — opaque, 16 bytes, 2-byte aligned.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
internal struct SYSTEMTIME16
{
    private ushort _first;
}

/// <summary>TIME_ZONE_INFORMATION, 172 bytes, 4-byte aligned.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TIME_ZONE_INFORMATION
{
    public int Bias;                   // 0
    public WCHAR32 StandardName;       // 4
    public SYSTEMTIME16 StandardDate;  // 68
    public int StandardBias;           // 84
    public WCHAR32 DaylightName;       // 88
    public SYSTEMTIME16 DaylightDate;  // 152
    public int DaylightBias;           // 168
}

/// <summary>TRACE_LOGFILE_HEADER (evntrace.h), 280 bytes on x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TRACE_LOGFILE_HEADER
{
    public uint BufferSize;                // 0
    public uint Version;                   // 4
    public uint ProviderVersion;           // 8
    public uint NumberOfProcessors;        // 12
    public long EndTime;                   // 16
    public uint TimerResolution;           // 24
    public uint MaximumFileSize;           // 28
    public uint LogFileMode;               // 32
    public uint BuffersWritten;            // 36
    public Guid LogInstanceGuid;           // 40 union { LogInstanceGuid; { StartBuffers; PointerSize; EventsLost; CpuSpeedInMHz } }
    public IntPtr LoggerName;              // 56
    public IntPtr LogFileName;             // 64
    public TIME_ZONE_INFORMATION TimeZone; // 72
    public long BootTime;                  // 248 (244 padded to 8)
    public long PerfFreq;                  // 256 QPC frequency of the session clock
    public long StartTime;                 // 264
    public uint ReservedFlags;             // 272
    public uint BuffersLost;               // 276
}

/// <summary>EVENT_TRACE_LOGFILEW (evntrace.h), 448 bytes on x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_TRACE_LOGFILEW
{
    public IntPtr LogFileName;                  // 0   null for real time
    public IntPtr LoggerName;                   // 8   session name (UTF-16, caller-owned)
    public long CurrentTime;                    // 16
    public uint BuffersRead;                    // 24
    public uint ProcessTraceMode;               // 28  union { LogFileMode; ProcessTraceMode }
    public EVENT_TRACE CurrentEvent;            // 32
    public TRACE_LOGFILE_HEADER LogfileHeader;  // 120
    public IntPtr BufferCallback;               // 400
    public uint BufferSize;                     // 408
    public uint Filled;                         // 412
    public uint EventsLost;                     // 416
    public IntPtr EventRecordCallback;          // 424 union { EventCallback; EventRecordCallback }
    public uint IsKernelTrace;                  // 432
    public IntPtr Context;                      // 440
}

/// <summary>EVENT_FILTER_DESCRIPTOR (evntprov.h), 16 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_FILTER_DESCRIPTOR
{
    public ulong Ptr;   // 0
    public uint Size;   // 8
    public uint Type;   // 12
}

/// <summary>ENABLE_TRACE_PARAMETERS (evntrace.h, version 2), 48 bytes on x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ENABLE_TRACE_PARAMETERS
{
    public uint Version;            // 0  ENABLE_TRACE_PARAMETERS_VERSION_2
    public uint EnableProperty;     // 4
    public uint ControlFlags;       // 8
    public Guid SourceId;           // 12
    public IntPtr EnableFilterDesc; // 32 (28 padded to 8)
    public uint FilterDescCount;    // 40
}

/// <summary>advapi32 entry points and constants used by <see cref="EtwSession"/>.</summary>
internal static class EtwNative
{
    public const uint ERROR_SUCCESS = 0;
    public const uint ERROR_ACCESS_DENIED = 5;
    public const uint ERROR_NOT_SUPPORTED = 50;
    public const uint ERROR_INVALID_PARAMETER = 87;
    public const uint ERROR_ALREADY_EXISTS = 183;
    public const uint ERROR_MORE_DATA = 234;
    public const uint ERROR_WMI_INSTANCE_NOT_FOUND = 4201;

    public const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    public const uint CLIENT_CONTEXT_QPC = 1;
    public const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    public const uint EVENT_TRACE_CONTROL_STOP = 1;
    public const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;

    public const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    public const uint PROCESS_TRACE_MODE_RAW_TIMESTAMP = 0x00001000;
    public const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;

    public const uint ENABLE_TRACE_PARAMETERS_VERSION_2 = 2;
    public const uint EVENT_FILTER_TYPE_PID = 0x80000004;
    public const uint EVENT_FILTER_TYPE_EVENT_ID = 0x80000200;
    public const int MAX_EVENT_FILTER_PID_COUNT = 8;
    public const int MAX_EVENT_FILTER_EVENT_ID_COUNT = 64;

    public const ushort EVENT_HEADER_FLAG_32_BIT_HEADER = 0x0020;
    public const ushort EVENT_HEADER_FLAG_64_BIT_HEADER = 0x0040;

    public const byte TRACE_LEVEL_INFORMATION = 4;

    /// <summary>INVALID_PROCESSTRACE_HANDLE: (TRACEHANDLE)INVALID_HANDLE_VALUE, sign-extended on 64-bit.</summary>
    public const ulong INVALID_PROCESSTRACE_HANDLE_64 = ulong.MaxValue;
    public const ulong INVALID_PROCESSTRACE_HANDLE_32 = 0xFFFFFFFF;

    // EVENT_RECORD field offsets read directly in the callback (no per-event marshalling).
    // FrameCaptureEtwLayoutTests asserts each equals Marshal.OffsetOf on the mirrors above.
    public const int RecordHeaderFlags = 4;
    public const int RecordProcessId = 12;
    public const int RecordTimeStamp = 16;
    public const int RecordProviderId = 24;
    public const int RecordEventId = 40;
    public const int RecordUserDataLength = 86;
    public const int RecordUserData = 96;

    /// <summary>Space reserved behind EVENT_TRACE_PROPERTIES for each name (chars). Session names are ≤ 1024 chars.</summary>
    public const int NameBufferChars = 1024;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void EventRecordCallback(IntPtr eventRecord);

    [DllImport("advapi32.dll", EntryPoint = "StartTraceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint StartTrace(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint ControlTrace(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    public static extern uint EnableTraceEx2(ulong sessionHandle, ref Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeoutMs, IntPtr enableParameters);

    [DllImport("advapi32.dll", EntryPoint = "OpenTraceW", ExactSpelling = true, SetLastError = true)]
    public static extern ulong OpenTrace(ref EVENT_TRACE_LOGFILEW logfile);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    public static extern uint ProcessTrace([In] ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    public static extern uint CloseTrace(ulong traceHandle);

    public static bool IsInvalidProcessTraceHandle(ulong h) =>
        h == INVALID_PROCESSTRACE_HANDLE_64 || (IntPtr.Size == 4 && h == INVALID_PROCESSTRACE_HANDLE_32);
}
