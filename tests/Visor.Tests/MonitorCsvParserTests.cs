using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class MonitorCsvParserTests
{
    private const string Header2x =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,FrameType," +
        "CPUStartTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait,VideoBusy,DisplayLatency,DisplayedTime," +
        "AnimationError,AnimationTime,ClickToPhotonLatency,AllInputToPhotonLatency";

    private static string Row2x(double t, double ft, string cpuBusy = "5.0", string gpuBusy = "12.0",
        string dispLat = "20.0", string displayed = "16.6", string c2p = "NA") =>
        $"game.exe,1234,0x0000000000ABCDEF,DXGI,1,0,1,Hardware: Independent Flip,Application,{t:F4},{ft:F3},{cpuBusy},1.0,8.0,13.0,{gpuBusy},0.5,0.0,{dispLat},{displayed},0.1,16.7,{c2p},NA";

    [Fact]
    public void Header_2x_maps_by_name_and_rows_parse()
    {
        var p = new PresentMonCsvParser();
        Assert.True(p.TryParseHeader(Header2x));
        Assert.True(p.HasHeader);
        Assert.True(p.HasGpuBusy);
        Assert.True(p.HasCpuBusy);
        Assert.True(p.HasDisplayLatency);
        Assert.True(p.HasClickToPhoton);

        Assert.True(p.TryParseRow(Row2x(1.5, 16.667, c2p: "35.5"), out var r));
        Assert.Equal(1.5, r.Time, 6);
        Assert.Equal(16.667, r.FrameTimeMs, 3);
        Assert.Equal(12.0, r.GpuBusyMs);
        Assert.Equal(5.0, r.CpuBusyMs);
        Assert.Equal(20.0, r.DisplayLatencyMs);
        Assert.Equal(35.5, r.ClickToPhotonMs);
        Assert.False(r.Dropped);
        Assert.Equal("game.exe", r.Application);
        Assert.Equal(1234, r.ProcessId);
        Assert.Equal(60.0, r.Fps, 1);
    }

    [Fact]
    public void Dropped_2x_comes_from_DisplayedTime_NA()
    {
        var p = new PresentMonCsvParser();
        Assert.True(p.TryParseHeader(Header2x));
        Assert.True(p.TryParseRow(Row2x(1, 8.0, displayed: "NA", dispLat: "NA"), out var r));
        Assert.True(r.Dropped);
        Assert.Null(r.DisplayLatencyMs);
    }

    [Fact]
    public void Header_1x_names_are_accepted()
    {
        const string header1x = "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,Dropped," +
            "TimeInSeconds,msBetweenPresents,msBetweenDisplayChange,msInPresentAPI,msUntilRenderComplete,msUntilDisplayed,msGPUActive";
        var p = new PresentMonCsvParser();
        Assert.True(p.TryParseHeader(header1x));
        Assert.True(p.HasGpuBusy);
        Assert.False(p.HasCpuBusy);
        Assert.True(p.HasDisplayLatency);
        Assert.False(p.HasClickToPhoton);

        Assert.True(p.TryParseRow("game.exe,99,0x1,DXGI,1,0,0,Composed: Flip,0,2.5,33.3,33.3,0.2,30.0,45.0,25.0", out var r));
        Assert.Equal(2.5, r.Time);
        Assert.Equal(33.3, r.FrameTimeMs);
        Assert.Equal(25.0, r.GpuBusyMs);
        Assert.Null(r.CpuBusyMs);
        Assert.Equal(45.0, r.DisplayLatencyMs);
        Assert.Null(r.ClickToPhotonMs);
        Assert.False(r.Dropped);

        Assert.True(p.TryParseRow("game.exe,99,0x1,DXGI,1,0,0,Composed: Flip,1,2.53,33.3,NA,0.2,30.0,NA,25.0", out var dropped));
        Assert.True(dropped.Dropped);
        Assert.Null(dropped.DisplayLatencyMs);
    }

    [Fact]
    public void Missing_optional_columns_yield_nulls_and_reordered_columns_still_map()
    {
        var p = new PresentMonCsvParser();
        Assert.True(p.TryParseHeader("FrameTime,Application,CPUStartTime"));
        Assert.True(p.TryParseRow("7.5,x.exe,0.25", out var r));
        Assert.Equal(7.5, r.FrameTimeMs);
        Assert.Equal(0.25, r.Time);
        Assert.Equal("x.exe", r.Application);
        Assert.Null(r.GpuBusyMs);
        Assert.Null(r.CpuBusyMs);
        Assert.Null(r.DisplayLatencyMs);
        Assert.Null(r.ClickToPhotonMs);
        Assert.False(r.Dropped);
        Assert.Equal(0, r.ProcessId);
    }

    [Fact]
    public void Without_time_column_time_is_accumulated_from_frame_times()
    {
        var p = new PresentMonCsvParser();
        Assert.True(p.TryParseHeader("Application,FrameTime"));
        Assert.True(p.TryParseRow("a,10", out var r1));
        Assert.True(p.TryParseRow("a,20", out var r2));
        Assert.True(p.TryParseRow("a,30", out var r3));
        Assert.Equal(0.010, r1.Time, 6);
        Assert.Equal(0.030, r2.Time, 6);
        Assert.Equal(0.060, r3.Time, 6);
    }

    [Fact]
    public void Header_without_frame_time_is_rejected()
    {
        var p = new PresentMonCsvParser();
        Assert.False(p.TryParseHeader("Application,ProcessID,SwapChainAddress"));
        Assert.False(p.HasHeader);
        Assert.False(p.TryParseRow("a,1,2", out _));
        Assert.False(p.TryParseHeader(""));
        Assert.False(p.TryParseHeader(null));
    }

    [Fact]
    public void Malformed_rows_are_counted_not_thrown()
    {
        var p = new PresentMonCsvParser();
        Assert.True(p.TryParseHeader(Header2x));
        Assert.False(p.TryParseRow("game.exe,1234", out _));                        // too short
        Assert.False(p.TryParseRow(Row2x(1, 10).Replace("10.000", "abc"), out _));  // bad number
        Assert.False(p.TryParseRow("", out _));
        Assert.Equal(2, p.RejectedRows);
        Assert.Equal(0, p.AcceptedRows);
    }

    [Fact]
    public void Feed_detects_header_automatically_and_tolerates_repeated_header()
    {
        var p = new PresentMonCsvParser();
        Assert.False(p.Feed("some console noise", out _));
        Assert.False(p.HasHeader);
        Assert.False(p.Feed(Header2x, out _));
        Assert.True(p.HasHeader);
        Assert.True(p.Feed(Row2x(0.1, 16.0), out var r));
        Assert.Equal(16.0, r.FrameTimeMs);
        Assert.False(p.Feed(Header2x, out _));   // PresentMon restarted → new header, no exception
        Assert.True(p.Feed(Row2x(0.2, 8.0), out var r2));
        Assert.Equal(8.0, r2.FrameTimeMs);
    }

    [Fact]
    public void ParseAll_handles_bom_crlf_and_quoted_cells()
    {
        var csv = "﻿Application,FrameTime,CPUStartTime\r\n\"My Game, Deluxe.exe\",12.5,1\r\n\"My Game, Deluxe.exe\",13.5,1.0125\r\n";
        var rows = PresentMonCsvParser.ParseAll(csv);
        Assert.Equal(2, rows.Count);
        Assert.Equal("My Game, Deluxe.exe", rows[0].Application);
        Assert.Equal(13.5, rows[1].FrameTimeMs);
    }

    [Fact]
    public void Invariant_culture_decimal_point()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
            var rows = PresentMonCsvParser.ParseAll("FrameTime,CPUStartTime\n16.5,0.5\n");
            Assert.Single(rows);
            Assert.Equal(16.5, rows[0].FrameTimeMs);
        }
        finally { Thread.CurrentThread.CurrentCulture = old; }
    }
}
