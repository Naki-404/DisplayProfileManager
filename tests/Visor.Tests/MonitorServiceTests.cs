using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class MonitorServiceTests
{
    /// <summary>Scriptable session: the test raises frames / errors / exit by hand.</summary>
    private sealed class FakeSession : IFrameCaptureSession
    {
        public FrameCaptureTarget? Started;
        public int StartCalls, StopCalls, DisposeCalls;
        public Action<FrameCaptureTarget>? OnStart;
        public Action? OnStop;
        public bool IsRunning { get; set; }
        public event Action<FrameRecord>? Frame;
        public event Action<FrameCaptureErrorCode, string>? Error;
        public event Action<int?>? Exited;

        public void Start(FrameCaptureTarget target)
        {
            StartCalls++;
            Started = target;
            IsRunning = true;
            OnStart?.Invoke(target);
        }
        public void RequestStop() { StopCalls++; OnStop?.Invoke(); }
        public void Dispose() => DisposeCalls++;

        public void RaiseFrame(double t, double ft) => Frame?.Invoke(new FrameRecord(t, ft, null, null, null, null, false));
        public void RaiseError(FrameCaptureErrorCode c, string d = "") => Error?.Invoke(c, d);
        public void RaiseExit(int? code) { IsRunning = false; Exited?.Invoke(code); }

        /// <summary>Simulates the real session's synchronous UAC failure inside Start().</summary>
        public void FailStartWith(FrameCaptureErrorCode code)
        {
            IsRunning = false;
            Error?.Invoke(code, code.ToString());
            Exited?.Invoke(null);
        }
    }

    private sealed class FakeSignals : IBoundSignals
    {
        public double? Gpu;
        public double? Refresh;
        public int Begun, Ended;
        public IDisposable? BeginCapture() { Begun++; return new Token(this); }
        public double? GpuUtilizationPercent() => Gpu;
        public double? DisplayRefreshHz() => Refresh;
        private sealed class Token(FakeSignals s) : IDisposable { public void Dispose() => s.Ended++; }
    }

    private sealed class Sink : IMetricSink
    {
        public readonly List<Visor.Services.Optimize.MetricSample> Samples = new();
        public void OnSample(Visor.Services.Optimize.MetricSample sample) => Samples.Add(sample);
    }

    private static (MonitorService svc, FakeSession session, List<(MonitorState, MonitorErrorCode)> log) Make(
        bool processRunning = true, bool supported = true, FakeSignals? signals = null)
    {
        var session = new FakeSession();
        var svc = new MonitorService(() => session, _ => processRunning, windowSeconds: 5, signals, () => supported);
        var log = new List<(MonitorState, MonitorErrorCode)>();
        svc.StateChanged += (s, e) => log.Add((s, e));
        return (svc, session, log);
    }

    [Fact]
    public void Happy_path_idle_starting_capturing_stopping_idle()
    {
        var (svc, session, log) = Make();
        using (svc)
        {
            Assert.Equal(MonitorState.Idle, svc.State);
            var r = svc.StartCapture("game");
            Assert.True(r.Started);
            Assert.Equal(MonitorErrorCode.None, r.Error);
            Assert.Equal(MonitorState.Starting, svc.State);
            Assert.Equal("game.exe", svc.Target);
            Assert.Equal("game.exe", session.Started!.ExeName);
            Assert.Equal(1, session.StartCalls);
            Assert.True(svc.IsCapturing);

            session.RaiseFrame(0.016, 16);
            Assert.Equal(MonitorState.Capturing, svc.State);
            Assert.Equal(1, svc.Stats.Count);

            svc.StopCapture();
            Assert.Equal(MonitorState.Stopping, svc.State);
            Assert.Equal(1, session.StopCalls);
            session.RaiseExit(0);
            Assert.Equal(MonitorState.Idle, svc.State);
            Assert.Equal(MonitorErrorCode.None, svc.LastError);
            Assert.Equal(1, session.DisposeCalls);
            Assert.False(svc.IsCapturing);

            Assert.Equal(new[]
            {
                (MonitorState.Starting, MonitorErrorCode.None),
                (MonitorState.Capturing, MonitorErrorCode.None),
                (MonitorState.Stopping, MonitorErrorCode.None),
                (MonitorState.Idle, MonitorErrorCode.None),
            }, log);
        }
    }

    [Fact]
    public void No_elevation_host_fails_before_any_uac()
    {
        var (svc, session, log) = Make(supported: false);
        using (svc)
        {
            var r = svc.StartCapture("game.exe");
            Assert.False(r.Started);
            Assert.Equal(MonitorErrorCode.ElevationUnavailable, r.Error);
            Assert.Equal(MonitorState.Error, svc.State);
            Assert.Equal(0, session.StartCalls);
            Assert.Equal((MonitorState.Error, MonitorErrorCode.ElevationUnavailable), log[^1]);
            Assert.False(svc.IsCaptureSupported);

            svc.ClearError();
            Assert.Equal(MonitorState.Idle, svc.State);
            Assert.Equal(MonitorErrorCode.None, svc.LastError);
        }
    }

    [Fact]
    public void Target_not_running_is_reported_before_uac()
    {
        var (svc, session, _) = Make(processRunning: false);
        using (svc)
        {
            var r = svc.StartCapture("game.exe");
            Assert.Equal(MonitorErrorCode.ProcessNotFound, r.Error);
            Assert.Contains("game.exe", r.Detail);
            Assert.Equal(0, session.StartCalls);

            svc.RequireRunningTarget = false;
            Assert.True(svc.StartCapture("game.exe").Started);
            Assert.Equal(1, session.StartCalls);
        }
    }

    [Fact]
    public void Invalid_exe_name_is_rejected()
    {
        var (svc, session, _) = Make();
        using (svc)
        {
            Assert.Equal(MonitorErrorCode.InvalidTarget, svc.StartCapture(@"C:\games\game.exe").Error);
            Assert.Equal(MonitorErrorCode.InvalidTarget, svc.StartCapture("").Error);
            Assert.Equal(0, session.StartCalls);
        }
    }

    [Fact]
    public void Uac_declined_during_start_ends_in_Error_with_UacDeclined()
    {
        var (svc, session, log) = Make();
        session.OnStart = _ => session.FailStartWith(FrameCaptureErrorCode.UacDeclined);
        using (svc)
        {
            var r = svc.StartCapture("game.exe");
            Assert.False(r.Started);
            Assert.Equal(MonitorErrorCode.UacDeclined, r.Error);
            Assert.Equal(MonitorState.Error, svc.State);
            Assert.Equal(MonitorErrorCode.UacDeclined, svc.LastError);
            Assert.Equal(1, session.DisposeCalls);
            Assert.Equal((MonitorState.Error, MonitorErrorCode.UacDeclined), log[^1]);
            Assert.False(svc.IsCapturing);

            // A new attempt is allowed straight away (the user may accept the prompt this time).
            session.OnStart = null;
            Assert.True(svc.StartCapture("game.exe").Started);
            Assert.Equal(MonitorState.Starting, svc.State);
        }
    }

    [Theory]
    [InlineData(FrameCaptureErrorCode.EtwFailed, MonitorErrorCode.EtwFailed)]
    [InlineData(FrameCaptureErrorCode.ProcessNotFound, MonitorErrorCode.ProcessNotFound)]
    [InlineData(FrameCaptureErrorCode.StartFailed, MonitorErrorCode.StartFailed)]
    [InlineData(FrameCaptureErrorCode.StreamUnreadable, MonitorErrorCode.OutputUnreadable)]
    [InlineData(FrameCaptureErrorCode.ElevationUnavailable, MonitorErrorCode.ElevationUnavailable)]
    public void Session_errors_become_the_final_state_when_the_capture_ends(FrameCaptureErrorCode raised, MonitorErrorCode expected)
    {
        var (svc, session, _) = Make();
        using (svc)
        {
            Assert.True(svc.StartCapture("game.exe").Started);
            session.RaiseError(raised, "detail 5");
            Assert.Equal(MonitorState.Starting, svc.State);     // pending until the session ends
            session.RaiseExit(1);
            Assert.Equal(MonitorState.Error, svc.State);
            Assert.Equal(expected, svc.LastError);
            Assert.Equal("detail 5", svc.LastErrorDetail);
        }
    }

    [Fact]
    public void Exit_without_frames_is_NoOutput_but_after_frames_is_clean_idle()
    {
        var (svc, session, _) = Make();
        using (svc)
        {
            Assert.True(svc.StartCapture("game.exe").Started);
            session.RaiseExit(0);
            Assert.Equal(MonitorState.Error, svc.State);
            Assert.Equal(MonitorErrorCode.NoOutput, svc.LastError);

            Assert.True(svc.StartCapture("game.exe").Started);
            session.RaiseError(FrameCaptureErrorCode.NoOutput, "slow start");   // advisory while alive
            Assert.Equal(MonitorState.Starting, svc.State);
            session.RaiseFrame(0.016, 16);
            Assert.Equal(MonitorState.Capturing, svc.State);
            session.RaiseExit(0);                                                // game exited → clean
            Assert.Equal(MonitorState.Idle, svc.State);
            Assert.Equal(MonitorErrorCode.None, svc.LastError);
        }
    }

    [Fact]
    public void Stop_failure_returns_to_Capturing_with_StopFailed_detail()
    {
        var (svc, session, _) = Make();
        session.OnStop = () => throw new InvalidOperationException("pipe gone");
        using (svc)
        {
            Assert.True(svc.StartCapture("game.exe").Started);
            session.RaiseFrame(0.016, 16);
            svc.StopCapture();
            Assert.Equal(MonitorState.Capturing, svc.State);
            Assert.Equal(MonitorErrorCode.StopFailed, svc.LastError);
            Assert.Equal("pipe gone", svc.LastErrorDetail);
            session.RaiseExit(0);
            Assert.Equal(MonitorState.Idle, svc.State);
            Assert.Equal(MonitorErrorCode.None, svc.LastError);
        }
    }

    [Fact]
    public void One_capture_at_a_time()
    {
        var (svc, session, _) = Make();
        using (svc)
        {
            Assert.True(svc.StartCapture("game.exe").Started);
            var r = svc.StartCapture("other.exe");
            Assert.Equal(MonitorErrorCode.Busy, r.Error);
            Assert.Contains("game.exe", r.Detail);
            Assert.Equal(1, session.StartCalls);
            Assert.Equal("game.exe", svc.Target);
            svc.StopCapture();
            svc.StopCapture();                          // idempotent
            Assert.Equal(1, session.StopCalls);
        }
    }

    [Fact]
    public void Tick_publishes_summary_with_bound_and_metric_samples_only_while_capturing()
    {
        var signals = new FakeSignals { Gpu = 98, Refresh = 144 };
        var (svc, session, _) = Make(signals: signals);
        var sink = new Sink();
        var summaries = new List<FrameStatsSummary>();
        svc.SummaryUpdated += summaries.Add;
        svc.AttachSink(sink);
        svc.AttachSink(sink);                           // no duplicates
        using (svc)
        {
            Assert.Same(FrameStatsSummary.Empty, svc.Tick());   // idle → nothing
            Assert.Empty(summaries);

            Assert.True(svc.StartCapture("game.exe").Started);
            Assert.Equal(1, signals.Begun);
            Assert.Same(FrameStatsSummary.Empty, svc.Tick());   // starting, no frames → nothing published
            Assert.Empty(sink.Samples);

            for (int i = 1; i <= 100; i++) session.RaiseFrame(i * 0.01, 10);
            var s = svc.Tick();
            Assert.Equal(100, s.FrameCount);
            Assert.Equal(100, s.AvgFps, 6);
            Assert.Equal(BoundKind.LikelyGpu, s.Bound);
            Assert.Contains("GPU load 98 %", s.BoundEvidence);
            Assert.Same(s, svc.LastSummary);
            Assert.Single(summaries);
            Assert.Equal(6, sink.Samples.Count);                 // one named sample per metric, once
            var fps = Assert.Single(sink.Samples, x => x.Name == MonitorMetricNames.Fps);
            Assert.Equal(100, fps.Value, 6);
            Assert.DoesNotContain(sink.Samples, x => x.Name == MonitorMetricNames.ClickToPhotonMs);

            svc.DetachSink(sink);
            svc.Tick();
            Assert.Equal(6, sink.Samples.Count);
            Assert.Equal(2, summaries.Count);

            session.RaiseExit(0);
            Assert.Equal(MonitorState.Idle, svc.State);
            Assert.Equal(1, signals.Ended);                      // sensor subscription released with the capture
            Assert.Same(svc.LastSummary, svc.Tick());           // idle again: last summary stays visible
        }
    }

    [Fact]
    public void Gpu_load_is_averaged_over_the_window_and_missing_samples_are_skipped()
    {
        var signals = new FakeSignals { Refresh = 144 };
        var (svc, session, _) = Make(signals: signals);
        using (svc)
        {
            Assert.True(svc.StartCapture("game.exe").Started);
            for (int i = 1; i <= 100; i++) session.RaiseFrame(i * 0.01, 10 + (i % 2) * 4);   // jittery 83 FPS
            Assert.Equal(BoundKind.Unknown, svc.Tick().Bound);   // no GPU reading yet
            signals.Gpu = 50; svc.Tick();
            signals.Gpu = null; svc.Tick();                      // a missing reading does not drag the average
            signals.Gpu = 60;
            var s = svc.Tick();
            Assert.Equal(BoundKind.LikelyCpu, s.Bound);
            Assert.Contains("GPU load 55 %", s.BoundEvidence);
        }
    }

    [Fact]
    public void Frames_are_forwarded_and_stats_reset_per_capture()
    {
        var (svc, session, _) = Make();
        var frames = new List<FrameRecord>();
        svc.FrameReceived += frames.Add;
        using (svc)
        {
            Assert.True(svc.StartCapture("game.exe").Started);
            session.RaiseFrame(0.01, 10);
            session.RaiseFrame(0.02, 10);
            Assert.Equal(2, frames.Count);
            Assert.Equal(2, svc.Stats.Count);
            session.RaiseExit(0);

            Assert.True(svc.StartCapture("game.exe").Started);
            Assert.Equal(0, svc.Stats.Count);
            Assert.Same(FrameStatsSummary.Empty, svc.LastSummary);
        }
    }

    [Fact]
    public void IsCaptureSupported_reflects_the_host_and_starts_nothing()
    {
        var (svc, session, log) = Make();
        using (svc)
        {
            Assert.True(svc.IsCaptureSupported);
            Assert.Equal(0, session.StartCalls);
            Assert.Empty(log);
        }
        var (svc2, session2, _) = Make(supported: false);
        using (svc2)
        {
            Assert.False(svc2.IsCaptureSupported);
            Assert.Equal(0, session2.StartCalls);
        }
    }

    [Fact]
    public void Monitor_enums_have_no_external_binary_leftovers()
    {
        var errors = Enum.GetNames<MonitorErrorCode>();
        Assert.DoesNotContain("BinaryMissing", errors);
        Assert.DoesNotContain("Unverified", errors);
        Assert.DoesNotContain("Locating", Enum.GetNames<MonitorState>());
    }

    [Fact]
    public void Dispose_detaches_the_session_without_requesting_a_stop()
    {
        var signals = new FakeSignals();
        var (svc, session, _) = Make(signals: signals);
        Assert.True(svc.StartCapture("game.exe").Started);
        svc.Dispose();
        Assert.Equal(0, session.StopCalls);                 // closing the pipe is the stop signal
        Assert.Equal(1, session.DisposeCalls);
        Assert.Equal(1, signals.Ended);
        session.RaiseFrame(0.01, 10);                       // ignored after detach
        Assert.Equal(0, svc.Stats.Count);
        Assert.Throws<ObjectDisposedException>(() => svc.StartCapture("game.exe"));
    }

    [Fact]
    public void Session_factory_failure_is_StartFailed()
    {
        var svc = new MonitorService(() => throw new InvalidOperationException("no session"), _ => true);
        using (svc)
        {
            var r = svc.StartCapture("game.exe");
            Assert.Equal(MonitorErrorCode.StartFailed, r.Error);
            Assert.Equal("no session", r.Detail);
            Assert.Equal(MonitorState.Error, svc.State);
        }
    }

    [Fact]
    public void Every_session_error_code_maps_to_a_monitor_code()
    {
        foreach (FrameCaptureErrorCode c in Enum.GetValues<FrameCaptureErrorCode>())
        {
            var m = MonitorService.Map(c);
            Assert.True(c == FrameCaptureErrorCode.None ? m == MonitorErrorCode.None : m != MonitorErrorCode.None, c.ToString());
        }
    }
}
