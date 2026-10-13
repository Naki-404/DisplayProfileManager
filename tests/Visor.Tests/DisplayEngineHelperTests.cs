using Visor.Services;
using Xunit;

namespace Visor.Tests;

/// <summary>Pure helpers only — nothing here touches ChangeDisplaySettings / power schemes.</summary>
public class DisplayEngineHelperTests
{
    [Theory]
    [InlineData("2560x1440", 2560, 1440)]
    [InlineData(" 1920 x 1080 ", 1920, 1080)]
    [InlineData("1920X1080", 1920, 1080)]
    [InlineData("1280×720", 1280, 720)]
    [InlineData("640x480", 640, 480)]
    public void TryParseRes_parses(string input, int w, int h)
    {
        Assert.True(DisplayEngine.TryParseRes(input, out var pw, out var ph));
        Assert.Equal(w, pw);
        Assert.Equal(h, ph);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1920")]
    [InlineData("1920x")]
    [InlineData("x1080")]
    [InlineData("1920x1080x60")]
    [InlineData("abcxdef")]
    [InlineData("639x480")]
    [InlineData("640x479")]
    [InlineData("-1920x1080")]
    [InlineData("1920x1080@144")]
    public void TryParseRes_rejects(string? input)
    {
        // Only the return value is the contract; the out values are unspecified on failure.
        Assert.False(DisplayEngine.TryParseRes(input, out _, out _));
    }

    [Fact]
    public void SortResolutions_is_numeric_largest_first_and_distinct()
    {
        var sorted = DisplayEngine.SortResolutions(new[]
        {
            (1024, 768), (800, 600), (3840, 2160), (1920, 1080), (1920, 1200), (2560, 1440), (1920, 1080), (1280, 1024)
        });

        Assert.Equal(new[] { "3840x2160", "2560x1440", "1920x1200", "1920x1080", "1280x1024", "1024x768", "800x600" }, sorted);
        Assert.Equal("800x600", sorted[^1]);
    }

    [Fact]
    public void SortResolutions_empty_input()
    {
        Assert.Empty(DisplayEngine.SortResolutions(Array.Empty<(int, int)>()));
    }

    [Theory]
    [InlineData("highPerformance", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")]
    [InlineData("HIGHPERFORMANCE", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")]
    [InlineData("high", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")]
    [InlineData("balanced", "381b4222-f694-41f0-9685-ff5bb260df2e")]
    [InlineData("powerSaver", "a1841308-3541-4fab-bc81-f71556f20b4a")]
    [InlineData("ultimate", "e9a42b02-d5df-448d-aa00-03f14749eb61")]
    [InlineData(" balanced ", "381b4222-f694-41f0-9685-ff5bb260df2e")]
    [InlineData("{381B4222-F694-41F0-9685-FF5BB260DF2E}", "381b4222-f694-41f0-9685-ff5bb260df2e")]
    [InlineData("11111111-2222-3333-4444-555555555555", "11111111-2222-3333-4444-555555555555")]
    public void ResolvePowerPlanGuid_maps_known_aliases_and_guids(string plan, string expected)
    {
        Assert.Equal(expected, DisplayEngine.ResolvePowerPlanGuid(plan));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("turbo")]
    [InlineData("balanced-ish")]
    [InlineData("not-a-guid")]
    public void ResolvePowerPlanGuid_unknown_returns_null_never_balanced(string? plan)
    {
        Assert.Null(DisplayEngine.ResolvePowerPlanGuid(plan));
    }

    [Fact]
    public void PowerPlanAlias_round_trips_stock_plans()
    {
        Assert.Equal("balanced", DisplayEngine.PowerPlanAlias("381B4222-F694-41F0-9685-FF5BB260DF2E"));
        Assert.Equal("highPerformance", DisplayEngine.PowerPlanAlias("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"));
        Assert.Equal("powerSaver", DisplayEngine.PowerPlanAlias("a1841308-3541-4fab-bc81-f71556f20b4a"));
        Assert.Equal("ultimate", DisplayEngine.PowerPlanAlias("e9a42b02-d5df-448d-aa00-03f14749eb61"));
        Assert.Null(DisplayEngine.PowerPlanAlias("11111111-2222-3333-4444-555555555555"));

        foreach (var alias in new[] { "balanced", "highPerformance", "powerSaver", "ultimate" })
            Assert.Equal(alias, DisplayEngine.PowerPlanAlias(DisplayEngine.ResolvePowerPlanGuid(alias)!));
    }
}
