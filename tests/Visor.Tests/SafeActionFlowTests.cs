using Visor.Services;
using Xunit;

namespace Visor.Tests;

/// <summary>Dictionary-backed registry: tests never touch HKCU.</summary>
internal sealed class FakeGameModeRegistry : IGameModeRegistry
{
    public readonly Dictionary<string, int> Values = new(StringComparer.OrdinalIgnoreCase);
    public bool FailWrites;
    public bool FailReads;
    /// <summary>Simulates a write that "succeeds" but does not stick (policy / virtualization).</summary>
    public bool IgnoreWrites;
    public int Writes;

    public FakeGameModeRegistry(int? auto = null, int? allow = null)
    {
        if (auto is int a) Values[GameModeSetting.AutoValue] = a;
        if (allow is int l) Values[GameModeSetting.AllowValue] = l;
    }

    public int? GetDword(string valueName)
    {
        if (FailReads) throw new UnauthorizedAccessException("read");
        return Values.TryGetValue(valueName, out var v) ? v : null;
    }

    public void SetDword(string valueName, int value)
    {
        Writes++;
        if (FailWrites) throw new UnauthorizedAccessException("write");
        if (IgnoreWrites) return;
        Values[valueName] = value;
    }

    public void DeleteValue(string valueName)
    {
        Writes++;
        if (FailWrites) throw new UnauthorizedAccessException("delete");
        if (IgnoreWrites) return;
        Values.Remove(valueName);
    }
}

public class SafeActionFlowTests
{
    [Fact]
    public void Begin_MarkCompleted_sets_RollbackAvailable_when_previous_present()
    {
        var s = SafeActionFlow.Begin("gameMode", "false", "true");
        Assert.Equal(SafeActionState.Pending, s.State);
        SafeActionFlow.MarkRunning(s);
        SafeActionFlow.MarkCompleted(s, "true");
        Assert.Equal(SafeActionState.RollbackAvailable, s.State);
        Assert.True(s.CanRollback);
    }

    [Fact]
    public void MarkCompleted_keeps_rollback_on_verify_mismatch_when_backup_exists()
    {
        var s = SafeActionFlow.Begin("gameMode", "false", "true");
        SafeActionFlow.MarkRunning(s);
        SafeActionFlow.MarkCompleted(s, "false");
        Assert.Equal(SafeActionState.RollbackAvailable, s.State);
        Assert.True(s.CanRollback);
        Assert.Equal(SafeActionCodes.VerifyMismatch, s.Error);
    }

    [Fact]
    public void MarkApplyFailed_is_Failed_when_value_did_not_move()
    {
        var s = SafeActionFlow.Begin("x", "a", "b");
        SafeActionFlow.MarkApplyFailed(s, SafeActionCodes.ApplyFailed, previousRaw: "a", currentRaw: "a");
        Assert.Equal(SafeActionState.Failed, s.State);
        Assert.False(s.CanRollback);
        Assert.Equal(SafeActionCodes.ApplyFailed, s.Error);
    }

    [Fact]
    public void MarkApplyFailed_keeps_rollback_when_value_moved_elsewhere()
    {
        var s = SafeActionFlow.Begin("x", "a", "b");
        SafeActionFlow.MarkApplyFailed(s, SafeActionCodes.VerifyMismatch, previousRaw: "a", currentRaw: "c");
        Assert.Equal(SafeActionState.RollbackAvailable, s.State);
        Assert.True(s.CanRollback);
    }

    [Fact]
    public void MarkRollback_restores_Completed_when_verified()
    {
        var s = SafeActionFlow.Begin("powerHighPerf", "guid-a", "highPerformance");
        SafeActionFlow.MarkCompleted(s, "highPerformance");
        Assert.Equal(SafeActionState.RollbackAvailable, s.State);
        SafeActionFlow.MarkRollback(s, "guid-a");
        Assert.Equal(SafeActionState.Completed, s.State);
    }

    [Fact]
    public void MarkFailed_records_code()
    {
        var s = SafeActionFlow.Begin("x", "a", "b");
        SafeActionFlow.MarkFailed(s, SafeActionCodes.Unavailable);
        Assert.Equal(SafeActionState.Failed, s.State);
        Assert.Equal(SafeActionCodes.Unavailable, s.Error);
        Assert.Equal("safeAction.code.unavailable", SafeActionCodes.LocKey(s.Error));
        Assert.Equal("safeAction.code.unknown", SafeActionCodes.LocKey(null));
    }

