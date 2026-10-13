using Visor.Models;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class ThemePaletteTests
{
    [Fact]
    public void DarkPalette_includes_success_and_warning()
    {
        var p = ThemeService.DarkPalette();
        Assert.False(string.IsNullOrWhiteSpace(p.Success));
        Assert.False(string.IsNullOrWhiteSpace(p.Warning));
        Assert.StartsWith("#", p.Success);
        Assert.StartsWith("#", p.Warning);
    }

    [Fact]
    public void ThemePalette_Clone_copies_success_and_warning()
    {
        var original = ThemeService.DarkPalette();
        original.Success = "#11AA22";
        original.Warning = "#BBAA00";

        var clone = original.Clone();

        Assert.Equal("#11AA22", clone.Success);
        Assert.Equal("#BBAA00", clone.Warning);
        Assert.NotSame(original, clone);
    }

    [Fact]
    public void SeedCustom_fills_success_and_warning_for_dark_bg()
    {
        var p = ThemeService.SeedCustom("#22D3EE", "#0A0C10");
        Assert.Equal("#22C55E", p.Success);
        Assert.Equal("#F59E0B", p.Warning);
    }

    [Fact]
    public void SeedCustom_uses_darker_status_accents_on_light_bg()
    {
        var p = ThemeService.SeedCustom("#0E7490", "#F3F6FA");
        Assert.Equal("#15803D", p.Success);
        Assert.Equal("#B45309", p.Warning);
    }

    [Fact]
    public void SeedCustom_uses_the_deeper_danger_red_on_light_bg()
    {
        Assert.Equal("#E05252", ThemeService.SeedCustom("#22D3EE", "#0A0C10").Danger);
        Assert.Equal(ThemeService.LightPalette().Danger, ThemeService.SeedCustom("#0E7490", "#F3F6FA").Danger);
    }

    [Fact]
    public void LightPalette_uses_darker_status_accents_for_contrast()
    {
        var p = ThemeService.LightPalette();
        // Light theme uses deeper greens/ambers so status text stays readable on white panels.
        Assert.Equal("#15803D", p.Success);
        Assert.Equal("#B45309", p.Warning);
    }

    [Fact]
    public void ThemePalette_defaults_match_noir_brand()
    {
        var p = new ThemePalette();
        Assert.Equal("#0A0C10", p.Bg);
        Assert.Equal("#22D3EE", p.Accent);
        Assert.Equal("#22C55E", p.Success);
        Assert.Equal("#F59E0B", p.Warning);
    }

    [Fact]
    public void BestContrastingLabel_prefers_dark_ink_on_bright_success_green()
    {
        var label = ThemeService.BestContrastingLabel(ThemeService.Hex("#22C55E"));
        Assert.Equal("#052E16", ThemeService.ToHex(label));
    }

    [Fact]
    public void BestContrastingLabel_prefers_white_on_deep_success_green()
    {
        var label = ThemeService.BestContrastingLabel(ThemeService.Hex("#15803D"));
        Assert.Equal("#FFFFFF", ThemeService.ToHex(label));
    }
}
