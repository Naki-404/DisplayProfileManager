using Visor.Services;
using Visor.Services.FrameCapture;
using Xunit;

namespace Visor.Tests;

public class FrameCaptureTrackerTests
{
    private const long Freq = 10_000_000;   // 10 MHz, the usual QPC rate on Windows 10+
    private const long Origin = 1_000_000_000;

    private static PresentEvent Dxgi(double sec, int pid = 100, ulong swap = 0xA000, long freq = Freq) =>
        new(Origin + (long)Math.Round(sec * freq), pid, PresentSource.Dxgi, PresentProviders.DxgiPresentStart, swap);

    private static PresentEvent D3d9(double sec, int pid = 100, ulong swap = 0xD000) =>
        new(Origin + (long)Math.Round(sec * Freq), pid, PresentSource.D3d9, PresentProviders.D3d9PresentStart, swap);

    private static PresentEvent Kernel(double sec, ushort id, int pid = 100) =>
        new(Origin + (long)Math.Round(sec * Freq), pid, PresentSource.DxgKrnl, id);

    private static List<FrameRecord> Run(PresentTracker t, IEnumerable<PresentEvent> events)
    {
        var frames = new List<FrameRecord>();
        foreach (var e in events)
            if (t.TryFeed(e, out var f)) frames.Add(f);
        return frames;
    }

    [Fact]
    public void Steady_144_fps_dxgi_gives_one_frame_per_present()
    {
        var t = new PresentTracker(Freq, new[] { 100 });
        var frames = Run(t, Enumerable.Range(0, 1000).Select(i => Dxgi(i / 144.0)));

        // The first MinEventsToSelect − 1 presents only establish the channel; every later present is a frame.
        Assert.Equal(1000 - (PresentTracker.MinEventsToSelect - 1), frames.Count);
        Assert.All(frames, f => Assert.Equal(1000.0 / 144, f.FrameTimeMs, 3));
        Assert.All(frames, f => Assert.Equal(100, f.ProcessId));
        Assert.All(frames, f => { Assert.Null(f.GpuBusyMs); Assert.Null(f.DisplayLatencyMs); Assert.False(f.Dropped); });
        Assert.Equal(999 / 144.0, frames[^1].Time, 6);   // seconds since the first present seen
        for (int i = 1; i < frames.Count; i++) Assert.True(frames[i].Time > frames[i - 1].Time);

        var s = FrameStats.Compute(frames);
        Assert.Equal(144, s.AvgFps, 2);
        Assert.Equal(PresentSource.Dxgi, t.MainStream!.Value.Source);
    }

    [Theory]
    [InlineData(3_579_545L, 60.0)]     // legacy ACPI PM timer frequency
    [InlineData(24_000_000L, 240.0)]   // ARM64 generic timer
    [InlineData(10_000_000L, 30.0)]
    public void Qpc_frequency_is_honoured(long freq, double fps)
    {
        var t = new PresentTracker(freq);
        var frames = Run(t, Enumerable.Range(0, 200).Select(i => Dxgi(i / fps, freq: freq)));
        Assert.Equal(198, frames.Count);
        Assert.All(frames, f => Assert.Equal(1000.0 / fps, f.FrameTimeMs, 2));
        Assert.Equal(199 / fps, frames[^1].Time, 4);
    }

    [Fact]
    public void Mixed_providers_never_double_count_a_frame()
    {
        // Every frame is seen three times: DXGI Present_Start, then DxgKrnl Present_Info and PresentHistoryDetailed_Start.
        var events = new List<PresentEvent>();
        for (int i = 0; i < 500; i++)
        {
            double t0 = i / 100.0;
            events.Add(Dxgi(t0));
            events.Add(Kernel(t0 + 0.0005, PresentProviders.DxgkPresentInfo));
            events.Add(Kernel(t0 + 0.0006, PresentProviders.DxgkPresentHistoryDetailedStart));
        }
        var t = new PresentTracker(Freq, new[] { 100 });
        var frames = Run(t, events);

        Assert.InRange(frames.Count, 495, 499);                         // ≤ one per real frame
        Assert.All(frames, f => Assert.Equal(10, f.FrameTimeMs, 3));    // never a 0.5 ms "frame" between providers
        Assert.Equal(frames.Sum(f => f.FrameTimeMs), (frames[^1].Time - frames[0].Time) * 1000 + frames[0].FrameTimeMs, 3);
        Assert.Equal(PresentSource.Dxgi, t.MainStream!.Value.Source);
        Assert.Equal(1500, t.EventsSeen);
    }