    [Fact]
    public void SafeActionState_has_no_Idle_or_Cancelled()
    {
        var names = Enum.GetNames<SafeActionState>();
        Assert.DoesNotContain("Idle", names);
        Assert.DoesNotContain("Cancelled", names);
        Assert.Equal(SafeActionState.Pending, new SafeActionSession { ActionId = "x" }.State);
    }
}

public class GameModeRawStateTests
{
    [Theory]
    [InlineData(null, null, true)]
    [InlineData(1, null, true)]
    [InlineData(0, null, false)]
    [InlineData(null, 0, false)]
    [InlineData(0, 1, false)] // AutoGameModeEnabled wins
    [InlineData(1, 0, true)]
    public void Enabled_follows_windows_semantics(int? auto, int? allow, bool expected)
    {
        Assert.Equal(expected, new GameModeRawState(auto, allow).Enabled);
    }

    [Fact]
    public void Serialize_round_trips_absent_values()
    {
        var s = new GameModeRawState(null, 0);
        Assert.Equal("auto:-|allow:0", s.Serialize());
        Assert.True(GameModeRawState.TryParse(s.Serialize(), out var back));
        Assert.Equal(s, back);
        Assert.False(GameModeRawState.TryParse("true", out _));
        Assert.False(GameModeRawState.TryParse("auto:1", out _));
    }
}

public class GameModeSettingTests
{
    [Fact]
    public void Read_absent_values_means_enabled_and_failure_means_null()
    {
        Assert.True(GameModeSetting.Read(new FakeGameModeRegistry()));
        Assert.False(GameModeSetting.Read(new FakeGameModeRegistry(auto: 0)));
        Assert.Null(GameModeSetting.Read(new FakeGameModeRegistry { FailReads = true }));
    }

    [Fact]
    public void Apply_same_value_completes_without_rollback_and_without_writes()
    {
        var reg = new FakeGameModeRegistry(auto: 1, allow: 1);
        var session = GameModeSetting.Apply(true, reg);
        Assert.Equal(SafeActionState.Completed, session.State);
        Assert.False(session.CanRollback);
        Assert.Equal(0, reg.Writes);
    }

    [Fact]
    public void Apply_writes_both_dwords_and_keeps_exact_previous_state()
    {
        var reg = new FakeGameModeRegistry(); // both absent = on
        var session = GameModeSetting.Apply(false, reg);
        Assert.Equal(SafeActionState.RollbackAvailable, session.State);
        Assert.Equal("auto:-|allow:-", session.PreviousValue);
        Assert.Equal(0, reg.Values[GameModeSetting.AutoValue]);
        Assert.Equal(0, reg.Values[GameModeSetting.AllowValue]);
    }

    [Fact]
    public void Apply_failed_write_with_unchanged_value_is_Failed_not_success()
    {
        var reg = new FakeGameModeRegistry(auto: 1, allow: 1) { FailWrites = true };
        var session = GameModeSetting.Apply(false, reg);
        Assert.Equal(SafeActionState.Failed, session.State);
        Assert.Equal(SafeActionCodes.ApplyFailed, session.Error);
        Assert.False(session.CanRollback);
    }

    [Fact]
    public void Apply_ignored_write_is_verify_mismatch_not_success()
    {
        var reg = new FakeGameModeRegistry(auto: 1, allow: 1) { IgnoreWrites = true };
        var session = GameModeSetting.Apply(false, reg);
        Assert.Equal(SafeActionState.Failed, session.State);
        Assert.Equal(SafeActionCodes.VerifyMismatch, session.Error);
        Assert.False(session.CanRollback);
    }

    [Fact]
    public void Rollback_deletes_values_that_were_absent()
    {
        var reg = new FakeGameModeRegistry(auto: null, allow: 1);
        var session = GameModeSetting.Apply(false, reg);
        Assert.True(session.CanRollback);
        session = GameModeSetting.Rollback(session, reg);
        Assert.Equal(SafeActionState.Completed, session.State);
        Assert.False(reg.Values.ContainsKey(GameModeSetting.AutoValue));
        Assert.Equal(1, reg.Values[GameModeSetting.AllowValue]);
    }

