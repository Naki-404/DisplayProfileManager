using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class RecommendationEngineTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    private static RecommendationContext Empty => new() { NowUtc = Now };

    [Fact]
    public void Evaluate_empty_context_yields_empty_library_only()
    {
        var list = RecommendationEngine.Evaluate(Empty);
        Assert.Contains(list, r => r.Id == RecommendationEngine.IdEmptyLibrary);
        Assert.DoesNotContain(list, r => r.Id == RecommendationEngine.IdPaused);
        Assert.DoesNotContain(list, r => r.Id == RecommendationEngine.IdGameModeOff);
    }

    [Fact]
    public void Evaluate_paused_emits_resume_action()
    {
        var list = RecommendationEngine.Evaluate(new RecommendationContext
        {
            MonitoringPaused = true,
            ProfileCount = 1,
            NowUtc = Now
        });
        var r = Assert.Single(list, x => x.Id == RecommendationEngine.IdPaused);
        Assert.Equal(RecommendationAction.ResumeMonitoring, r.Action);
        Assert.Equal(RecommendationSeverity.Warning, r.Severity);
        Assert.Equal("rec.severity.warning", r.SeverityKey);
    }

    [Fact]
    public void Evaluate_gameMode_off_is_safeAction()
    {
        var list = RecommendationEngine.Evaluate(new RecommendationContext
        {
            ProfileCount = 1,
            GameModeEnabled = false,
            NowUtc = Now
        });
        var r = Assert.Single(list, x => x.Id == RecommendationEngine.IdGameModeOff);
        Assert.Equal(RecommendationAction.SafeActionEnableGameMode, r.Action);
        Assert.Equal(RecommendationSeverity.Tip, r.Severity);
    }

    [Fact]
    public void Evaluate_gameMode_on_or_unknown_skips()
    {
        Assert.DoesNotContain(
            RecommendationEngine.Evaluate(new RecommendationContext { ProfileCount = 1, GameModeEnabled = true }),
            r => r.Id == RecommendationEngine.IdGameModeOff);
        Assert.DoesNotContain(
            RecommendationEngine.Evaluate(new RecommendationContext { ProfileCount = 1, GameModeEnabled = null }),
            r => r.Id == RecommendationEngine.IdGameModeOff);
    }

    [Theory]
    [InlineData("balanced")]
    [InlineData("powerSaver")]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e")]
    [InlineData(PowerPlanSafeAction.HighPerformanceAlias)]
    public void Evaluate_never_recommends_high_performance_plan(string plan)
    {
        // Visor switches the plan per game and restores it afterwards; a permanent "use High performance"
        // tip contradicts that (heat / battery) and fought the session snapshot.
        var list = RecommendationEngine.Evaluate(new RecommendationContext
        {
            ProfileCount = 1,
            PowerPlanAliasOrGuid = plan,
            NowUtc = Now
        });
        Assert.DoesNotContain(list, r => r.Id == RecommendationEngine.IdPowerNotHigh);
        Assert.DoesNotContain(list, r => r.Action == RecommendationAction.SafeActionHighPerfPower);
    }

    [Fact]
    public void IsPerformanceClassPlan_keeps_label_helper()
    {
        Assert.True(RecommendationEngine.IsPerformanceClassPlan("ultimate"));
        Assert.True(RecommendationEngine.IsPerformanceClassPlan(PowerPlanSafeAction.HighPerformanceAlias));
        Assert.False(RecommendationEngine.IsPerformanceClassPlan("balanced"));
        Assert.False(RecommendationEngine.IsPerformanceClassPlan(null));
    }

    [Fact]
    public void Evaluate_conflicts_and_crash_are_left_to_the_health_block()
    {
        var list = RecommendationEngine.Evaluate(new RecommendationContext
        {
            ProfileCount = 1,
            NightLightOn = true,
            ConflictApps = true,
            ConflictAppNames = new[] { "f.lux" },
            CrashRestored = true,
            NowUtc = Now
        });
        Assert.DoesNotContain(list, r => r.Id == RecommendationEngine.IdConflicts);
        Assert.DoesNotContain(list, r => r.Id == RecommendationEngine.IdCrashRestored);
        Assert.Empty(list);
    }

    [Fact]
    public void Evaluate_no_gateway_when_adapter_present()
    {
        var list = RecommendationEngine.Evaluate(new RecommendationContext
        {
            ProfileCount = 1,
            HasUsableNetworkAdapter = true,
            HasUsableGateway = false,
            NowUtc = Now
        });
        var r = Assert.Single(list, x => x.Id == RecommendationEngine.IdNoGateway);
        Assert.Equal(RecommendationSeverity.Info, r.Severity);
    }

    [Fact]
    public void Evaluate_no_gateway_skipped_without_adapter()
    {
        var list = RecommendationEngine.Evaluate(new RecommendationContext
        {
            ProfileCount = 1,
            HasUsableNetworkAdapter = false,
            HasUsableGateway = false,
            NowUtc = Now
        });
        Assert.DoesNotContain(list, r => r.Id == RecommendationEngine.IdNoGateway);
    }

    [Fact]
    public void Evaluate_orders_by_severity_warning_first()
    {
        var list = RecommendationEngine.Evaluate(new RecommendationContext
        {
            MonitoringPaused = true,
            ProfileCount = 0,
            HasUsableNetworkAdapter = true,
            HasUsableGateway = false,
            NowUtc = Now
        });
        Assert.Equal(RecommendationEngine.IdPaused, list[0].Id);
        Assert.Equal(RecommendationEngine.IdNoGateway, list[^1].Id);
    }

    [Fact]
    public void Dismissal_hides_only_that_occurrence_until_expiry()
    {
        var store = new List<string>();
        var ctx = new RecommendationContext { MonitoringPaused = true, ProfileCount = 1, NowUtc = Now };
        var paused = Assert.Single(RecommendationEngine.Evaluate(ctx));

        RecommendationDismissals.Add(store, paused, Now);
        Assert.Single(store);
        Assert.StartsWith(paused.Id + "|" + paused.ContextHash + "|", store[0]);

        var dismissed = new HashSet<string>(store, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(RecommendationEngine.Evaluate(new RecommendationContext
        {
            MonitoringPaused = true, ProfileCount = 1, NowUtc = Now.AddDays(13), DismissedIds = dismissed
        }));
        Assert.Single(RecommendationEngine.Evaluate(new RecommendationContext
        {
            MonitoringPaused = true, ProfileCount = 1, NowUtc = Now.AddDays(14), DismissedIds = dismissed
        }));
    }

    [Fact]
    public void Dismissal_context_hash_distinguishes_occurrences()
    {
        var a = new Recommendation { Id = "x", TitleKey = "t", BodyKey = "b", BodyArgs = new[] { "f.lux" } };
        var b = new Recommendation { Id = "x", TitleKey = "t", BodyKey = "b", BodyArgs = new[] { "Night Light" } };
        Assert.NotEqual(a.ContextHash, b.ContextHash);
        Assert.Equal(a.ContextHash, new Recommendation { Id = "x", TitleKey = "t", BodyKey = "b", BodyArgs = new[] { "f.lux" } }.ContextHash);

        var store = new List<string>();
        RecommendationDismissals.Add(store, a, Now);
        Assert.True(RecommendationDismissals.IsDismissed(store, "x", a.ContextHash, Now));
        Assert.False(RecommendationDismissals.IsDismissed(store, "x", b.ContextHash, Now));
    }

    [Fact]
    public void Legacy_plain_ids_no_longer_dismiss_and_are_pruned()
    {
        var store = new List<string> { RecommendationEngine.IdPaused, RecommendationEngine.IdEmptyLibrary };
        var list = RecommendationEngine.Evaluate(new RecommendationContext
        {
            MonitoringPaused = true,
            ProfileCount = 0,
            NowUtc = Now,
            DismissedIds = new HashSet<string>(store, StringComparer.OrdinalIgnoreCase)
        });
        Assert.Contains(list, r => r.Id == RecommendationEngine.IdPaused);
        Assert.Contains(list, r => r.Id == RecommendationEngine.IdEmptyLibrary);

        Assert.True(RecommendationDismissals.Prune(store, Now));
        Assert.Empty(store);
        Assert.False(RecommendationDismissals.Prune(store, Now));
    }

    [Fact]
    public void ResetDismissed_clears_store()
    {
        var store = new List<string>();
        var rec = new Recommendation { Id = "x", TitleKey = "t", BodyKey = "b" };
        RecommendationDismissals.Add(store, rec, Now);
        Assert.Single(store);
        RecommendationEngine.ResetDismissed(store);
        Assert.Empty(store);
    }

    [Fact]
    public void Dismissal_token_round_trips()
    {
        var d = new RecommendationDismissal("visor.paused", "deadbeef", Now);
        Assert.Equal("visor.paused|deadbeef|2026-10-13T12:00:00Z", d.Serialize());
        Assert.True(RecommendationDismissal.TryParse(d.Serialize(), out var back));
        Assert.Equal(d, back);
        Assert.False(RecommendationDismissal.TryParse("visor.paused", out _));
        Assert.False(RecommendationDismissal.TryParse("a|b|not-a-date", out _));
        Assert.True(d.IsExpired(Now.AddDays(14)));
        Assert.False(d.IsExpired(Now.AddDays(13.9)));
    }

    [Fact]
    public void SafeAction_kinds_are_only_windows_mutations()
    {
        var list = RecommendationEngine.Evaluate(new RecommendationContext
        {
            MonitoringPaused = true,
            ProfileCount = 0,
            GameModeEnabled = false,
            PowerPlanAliasOrGuid = "balanced",
            HasUsableNetworkAdapter = true,
            HasUsableGateway = false,
            NightLightOn = true,
            CrashRestored = true,
            NowUtc = Now
        });
        foreach (var r in list)
        {
            if (r.Action is RecommendationAction.SafeActionEnableGameMode
                or RecommendationAction.SafeActionHighPerfPower)
            {
                Assert.Equal(RecommendationEngine.IdGameModeOff, r.Id);
            }
        }
        Assert.Contains(list, r => r.Action == RecommendationAction.SafeActionEnableGameMode);
        Assert.DoesNotContain(list, r => r.Action == RecommendationAction.SafeActionHighPerfPower);
    }
}