    [Fact]
    public void Kernel_only_presents_are_used_for_apis_that_bypass_dxgi()
    {
        // Native Vulkan / OpenGL: no DXGI or D3D9 events; DxgKrnl Present_Info + Flip_Info per frame.
        var events = new List<PresentEvent>();
        for (int i = 0; i < 300; i++)
        {
            events.Add(Kernel(i / 120.0, PresentProviders.DxgkFlipInfo));
            events.Add(Kernel(i / 120.0 + 0.0001, PresentProviders.DxgkPresentInfo));
        }
        var t = new PresentTracker(Freq);
        var frames = Run(t, events);
        Assert.InRange(frames.Count, 296, 298);
        Assert.All(frames, f => Assert.Equal(1000.0 / 120, f.FrameTimeMs, 2));
        Assert.Equal(PresentSource.DxgKrnl, t.MainStream!.Value.Source);
        Assert.Equal(PresentProviders.DxgkPresentInfo, t.MainStream!.Value.EventId);   // preferred over Flip_Info
    }

    [Fact]
    public void D3d9_is_preferred_over_kernel_events()
    {
        var events = new List<PresentEvent>();
        for (int i = 0; i < 200; i++)
        {
            events.Add(Kernel(i / 60.0, PresentProviders.DxgkBlitInfo));          // kernel event arrives first here
            events.Add(D3d9(i / 60.0 + 0.0002));
        }
        var t = new PresentTracker(Freq);
        var frames = Run(t, events);
        Assert.Equal(PresentSource.D3d9, t.MainStream!.Value.Source);
        Assert.All(frames, f => Assert.Equal(1000.0 / 60, f.FrameTimeMs, 2));
        Assert.InRange(frames.Count, 195, 199);
    }

    [Fact]
    public void The_busiest_swap_chain_is_the_game()
    {
        // A 10 Hz launcher / overlay swap chain starts first; the game's 144 Hz chain takes over and stays.
        var events = new List<PresentEvent>();
        for (int i = 0; i < 30; i++) events.Add(Dxgi(i / 10.0, swap: 0xB));
        for (int i = 0; i < 3 * 144; i++) events.Add(Dxgi(3 + i / 144.0, swap: 0xA));
        for (int i = 30; i < 60; i++) events.Add(Dxgi(i / 10.0, swap: 0xB));
        events.Sort((a, b) => a.Qpc.CompareTo(b.Qpc));

        var t = new PresentTracker(Freq);
        var frames = Run(t, events);
        Assert.Equal(0xAul, t.MainStream!.Value.SwapChain);
        var gameFrames = frames.Where(f => f.Time > 3.5).ToList();
        Assert.True(gameFrames.Count > 300);
        Assert.All(gameFrames, f => Assert.Equal(1000.0 / 144, f.FrameTimeMs, 2));   // the slow chain never interleaves
    }

    [Fact]
    public void Gaps_longer_than_five_seconds_restart_the_stream()
    {
        var events = Enumerable.Range(0, 100).Select(i => Dxgi(i / 60.0))
            .Concat(Enumerable.Range(0, 100).Select(i => Dxgi(10 + i / 60.0)))   // 8.35 s pause (alt-tab / loading)
            .ToList();
        var t = new PresentTracker(Freq);
        var frames = Run(t, events);
        Assert.Equal(98 + 99, frames.Count);
        Assert.All(frames, f => Assert.True(f.FrameTimeMs <= PresentTracker.GapMs));
        Assert.All(frames, f => Assert.Equal(1000.0 / 60, f.FrameTimeMs, 2));

        // A long hitch below the gap limit is a real (terrible) frame and is kept.
        var hitch = new PresentTracker(Freq);
        var hf = Run(hitch, new[] { Dxgi(0), Dxgi(0.01), Dxgi(0.02), Dxgi(0.03), Dxgi(3.03) });
        Assert.Equal(3000, hf[^1].FrameTimeMs, 3);
    }

