using System.Buffers.Binary;

namespace Visor.Services.FrameCapture;

/// <summary>Status messages the elevated host sends.</summary>
public enum HostStatusKind : byte
{
    /// <summary>ETW session running. Detail = number of target processes.</summary>
    Started = 1,
    /// <summary>The capture could not start (Code = <see cref="HostError"/>, Detail = Win32 error when relevant). The host exits after it.</summary>
    Error = 2,
    /// <summary>Every target process exited.</summary>
    TargetExited = 3,
    /// <summary>Capture ended (Code = <see cref="HostStopReason"/>). Last message before the host closes the pipe.</summary>
    Stopped = 4
}

public enum HostError : byte
{
    None = 0,
    NotElevated = 1,
    BadRequest = 2,
    /// <summary>StartTrace / EnableTraceEx2 / OpenTrace failed (Detail = Win32 error).</summary>
    EtwStartFailed = 3,
    /// <summary>None of the requested processes is running.</summary>
    ProcessNotFound = 4,
    /// <summary>The pipe server is not the parent Visor process.</summary>
    PipeRejected = 5,
    Internal = 6
}

public enum HostStopReason : byte
{
    None = 0,
    /// <summary>Visor sent the stop byte.</summary>
    StopRequested = 1,
    /// <summary>The pipe closed or broke (Visor disposed the session / crashed).</summary>
    PipeClosed = 2,
    /// <summary>The parent Visor process exited.</summary>
    ParentExited = 3,
    /// <summary>All target processes exited.</summary>
    TargetExited = 4,
    /// <summary>The request's safety cap elapsed.</summary>
    SafetyCap = 5,
    /// <summary>The ETW consumer ended on its own (session stopped from outside).</summary>
    SessionEnded = 6
}

public readonly record struct HostStatus(HostStatusKind Kind, byte Code, int Detail)
{
    public HostError Error => Kind == HostStatusKind.Error ? (HostError)Code : HostError.None;
    public HostStopReason StopReason => Kind == HostStatusKind.Stopped ? (HostStopReason)Code : HostStopReason.None;

    public static HostStatus Started(int targets) => new(HostStatusKind.Started, 0, targets);
    public static HostStatus Failed(HostError error, int win32 = 0) => new(HostStatusKind.Error, (byte)error, win32);
    public static HostStatus TargetExited() => new(HostStatusKind.TargetExited, 0, 0);
    public static HostStatus Stopped(HostStopReason reason) => new(HostStatusKind.Stopped, (byte)reason, 0);
}

/// <summary>
/// Host → Visor byte stream. Starts with a 3-byte hello (<c>'V' 'F' version</c>), then messages,
/// each a type byte followed by a fixed-size little-endian payload:
/// <list type="bullet">
/// <item><c>0x01</c> frame, 24 bytes: f64 time (s since capture start), f64 frame time (ms), i32 pid, u8 source, 3 reserved.</item>
/// <item><c>0x02</c> status, 8 bytes: u8 kind, u8 code, u16 reserved, i32 detail.</item>
/// </list>
/// Visor → host: any single byte; <see cref="StopByte"/> asks the host to stop, others are ignored.
/// A new version bumps <see cref="Version"/>; a reader refuses a hello it does not know rather than guessing sizes.
/// </summary>
public static class FrameWire
{
    public const byte Magic0 = (byte)'V';
    public const byte Magic1 = (byte)'F';
    public const byte Version = 1;
    public const int HelloSize = 3;
    public const byte TypeFrame = 0x01;
    public const byte TypeStatus = 0x02;
    public const int FramePayloadSize = 24;
    public const int StatusPayloadSize = 8;
    public const int FrameMessageSize = 1 + FramePayloadSize;
    public const int StatusMessageSize = 1 + StatusPayloadSize;
    public const byte StopByte = (byte)'S';

    public static byte[] Hello() => new[] { Magic0, Magic1, Version };

