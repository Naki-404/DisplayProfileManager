using Visor.Models;
using Visor.Services.Optimize;
using Xunit;

namespace Visor.Tests;

public class OptimizeInfoTests
{
    private static readonly RegAddress Hags = new(OptimizeStatus.HagsKey, OptimizeStatus.HagsValue);

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(0, null)]
    [InlineData(7, null)]
    public void Hags_reads_HKLM_HwSchMode_read_only(int mode, bool? expected)
    {
        var reg = new FakeOptimizeRegistry();
        reg.MachineValues[OptimizeStatus.HagsKey + "\\" + OptimizeStatus.HagsValue] = RegValue.Dword(mode);
        Assert.Equal(expected, OptimizeStatus.ReadHags(reg));
        var item = OptimizeStatus.HagsItem(reg);
        Assert.Equal(OptimizeInfoKind.RequiresAdmin, item.Kind);
        Assert.Equal(expected, item.Status);
        Assert.Equal("optimize.hags.title", item.TitleKey);
        Assert.Equal("optimize.hags.why", item.WhyKey);
        Assert.Equal("optimize.hags.risk", item.RiskKey);
        Assert.Equal("optimize.hags.status." + (expected switch { true => "on", false => "off", null => "unknown" }), item.StatusKey);
        if (expected != null) Assert.Contains("HwSchMode=" + mode, item.Evidence);
        Assert.Equal(0, reg.Writes);
    }

    [Fact]
    public void Hags_absent_or_unreadable_is_unknown()
    {
        var reg = new FakeOptimizeRegistry();
        Assert.Null(OptimizeStatus.ReadHags(reg));
        reg.FailReads = true;
        Assert.Null(OptimizeStatus.ReadHags(reg));
        Assert.Empty(OptimizeStatus.HagsItem(reg).Evidence);
    }

    [Fact]
    public void PowerPlan_managed_by_profile_counts_enabled_profiles_with_a_plan()
    {
        var profiles = new List<GameProfile>
        {
            new() { Name = "A", Enabled = true, PowerPlan = "highPerformance", ApplyPowerPlan = true },
            new() { Name = "B", Enabled = true, PowerPlan = "ultimate", ApplyPowerPlan = true },
            new() { Name = "C", Enabled = true, PowerPlan = "highPerformance", ApplyPowerPlan = false },
            new() { Name = "D", Enabled = false, PowerPlan = "highPerformance", ApplyPowerPlan = true },
            new() { Name = "E", Enabled = true, PowerPlan = null, ApplyPowerPlan = true }
        };
        var s = OptimizeStatus.PowerPlanManaged(profiles);
        Assert.True(s.Managed);
        Assert.Equal(4, s.EnabledProfiles);
        Assert.Equal(2, s.ProfilesWithPlan);
        Assert.Equal(new[] { "highPerformance", "ultimate" }, s.Plans);

        var item = OptimizeStatus.PowerPerGameItem(profiles);
        Assert.Equal(OptimizeInfoKind.ManagedByProfile, item.Kind);
        Assert.True(item.Status);
        Assert.Contains("profiles=2/4", item.Evidence);
        Assert.Contains("plan=ultimate", item.Evidence);
        Assert.Equal("optimize.powerPerGame.status.on", item.StatusKey);
    }

    [Fact]
    public void PowerPlan_not_managed_without_profiles()
    {
        Assert.False(OptimizeStatus.PowerPlanManaged(null).Managed);
        Assert.False(OptimizeStatus.PowerPlanManaged(Array.Empty<GameProfile>()).Managed);
        Assert.False(OptimizeStatus.PowerPerGameItem(null).Status);
    }

    [Fact]
    public void VisualEffects_is_manual_only_with_unknown_status()
    {
        var item = OptimizeStatus.VisualEffectsItem();
        Assert.Equal(OptimizeInfoKind.ManualOnly, item.Kind);
        Assert.Null(item.Status);
        Assert.Equal("optimize.visualEffects.status.unknown", item.StatusKey);
    }

    [Fact]
    public void InfoItems_lists_three_cards_in_order()
    {
        var items = OptimizeStatus.InfoItems(null, new FakeOptimizeRegistry());
        Assert.Equal(new[] { OptimizeStatus.PowerPerGameId, OptimizeStatus.HagsId, OptimizeStatus.VisualEffectsId },
            items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void FsoStatuses_covers_enabled_profiles_with_exe_and_collapses_duplicates()
    {
        const string a = @"C:\G\a.exe", b = @"C:\G\b.exe";
        var reg = new FakeOptimizeRegistry()
            .Str(new RegAddress(FullscreenOptimizationSafeActionTarget.LayersKey, b), "~ DISABLEDXMAXIMIZEDWINDOWEDMODE");
        var profiles = new List<GameProfile>
        {
            new() { Name = "A", Enabled = true, ExePath = a },
            new() { Name = "A2", Enabled = true, ExePath = a.ToUpperInvariant() },
            new() { Name = "B", Enabled = true, ExePath = " " + b + " " },
            new() { Name = "Off", Enabled = false, ExePath = @"C:\G\off.exe" },
            new() { Name = "NoExe", Enabled = true, ExePath = null }
        };
        var statuses = OptimizeStatus.FsoStatuses(profiles, reg);
        Assert.Equal(2, statuses.Count);
        Assert.Equal(new GameFsoStatus("A", a, false), statuses[0]);
        Assert.Equal(new GameFsoStatus("B", b, true), statuses[1]);

        reg.FailReads = true;
        Assert.All(OptimizeStatus.FsoStatuses(profiles, reg), s => Assert.Null(s.FsoDisabled));
        Assert.Empty(OptimizeStatus.FsoStatuses(null, reg));
    }
}