    [Fact]
    public void Out_of_order_and_duplicate_timestamps_are_dropped()
    {
        var t = new PresentTracker(Freq);
        var frames = Run(t, new[] { Dxgi(0), Dxgi(0.01), Dxgi(0.02), Dxgi(0.015), Dxgi(0.02), Dxgi(0.03) });
        Assert.Equal(new[] { 10.0, 10.0 }, frames.Select(f => Math.Round(f.FrameTimeMs, 6)).ToArray());
    }

    [Fact]
    public void Pid_filter_ignores_other_processes_even_when_they_present_faster()
    {
        var events = new List<PresentEvent>();
        for (int i = 0; i < 300; i++) events.Add(Dxgi(i / 240.0, pid: 200));   // e.g. a browser or another game
        for (int i = 0; i < 60; i++) events.Add(Dxgi(i / 60.0, pid: 100));
        events.Sort((a, b) => a.Qpc.CompareTo(b.Qpc));

        var filtered = Run(new PresentTracker(Freq, new[] { 100 }), events);
        Assert.Equal(58, filtered.Count);
        Assert.All(filtered, f => Assert.Equal(100, f.ProcessId));

        // Without a filter the busiest process is chosen — never a mix of both.
        var t = new PresentTracker(Freq);
        var any = Run(t, events);
        Assert.Equal(200, t.MainStream!.Value.ProcessId);
        Assert.All(any.Where(f => f.Time > 0.1), f => Assert.Equal(200, f.ProcessId));
    }

    [Fact]
    public void Events_we_do_not_track_are_ignored()
    {
        var t = new PresentTracker(Freq);
        var stop = new PresentEvent(Origin, 100, PresentSource.Dxgi, 43);                 // DXGI Present_Stop
        var dma = new PresentEvent(Origin, 100, PresentSource.DxgKrnl, 177);              // DmaPacket_Info
        var unknown = new PresentEvent(Origin, 100, PresentSource.Unknown, 1);
        Assert.False(t.TryFeed(stop, out _));
        Assert.False(t.TryFeed(dma, out _));
        Assert.False(t.TryFeed(unknown, out _));
        Assert.Equal(0, t.EventsSeen);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentTracker(0));
    }

    [Fact]
    public void Channel_rank_matches_the_documented_preference()
    {
        Assert.Equal(0, PresentTracker.ChannelRank(PresentSource.Dxgi, PresentProviders.DxgiPresentStart));
        Assert.Equal(0, PresentTracker.ChannelRank(PresentSource.Dxgi, PresentProviders.DxgiPresentMpoStart));
        Assert.Equal(1, PresentTracker.ChannelRank(PresentSource.D3d9, PresentProviders.D3d9PresentStart));
        var kernel = PresentProviders.DxgKrnlEventIds.Select(id => PresentTracker.ChannelRank(PresentSource.DxgKrnl, id)).ToArray();
        Assert.Equal(new[] { 2, 3, 4, 5, 6, 7 }, kernel);
        Assert.Equal(PresentSource.Dxgi, PresentProviders.Classify(PresentProviders.Dxgi, 42));
        Assert.Equal(PresentSource.Unknown, PresentProviders.Classify(PresentProviders.Dxgi, 43));
        Assert.Equal(PresentSource.DxgKrnl, PresentProviders.Classify(PresentProviders.DxgKrnl, 184));
        Assert.Equal(PresentSource.Unknown, PresentProviders.Classify(Guid.NewGuid(), 42));
    }
}
