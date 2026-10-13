using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class CommandPaletteCatalogTests
{
    private static List<CommandPaletteItem> Sample() => new()
    {
        new() { Id = "nav.home", Title = "Home", Group = "Navigate" },
        new() { Id = "nav.games", Title = "Games", Group = "Navigate" },
        new() { Id = "cmd.pause", Title = "Pause monitoring", Group = "Actions" },
        new() { Id = "game.tarkov", Title = "Escape from Tarkov", Subtitle = "EscapeFromTarkov.exe", Group = "Games", SearchText = "eft" },
        new() { Id = "cmd.emergency", Title = "Reset display", Group = "Actions" },
    };

    [Fact]
    public void Filter_empty_query_returns_all_in_order()
    {
        var items = Sample();
        var result = CommandPaletteCatalog.Filter("", items);
        Assert.Equal(items.Count, result.Count);
        Assert.Equal("nav.home", result[0].Id);
    }

    [Theory]
    [InlineData("home", "nav.home")]
    [InlineData("pause", "cmd.pause")]
    [InlineData("eft", "game.tarkov")]
    [InlineData("reset", "cmd.emergency")]
    public void Filter_ranks_expected_hit_first(string query, string expectedId)
    {
        var result = CommandPaletteCatalog.Filter(query, Sample());
        Assert.NotEmpty(result);
        Assert.Equal(expectedId, result[0].Id);
    }

    [Fact]
    public void Filter_no_match_returns_empty()
    {
        Assert.Empty(CommandPaletteCatalog.Filter("zzzz-not-a-command", Sample()));
    }

    [Fact]
    public void Filter_does_not_match_internal_ids()
    {
        Assert.Empty(CommandPaletteCatalog.Filter("nav.", Sample()));
        Assert.Empty(CommandPaletteCatalog.Filter("cmd.", Sample()));
        Assert.Empty(CommandPaletteCatalog.Filter("emergency", Sample())); // only in the id
        Assert.Single(CommandPaletteCatalog.Filter("tarkov", Sample()));   // via the title
    }

    [Fact]
    public void Filter_token_match_requires_all_words()
    {
        Assert.Equal("game.tarkov", Assert.Single(CommandPaletteCatalog.Filter("escape exe", Sample())).Id);
        Assert.Empty(CommandPaletteCatalog.Filter("escape zzz", Sample()));
    }

    [Fact]
    public void ApplyEnabled_marks_items_via_seam_and_keeps_order()
    {
        var items = CommandPaletteCatalog.ApplyEnabled(Sample(), id => id != "cmd.pause");
        Assert.Equal(Sample().Count, items.Count);
        Assert.False(Assert.Single(items, i => i.Id == "cmd.pause").IsEnabled);
        Assert.Equal(items.Count - 1, items.Count(i => i.IsEnabled));
        Assert.Equal("nav.home", items[0].Id);
        Assert.All(CommandPaletteCatalog.ApplyEnabled(Sample(), null), i => Assert.True(i.IsEnabled));
    }

    [Fact]
    public void ApplyEnabled_survives_throwing_seam()
    {
        var items = CommandPaletteCatalog.ApplyEnabled(Sample(), _ => throw new InvalidOperationException());
        Assert.All(items, i => Assert.True(i.IsEnabled));
    }

    [Fact]
    public void WithEnabled_copies_all_fields()
    {
        var src = Sample()[3];
        var off = src.WithEnabled(false);
        Assert.NotSame(src, off);
        Assert.False(off.IsEnabled);
        Assert.Equal(src.Id, off.Id);
        Assert.Equal(src.Title, off.Title);
        Assert.Equal(src.Subtitle, off.Subtitle);
        Assert.Equal(src.Group, off.Group);
        Assert.Equal(src.SearchText, off.SearchText);
        Assert.Same(src, src.WithEnabled(true));
    }
}
