using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class ShellFitTests
{
    // DIP work areas ≈ (physical / scale) minus ~40–48 px taskbar.
    // 1920×1080 @100% → ~1920×1032
    // 1366×768 @100% → ~1366×720
    // 1366×768 @125% → ~1093×566
    // 1920×1080 @150% → ~1280×672
    // 1920×1080 @125% → ~1536×816

    [Theory]
    [InlineData(1920, 1032)] // 1080p @100%
    [InlineData(1366, 720)]  // 768p @100%
    [InlineData(1093, 566)]  // 1366×768 @125%
    [InlineData(1280, 672)]  // 1080p @150%
    [InlineData(1536, 816)]  // 1080p @125%
    public void FitsWorkArea_for_common_dpi_targets(double w, double h)
    {
        Assert.True(ShellFit.FitsWorkArea(w, h), $"default shell should fit {w}×{h} DIP work area");
        var (dw, dh) = ShellFit.DefaultSize(w, h);
        Assert.True(dw <= w - 16);
        Assert.True(dh <= h - 16);
        Assert.True(ShellFit.MinWidthFor(w) <= w - 16);
        Assert.True(ShellFit.MinHeightFor(h) <= h - 16);
    }

    [Fact]
    public void MinWidth_prefers_1100_when_space_allows()
    {
        Assert.Equal(1100, ShellFit.MinWidthFor(1920));
        Assert.True(ShellFit.MinWidthFor(1093) < 1100);
        Assert.True(ShellFit.MinWidthFor(1093) >= ShellFit.AbsoluteMinWidth);
    }

    [Fact]
    public void MinHeight_clamps_for_short_768_at_125()
    {
        // ~566 DIP work area — preferred 520 still fits (566-16=550)
        Assert.Equal(520, ShellFit.MinHeightFor(566));
        // Very short work area
        Assert.Equal(420, ShellFit.MinHeightFor(430));
    }

    [Fact]
    public void DefaultSize_never_exceeds_preferred_caps()
    {
        var (w, h) = ShellFit.DefaultSize(2560, 1400);
        Assert.Equal(ShellFit.PreferredWidth, w);
        Assert.Equal(ShellFit.PreferredHeight, h);
    }
}
