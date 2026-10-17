using System.Runtime.InteropServices;
using Visor.Services.FrameCapture;
using Xunit;

namespace Visor.Tests;

/// <summary>
/// The ETW mirrors must match the native x64 layouts (evntrace.h / evntcons.h / evntprov.h) byte
/// for byte — StartTrace / OpenTrace / the event callback read them in place. No session is started.
/// </summary>
public class FrameCaptureEtwLayoutTests
{
    private static int Off<T>(string field) => (int)Marshal.OffsetOf<T>(field);

    [Fact]
    public void Running_as_64_bit()
    {
        // All expected values below are the x64 / ARM64 ones; Visor ships x64 only.
        Assert.Equal(8, IntPtr.Size);
    }

    [Fact]
    public void Event_trace_properties()
    {
        Assert.Equal(48, Marshal.SizeOf<WNODE_HEADER>());
        Assert.Equal(8, Off<WNODE_HEADER>(nameof(WNODE_HEADER.HistoricalContext)));
        Assert.Equal(16, Off<WNODE_HEADER>(nameof(WNODE_HEADER.TimeStamp)));
        Assert.Equal(24, Off<WNODE_HEADER>(nameof(WNODE_HEADER.Guid)));
        Assert.Equal(40, Off<WNODE_HEADER>(nameof(WNODE_HEADER.ClientContext)));
        Assert.Equal(44, Off<WNODE_HEADER>(nameof(WNODE_HEADER.Flags)));

        Assert.Equal(120, Marshal.SizeOf<EVENT_TRACE_PROPERTIES>());
        Assert.Equal(48, Off<EVENT_TRACE_PROPERTIES>(nameof(EVENT_TRACE_PROPERTIES.BufferSize)));
        Assert.Equal(64, Off<EVENT_TRACE_PROPERTIES>(nameof(EVENT_TRACE_PROPERTIES.LogFileMode)));
        Assert.Equal(68, Off<EVENT_TRACE_PROPERTIES>(nameof(EVENT_TRACE_PROPERTIES.FlushTimer)));
        Assert.Equal(88, Off<EVENT_TRACE_PROPERTIES>(nameof(EVENT_TRACE_PROPERTIES.EventsLost)));
        Assert.Equal(104, Off<EVENT_TRACE_PROPERTIES>(nameof(EVENT_TRACE_PROPERTIES.LoggerThreadId)));
        Assert.Equal(112, Off<EVENT_TRACE_PROPERTIES>(nameof(EVENT_TRACE_PROPERTIES.LogFileNameOffset)));
        Assert.Equal(116, Off<EVENT_TRACE_PROPERTIES>(nameof(EVENT_TRACE_PROPERTIES.LoggerNameOffset)));
    }

    [Fact]
    public void Event_record_and_header()
    {
        Assert.Equal(16, Marshal.SizeOf<EVENT_DESCRIPTOR>());
        Assert.Equal(6, Off<EVENT_DESCRIPTOR>(nameof(EVENT_DESCRIPTOR.Task)));
        Assert.Equal(8, Off<EVENT_DESCRIPTOR>(nameof(EVENT_DESCRIPTOR.Keyword)));

        Assert.Equal(80, Marshal.SizeOf<EVENT_HEADER>());
        Assert.Equal(EtwNative.RecordHeaderFlags, Off<EVENT_HEADER>(nameof(EVENT_HEADER.Flags)));
        Assert.Equal(8, Off<EVENT_HEADER>(nameof(EVENT_HEADER.ThreadId)));
        Assert.Equal(EtwNative.RecordProcessId, Off<EVENT_HEADER>(nameof(EVENT_HEADER.ProcessId)));
        Assert.Equal(EtwNative.RecordTimeStamp, Off<EVENT_HEADER>(nameof(EVENT_HEADER.TimeStamp)));
        Assert.Equal(EtwNative.RecordProviderId, Off<EVENT_HEADER>(nameof(EVENT_HEADER.ProviderId)));
        Assert.Equal(EtwNative.RecordEventId, Off<EVENT_HEADER>(nameof(EVENT_HEADER.EventDescriptor)));
        Assert.Equal(56, Off<EVENT_HEADER>(nameof(EVENT_HEADER.ProcessorTime)));
        Assert.Equal(64, Off<EVENT_HEADER>(nameof(EVENT_HEADER.ActivityId)));

        Assert.Equal(4, Marshal.SizeOf<ETW_BUFFER_CONTEXT>());
        Assert.Equal(112, Marshal.SizeOf<EVENT_RECORD>());
        Assert.Equal(80, Off<EVENT_RECORD>(nameof(EVENT_RECORD.BufferContext)));
        Assert.Equal(84, Off<EVENT_RECORD>(nameof(EVENT_RECORD.ExtendedDataCount)));
        Assert.Equal(EtwNative.RecordUserDataLength, Off<EVENT_RECORD>(nameof(EVENT_RECORD.UserDataLength)));
        Assert.Equal(88, Off<EVENT_RECORD>(nameof(EVENT_RECORD.ExtendedData)));
        Assert.Equal(EtwNative.RecordUserData, Off<EVENT_RECORD>(nameof(EVENT_RECORD.UserData)));
        Assert.Equal(104, Off<EVENT_RECORD>(nameof(EVENT_RECORD.UserContext)));
    }

