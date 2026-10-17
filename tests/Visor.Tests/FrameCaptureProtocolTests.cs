using Visor.Services;
using Visor.Services.FrameCapture;
using Xunit;

namespace Visor.Tests;

public class FrameCaptureProtocolTests
{
    private static FrameRecord F(double t, double ft, int pid = 4242) => new(t, ft, null, null, null, null, false) { ProcessId = pid };

    private static byte[] Stream(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void Frame_and_status_round_trip()
    {
        var bytes = Stream(
            FrameWire.Hello(),
            FrameWire.EncodeStatus(HostStatus.Started(2)),
            FrameWire.EncodeFrame(F(1.25, 6.944), PresentSource.Dxgi),
            FrameWire.EncodeFrame(F(1.26, 16.6, 7), PresentSource.DxgKrnl),
            FrameWire.EncodeStatus(HostStatus.Failed(HostError.EtwStartFailed, 5)),
            FrameWire.EncodeStatus(HostStatus.TargetExited()),
            FrameWire.EncodeStatus(HostStatus.Stopped(HostStopReason.SafetyCap)));

        var d = new FrameWireDecoder();
        var msgs = new List<WireMessage>();
        Assert.True(d.Feed(bytes, msgs));
        Assert.Equal(6, msgs.Count);
        Assert.Equal(0, d.PendingBytes);

        Assert.Equal(HostStatusKind.Started, msgs[0].Status.Kind);
        Assert.Equal(2, msgs[0].Status.Detail);
        Assert.True(msgs[1].IsFrame);
        Assert.Equal(1.25, msgs[1].Frame.Time);
        Assert.Equal(6.944, msgs[1].Frame.FrameTimeMs);
        Assert.Equal(4242, msgs[1].Frame.ProcessId);
        Assert.Equal(PresentSource.Dxgi, msgs[1].Source);
        Assert.Equal(7, msgs[2].Frame.ProcessId);
        Assert.Equal(PresentSource.DxgKrnl, msgs[2].Source);
        Assert.Equal(HostError.EtwStartFailed, msgs[3].Status.Error);
        Assert.Equal(5, msgs[3].Status.Detail);
        Assert.Equal(HostStatusKind.TargetExited, msgs[4].Status.Kind);
        Assert.Equal(HostStopReason.SafetyCap, msgs[5].Status.StopReason);
        Assert.Equal(HostError.None, msgs[5].Status.Error);
    }

    [Fact]
    public void Sizes_are_fixed()
    {
        Assert.Equal(3, FrameWire.Hello().Length);
        Assert.Equal(25, FrameWire.EncodeFrame(F(0, 1)).Length);
        Assert.Equal(9, FrameWire.EncodeStatus(HostStatus.Started(1)).Length);
        Assert.Throws<ArgumentException>(() => FrameWire.EncodeFrame(F(0, 1), PresentSource.Dxgi, new byte[24]));
    }

    [Fact]
    public void Messages_split_across_reads_byte_by_byte_decode_identically()
    {
        var bytes = Stream(FrameWire.Hello(),
            FrameWire.EncodeFrame(F(0.5, 10)),
            FrameWire.EncodeStatus(HostStatus.Stopped(HostStopReason.StopRequested)),
            FrameWire.EncodeFrame(F(0.6, 11)));
        var d = new FrameWireDecoder();
        var msgs = new List<WireMessage>();
        foreach (byte b in bytes) Assert.True(d.Feed(new[] { b }, msgs));
        Assert.Equal(3, msgs.Count);
        Assert.Equal(11, msgs[2].Frame.FrameTimeMs);
    }

    [Fact]
    public void Truncated_stream_keeps_the_partial_message_pending()
    {
        var bytes = Stream(FrameWire.Hello(), FrameWire.EncodeFrame(F(0.5, 10)), FrameWire.EncodeFrame(F(0.6, 11)));
        var d = new FrameWireDecoder();
        var msgs = new List<WireMessage>();
        Assert.True(d.Feed(bytes.AsSpan(0, bytes.Length - 7), msgs));
        Assert.Single(msgs);
        Assert.Equal(25 - 7, d.PendingBytes);
        Assert.False(d.Faulted);

        Assert.True(d.Feed(bytes.AsSpan(bytes.Length - 7), msgs));   // the rest arrives later
        Assert.Equal(2, msgs.Count);
        Assert.Equal(0, d.PendingBytes);
    }

    [Theory]
    [InlineData(new byte[] { (byte)'V', (byte)'F', 2 }, "version")]
    [InlineData(new byte[] { (byte)'X', (byte)'F', 1 }, "hello")]
    [InlineData(new byte[] { (byte)'V', (byte)'F', 1, 0x7F }, "type")]
    [InlineData(new byte[] { (byte)'V', (byte)'F', 1, 0x02, 9, 0, 0, 0, 0, 0, 0, 0 }, "status kind")]
    public void Bad_streams_fault_closed(byte[] bytes, string reason)
    {
        var d = new FrameWireDecoder();
        var msgs = new List<WireMessage>();
        Assert.False(d.Feed(bytes, msgs));
        Assert.True(d.Faulted);
        Assert.Contains(reason, d.FaultReason);
        Assert.Empty(msgs);
        // Once faulted, nothing more is decoded — not even valid data.
        Assert.False(d.Feed(Stream(FrameWire.Hello(), FrameWire.EncodeFrame(F(0, 1))), msgs));
        Assert.Empty(msgs);
    }

    [Fact]
    public void Non_finite_or_negative_frame_values_are_rejected()
    {
        var d = new FrameWireDecoder();
        var msgs = new List<WireMessage>();
        Assert.False(d.Feed(Stream(FrameWire.Hello(), FrameWire.EncodeFrame(F(double.NaN, 5))), msgs));
        Assert.Contains("frame", d.FaultReason);
        var d2 = new FrameWireDecoder();
        Assert.False(d2.Feed(Stream(FrameWire.Hello(), FrameWire.EncodeFrame(F(1, -5))), msgs));
        Assert.Empty(msgs);
    }

    [Fact]
    public void Host_errors_map_to_session_codes()
    {
        Assert.Equal(FrameCaptureErrorCode.EtwFailed, NativeFrameCaptureSession.MapHostError(HostError.EtwStartFailed, 5).Code);
        Assert.Contains("access denied", NativeFrameCaptureSession.MapHostError(HostError.EtwStartFailed, 5).Detail);
        Assert.Contains("1450", NativeFrameCaptureSession.MapHostError(HostError.EtwStartFailed, 1450).Detail);
        Assert.Equal(FrameCaptureErrorCode.ProcessNotFound, NativeFrameCaptureSession.MapHostError(HostError.ProcessNotFound, 0).Code);
        foreach (var e in new[] { HostError.NotElevated, HostError.BadRequest, HostError.PipeRejected, HostError.Internal })
            Assert.Equal(FrameCaptureErrorCode.StartFailed, NativeFrameCaptureSession.MapHostError(e, 0).Code);
    }
}