    [Fact]
    public void Rollback_reports_conflict_when_value_changed_elsewhere()
    {
        var reg = new FakeGameModeRegistry(auto: 1, allow: 1);
        var session = GameModeSetting.Apply(false, reg);
        // Someone flipped it back on from Settings, and also set a different raw layout.
        reg.Values[GameModeSetting.AutoValue] = 1;
        reg.Values.Remove(GameModeSetting.AllowValue);
        var r = GameModeSetting.Rollback(session, reg);
        Assert.Equal(SafeActionState.RollbackFailed, r.State);
        Assert.Equal(SafeActionCodes.Conflict, r.Error);
        Assert.True(r.CanRollback);
        r = GameModeSetting.Rollback(r, reg, confirmedConflict: true);
        Assert.Equal(SafeActionState.Completed, r.State);
        Assert.Equal(1, reg.Values[GameModeSetting.AllowValue]);
    }

    [Fact]
    public void Rollback_noop_session_without_backup_fails()
    {
        var s = SafeActionFlow.Begin(GameModeSetting.ActionId, null, "true");
        s.State = SafeActionState.Completed;
        var result = GameModeSetting.Rollback(s, new FakeGameModeRegistry());
        Assert.Equal(SafeActionState.Failed, result.State);
        Assert.Equal(SafeActionCodes.CannotRollback, result.Error);
    }

    [Fact]
    public void Rollback_accepts_legacy_bool_backup()
    {
        var reg = new FakeGameModeRegistry(auto: 0, allow: 0);
        var s = SafeActionFlow.Begin(GameModeSetting.ActionId, "true", "false");
        s.State = SafeActionState.RollbackAvailable;
        var r = GameModeSetting.Rollback(s, reg);
        Assert.Equal(SafeActionState.Completed, r.State);
        Assert.Equal(1, reg.Values[GameModeSetting.AutoValue]);
    }
}

public class SystemInfoServiceTests
{
    [Fact]
    public void Capture_returns_core_fields()
    {
        var snap = SystemInfoService.Capture();
        Assert.True(snap.LogicalProcessors >= 1);
        Assert.True(snap.RamTotalBytes > 0);
        Assert.True(snap.RamAvailableBytes >= 0);
        Assert.True(snap.RamAvailableBytes <= snap.RamTotalBytes);
        Assert.False(string.IsNullOrWhiteSpace(snap.GpuLabel));
        Assert.False(string.IsNullOrWhiteSpace(snap.WindowsVersion));
    }

    [Fact]
    public void FormatBytes_scales()
    {
        Assert.Equal("512 B", SystemInfoService.FormatBytes(512));
        Assert.Equal("1 KB", SystemInfoService.FormatBytes(1024));
        Assert.Equal("1.5 GB", SystemInfoService.FormatBytes((long)(1.5 * 1024 * 1024 * 1024)));
    }

    [Theory]
    [InlineData(10, 0, 22631, "Windows 11 (build 22631)")]
    [InlineData(10, 0, 22000, "Windows 11 (build 22000)")]
    [InlineData(10, 0, 19045, "Windows 10 (build 19045)")]
    [InlineData(6, 3, 9600, "Windows 6.3 (build 9600)")]
    public void FormatWindowsLabel_maps_build_numbers(int major, int minor, int build, string expected)
    {
        Assert.Equal(expected, SystemInfoService.FormatWindowsLabel(new Version(major, minor, build)));
    }

    [Fact]
    public void TryEndProcess_feature_is_gone()
    {
        Assert.Null(typeof(SystemInfoService).GetMethod("TryEndProcess"));
    }