    [Fact]
    public void Event_trace_logfile()
    {
        Assert.Equal(48, Marshal.SizeOf<EVENT_TRACE_HEADER>());
        Assert.Equal(88, Marshal.SizeOf<EVENT_TRACE>());
        Assert.Equal(72, Off<EVENT_TRACE>(nameof(EVENT_TRACE.MofData)));
        Assert.Equal(84, Off<EVENT_TRACE>(nameof(EVENT_TRACE.ClientContext)));

        Assert.Equal(172, Marshal.SizeOf<TIME_ZONE_INFORMATION>());
        Assert.Equal(68, Off<TIME_ZONE_INFORMATION>(nameof(TIME_ZONE_INFORMATION.StandardDate)));
        Assert.Equal(168, Off<TIME_ZONE_INFORMATION>(nameof(TIME_ZONE_INFORMATION.DaylightBias)));

        Assert.Equal(280, Marshal.SizeOf<TRACE_LOGFILE_HEADER>());
        Assert.Equal(40, Off<TRACE_LOGFILE_HEADER>(nameof(TRACE_LOGFILE_HEADER.LogInstanceGuid)));
        Assert.Equal(56, Off<TRACE_LOGFILE_HEADER>(nameof(TRACE_LOGFILE_HEADER.LoggerName)));
        Assert.Equal(72, Off<TRACE_LOGFILE_HEADER>(nameof(TRACE_LOGFILE_HEADER.TimeZone)));
        Assert.Equal(248, Off<TRACE_LOGFILE_HEADER>(nameof(TRACE_LOGFILE_HEADER.BootTime)));
        Assert.Equal(256, Off<TRACE_LOGFILE_HEADER>(nameof(TRACE_LOGFILE_HEADER.PerfFreq)));
        Assert.Equal(276, Off<TRACE_LOGFILE_HEADER>(nameof(TRACE_LOGFILE_HEADER.BuffersLost)));

        Assert.Equal(448, Marshal.SizeOf<EVENT_TRACE_LOGFILEW>());
        Assert.Equal(8, Off<EVENT_TRACE_LOGFILEW>(nameof(EVENT_TRACE_LOGFILEW.LoggerName)));
        Assert.Equal(28, Off<EVENT_TRACE_LOGFILEW>(nameof(EVENT_TRACE_LOGFILEW.ProcessTraceMode)));
        Assert.Equal(32, Off<EVENT_TRACE_LOGFILEW>(nameof(EVENT_TRACE_LOGFILEW.CurrentEvent)));
        Assert.Equal(120, Off<EVENT_TRACE_LOGFILEW>(nameof(EVENT_TRACE_LOGFILEW.LogfileHeader)));
        Assert.Equal(400, Off<EVENT_TRACE_LOGFILEW>(nameof(EVENT_TRACE_LOGFILEW.BufferCallback)));
        Assert.Equal(424, Off<EVENT_TRACE_LOGFILEW>(nameof(EVENT_TRACE_LOGFILEW.EventRecordCallback)));
        Assert.Equal(432, Off<EVENT_TRACE_LOGFILEW>(nameof(EVENT_TRACE_LOGFILEW.IsKernelTrace)));
        Assert.Equal(440, Off<EVENT_TRACE_LOGFILEW>(nameof(EVENT_TRACE_LOGFILEW.Context)));
    }

    [Fact]
    public void Enable_parameters_and_filters()
    {
        Assert.Equal(16, Marshal.SizeOf<EVENT_FILTER_DESCRIPTOR>());
        Assert.Equal(8, Off<EVENT_FILTER_DESCRIPTOR>(nameof(EVENT_FILTER_DESCRIPTOR.Size)));
        Assert.Equal(12, Off<EVENT_FILTER_DESCRIPTOR>(nameof(EVENT_FILTER_DESCRIPTOR.Type)));
        Assert.Equal(48, Marshal.SizeOf<ENABLE_TRACE_PARAMETERS>());
        Assert.Equal(12, Off<ENABLE_TRACE_PARAMETERS>(nameof(ENABLE_TRACE_PARAMETERS.SourceId)));
        Assert.Equal(32, Off<ENABLE_TRACE_PARAMETERS>(nameof(ENABLE_TRACE_PARAMETERS.EnableFilterDesc)));
        Assert.Equal(40, Off<ENABLE_TRACE_PARAMETERS>(nameof(ENABLE_TRACE_PARAMETERS.FilterDescCount)));
    }

