using System.IO;
using Visor.Services;
using Visor.Services.Optimize;
using Xunit;

namespace Visor.Tests;

public class OptimizeTargetsTests
{
    private const string Exe = @"C:\Games\Doom\DOOMx64.exe";

    private sealed class Broadcasts
    {
        public readonly List<(int T1, int T2, int Speed)> Calls = new();
        public void Record(int t1, int t2, int speed) => Calls.Add((t1, t2, speed));
    }

    private static SafeActionService Build(FakeOptimizeRegistry reg, Broadcasts? bc = null, string? dir = null,
        Func<bool>? session = null)
    {
        bc ??= new Broadcasts();
        return new SafeActionService(new SafeActionJournal(dir), new ISafeActionTarget[]
        {
            new GameBarSafeActionTarget(reg),
            new MouseAccelerationSafeActionTarget(reg, bc.Record)
        }, session, id => SafeActionService.ResolvePerGame(id, reg));
    }

    // ---------------- Xbox Game Bar background ----------------

    [Fact]
    public void GameBar_apply_writes_three_zeros_and_undo_deletes_absent_values()
    {
        var reg = new FakeOptimizeRegistry(); // everything absent = Windows default (on)
        var svc = Build(reg);
        Assert.False(svc.IsAtTarget(GameBarSafeActionTarget.Id));
        Assert.True(GameBarSafeActionTarget.ReadBackgroundOn(reg));

        var r = svc.Apply(GameBarSafeActionTarget.Id);
        Assert.Equal(SafeActionResult.Applied, r.Result);
        Assert.Equal(0, reg.Get(GameBarSafeActionTarget.UseNexus)!.AsDword);
        Assert.Equal(0, reg.Get(GameBarSafeActionTarget.AppCapture)!.AsDword);
        Assert.Equal(0, reg.Get(GameBarSafeActionTarget.GameDvrEnabled)!.AsDword);
        Assert.Equal(RegValueKind.Dword, reg.Get(GameBarSafeActionTarget.AppCapture)!.Kind);
        Assert.True(svc.IsAtTarget(GameBarSafeActionTarget.Id));
        Assert.False(GameBarSafeActionTarget.ReadBackgroundOn(reg));

        var entry = svc.Journal.Get(GameBarSafeActionTarget.Id)!;
        Assert.Equal(GameBarSafeActionTarget.Id, entry.ActionId);
        Assert.True(RegistryValueSet.TryParse(entry.PreviousState, out var prev));
        Assert.All(prev.Items, kv => Assert.Null(kv.Value));
        Assert.True(RegistryValueSet.TryParse(entry.TargetState, out var tgt));
        Assert.All(tgt.Items, kv => Assert.Equal(0, kv.Value!.AsDword));

        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(GameBarSafeActionTarget.Id).Result);
        Assert.Empty(reg.Values);
        Assert.False(svc.CanUndo(GameBarSafeActionTarget.Id));
    }

    [Fact]
    public void GameBar_undo_restores_exact_mixed_previous_values()
    {
        var reg = new FakeOptimizeRegistry()
            .Dword(GameBarSafeActionTarget.UseNexus, 1)
            .Dword(GameBarSafeActionTarget.GameDvrEnabled, 1); // AppCapture absent
        var svc = Build(reg);
        Assert.Equal(SafeActionResult.Applied, svc.Apply(GameBarSafeActionTarget.Id).Result);
        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(GameBarSafeActionTarget.Id).Result);
        Assert.Equal(1, reg.Get(GameBarSafeActionTarget.UseNexus)!.AsDword);
        Assert.Equal(1, reg.Get(GameBarSafeActionTarget.GameDvrEnabled)!.AsDword);
        Assert.False(reg.Has(GameBarSafeActionTarget.AppCapture));
    }

    [Fact]
    public void GameBar_partially_off_still_applies_and_fully_off_is_NoOp()
    {
        var reg = new FakeOptimizeRegistry()
            .Dword(GameBarSafeActionTarget.AppCapture, 0)
            .Dword(GameBarSafeActionTarget.GameDvrEnabled, 0); // UseNexus absent → still on
        var svc = Build(reg);
        Assert.Equal(SafeActionResult.Applied, svc.Apply(GameBarSafeActionTarget.Id).Result);

        int writes = reg.Writes;
        var entry = svc.Journal.Get(GameBarSafeActionTarget.Id)!;
        Assert.Equal(SafeActionResult.NoOp, svc.Apply(GameBarSafeActionTarget.Id).Result);
        Assert.Equal(writes, reg.Writes);
        Assert.Equal(entry.PreviousState, svc.Journal.Get(GameBarSafeActionTarget.Id)!.PreviousState);
        Assert.Equal(entry.Utc, svc.Journal.Get(GameBarSafeActionTarget.Id)!.Utc);
    }

    [Fact]
    public void GameBar_verify_mismatch_when_writes_do_not_stick()
    {
        var reg = new FakeOptimizeRegistry { IgnoreWrites = true };
        var svc = Build(reg);
        var r = svc.Apply(GameBarSafeActionTarget.Id);
        Assert.Equal(SafeActionResult.Failed, r.Result);
        Assert.Equal(SafeActionCodes.VerifyMismatch, r.Code);
        Assert.False(svc.CanUndo(GameBarSafeActionTarget.Id));

        reg.IgnoreWrites = false;
        reg.FailWrites = true;
        r = svc.Apply(GameBarSafeActionTarget.Id);
        Assert.Equal(SafeActionCodes.ApplyFailed, r.Code);
        Assert.False(svc.CanUndo(GameBarSafeActionTarget.Id));

        reg.FailWrites = false;
        reg.FailReads = true;
        r = svc.Apply(GameBarSafeActionTarget.Id);
        Assert.Equal(SafeActionCodes.Unavailable, r.Code);
    }

    [Fact]
    public void GameBar_undo_conflict_when_changed_outside_then_forced()
    {
        var reg = new FakeOptimizeRegistry();
        var svc = Build(reg);
        svc.Apply(GameBarSafeActionTarget.Id);
        reg.Dword(GameBarSafeActionTarget.AppCapture, 1); // user re-enabled capture in Settings

        var u = svc.Undo(GameBarSafeActionTarget.Id);
        Assert.Equal(SafeActionUndoResult.Conflict, u.Result);
        Assert.True(svc.CanUndo(GameBarSafeActionTarget.Id));

        u = svc.Undo(GameBarSafeActionTarget.Id, confirmedConflict: true);
        Assert.Equal(SafeActionUndoResult.Undone, u.Result);
        Assert.Empty(reg.Values);
    }

    [Fact]
    public void GameBar_metadata_keys_and_scope()
    {
        var t = new GameBarSafeActionTarget(new FakeOptimizeRegistry());
        ISafeActionTarget i = t;
        Assert.Equal(SafeActionScope.Global, i.Scope);
        Assert.False(i.BlockedDuringGameSession);
        Assert.Equal("optimize.gameBar.off.why", i.WhyKey);
        Assert.Equal("optimize.gameBar.off.risk", i.RiskKey);
        Assert.Equal("optimize.gameBar.off.undo", i.UndoKey);
        Assert.Equal("optimize.gameBar.off.title", i.TitleKey);
    }

    [Fact]
    public void GameBar_state_from_another_target_is_rejected()
    {
        var reg = new FakeOptimizeRegistry();
        var t = new GameBarSafeActionTarget(reg);
        var foreign = new RegistryValueSet().Add(MouseAccelerationSafeActionTarget.Speed, RegValue.String("0")).Serialize();
        Assert.False(t.WriteState(foreign));
        Assert.Equal(0, reg.Writes);
        Assert.False(t.SatisfiesTarget(foreign, t.ResolveTargetState()!));
    }

    // ---------------- Fullscreen optimizations per exe ----------------

    [Theory]
    [InlineData(null, "~ DISABLEDXMAXIMIZEDWINDOWEDMODE")]
    [InlineData("", "~ DISABLEDXMAXIMIZEDWINDOWEDMODE")]
    [InlineData("~ HIGHDPIAWARE", "~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE")]
    [InlineData("RUNASADMIN", "RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE")]
    [InlineData("~ DISABLEDXMAXIMIZEDWINDOWEDMODE", "~ DISABLEDXMAXIMIZEDWINDOWEDMODE")]
    [InlineData("~  HIGHDPIAWARE   disabledxmaximizedwindowedmode ", "~  HIGHDPIAWARE   disabledxmaximizedwindowedmode")]
    public void Fso_merge_keeps_other_tokens(string? existing, string expected)
    {
        Assert.Equal(expected, FullscreenOptimizationSafeActionTarget.MergeToken(existing));
        Assert.True(FullscreenOptimizationSafeActionTarget.HasToken(expected));
    }

    [Theory]
    [InlineData("~ DISABLEDXMAXIMIZEDWINDOWEDMODE", null)]
    [InlineData("DISABLEDXMAXIMIZEDWINDOWEDMODE", null)]
    [InlineData("~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE", "~ HIGHDPIAWARE")]
    [InlineData("~ HIGHDPIAWARE", "~ HIGHDPIAWARE")]
    public void Fso_remove_token_only_ours(string existing, string? expected)
    {
        Assert.Equal(expected, FullscreenOptimizationSafeActionTarget.RemoveToken(existing));
    }

    [Fact]
    public void Fso_action_id_embeds_exe_and_round_trips()
    {
        var id = FullscreenOptimizationSafeActionTarget.ActionIdFor(Exe);
        Assert.Equal("fso.disable|" + Exe, id);
        Assert.Equal(Exe, FullscreenOptimizationSafeActionTarget.ExePathFromActionId(id));
        Assert.Equal(Exe, FullscreenOptimizationSafeActionTarget.ExePathFromActionId("FSO.DISABLE|" + Exe));
        Assert.Null(FullscreenOptimizationSafeActionTarget.ExePathFromActionId("gameBar.off"));
        Assert.Null(FullscreenOptimizationSafeActionTarget.ExePathFromActionId("fso.disable|  "));
        Assert.Null(FullscreenOptimizationSafeActionTarget.ExePathFromActionId(null));
        Assert.Throws<ArgumentException>(() => new FullscreenOptimizationSafeActionTarget(" "));
    }

    [Fact]
    public void Fso_apply_creates_value_and_undo_deletes_it()
    {
        var reg = new FakeOptimizeRegistry();
        var svc = Build(reg);
        var id = FullscreenOptimizationSafeActionTarget.ActionIdFor(Exe);
        var addr = new RegAddress(FullscreenOptimizationSafeActionTarget.LayersKey, Exe);

        // Resolved on demand — never registered explicitly.
        Assert.DoesNotContain(id, svc.ActionIds);
        var target = svc.FindTarget(id)!;
        Assert.Equal(SafeActionScope.PerGame(Exe), target.Scope);
        Assert.Equal("optimize.fso.why", target.WhyKey);
        Assert.Equal("optimize.fso.risk", target.RiskKey);
        Assert.Equal("optimize.fso.undo", target.UndoKey);
        Assert.Contains(id, svc.ActionIds);

        Assert.False(FullscreenOptimizationSafeActionTarget.ReadDisabledFor(Exe, reg));
        Assert.Equal(SafeActionResult.Applied, svc.Apply(id).Result);
        Assert.Equal("~ DISABLEDXMAXIMIZEDWINDOWEDMODE", reg.Get(addr)!.AsString);
        Assert.Equal(RegValueKind.String, reg.Get(addr)!.Kind);
        Assert.True(FullscreenOptimizationSafeActionTarget.ReadDisabledFor(Exe, reg));
        Assert.Equal(SafeActionResult.NoOp, svc.Apply(id).Result);

        var entry = Assert.Single(svc.PerGameEntries());
        Assert.Equal(id, entry.Key);

        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(id).Result);
        Assert.False(reg.Has(addr));
        Assert.Empty(svc.PerGameEntries());
    }

    [Fact]
    public void Fso_apply_merges_with_existing_flags_and_undo_restores_them_exactly()
    {
        var addr = new RegAddress(FullscreenOptimizationSafeActionTarget.LayersKey, Exe);
        var reg = new FakeOptimizeRegistry().Str(addr, "~ HIGHDPIAWARE");
        var svc = Build(reg);
        var id = FullscreenOptimizationSafeActionTarget.ActionIdFor(Exe);

        Assert.Equal(SafeActionResult.Applied, svc.Apply(id).Result);
        Assert.Equal("~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE", reg.Get(addr)!.AsString);
        Assert.True(RegistryValueSet.TryParse(svc.Journal.Get(id)!.PreviousState, out var prev));
        Assert.Equal("~ HIGHDPIAWARE", prev.Get(addr)!.AsString);

        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(id).Result);
        Assert.Equal("~ HIGHDPIAWARE", reg.Get(addr)!.AsString);
    }

    [Fact]
    public void Fso_already_disabled_via_properties_dialog_is_NoOp_without_journal()
    {
        var addr = new RegAddress(FullscreenOptimizationSafeActionTarget.LayersKey, Exe);
        var reg = new FakeOptimizeRegistry().Str(addr, "~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE");
        var svc = Build(reg);
        var id = FullscreenOptimizationSafeActionTarget.ActionIdFor(Exe);
        Assert.True(svc.IsAtTarget(id));
        Assert.Equal(SafeActionResult.NoOp, svc.Apply(id).Result);
        Assert.Equal(0, reg.Writes);
        Assert.False(svc.CanUndo(id));
    }

    [Fact]
    public void Fso_undo_conflict_when_user_edited_flags_meanwhile()
    {
        var addr = new RegAddress(FullscreenOptimizationSafeActionTarget.LayersKey, Exe);
        var reg = new FakeOptimizeRegistry();
        var svc = Build(reg);
        var id = FullscreenOptimizationSafeActionTarget.ActionIdFor(Exe);
        svc.Apply(id);
        reg.Str(addr, "~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE");
        Assert.Equal(SafeActionUndoResult.Conflict, svc.Undo(id).Result);
        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(id, confirmedConflict: true).Result);
        Assert.False(reg.Has(addr));
    }

    [Fact]
    public void Fso_journal_entry_survives_restart_and_resolves_without_registration()
    {
        using var dir = new TempDir();
        var addr = new RegAddress(FullscreenOptimizationSafeActionTarget.LayersKey, Exe);
        var reg = new FakeOptimizeRegistry();
        var id = FullscreenOptimizationSafeActionTarget.ActionIdFor(Exe);
        Assert.Equal(SafeActionResult.Applied, Build(reg, dir: dir.Path).Apply(id).Result);

        var reloaded = Build(reg, dir: dir.Path);
        Assert.True(reloaded.CanUndo(id));
        var entry = Assert.Single(reloaded.PerGameEntries());
        Assert.Equal(Exe, FullscreenOptimizationSafeActionTarget.ExePathFromActionId(entry.ActionId));
        Assert.Equal(SafeActionUndoResult.Undone, reloaded.Undo(id).Result);
        Assert.False(reg.Has(addr));
        Assert.Empty(new SafeActionJournal(dir.Path).Entries);
    }

    [Fact]
    public void Fso_two_games_have_independent_undo_slots()
    {
        const string other = @"D:\Games\Other\other.exe";
        var reg = new FakeOptimizeRegistry();
        var svc = Build(reg);
        Assert.Equal(SafeActionResult.Applied, svc.Apply(FullscreenOptimizationSafeActionTarget.ActionIdFor(Exe)).Result);
        Assert.Equal(SafeActionResult.Applied, svc.Apply(FullscreenOptimizationSafeActionTarget.ActionIdFor(other)).Result);
        Assert.Equal(2, svc.PerGameEntries().Count);
        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(FullscreenOptimizationSafeActionTarget.ActionIdFor(other)).Result);
        Assert.True(reg.Has(new RegAddress(FullscreenOptimizationSafeActionTarget.LayersKey, Exe)));
        Assert.False(reg.Has(new RegAddress(FullscreenOptimizationSafeActionTarget.LayersKey, other)));
    }

    [Fact]
    public void Fso_value_of_wrong_kind_is_replaced_and_restored_exactly()
    {
        var addr = new RegAddress(FullscreenOptimizationSafeActionTarget.LayersKey, Exe);
        var reg = new FakeOptimizeRegistry().Dword(addr, 1);
        var svc = Build(reg);
        var id = FullscreenOptimizationSafeActionTarget.ActionIdFor(Exe);
        Assert.False(svc.IsAtTarget(id));
        Assert.Equal(SafeActionResult.Applied, svc.Apply(id).Result);
        Assert.Equal(RegValueKind.String, reg.Get(addr)!.Kind);
        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(id).Result);
        Assert.Equal(RegValueKind.Dword, reg.Get(addr)!.Kind);
        Assert.Equal(1, reg.Get(addr)!.AsDword);
    }

    // ---------------- Mouse acceleration ----------------

    [Fact]
    public void Mouse_apply_writes_zero_strings_and_broadcasts_then_undo_restores_and_broadcasts_defaults()
    {
        var reg = new FakeOptimizeRegistry()
            .Str(MouseAccelerationSafeActionTarget.Speed, "1")
            .Str(MouseAccelerationSafeActionTarget.Threshold1, "6")
            .Str(MouseAccelerationSafeActionTarget.Threshold2, "10");
        var bc = new Broadcasts();
        var svc = Build(reg, bc);
        Assert.True(MouseAccelerationSafeActionTarget.ReadAccelerationOn(reg));

        Assert.Equal(SafeActionResult.Applied, svc.Apply(MouseAccelerationSafeActionTarget.Id).Result);
        Assert.Equal("0", reg.Get(MouseAccelerationSafeActionTarget.Speed)!.AsString);
        Assert.Equal("0", reg.Get(MouseAccelerationSafeActionTarget.Threshold1)!.AsString);
        Assert.Equal("0", reg.Get(MouseAccelerationSafeActionTarget.Threshold2)!.AsString);
        Assert.Equal(RegValueKind.String, reg.Get(MouseAccelerationSafeActionTarget.Speed)!.Kind);
        Assert.Equal((0, 0, 0), Assert.Single(bc.Calls));
        Assert.False(MouseAccelerationSafeActionTarget.ReadAccelerationOn(reg));

        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(MouseAccelerationSafeActionTarget.Id).Result);
        Assert.Equal("1", reg.Get(MouseAccelerationSafeActionTarget.Speed)!.AsString);
        Assert.Equal("6", reg.Get(MouseAccelerationSafeActionTarget.Threshold1)!.AsString);
        Assert.Equal("10", reg.Get(MouseAccelerationSafeActionTarget.Threshold2)!.AsString);
        Assert.Equal(2, bc.Calls.Count);
        Assert.Equal((6, 10, 1), bc.Calls[1]);
    }

    [Fact]
    public void Mouse_absent_values_mean_acceleration_on_and_undo_deletes_them_broadcasting_defaults()
    {
        var reg = new FakeOptimizeRegistry();
        var bc = new Broadcasts();
        var svc = Build(reg, bc);
        Assert.True(MouseAccelerationSafeActionTarget.ReadAccelerationOn(reg));
        Assert.Equal(SafeActionResult.Applied, svc.Apply(MouseAccelerationSafeActionTarget.Id).Result);
        Assert.True(RegistryValueSet.TryParse(svc.Journal.Get(MouseAccelerationSafeActionTarget.Id)!.PreviousState, out var prev));
        Assert.All(prev.Items, kv => Assert.Null(kv.Value));

        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(MouseAccelerationSafeActionTarget.Id).Result);
        Assert.Empty(reg.Values);
        Assert.Equal((MouseAccelerationSafeActionTarget.DefaultThreshold1, MouseAccelerationSafeActionTarget.DefaultThreshold2,
            MouseAccelerationSafeActionTarget.DefaultSpeed), bc.Calls[^1]);
    }

    [Fact]
    public void Mouse_already_off_is_NoOp_without_broadcast()
    {
        var reg = new FakeOptimizeRegistry()
            .Str(MouseAccelerationSafeActionTarget.Speed, "0")
            .Str(MouseAccelerationSafeActionTarget.Threshold1, "0")
            .Str(MouseAccelerationSafeActionTarget.Threshold2, "0");
        var bc = new Broadcasts();
        var svc = Build(reg, bc);
        Assert.Equal(SafeActionResult.NoOp, svc.Apply(MouseAccelerationSafeActionTarget.Id).Result);
        Assert.Empty(bc.Calls);
        Assert.Equal(0, reg.Writes);
        Assert.False(svc.CanUndo(MouseAccelerationSafeActionTarget.Id));
    }

    [Fact]
    public void Mouse_speed_zero_but_thresholds_set_still_applies()
    {
        // Speed "0" alone already disables the curve, but Windows' own checkbox writes all three — we do the same.
        var reg = new FakeOptimizeRegistry()
            .Str(MouseAccelerationSafeActionTarget.Speed, "0")
            .Str(MouseAccelerationSafeActionTarget.Threshold1, "6")
            .Str(MouseAccelerationSafeActionTarget.Threshold2, "10");
        var svc = Build(reg);
        Assert.False(MouseAccelerationSafeActionTarget.ReadAccelerationOn(reg));
        Assert.False(svc.IsAtTarget(MouseAccelerationSafeActionTarget.Id));
        Assert.Equal(SafeActionResult.Applied, svc.Apply(MouseAccelerationSafeActionTarget.Id).Result);
    }

    [Fact]
    public void Mouse_verify_mismatch_and_broadcast_failure_do_not_lose_journal()
    {
        var reg = new FakeOptimizeRegistry { IgnoreWrites = true };
        var bc = new Broadcasts();
        var svc = Build(reg, bc);
        var r = svc.Apply(MouseAccelerationSafeActionTarget.Id);
        Assert.Equal(SafeActionResult.Failed, r.Result);
        Assert.Equal(SafeActionCodes.VerifyMismatch, r.Code);
        Assert.False(svc.CanUndo(MouseAccelerationSafeActionTarget.Id));

        reg.IgnoreWrites = false;
        var throwing = new SafeActionService(new SafeActionJournal(null), new ISafeActionTarget[]
        {
            new MouseAccelerationSafeActionTarget(reg, (_, _, _) => throw new InvalidOperationException("spi"))
        });
        Assert.Equal(SafeActionResult.Applied, throwing.Apply(MouseAccelerationSafeActionTarget.Id).Result);
        Assert.True(throwing.CanUndo(MouseAccelerationSafeActionTarget.Id));
    }

    [Fact]
    public void Mouse_unparsable_value_counts_as_on_and_is_restored_verbatim()
    {
        var reg = new FakeOptimizeRegistry().Str(MouseAccelerationSafeActionTarget.Speed, "abc");
        var bc = new Broadcasts();
        var svc = Build(reg, bc);
        Assert.True(MouseAccelerationSafeActionTarget.ReadAccelerationOn(reg));
        Assert.Equal(SafeActionResult.Applied, svc.Apply(MouseAccelerationSafeActionTarget.Id).Result);
        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(MouseAccelerationSafeActionTarget.Id).Result);
        Assert.Equal("abc", reg.Get(MouseAccelerationSafeActionTarget.Speed)!.AsString);
        Assert.Equal((6, 10, 1), bc.Calls[^1]); // unparsable → Windows default for the live broadcast
    }

    [Fact]
    public void Mouse_metadata_keys()
    {
        ISafeActionTarget t = new MouseAccelerationSafeActionTarget(new FakeOptimizeRegistry(), (_, _, _) => { });
        Assert.Equal("optimize.mouseAccel.off.why", t.WhyKey);
        Assert.Equal("optimize.mouseAccel.off.risk", t.RiskKey);
        Assert.Equal("optimize.mouseAccel.off.undo", t.UndoKey);
        Assert.Equal(SafeActionScope.Global, t.Scope);
        Assert.False(t.BlockedDuringGameSession);
    }

    // ---------------- Service wiring ----------------

    [Fact]
    public void New_targets_are_not_blocked_by_a_game_session()
    {
        var reg = new FakeOptimizeRegistry();
        var svc = Build(reg, session: () => true);
        Assert.Equal(SafeActionResult.Applied, svc.Apply(GameBarSafeActionTarget.Id).Result);
        Assert.Equal(SafeActionResult.Applied, svc.Apply(MouseAccelerationSafeActionTarget.Id).Result);
        Assert.Equal(SafeActionResult.Applied, svc.Apply(FullscreenOptimizationSafeActionTarget.ActionIdFor(Exe)).Result);
    }

    [Fact]
    public void Register_replaces_a_target_and_resolver_failures_are_swallowed()
    {
        var reg = new FakeOptimizeRegistry();
        var svc = new SafeActionService(new SafeActionJournal(null), Array.Empty<ISafeActionTarget>(),
            resolver: _ => throw new InvalidOperationException("boom"));
        Assert.Null(svc.FindTarget("anything"));
        Assert.Equal(SafeActionCodes.UnknownAction, svc.Apply("anything").Code);

        svc.Register(new GameBarSafeActionTarget(reg));
        Assert.Contains(GameBarSafeActionTarget.Id, svc.ActionIds);
        Assert.Equal(SafeActionResult.Applied, svc.Apply(GameBarSafeActionTarget.Id).Result);
    }

    [Fact]
    public void CreateDefault_registers_new_actions_and_resolves_per_game_ids()
    {
        var svc = SafeActionService.CreateDefault(null, _ => false, null);
        Assert.Contains(GameBarSafeActionTarget.Id, svc.ActionIds);
        Assert.Contains(MouseAccelerationSafeActionTarget.Id, svc.ActionIds);
        Assert.Contains(GameModeSetting.ActionIdOn, svc.ActionIds);
        Assert.Contains(PowerPlanSafeAction.ActionId, svc.ActionIds);
        // Resolving only constructs the target — nothing is read or written here.
        var t = svc.FindTarget(FullscreenOptimizationSafeActionTarget.ActionIdFor(Exe));
        Assert.NotNull(t);
        Assert.Equal(SafeActionScope.PerGame(Exe), t!.Scope);
        Assert.Null(svc.FindTarget("nope"));
    }

    [Fact]
    public void Existing_targets_get_default_loc_keys()
    {
        ISafeActionTarget gm = new GameModeSafeActionTarget(true, new FakeGameModeRegistry());
        Assert.Equal("optimize.gameMode.why", gm.WhyKey);
        Assert.Equal("optimize.gameMode.title", gm.TitleKey);
        Assert.Equal(SafeActionScope.Global, gm.Scope);
        ISafeActionTarget pp = new PowerPlanSafeActionTarget(_ => false, () => null, _ => null);
        Assert.Equal("optimize.powerHighPerf.undo", pp.UndoKey);
    }
}
