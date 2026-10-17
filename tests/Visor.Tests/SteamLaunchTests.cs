using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class SteamLaunchTests
{
    [Theory]
    [InlineData(@"C:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\csgo.exe",
        @"C:\SteamLibrary\steamapps", "Counter-Strike Global Offensive")]
    [InlineData(@"D:\Steam\steamapps\common\Game\Binaries\Win64\Game.exe",
        @"D:\Steam\steamapps", "Game")]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Foo\foo.exe",
        @"C:\Program Files (x86)\Steam\steamapps", "Foo")]
    public void TryGetSteamCommonParts_parses_library_layout(string exe, string apps, string install)
    {
        Assert.True(SteamLaunch.TryGetSteamCommonParts(exe, out var steamApps, out var dir));
        Assert.Equal(apps, steamApps);
        Assert.Equal(install, dir);
    }

    [Theory]
    [InlineData(@"C:\Games\NotSteam\game.exe")]
    [InlineData(@"C:\SteamLibrary\steamapps\downloading\123\x.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void TryGetSteamCommonParts_rejects_non_common(string? exe)
    {
        Assert.False(SteamLaunch.TryGetSteamCommonParts(exe, out _, out _));
    }

    [Fact]
    public void IsUnderSteamCommon_matches()
    {
        Assert.True(SteamLaunch.IsUnderSteamCommon(
            @"E:\SteamLibrary\steamapps\common\Bar\bar.exe"));
        Assert.False(SteamLaunch.IsUnderSteamCommon(@"E:\Games\bar.exe"));
    }
}