    [Fact]
    public void Session_properties_use_qpc_real_time_and_a_trailing_name_buffer()
    {
        var p = EtwSession.BuildProperties(120 + 4096);
        Assert.Equal(120u + 4096, p.Wnode.BufferSize);
        Assert.Equal(EtwNative.WNODE_FLAG_TRACED_GUID, p.Wnode.Flags);
        Assert.Equal(1u, p.Wnode.ClientContext);                    // QPC timestamps
        Assert.Equal(EtwNative.EVENT_TRACE_REAL_TIME_MODE, p.LogFileMode);
        Assert.Equal(120u, p.LoggerNameOffset);                     // name right after the struct
        Assert.Equal(0u, p.LogFileNameOffset);                      // no file — real time only
        Assert.Equal(1u, p.FlushTimer);
        Assert.Equal("VisorFrameCapture", EtwSession.DefaultSessionName);
    }

    /// <summary>Builds an EVENT_RECORD in unmanaged memory the way ETW hands it to the callback.</summary>
    private static (IntPtr record, IntPtr payload) MakeRecord(Guid provider, ushort id, int pid, long qpc, ushort flags, byte[] payload)
    {
        var rec = new EVENT_RECORD
        {
            EventHeader = new EVENT_HEADER
            {
                Size = 80, Flags = flags, ProcessId = (uint)pid, TimeStamp = qpc, ProviderId = provider,
                EventDescriptor = new EVENT_DESCRIPTOR { Id = id }
            },
            UserDataLength = (ushort)payload.Length
        };
        IntPtr data = Marshal.AllocHGlobal(Math.Max(1, payload.Length));
        Marshal.Copy(payload, 0, data, payload.Length);
        rec.UserData = payload.Length > 0 ? data : IntPtr.Zero;
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<EVENT_RECORD>());
        Marshal.StructureToPtr(rec, p, false);
        return (p, data);
    }

    private static PresentEvent? Decode(Guid provider, ushort id, int pid, long qpc, ushort flags, byte[] payload)
    {
        var (p, data) = MakeRecord(provider, id, pid, qpc, flags, payload);
        try { return EtwSession.TryDecode(p, out var ev) ? ev : null; }
        finally { Marshal.FreeHGlobal(p); Marshal.FreeHGlobal(data); }
    }

    [Fact]
    public void Decodes_dxgi_present_with_64_bit_swap_chain()
    {
        var payload = new byte[16];
        BitConverter.GetBytes(0x0000_7FF6_1234_5678UL).CopyTo(payload, 0);   // pIDXGISwapChain, then Flags, SyncInterval
        var ev = Decode(PresentProviders.Dxgi, 42, 4321, 987654321, EtwNative.EVENT_HEADER_FLAG_64_BIT_HEADER, payload);
        Assert.NotNull(ev);
        Assert.Equal(PresentSource.Dxgi, ev!.Value.Source);
        Assert.Equal(4321, ev.Value.ProcessId);
        Assert.Equal(987654321, ev.Value.Qpc);
        Assert.Equal((ushort)42, ev.Value.EventId);
        Assert.Equal(0x0000_7FF6_1234_5678UL, ev.Value.SwapChain);
    }

    [Fact]
    public void Decodes_32_bit_d3d9_swap_chain_and_kernel_events_without_payload()
    {
        var payload = new byte[8];
        BitConverter.GetBytes(0x0BADF00Du).CopyTo(payload, 0);
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(payload, 4);              // Flags — must not leak into the pointer
        var d3d9 = Decode(PresentProviders.D3d9, 1, 10, 5, EtwNative.EVENT_HEADER_FLAG_32_BIT_HEADER, payload);
        Assert.Equal(0x0BADF00DUL, d3d9!.Value.SwapChain);

        var k = Decode(PresentProviders.DxgKrnl, 184, 11, 6, EtwNative.EVENT_HEADER_FLAG_64_BIT_HEADER, new byte[32]);
        Assert.Equal(PresentSource.DxgKrnl, k!.Value.Source);
        Assert.Equal(0UL, k.Value.SwapChain);

        // Short payload: no swap chain, event still counted.
        var shortPayload = Decode(PresentProviders.Dxgi, 55, 12, 7, EtwNative.EVENT_HEADER_FLAG_64_BIT_HEADER, new byte[4]);
        Assert.Equal(0UL, shortPayload!.Value.SwapChain);
    }

    [Fact]
    public void Ignores_other_events_and_providers()
    {
        Assert.Null(Decode(PresentProviders.Dxgi, 43, 1, 1, 0, Array.Empty<byte>()));          // Present_Stop
        Assert.Null(Decode(PresentProviders.D3d9, 2, 1, 1, 0, Array.Empty<byte>()));
        Assert.Null(Decode(PresentProviders.DxgKrnl, 177, 1, 1, 0, Array.Empty<byte>()));      // DmaPacket_Info
        Assert.Null(Decode(Guid.NewGuid(), 42, 1, 1, 0, Array.Empty<byte>()));
        Assert.False(EtwSession.TryDecode(IntPtr.Zero, out _));
    }
}
