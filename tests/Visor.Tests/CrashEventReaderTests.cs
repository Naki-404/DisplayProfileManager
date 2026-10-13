using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class CrashEventReaderTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    [Theory]
    [InlineData("Microsoft-Windows-Kernel-Power", 41, 1, CrashEventKind.UnexpectedShutdown)]
    [InlineData("microsoft-windows-kernel-power", 41, 1, CrashEventKind.UnexpectedShutdown)]
    [InlineData("Microsoft-Windows-Kernel-Power", 42, 4, null)]       // sleep, not a crash
    [InlineData("Microsoft-Windows-WHEA-Logger", 18, 1, CrashEventKind.WheaFatal)]
    [InlineData("Microsoft-Windows-WHEA-Logger", 17, 3, CrashEventKind.WheaCorrected)]
    [InlineData("Microsoft-Windows-WHEA-Logger", 19, 3, CrashEventKind.WheaCorrected)]
    [InlineData("Microsoft-Windows-WHEA-Logger", 20, 3, null)]
    [InlineData("Display", 4101, 3, CrashEventKind.DisplayTdr)]
    [InlineData("Display", 4100, 3, null)]
    [InlineData("nvlddmkm", 153, 2, CrashEventKind.DisplayDriverError)]
    [InlineData("nvlddmkm", 14, 1, CrashEventKind.DisplayDriverError)]
    [InlineData("nvlddmkm", 13, 3, null)]                              // warning-level driver chatter
    [InlineData("nvlddmkm", 13, 4, null)]
    [InlineData("amdkmdag", 1, 2, CrashEventKind.DisplayDriverError)]
    [InlineData("amdkmdag", 1, 0, null)]
    [InlineData("Service Control Manager", 7000, 2, null)]
    [InlineData("", 41, 1, null)]
    [InlineData(null, 41, 1, null)]
    public void Classify(string? provider, int id, int level, CrashEventKind? expected)
    {
        Assert.Equal(expected, CrashEventReader.Classify(provider, id, level));
    }

    [Fact]
    public void Summarize_counts_per_kind_with_latest_timestamp()
    {
        var records = new[]
        {
            new CrashEventRecord(Now.AddDays(-5), "Microsoft-Windows-Kernel-Power", 41, 1),
            new CrashEventRecord(Now.AddDays(-1), "Microsoft-Windows-Kernel-Power", 41, 1),
            new CrashEventRecord(Now.AddDays(-3), "Microsoft-Windows-Kernel-Power", 41, 1),
            new CrashEventRecord(Now.AddDays(-2), "Microsoft-Windows-WHEA-Logger", 17, 3),
            new CrashEventRecord(Now.AddDays(-2), "Microsoft-Windows-WHEA-Logger", 19, 3),
            new CrashEventRecord(Now.AddHours(-1), "nvlddmkm", 153, 2),
            new CrashEventRecord(Now.AddHours(-2), "Service Control Manager", 7034, 2)   // noise
        };

        var s = CrashEventReader.Summarize(records, Now, Window);

        Assert.Equal(UnavailableReason.None, s.Reason);
        Assert.Equal(Now - Window, s.FromUtc);
        Assert.Equal(Now, s.ToUtc);
        Assert.Equal(3, s.Count(CrashEventKind.UnexpectedShutdown));
        Assert.Equal(Now.AddDays(-1), s.Last(CrashEventKind.UnexpectedShutdown));
        Assert.Equal(2, s.Count(CrashEventKind.WheaCorrected));
        Assert.Equal(0, s.Count(CrashEventKind.WheaFatal));
        Assert.Null(s.Last(CrashEventKind.WheaFatal));
        Assert.Equal(1, s.Count(CrashEventKind.DisplayDriverError));
        Assert.Equal(0, s.Count(CrashEventKind.DisplayTdr));
        Assert.Equal(6, s.Total);
        Assert.Equal(Enum.GetValues<CrashEventKind>().Length, s.Buckets.Count); // every kind has a bucket, even at 0
    }

    [Fact]
    public void Summarize_applies_window_and_ignores_future_records()
    {
        var records = new[]
        {
            new CrashEventRecord(Now - Window, "Microsoft-Windows-Kernel-Power", 41, 1),                 // on the edge: in
            new CrashEventRecord(Now - Window - TimeSpan.FromSeconds(1), "Microsoft-Windows-Kernel-Power", 41, 1), // out
            new CrashEventRecord(Now.AddMinutes(5), "Microsoft-Windows-Kernel-Power", 41, 1)               // clock skew: out
        };
        var s = CrashEventReader.Summarize(records, Now, Window);
        Assert.Equal(1, s.Count(CrashEventKind.UnexpectedShutdown));
        Assert.Equal(Now - Window, s.Last(CrashEventKind.UnexpectedShutdown));
    }

    [Fact]
    public void Summarize_empty_is_all_zero()
    {
        var s = CrashEventReader.Summarize([], Now, Window);
        Assert.Equal(0, s.Total);
        Assert.All(s.Buckets, b => { Assert.Equal(0, b.Count); Assert.Null(b.LastUtc); });
    }

    [Fact]
    public void Summarize_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => CrashEventReader.Summarize(null!, Now, Window));
    }

    [SkippableFact]
    public void Live_read_of_system_log_never_throws()
    {
        var s = CrashEventReader.Read(TimeSpan.FromDays(7));
        Skip.If(s.Reason != UnavailableReason.None, "System log not readable here: " + s.Reason);
        Assert.Equal(Enum.GetValues<CrashEventKind>().Length, s.Buckets.Count);
        Assert.True(s.ToUtc > s.FromUtc);
        foreach (var b in s.Buckets)
        {
            Assert.True(b.Count >= 0);
            if (b.Count > 0) Assert.NotNull(b.LastUtc);
        }
    }
}
