using System.Windows;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class WindowBoundsTests
{
    private static readonly Rect Primary = new(0, 0, 2560, 1400);          // taskbar at the bottom
    private static readonly Rect Secondary = new(2560, 0, 1920, 1040);     // to the right
    private static readonly Rect LeftNegative = new(-1920, 0, 1920, 1040); // monitor left of primary

    [Fact]
    public void Rect_fully_inside_primary_is_unchanged()
    {
        var r = new Rect(100, 50, 1080, 720);
        Assert.Equal(r, UiStateStore.ClampToWorkAreas(r, new[] { Primary, Secondary }));
    }

    [Fact]
    public void Rect_on_secondary_monitor_is_unchanged()
    {
        var r = new Rect(2700, 100, 1080, 720);
        Assert.Equal(r, UiStateStore.ClampToWorkAreas(r, new[] { Primary, Secondary }));
    }

    [Fact]
    public void Rect_on_negative_coordinate_monitor_is_unchanged()
    {
        var r = new Rect(-1800, 100, 1080, 720);
        Assert.Equal(r, UiStateStore.ClampToWorkAreas(r, new[] { Primary, LeftNegative }));
    }

    [Fact]
    public void Rect_partially_visible_enough_is_unchanged()
    {
        // 200 px visible on the right edge of the primary — still grabbable.
        var r = new Rect(2360, 100, 1080, 720);
        Assert.Equal(r, UiStateStore.ClampToWorkAreas(r, new[] { Primary }));
    }

    [Fact]
    public void Off_screen_rect_is_moved_into_primary_work_area()
    {
        var r = new Rect(5000, 3000, 1080, 720);
        var c = UiStateStore.ClampToWorkAreas(r, new[] { Primary, Secondary });
        Assert.Equal(1080, c.Width);
        Assert.Equal(720, c.Height);
        Assert.Equal(Primary.Right - 1080, c.Left);
        Assert.Equal(Primary.Bottom - 720, c.Top);
        Assert.True(Primary.Contains(c));
    }

    [Fact]
    public void Rect_above_and_left_of_everything_is_moved_to_origin()
    {
        var r = new Rect(-3000, -2000, 1080, 720);
        var c = UiStateStore.ClampToWorkAreas(r, new[] { Primary });
        Assert.Equal(new Rect(0, 0, 1080, 720), c);
    }

    [Fact]
    public void Rect_on_monitor_that_no_longer_exists_is_moved()
    {
        var saved = new Rect(2700, 100, 1080, 720); // was on Secondary
        var c = UiStateStore.ClampToWorkAreas(saved, new[] { Primary }); // Secondary unplugged
        Assert.True(Primary.Contains(c), $"got {c}");
        Assert.Equal(1080, c.Width);
        Assert.Equal(720, c.Height);
        Assert.Equal(100, c.Top); // only the axis that was off-screen moves
    }

    [Fact]
    public void Barely_visible_sliver_counts_as_off_screen()
    {
        var r = new Rect(Primary.Right - 20, 100, 1080, 720); // 20 px < 48 px threshold
        var c = UiStateStore.ClampToWorkAreas(r, new[] { Primary });
        Assert.True(Primary.Contains(c));
    }

    [Fact]
    public void Oversized_rect_is_shrunk_to_largest_work_area()
    {
        var r = new Rect(0, 0, 4000, 3000);
        var c = UiStateStore.ClampToWorkAreas(r, new[] { Primary, Secondary });
        Assert.Equal(new Rect(0, 0, 2560, 1400), c);
    }

    [Fact]
    public void Oversized_off_screen_rect_is_shrunk_and_moved()
    {
        var r = new Rect(9000, 9000, 4000, 3000);
        var c = UiStateStore.ClampToWorkAreas(r, new[] { Secondary, Primary }); // first = primary here
        Assert.Equal(new Rect(2560, 0, 1920, 1040), c);
    }

    [Fact]
    public void No_work_areas_or_empty_rect_returns_input()
    {
        var r = new Rect(5000, 5000, 100, 100);
        Assert.Equal(r, UiStateStore.ClampToWorkAreas(r, Array.Empty<Rect>()));
        Assert.Equal(Rect.Empty, UiStateStore.ClampToWorkAreas(Rect.Empty, new[] { Primary }));
    }
}
