using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class ProcessNamesTests
{
    [Theory]
    [InlineData("Game.EXE", "game")]
    [InlineData("game", "game")]
    [InlineData(@"C:\Games\Dir\Game.exe", "game")]
    [InlineData("  Game.exe  ", "game")]
    [InlineData("VALORANT-Win64-Shipping.exe", "valorant-win64-shipping")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Key_normalizes(string? input, string expected)
    {
        Assert.Equal(expected, ProcessNames.Key(input));
    }

    [Theory]
    [InlineData("Game", "Game.exe")]
    [InlineData("Game.exe", "Game.exe")]
    [InlineData("Game.EXE", "Game.EXE")]
    [InlineData("  Game ", "Game.exe")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ToExeName_keeps_casing_and_adds_suffix(string? input, string expected)
    {
        Assert.Equal(expected, ProcessNames.ToExeName(input));
    }

    [Theory]
    [InlineData("Game.exe", "Game")]
    [InlineData(@"C:\x\Game.EXE", "Game")]
    [InlineData("Game", "Game")]
    [InlineData(null, "")]
    public void Bare_strips_directory_and_extension(string? input, string expected)
    {
        Assert.Equal(expected, ProcessNames.Bare(input));
    }

    [Fact]
    public void Same_compares_keys_and_never_matches_empty()
    {
        Assert.True(ProcessNames.Same("Game.exe", @"D:\g\GAME"));
        Assert.True(ProcessNames.Same("game", "game.EXE "));
        Assert.False(ProcessNames.Same("a.exe", "b.exe"));
        Assert.False(ProcessNames.Same("", ""));
        Assert.False(ProcessNames.Same(null, null));
    }

    [Theory]
    [InlineData("game.exe", true)]
    [InlineData("game", true)]
    [InlineData("game.v2", true)]
    [InlineData("Shipping-Win64.exe", true)]
    [InlineData(@"C:\x\game.exe", false)]
    [InlineData("dir/game.exe", false)]
    [InlineData("game.exe:ads", false)]
    [InlineData("..", false)]
    [InlineData(".hidden", false)]
    [InlineData("game.bat", false)]
    [InlineData("game.dll", false)]
    [InlineData("game.lnk", false)]
    [InlineData("ga<me.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPlainExeName(string? input, bool expected)
    {
        Assert.Equal(expected, ProcessNames.IsPlainExeName(input));
    }

    [Theory]
    [InlineData("explorer.exe", true)]
    [InlineData("EXPLORER", true)]
    [InlineData(@"C:\Windows\svchost.exe", true)]
    [InlineData("powershell.exe", true)]
    [InlineData("cmd", true)]
    [InlineData("game.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsProtectedSystemProcess(string? input, bool expected)
    {
        Assert.Equal(expected, ProcessNames.IsProtectedSystemProcess(input));
    }
}