    public static void EncodeFrame(in FrameRecord f, PresentSource source, Span<byte> dst)
    {
        if (dst.Length < FrameMessageSize) throw new ArgumentException("Buffer too small.", nameof(dst));
        dst[0] = TypeFrame;
        BinaryPrimitives.WriteDoubleLittleEndian(dst.Slice(1, 8), f.Time);
        BinaryPrimitives.WriteDoubleLittleEndian(dst.Slice(9, 8), f.FrameTimeMs);
        BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(17, 4), f.ProcessId);
        dst[21] = (byte)source;
        dst[22] = 0; dst[23] = 0; dst[24] = 0;
    }

    public static byte[] EncodeFrame(in FrameRecord f, PresentSource source = PresentSource.Unknown)
    {
        var b = new byte[FrameMessageSize];
        EncodeFrame(f, source, b);
        return b;
    }

    public static void EncodeStatus(HostStatus s, Span<byte> dst)
    {
        if (dst.Length < StatusMessageSize) throw new ArgumentException("Buffer too small.", nameof(dst));
        dst[0] = TypeStatus;
        dst[1] = (byte)s.Kind;
        dst[2] = s.Code;
        dst[3] = 0; dst[4] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(5, 4), s.Detail);
    }

    public static byte[] EncodeStatus(HostStatus s)
    {
        var b = new byte[StatusMessageSize];
        EncodeStatus(s, b);
        return b;
    }
}

/// <summary>One decoded host message: a frame or a status.</summary>
public readonly record struct WireMessage(bool IsFrame, FrameRecord Frame, PresentSource Source, HostStatus Status);

/// <summary>
/// Incremental decoder for <see cref="FrameWire"/>: accepts arbitrary chunks (a message may be
/// split across reads), keeps the incomplete tail, and fails closed on a bad hello, an unknown
/// version or an unknown message type (after which it decodes nothing more).
/// </summary>
public sealed class FrameWireDecoder
{
    private readonly byte[] _pending = new byte[FrameWire.FrameMessageSize];
    private int _pendingLength;
    private bool _helloSeen;

    public bool Faulted { get; private set; }
    public string FaultReason { get; private set; } = "";
    public bool HelloSeen => _helloSeen;
    /// <summary>Bytes of an incomplete message (non-zero at end of stream = truncated).</summary>
    public int PendingBytes => _pendingLength;

    /// <summary>Decodes everything complete in <paramref name="data"/>; returns false once the stream is faulted.</summary>
    public bool Feed(ReadOnlySpan<byte> data, List<WireMessage> output)
    {
        if (Faulted) return false;
        int i = 0;
        while (i < data.Length)
        {
            if (_helloSeen && _pendingLength == 0)
            {
                // The type byte alone decides the size; an unknown one ends the stream at once.
                byte type = data[i];
                if (type != FrameWire.TypeFrame && type != FrameWire.TypeStatus)
                    return Fault($"unknown message type 0x{type:x2}");
                _pending[0] = type;
                _pendingLength = 1;
                i++;
                continue;
            }
            int need = !_helloSeen ? FrameWire.HelloSize
                : _pending[0] == FrameWire.TypeFrame ? FrameWire.FrameMessageSize : FrameWire.StatusMessageSize;
            int take = Math.Min(need - _pendingLength, data.Length - i);
            data.Slice(i, take).CopyTo(_pending.AsSpan(_pendingLength));
            _pendingLength += take;
            i += take;
            if (_pendingLength < need) break;
            if (!Complete(output)) return false;
            _pendingLength = 0;
        }
        return true;
    }

    private bool Complete(List<WireMessage> output)
    {
        var m = _pending.AsSpan(0, _pendingLength);
        if (!_helloSeen)
        {
            if (m[0] != FrameWire.Magic0 || m[1] != FrameWire.Magic1) return Fault("bad hello");
            if (m[2] != FrameWire.Version) return Fault($"unsupported protocol version {m[2]}");
            _helloSeen = true;
            return true;
        }
        if (m[0] == FrameWire.TypeFrame)
        {
            double time = BinaryPrimitives.ReadDoubleLittleEndian(m.Slice(1, 8));
            double ft = BinaryPrimitives.ReadDoubleLittleEndian(m.Slice(9, 8));
            int pid = BinaryPrimitives.ReadInt32LittleEndian(m.Slice(17, 4));
            var source = (PresentSource)m[21];
            if (!double.IsFinite(time) || !double.IsFinite(ft) || ft < 0) return Fault("invalid frame values");
            output.Add(new WireMessage(true, new FrameRecord(time, ft, null, null, null, null, false) { ProcessId = pid }, source, default));
            return true;
        }
        var kind = (HostStatusKind)m[1];
        if (kind is < HostStatusKind.Started or > HostStatusKind.Stopped) return Fault($"unknown status kind {m[1]}");
        int detail = BinaryPrimitives.ReadInt32LittleEndian(m.Slice(5, 4));
        output.Add(new WireMessage(false, default, PresentSource.Unknown, new HostStatus(kind, m[2], detail)));
        return true;
    }

    private bool Fault(string reason)
    {
        Faulted = true;
        FaultReason = reason;
        _pendingLength = 0;
        return false;
    }
}