    [Fact]
    public void ComputeTopProcesses_cpu_percent_from_two_samples()
    {
        // 4 cores, 200 ms window (2_000_000 ticks of 100 ns): a process burning 1 core = 25 %.
        long window = TimeSpan.FromMilliseconds(200).Ticks;
        var first = new[]
        {
            new ProcessCpuSample(0, "Idle", 0, 0, 0),
            new ProcessCpuSample(100, "game", 11, 1_000_000, 500),
            new ProcessCpuSample(200, "idleapp", 22, 50_000, 900),
            new ProcessCpuSample(300, "old", 33, 10, 100),
        };
        var second = new[]
        {
            new ProcessCpuSample(0, "Idle", 0, 4 * window, 0),
            new ProcessCpuSample(100, "game", 11, 1_000_000 + window, 600),
            new ProcessCpuSample(200, "idleapp", 22, 50_000, 900),
            new ProcessCpuSample(300, "reused", 44, 999_999_999, 100), // same PID, new CreateTime → no delta
            new ProcessCpuSample(400, "", 55, 0, 42),
        };
        var rows = SystemInfoService.ComputeTopProcesses(first, second, window, cores: 4, take: 10);

        Assert.DoesNotContain(rows, r => r.Pid == 0);
        var game = Assert.Single(rows, r => r.Pid == 100);
        Assert.Equal(25.0, game.CpuPercent, 3);
        Assert.Equal(600, game.WorkingSetBytes);
        Assert.Equal(0, Assert.Single(rows, r => r.Pid == 200).CpuPercent);
        Assert.Equal(0, Assert.Single(rows, r => r.Pid == 300).CpuPercent);
        Assert.Equal("#400", Assert.Single(rows, r => r.Pid == 400).Name);
        Assert.Equal(100, rows[0].Pid); // sorted by CPU
    }

    [Fact]
    public void ComputeTopProcesses_clamps_and_takes()
    {
        long window = 1000;
        var first = new[] { new ProcessCpuSample(1, "a", 1, 0, 1), new ProcessCpuSample(2, "b", 2, 0, 2) };
        var second = new[] { new ProcessCpuSample(1, "a", 1, 50_000, 1), new ProcessCpuSample(2, "b", 2, 10, 2) };
        var rows = SystemInfoService.ComputeTopProcesses(first, second, window, cores: 1, take: 1);
        var only = Assert.Single(rows);
        Assert.Equal(1, only.Pid);
        Assert.Equal(100, only.CpuPercent);
        Assert.Empty(SystemInfoService.ComputeTopProcesses(first, second, window, 1, 0));
    }
}

public class PowerPlanSafeActionTests
{
    private const string Prev = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string High = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    private static Func<string, string?> Resolve => alias =>
        string.Equals(alias, PowerPlanSafeAction.HighPerformanceAlias, StringComparison.OrdinalIgnoreCase) ? High
        : Guid.TryParse(alias, out var g) ? g.ToString("D") : null;

    [Fact]
    public void ApplyHighPerformance_noop_when_already_active()
    {
        bool called = false;
        var session = PowerPlanSafeAction.ApplyHighPerformance(_ => { called = true; return true; },
            readActiveGuid: () => High, resolveGuid: Resolve);
        Assert.False(called);
        Assert.Equal(SafeActionState.Completed, session.State);
        Assert.False(session.CanRollback);
    }

    [Fact]
    public void ApplyHighPerformance_applies_and_verifies()
    {
        string active = Prev;
        var session = PowerPlanSafeAction.ApplyHighPerformance(p => { active = Resolve(p)!; return true; },
            readActiveGuid: () => active, resolveGuid: Resolve);
        Assert.Equal(SafeActionState.RollbackAvailable, session.State);
        Assert.Equal(Prev, session.PreviousValue);
        Assert.Null(session.Error);
    }

    [Fact]
    public void ApplyHighPerformance_failed_set_with_unchanged_plan_is_Failed()
    {
        var session = PowerPlanSafeAction.ApplyHighPerformance(_ => false,
            readActiveGuid: () => Prev, resolveGuid: Resolve);
        Assert.Equal(SafeActionState.Failed, session.State);
        Assert.Equal(SafeActionCodes.ApplyFailed, session.Error);
        Assert.False(session.CanRollback);
    }

    [Fact]
    public void ApplyHighPerformance_keeps_rollback_when_plan_moved_but_not_to_target()
    {
        string active = Prev;
        const string other = "a1841308-3541-4fab-bc81-f71556f20b4a";
        var session = PowerPlanSafeAction.ApplyHighPerformance(_ => { active = other; return false; },
            readActiveGuid: () => active, resolveGuid: Resolve);
        Assert.Equal(SafeActionState.RollbackAvailable, session.State);
        Assert.Equal(SafeActionCodes.ApplyFailed, session.Error);
        Assert.True(session.CanRollback);
    }

