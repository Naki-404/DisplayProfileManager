using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class MonitorServiceTests
{
    /// <summary>Scriptable session: the test raises frames / errors / exit by hand.</summary>
    private sealed class FakeSession : IPresentMonSession
    {
        public PresentMonTarget? Started;
        public int StartCalls, StopCalls, DisposeCalls;
        public Action<PresentMonTarget>? OnStart;
        public Action? OnStop;
        public bool IsRunning { get; set; }
        public event Action<FrameRecord>? Frame;
        public event Action<PresentMonErrorCode, string>? Error;
        public event Action<int?>? Exited;

        public void Start(PresentMonTarget target)
        {
            StartCalls++;
            Started = target;
            IsRunning = true;
            OnStart?.Invoke(target);
        }
        public void RequestStop() { StopCalls++; OnStop?.Invoke(); }
        public void Dispose() => DisposeCalls++;

        public void RaiseFrame(double t, double ft, double? gpu = null, double? cpu = null) =>
            Frame?.Invoke(new FrameRecord(t, ft, gpu, cpu, null, null, false));
        public void RaiseError(PresentMonErrorCode c, string d = "") => Error?.Invoke(c, d);
        public void RaiseExit(int? code) { IsRunning = false; Exited?.Invoke(code); }

        /// <summary>Simulates the real session's synchronous UAC failure inside Start().</summary>
        public void FailStartWith(PresentMonErrorCode code)
        {
            IsRunning = false;
            Error?.Invoke(code, code.ToString());
            Exited?.Invoke(null);
        }
    }

    private sealed class Sink : IMetricSink
    {
        public readonly List<Visor.Services.Optimize.MetricSample> Samples = new();
        public void OnSample(Visor.Services.Optimize.MetricSample sample) => Samples.Add(sample);
    }

    private static PresentMonLocateResult Found(string path = @"C:\fake\PresentMon.exe") =>
        new(PresentMonStatus.Found, path, "2.3.1", PresentMonVerification.AuthenticodeIntel, "test");

    private static (MonitorService svc, FakeSession session, List<(MonitorState, MonitorErrorCode)> log) Make(
        Func<string?, PresentMonLocateResult>? locate = null, bool processRunning = true)
    {
        var session = new FakeSession();
        var svc = new MonitorService(locate ?? (_ => Found()), _ => session, _ => processRunning, windowSeconds: 5);
        var log = new List<(MonitorState, MonitorErrorCode)>();
        svc.StateChanged += (s, e) => log.Add((s, e));
        return (svc, session, log);
    }

    [Fact]
    public void Happy_path_idle_locating_starting_capturing_stopping_idle()
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
                (MonitorState.Locating, MonitorErrorCode.None),
                (MonitorState.Starting, MonitorErrorCode.None),
                (MonitorState.Capturing, MonitorErrorCode.None),
                (MonitorState.Stopping, MonitorErrorCode.None),
                (MonitorState.Idle, MonitorErrorCode.None),
            }, log);
        }
    }

    [Fact]
    public void Binary_missing_fails_before_any_uac()
    {
        var (svc, session, log) = Make(_ => PresentMonLocateResult.Missing("nowhere"));
        using (svc)
        {
            var r = svc.StartCapture("game.exe");
            Assert.False(r.Started);
            Assert.Equal(MonitorErrorCode.BinaryMissing, r.Error);
            Assert.Equal("nowhere", r.Detail);
            Assert.Equal(MonitorState.Error, svc.State);
            Assert.Equal(MonitorErrorCode.BinaryMissing, svc.LastError);
            Assert.Equal(0, session.StartCalls);
            Assert.NotNull(svc.LastLocate);
            Assert.Equal((MonitorState.Error, MonitorErrorCode.BinaryMissing), log[^1]);

            svc.ClearError();
            Assert.Equal(MonitorState.Idle, svc.State);
            Assert.Equal(MonitorErrorCode.None, svc.LastError);
        }
    }

    [Fact]
    public void Unverified_binary_is_refused()
    {
        var (svc, session, _) = Make(_ => new PresentMonLocateResult(PresentMonStatus.Unverified, @"C:\x\PresentMon.exe", null, PresentMonVerification.None, "not signed"));
        using (svc)
        {
            var r = svc.StartCapture("game.exe");
            Assert.Equal(MonitorErrorCode.Unverified, r.Error);
            Assert.Equal(0, session.StartCalls);
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
        session.OnStart = _ => session.FailStartWith(PresentMonErrorCode.UacDeclined);
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

    [Fact]
    public void Etw_failure_maps_from_ExitedWithError()
    {
        var (svc, session, _) = Make();
        using (svc)
        {
            Assert.True(svc.StartCapture("game.exe").Started);
            session.RaiseError(PresentMonErrorCode.ExitedWithError, "code 1");
            session.RaiseExit(1);
            Assert.Equal(MonitorState.Error, svc.State);
            Assert.Equal(MonitorErrorCode.EtwFailed, svc.LastError);
            Assert.Equal("code 1", svc.LastErrorDetail);
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
            session.RaiseError(PresentMonErrorCode.NoOutput, "slow start");   // advisory while alive
            Assert.Equal(MonitorState.Starting, svc.State);
            session.RaiseFrame(0.016, 16);
            Assert.Equal(MonitorState.Capturing, svc.State);
            session.RaiseExit(0);                                              // game exited → clean
            Assert.Equal(MonitorState.Idle, svc.State);
            Assert.Equal(MonitorErrorCode.None, svc.LastError);
        }
    }

    [Fact]
    public void Stop_helper_declined_returns_to_Capturing_with_StopFailed_detail()
    {
        var (svc, session, _) = Make();
        session.OnStop = () => session.RaiseError(PresentMonErrorCode.StopFailed, "helper declined");
        using (svc)
        {
            Assert.True(svc.StartCapture("game.exe").Started);
            session.RaiseFrame(0.016, 16);
            svc.StopCapture();
            Assert.Equal(MonitorState.Capturing, svc.State);
            Assert.Equal(MonitorErrorCode.StopFailed, svc.LastError);
            Assert.Equal("helper declined", svc.LastErrorDetail);
            Assert.True(svc.IsCapturing);
            // The capture ends by itself later (game exit / safety cap) → Idle, error cleared.
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
    public void Tick_publishes_summary_and_metric_samples_only_while_capturing()
    {
        var (svc, session, _) = Make();
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
            Assert.Same(FrameStatsSummary.Empty, svc.Tick());   // starting, no frames → nothing published
            Assert.Empty(sink.Samples);

            for (int i = 1; i <= 100; i++) session.RaiseFrame(i * 0.01, 10, gpu: 9.5, cpu: 3);
            var s = svc.Tick();
            Assert.Equal(100, s.FrameCount);
            Assert.Equal(100, s.AvgFps, 6);
            Assert.Equal(BoundKind.LikelyGpu, s.Bound);
            Assert.Same(s, svc.LastSummary);
            Assert.Single(summaries);
            Assert.Equal(6, sink.Samples.Count);                 // one named sample per metric, once
            var fps = Assert.Single(sink.Samples, x => x.Name == MonitorMetricNames.Fps);
            Assert.Equal(100, fps.Value, 6);
            Assert.Contains(sink.Samples, x => x.Name == MonitorMetricNames.OnePercentLow);

            svc.DetachSink(sink);
            svc.Tick();
            Assert.Equal(6, sink.Samples.Count);
            Assert.Equal(2, summaries.Count);

            session.RaiseExit(0);
            Assert.Equal(MonitorState.Idle, svc.State);
            Assert.Same(svc.LastSummary, svc.Tick());           // idle again: last summary stays visible
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
    public void Locate_does_not_start_anything_and_remembers_the_result()
    {
        int calls = 0;
        var (svc, session, log) = Make(p => { calls++; return p == @"D:\pm" ? Found(@"D:\pm\PresentMon.exe") : PresentMonLocateResult.Missing("x"); });
        using (svc)
        {
            Assert.Equal(PresentMonStatus.Missing, svc.Locate().Status);
            svc.UserConfiguredPath = @"D:\pm";
            Assert.Equal(PresentMonStatus.Found, svc.Locate().Status);
            Assert.Equal(@"D:\pm\PresentMon.exe", svc.LastLocate!.Path);
            Assert.Equal(2, calls);
            Assert.Equal(0, session.StartCalls);
            Assert.Empty(log);
            Assert.Equal(MonitorState.Idle, svc.State);
        }
    }

    [Fact]
    public void Dispose_detaches_the_session_without_requesting_a_stop()
    {
        var (svc, session, _) = Make();
        Assert.True(svc.StartCapture("game.exe").Started);
        svc.Dispose();
        Assert.Equal(0, session.StopCalls);                 // never a UAC prompt from Dispose
        Assert.Equal(1, session.DisposeCalls);
        session.RaiseFrame(0.01, 10);                       // ignored after detach
        Assert.Equal(0, svc.Stats.Count);
        Assert.Throws<ObjectDisposedException>(() => svc.StartCapture("game.exe"));
    }

    [Fact]
    public void Session_factory_failure_is_StartFailed()
    {
        var svc = new MonitorService(_ => Found(), _ => throw new InvalidOperationException("no session"), _ => true);
        using (svc)
        {
            var r = svc.StartCapture("game.exe");
            Assert.Equal(MonitorErrorCode.StartFailed, r.Error);
            Assert.Equal("no session", r.Detail);
            Assert.Equal(MonitorState.Error, svc.State);
        }
    }
}
