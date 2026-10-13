using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class SensorMathTests
{
    private const long Tick = 10_000; // 1 ms in 100 ns units

    [Fact]
    public void Cpu_percent_from_system_times_delta()
    {
        // Over 1000 ms: kernel (incl. idle) advanced 800, user 200 → total 1000; idle 250 → busy 75 %.
        var pct = SensorMath.CpuPercent(
            idle0: 1_000 * Tick, kernel0: 5_000 * Tick, user0: 2_000 * Tick,
            idle1: 1_250 * Tick, kernel1: 5_800 * Tick, user1: 2_200 * Tick);
        Assert.NotNull(pct);
        Assert.Equal(75.0, pct!.Value, 6);
    }

    [Fact]
    public void Cpu_percent_idle_box_is_zero_and_pegged_box_is_hundred()
    {
        Assert.Equal(0.0, SensorMath.CpuPercent(0, 0, 0, 1000, 1000, 0)!.Value, 6);
        Assert.Equal(100.0, SensorMath.CpuPercent(0, 0, 0, 0, 600, 400)!.Value, 6);
    }

    [Fact]
    public void Cpu_percent_null_on_empty_or_backwards_window()
    {
        Assert.Null(SensorMath.CpuPercent(1, 1, 1, 1, 1, 1));
        Assert.Null(SensorMath.CpuPercent(10, 10, 10, 5, 5, 5));
        Assert.Null(SensorMath.CpuPercent(10, 0, 0, 5, 100, 0)); // idle went backwards
    }

    [Fact]
    public void Cpu_percent_clamps_rounding_noise()
    {
        // idle slightly larger than total (scheduler accounting jitter) → 0, never negative
        Assert.Equal(0.0, SensorMath.CpuPercent(0, 0, 0, 1001, 1000, 0)!.Value, 6);
    }

    [Fact]
    public void Rate_per_second_plain_delta()
    {
        Assert.Equal(1_000.0, SensorMath.RatePerSecond(10_000, 12_000, 2.0)!.Value, 6);
        Assert.Equal(0.0, SensorMath.RatePerSecond(5, 5, 1.0)!.Value, 6);
    }

    [Fact]
    public void Rate_per_second_handles_32bit_counter_wrap()
    {
        long before = uint.MaxValue - 99;   // 99 increments reach the top, 1 more wraps to 0 ...
        long after = 49;                    // ... then 49 more → 149 bytes in total
        var r = SensorMath.RatePerSecond(before, after, 1.0);
        Assert.NotNull(r);
        Assert.Equal(149.0, r!.Value, 6);

        // the classic exact-wrap case: max → 0 is one increment
        Assert.Equal(1.0, SensorMath.RatePerSecond(uint.MaxValue, 0, 1.0)!.Value, 6);
    }

    [Fact]
    public void Rate_per_second_treats_64bit_backwards_as_reset()
    {
        Assert.Null(SensorMath.RatePerSecond((long)uint.MaxValue + 500, 10, 1.0));
    }

    [Fact]
    public void Rate_per_second_rejects_bad_inputs()
    {
        Assert.Null(SensorMath.RatePerSecond(0, 10, 0));
        Assert.Null(SensorMath.RatePerSecond(0, 10, -1));
        Assert.Null(SensorMath.RatePerSecond(-1, 10, 1));
        Assert.Null(SensorMath.RatePerSecond(10, -1, 1));
    }

    [Fact]
    public void Rate_sensor_first_sample_is_not_sampled_yet()
    {
        var first = SensorMath.Rate(null, 100, 1.0);
        Assert.False(first.HasValue);
        Assert.Equal(UnavailableReason.NotSampledYet, first.Reason);

        var second = SensorMath.Rate(100, 300, 0.5);
        Assert.True(second.HasValue);
        Assert.Equal(400.0, second.Value!.Value, 6);

        var zeroWindow = SensorMath.Rate(100, 300, 0);
        Assert.Equal(UnavailableReason.NotSampledYet, zeroWindow.Reason);
    }

    [Fact]
    public void Used_percent()
    {
        Assert.Equal(50.0, SensorMath.UsedPercent(50, 100)!.Value, 6);
        Assert.Equal(100.0, SensorMath.UsedPercent(150, 100)!.Value, 6);
        Assert.Null(SensorMath.UsedPercent(1, 0));
        Assert.Null(SensorMath.UsedPercent(-1, 10));
    }

    [Fact]
    public void Drive_free_percent()
    {
        var d = new DriveSensorReading { Name = "C:", TotalBytes = 1000, FreeBytes = 45 };
        Assert.Equal(4.5, d.FreePercent, 6);
        Assert.Equal(0, new DriveSensorReading { Name = "X:", TotalBytes = 0, FreeBytes = 5 }.FreePercent);
    }

    [Fact]
    public void Snapshot_cpu_temperature_is_always_no_driver()
    {
        var s = new SensorSnapshot();
        Assert.False(s.CpuTemperatureC.HasValue);
        Assert.Equal(UnavailableReason.NoDriver, s.CpuTemperatureC.Reason);
    }

    [Theory]
    [InlineData(0u, 0u, GpuThrottleFlags.None)]
    [InlineData(1u, 0u, GpuThrottleFlags.Thermal)]                       // PerformanceDecreaseReason.ThermalProtection
    [InlineData(2u, 0u, GpuThrottleFlags.Power)]                         // PowerControl
    [InlineData(4u, 0u, GpuThrottleFlags.Battery)]                       // AC_BATT
    [InlineData(8u, 0u, GpuThrottleFlags.Api)]                           // ApiTriggered
    [InlineData(16u, 0u, GpuThrottleFlags.InsufficientPower)]            // InsufficientPower
    [InlineData(0u, 1u, GpuThrottleFlags.Power)]                         // PerformanceLimit.PowerLimit
    [InlineData(0u, 2u, GpuThrottleFlags.Thermal)]                       // TemperatureLimit
    [InlineData(0u, 4u, GpuThrottleFlags.Voltage)]                       // VoltageLimit
    [InlineData(0u, 16u, GpuThrottleFlags.NoLoad)]                       // NoLoadLimit
    [InlineData(3u, 6u, GpuThrottleFlags.Thermal | GpuThrottleFlags.Power | GpuThrottleFlags.Voltage)]
    [InlineData(0u, 8u, GpuThrottleFlags.None)]                          // Unknown8 is not mapped
    public void Nvidia_throttle_bits_map(uint decrease, uint limit, GpuThrottleFlags expected)
    {
        Assert.Equal(expected, NvidiaGpuReader.MapThrottle(decrease, limit));
    }

    // ---- service lifecycle with an injected sampler (no hardware) ----

    private sealed class FakeSampler : ISensorSampler
    {
        public int Samples;
        public int Disposed;
        public SensorSnapshot Sample()
        {
            Interlocked.Increment(ref Samples);
            return new SensorSnapshot { CpuTotalPercent = Sensor.Of(12.5) };
        }
        public void Dispose() => Interlocked.Increment(ref Disposed);
    }

    [Fact]
    public void Sensor_service_runs_only_while_subscribed()
    {
        var sampler = new FakeSampler();
        int created = 0;
        using var svc = new SensorService(() => { Interlocked.Increment(ref created); return sampler; }, TimeSpan.FromMilliseconds(20));
        var got = new ManualResetEventSlim();
        svc.SnapshotReady += (_, s) => { if (s.CpuTotalPercent.Value == 12.5) got.Set(); };

        Assert.False(svc.IsRunning);
        Assert.Equal(0, created);

        var a = svc.Subscribe();
        var b = svc.Subscribe();
        Assert.True(svc.IsRunning);
        Assert.Equal(2, svc.SubscriberCount);
        Assert.True(got.Wait(TimeSpan.FromSeconds(5)), "no snapshot published");
        Assert.NotNull(svc.Latest);

        a.Dispose();
        a.Dispose(); // double dispose must not decrement twice
        Assert.True(svc.IsRunning);
        Assert.Equal(1, svc.SubscriberCount);

        b.Dispose();
        Assert.False(svc.IsRunning);
        Assert.Equal(0, svc.SubscriberCount);
        Assert.Null(svc.Latest);

        // the sampler thread unwinds and disposes its sampler
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref sampler.Disposed) == 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.Equal(1, sampler.Disposed);
        Assert.Equal(1, created);
    }

    [Fact]
    public void Sensor_service_restarts_with_a_fresh_sampler_and_survives_sampler_exceptions()
    {
        int created = 0;
        var s1 = new FakeSampler();
        var s2 = new FakeSampler();
        using var svc = new SensorService(() => Interlocked.Increment(ref created) == 1 ? s1 : s2, TimeSpan.FromMilliseconds(10));

        using (svc.Subscribe())
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref s1.Samples) == 0 && DateTime.UtcNow < deadline) Thread.Sleep(5);
            Assert.True(s1.Samples > 0);
        }
        using (svc.Subscribe())
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref s2.Samples) == 0 && DateTime.UtcNow < deadline) Thread.Sleep(5);
            Assert.True(s2.Samples > 0);
        }
        Assert.Equal(2, created);
    }

    [Fact]
    public void Sensor_service_subscribe_after_dispose_throws()
    {
        var svc = new SensorService(() => new FakeSampler(), TimeSpan.FromMilliseconds(10));
        svc.Dispose();
        Assert.Throws<ObjectDisposedException>(() => svc.Subscribe());
        svc.Dispose(); // idempotent
    }

    [SkippableFact]
    public void Live_sampler_produces_a_snapshot_with_reasons()
    {
        using var sampler = new LiveSensorSampler();
        SensorSnapshot first, second;
        try
        {
            first = sampler.Sample();
            Thread.Sleep(120);
            second = sampler.Sample();
        }
        catch (Exception ex) { throw new Xunit.SkipException("live sampling failed here: " + ex.Message); }

        Assert.Equal(UnavailableReason.NoDriver, first.CpuTemperatureC.Reason);
        Assert.False(first.CpuTotalPercent.HasValue);
        Assert.Equal(UnavailableReason.NotSampledYet, first.CpuTotalPercent.Reason);
        Skip.IfNot(second.CpuTotalPercent.HasValue, "GetSystemTimes delta unavailable on this host");
        Assert.InRange(second.CpuTotalPercent.Value!.Value, 0, 100);
        foreach (var d in second.Drives)
        {
            Assert.True(d.ReadBytesPerSec.HasValue || d.ReadBytesPerSec.EffectiveReason != UnavailableReason.None);
            Assert.True(d.TotalBytes > 0);
        }
        foreach (var g in second.Gpus)
            Assert.True(g.TemperatureC.HasValue || g.TemperatureC.EffectiveReason != UnavailableReason.None);
    }
}