    [Fact]
    public void ApplyHighPerformance_scheme_missing_is_localizable_code()
    {
        var session = PowerPlanSafeAction.ApplyHighPerformance(_ => true,
            readActiveGuid: () => Prev, resolveGuid: _ => null);
        Assert.Equal(SafeActionState.Failed, session.State);
        Assert.Equal(SafeActionCodes.SchemeMissing, session.Error);
    }

    [Fact]
    public void ApplyHighPerformance_blocked_while_game_session_active()
    {
        bool called = false;
        var session = PowerPlanSafeAction.ApplyHighPerformance(_ => { called = true; return true; },
            readActiveGuid: () => Prev, resolveGuid: Resolve, isGameSessionActive: () => true);
        Assert.False(called);
        Assert.Equal(SafeActionState.Failed, session.State);
        Assert.Equal(SafeActionCodes.SessionActive, session.Error);
    }

    [Fact]
    public void Rollback_restores_previous_when_setPlan_succeeds()
    {
        string active = High;
        var session = SafeActionFlow.Begin(PowerPlanSafeAction.ActionId, Prev, PowerPlanSafeAction.HighPerformanceAlias);
        SafeActionFlow.MarkCompleted(session, PowerPlanSafeAction.HighPerformanceAlias);
        Assert.True(session.CanRollback);

        string? applied = null;
        var result = PowerPlanSafeAction.Rollback(session,
            g => { applied = g; active = g; return true; },
            readActiveGuid: () => active, resolveGuid: Resolve);
        Assert.Equal(Prev, applied);
        Assert.Equal(SafeActionState.Completed, result.State);
        Assert.Null(result.Error);
        Assert.False(result.CanRollback);
    }

    [Fact]
    public void Rollback_conflict_when_plan_is_no_longer_what_visor_set()
    {
        const string other = "a1841308-3541-4fab-bc81-f71556f20b4a";
        string active = other;
        var session = SafeActionFlow.Begin(PowerPlanSafeAction.ActionId, Prev, PowerPlanSafeAction.HighPerformanceAlias);
        SafeActionFlow.MarkCompleted(session, PowerPlanSafeAction.HighPerformanceAlias);

        bool called = false;
        var r = PowerPlanSafeAction.Rollback(session, _ => { called = true; return true; },
            readActiveGuid: () => active, resolveGuid: Resolve);
        Assert.False(called);
        Assert.Equal(SafeActionState.RollbackFailed, r.State);
        Assert.Equal(SafeActionCodes.Conflict, r.Error);
        Assert.True(r.CanRollback);

        r = PowerPlanSafeAction.Rollback(r, g => { active = g; return true; },
            readActiveGuid: () => active, resolveGuid: Resolve, confirmedConflict: true);
        Assert.Equal(SafeActionState.Completed, r.State);
        Assert.Equal(Prev, active);
    }

    [Fact]
    public void Rollback_is_noop_when_snapshot_already_restored_previous()
    {
        var session = SafeActionFlow.Begin(PowerPlanSafeAction.ActionId, Prev, PowerPlanSafeAction.HighPerformanceAlias);
        SafeActionFlow.MarkCompleted(session, PowerPlanSafeAction.HighPerformanceAlias);
        bool called = false;
        var r = PowerPlanSafeAction.Rollback(session, _ => { called = true; return true; },
            readActiveGuid: () => Prev, resolveGuid: Resolve);
        Assert.False(called);
        Assert.Equal(SafeActionState.Completed, r.State);
    }

    [Fact]
    public void Rollback_failed_set_is_RollbackFailed_with_code()
    {
        var session = SafeActionFlow.Begin(PowerPlanSafeAction.ActionId, Prev, PowerPlanSafeAction.HighPerformanceAlias);
        SafeActionFlow.MarkCompleted(session, PowerPlanSafeAction.HighPerformanceAlias);
        var r = PowerPlanSafeAction.Rollback(session, _ => false,
            readActiveGuid: () => High, resolveGuid: Resolve);
        Assert.Equal(SafeActionState.RollbackFailed, r.State);
        Assert.Equal(SafeActionCodes.ApplyFailed, r.Error);
        Assert.True(r.CanRollback);
    }
}
