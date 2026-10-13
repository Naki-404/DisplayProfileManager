using System.IO;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class SafeActionServiceTests
{
    private const string Prev = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string High = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string Other = "a1841308-3541-4fab-bc81-f71556f20b4a";

    private static string? Resolve(string alias) =>
        string.Equals(alias, PowerPlanSafeAction.HighPerformanceAlias, StringComparison.OrdinalIgnoreCase) ? High
        : Guid.TryParse(alias, out var g) ? g.ToString("D") : null;

    /// <summary>Fake power API: a mutable "active" GUID plus counters; nothing real is touched.</summary>
    private sealed class FakePower
    {
        public string? Active = Prev;
        public int Sets;
        public bool Fail;
        public bool Ignore;

        public bool Set(string plan)
        {
            Sets++;
            if (Fail) return false;
            if (Ignore) return true;
            Active = Resolve(plan);
            return true;
        }

        public PowerPlanSafeActionTarget Target() => new(Set, () => Active, Resolve);
    }

    private static SafeActionService Build(FakePower power, FakeGameModeRegistry reg, string? dir = null,
        Func<bool>? session = null) =>
        new(new SafeActionJournal(dir), new ISafeActionTarget[]
        {
            new GameModeSafeActionTarget(true, reg),
            new GameModeSafeActionTarget(false, reg),
            power.Target()
        }, session);

    [Fact]
    public void Apply_then_Undo_restores_power_plan_and_clears_journal()
    {
        var power = new FakePower();
        var svc = Build(power, new FakeGameModeRegistry());

        Assert.False(svc.CanUndo(PowerPlanSafeAction.ActionId));
        var r = svc.Apply(PowerPlanSafeAction.ActionId);
        Assert.Equal(SafeActionResult.Applied, r.Result);
        Assert.Equal(High, power.Active);
        Assert.True(svc.CanUndo(PowerPlanSafeAction.ActionId));
        Assert.True(svc.IsAtTarget(PowerPlanSafeAction.ActionId));

        var u = svc.Undo(PowerPlanSafeAction.ActionId);
        Assert.Equal(SafeActionUndoResult.Undone, u.Result);
        Assert.Equal(Prev, power.Active);
        Assert.False(svc.CanUndo(PowerPlanSafeAction.ActionId));
    }

    [Fact]
    public void Second_click_at_target_is_NoOp_and_keeps_existing_undo_entry()
    {
        var power = new FakePower();
        var svc = Build(power, new FakeGameModeRegistry());
        Assert.Equal(SafeActionResult.Applied, svc.Apply(PowerPlanSafeAction.ActionId).Result);
        var entry = svc.Journal.Get(PowerPlanSafeAction.ActionId)!;

        var r = svc.Apply(PowerPlanSafeAction.ActionId);
        Assert.Equal(SafeActionResult.NoOp, r.Result);
        Assert.Equal(1, power.Sets);
        var again = svc.Journal.Get(PowerPlanSafeAction.ActionId)!;
        Assert.Equal(entry.PreviousState, again.PreviousState);
        Assert.Equal(entry.Utc, again.Utc);
        Assert.Equal(Prev, again.PreviousState);
    }

    [Fact]
    public void Failed_write_with_unchanged_value_is_Failed_without_undo()
    {
        var power = new FakePower { Fail = true };
        var svc = Build(power, new FakeGameModeRegistry());
        var r = svc.Apply(PowerPlanSafeAction.ActionId);
        Assert.Equal(SafeActionResult.Failed, r.Result);
        Assert.Equal(SafeActionCodes.ApplyFailed, r.Code);
        Assert.False(svc.CanUndo(PowerPlanSafeAction.ActionId));

        power.Fail = false;
        power.Ignore = true;
        r = svc.Apply(PowerPlanSafeAction.ActionId);
        Assert.Equal(SafeActionResult.Failed, r.Result);
        Assert.Equal(SafeActionCodes.VerifyMismatch, r.Code);
        Assert.False(svc.CanUndo(PowerPlanSafeAction.ActionId));
    }

    [Fact]
    public void Power_plan_is_Blocked_while_game_session_active()
    {
        var power = new FakePower();
        bool active = true;
        var svc = Build(power, new FakeGameModeRegistry(), session: () => active);
        var r = svc.Apply(PowerPlanSafeAction.ActionId);
        Assert.Equal(SafeActionResult.Blocked, r.Result);
        Assert.Equal(SafeActionCodes.SessionActive, r.Code);
        Assert.Equal("safeAction.code.sessionActive", SafeActionCodes.LocKey(r.Code));
        Assert.Equal(0, power.Sets);

        // Game Mode is not owned by the session snapshot — still allowed.
        Assert.Equal(SafeActionResult.Applied, svc.Apply(GameModeSetting.ActionIdOff).Result);

        active = false;
        Assert.Equal(SafeActionResult.Applied, svc.Apply(PowerPlanSafeAction.ActionId).Result);
    }

    [Fact]
    public void Undo_reports_Conflict_until_confirmed_when_state_changed_elsewhere()
    {
        var power = new FakePower();
        var svc = Build(power, new FakeGameModeRegistry());
        svc.Apply(PowerPlanSafeAction.ActionId);
        power.Active = Other; // user picked another plan in Windows

        var u = svc.Undo(PowerPlanSafeAction.ActionId);
        Assert.Equal(SafeActionUndoResult.Conflict, u.Result);
        Assert.Equal(SafeActionCodes.Conflict, u.Code);
        Assert.Equal(Other, power.Active);
        Assert.True(svc.CanUndo(PowerPlanSafeAction.ActionId));

        u = svc.Undo(PowerPlanSafeAction.ActionId, confirmedConflict: true);
        Assert.Equal(SafeActionUndoResult.Undone, u.Result);
        Assert.Equal(Prev, power.Active);
    }

    [Fact]
    public void Undo_when_snapshot_already_restored_previous_is_Undone_without_write()
    {
        var power = new FakePower();
        var svc = Build(power, new FakeGameModeRegistry());
        svc.Apply(PowerPlanSafeAction.ActionId);
        power.Active = Prev; // session end restored it
        int sets = power.Sets;
        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(PowerPlanSafeAction.ActionId).Result);
        Assert.Equal(sets, power.Sets);
        Assert.False(svc.CanUndo(PowerPlanSafeAction.ActionId));
    }

    [Fact]
    public void Undo_without_entry_or_unknown_action_fails_with_codes()
    {
        var svc = Build(new FakePower(), new FakeGameModeRegistry());
        Assert.Equal(SafeActionCodes.CannotRollback, svc.Undo(PowerPlanSafeAction.ActionId).Code);
        Assert.Equal(SafeActionCodes.UnknownAction, svc.Apply("nope").Code);
        Assert.Equal(SafeActionResult.Failed, svc.Apply("nope").Result);
    }

    [Fact]
    public void GameMode_on_off_share_one_undo_slot_and_restore_absent_values()
    {
        var reg = new FakeGameModeRegistry(); // both absent = on
        var svc = Build(new FakePower(), reg);

        Assert.Equal(SafeActionResult.NoOp, svc.Apply(GameModeSetting.ActionIdOn).Result);
        Assert.Equal(SafeActionResult.Applied, svc.Apply(GameModeSetting.ActionIdOff).Result);
        Assert.True(svc.CanUndo(GameModeSetting.ActionId));
        Assert.True(svc.CanUndo(GameModeSetting.ActionIdOff));
        Assert.Equal("auto:-|allow:-", svc.Journal.Get(GameModeSetting.ActionId)!.PreviousState);

        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(GameModeSetting.ActionId).Result);
        Assert.Empty(reg.Values);
        Assert.False(svc.CanUndo(GameModeSetting.ActionId));
    }

    [Fact]
    public void Journal_survives_restart()
    {
        using var dir = new TempDir();
        var power = new FakePower();
        var svc = Build(power, new FakeGameModeRegistry(), dir.Path);
        Assert.Equal(SafeActionResult.Applied, svc.Apply(PowerPlanSafeAction.ActionId).Result);
        Assert.True(File.Exists(Path.Combine(dir.Path, SafeActionJournal.FileName)));
        Assert.False(File.Exists(Path.Combine(dir.Path, SafeActionJournal.FileName + ".tmp")));

        var reloaded = Build(power, new FakeGameModeRegistry(), dir.Path);
        Assert.True(reloaded.CanUndo(PowerPlanSafeAction.ActionId));
        var entry = reloaded.Journal.Get(PowerPlanSafeAction.ActionId)!;
        Assert.Equal(Prev, entry.PreviousState);
        Assert.Equal(High, entry.TargetState);
        Assert.Equal(SafeActionUndoResult.Undone, reloaded.Undo(PowerPlanSafeAction.ActionId).Result);
        Assert.Equal(Prev, power.Active);

        var third = new SafeActionJournal(dir.Path);
        Assert.Empty(third.Entries);
    }

    [Fact]
    public void Journal_recovers_from_corrupt_file()
    {
        using var dir = new TempDir();
        dir.Write(SafeActionJournal.FileName, "{ nope");
        var journal = new SafeActionJournal(dir.Path);
        Assert.Empty(journal.Entries);
        Assert.True(File.Exists(Path.Combine(dir.Path, SafeActionJournal.FileName + ".bak")));
        journal.Record(new SafeActionJournalEntry { Key = "k", ActionId = "k", PreviousState = "a", TargetState = "b", Utc = DateTime.UtcNow });
        Assert.Single(new SafeActionJournal(dir.Path).Entries);
    }

    [Fact]
    public void CreateDefault_registers_three_actions()
    {
        var svc = SafeActionService.CreateDefault(null, _ => false, null);
        Assert.Contains(PowerPlanSafeAction.ActionId, svc.ActionIds);
        Assert.Contains(GameModeSetting.ActionIdOn, svc.ActionIds);
        Assert.Contains(GameModeSetting.ActionIdOff, svc.ActionIds);
        Assert.True(svc.FindTarget(PowerPlanSafeAction.ActionId)!.BlockedDuringGameSession);
        Assert.False(svc.FindTarget(GameModeSetting.ActionIdOn)!.BlockedDuringGameSession);
    }
}
