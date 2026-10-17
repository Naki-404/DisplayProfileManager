using Visor.Services;
using Visor.Services.Elevation;
using Visor.Services.Optimize;
using Visor.Services.Optimize.Pc;
using Xunit;

namespace Visor.Tests;

public class PcTweakPowerHagsTests
{
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string Created = "ef6b3483-359a-44a0-b0f6-549a7073622c";
    private const string UltimateRaw = @"@C:\WINDOWS\system32\powrprof.dll,-19,Ultimate Performance";

    private static PcFakePowerSource StockSchemes(string active = Balanced)
    {
        var s = new PcFakePowerSource { Active = active };
        s.Schemes.Add(new PowerSchemeInfo(Balanced, @"@C:\WINDOWS\system32\powrprof.dll,-15,Balanced", "Balanced"));
        s.Schemes.Add(new PowerSchemeInfo(HighPerf, @"@C:\WINDOWS\system32\powrprof.dll,-13,High performance", "High performance"));
        return s;
    }

    // ---------------- Ultimate Performance detection ----------------

    [Fact]
    public void IsUltimate_matches_language_neutral_raw_name_template_guid_and_localized_names()
    {
        Assert.True(UltimatePlan.IsUltimate(new PowerSchemeInfo(Created, UltimateRaw, "Whatever")));
        Assert.True(UltimatePlan.IsUltimate(new PowerSchemeInfo(UltimatePlan.TemplateGuid, null, null)));
        Assert.True(UltimatePlan.IsUltimate(new PowerSchemeInfo(Created, null, "Максимальная производительность")));
        Assert.True(UltimatePlan.IsUltimate(new PowerSchemeInfo(Created, "@%SystemRoot%\\system32\\powrprof.dll,-19", null)));
        Assert.False(UltimatePlan.IsUltimate(new PowerSchemeInfo(HighPerf, @"@C:\WINDOWS\system32\powrprof.dll,-13,High performance", "High performance")));
        Assert.False(UltimatePlan.IsUltimate(new PowerSchemeInfo(Balanced, @"@x\powrprof.dll,-190,Other", "Mine")));
    }

    [Fact]
    public void FindInstalled_prefers_the_scheme_Visor_created()
    {
        var other = "11111111-2222-3333-4444-555555555555";
        var schemes = new[]
        {
            new PowerSchemeInfo(other, UltimateRaw, "Ultimate Performance"),
            new PowerSchemeInfo(Created, "renamed by user", "My plan")
        };
        Assert.Equal(other, UltimatePlan.FindInstalled(schemes));
        Assert.Equal(Created, UltimatePlan.FindInstalled(schemes, Created));
        Assert.Null(UltimatePlan.FindInstalled(Array.Empty<PowerSchemeInfo>()));
    }

    // ---------------- creating the scheme (elevated) ----------------

    [Fact]
    public async Task CreateAsync_duplicates_the_template_once_and_remembers_the_new_guid()
    {
        using var dir = new TempDir();
        var source = StockSchemes();
        var broker = new PcFakeBroker { DuplicateOutput = Created };
        broker.OnDuplicate = g => source.Schemes.Add(new PowerSchemeInfo(g, "renamed", "renamed"));
        var svc = new UltimatePlanService(source, broker, new PcTweakStore(dir.Path), () => new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc));

        Assert.Null(svc.FindInstalled());
        var r = await svc.CreateAsync();
        Assert.Equal(UltimateCreateStatus.Created, r.Status);
        Assert.Equal(Created, r.Guid);

        var op = Assert.Single(Assert.Single(broker.Batches));
        Assert.Equal(ElevatedOpKind.PowerDuplicateScheme, op.Kind);
        Assert.Null(ElevationAllowlist.Validate(op, _ => true));

        // Detection survives a rename because the GUID is stored (and reloaded from disk).
        Assert.Equal(Created, new PcTweakStore(dir.Path).CreatedUltimateGuid);
        Assert.Equal(Created, new UltimatePlanService(source, broker, new PcTweakStore(dir.Path)).FindInstalled());

        var again = await svc.CreateAsync();
        Assert.Equal(UltimateCreateStatus.AlreadyInstalled, again.Status);
        Assert.Single(broker.Batches);
    }

    [Fact]
    public async Task CreateAsync_reports_decline_failure_and_missing_scheme_honestly()
    {
        var declined = new UltimatePlanService(StockSchemes(), new PcFakeBroker { Outcome = ElevationOutcome.Declined });
        var r1 = await declined.CreateAsync();
        Assert.Equal(UltimateCreateStatus.Declined, r1.Status);
        Assert.Equal(SafeActionCodes.ElevationDeclined, r1.Code);

        var failing = new UltimatePlanService(StockSchemes(), new PcFakeBroker { DuplicateOutput = null });
        Assert.Equal(UltimateCreateStatus.Failed, (await failing.CreateAsync()).Status);

        // Helper claims success but the scheme is not listed afterwards → not remembered.
        var store = new PcTweakStore(null);
        var ghost = new UltimatePlanService(StockSchemes(), new PcFakeBroker { DuplicateOutput = Created }, store);
        var r3 = await ghost.CreateAsync();
        Assert.Equal(UltimateCreateStatus.Failed, r3.Status);
        Assert.Equal(SafeActionCodes.VerifyMismatch, r3.Code);
        Assert.Null(store.CreatedUltimateGuid);

        Assert.Equal(UltimateCreateStatus.Unavailable, (await new UltimatePlanService(StockSchemes()).CreateAsync()).Status);
    }

    // ---------------- activating (SafeAction, no admin) ----------------

    [Fact]
    public void UltimatePlanTarget_activates_and_undo_returns_to_the_previous_plan()
    {
        var source = StockSchemes(active: Balanced);
        source.Schemes.Add(new PowerSchemeInfo(Created, UltimateRaw, "Ultimate Performance"));
        var calls = new List<string>();
        bool SetPlan(string g) { calls.Add(g); source.Active = g; return true; }
        var target = new UltimatePlanTarget(SetPlan, source);
        Assert.False(((ISafeActionTarget)target).RequiresElevation);
        var svc = new SafeActionService(new SafeActionJournal(null), new ISafeActionTarget[] { target });

        Assert.Equal(SafeActionResult.Applied, svc.Apply(UltimatePlanTarget.Id).Result);
        Assert.Equal(Created, source.Active);
        Assert.Equal(SafeActionResult.NoOp, svc.Apply(UltimatePlanTarget.Id).Result);
        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(UltimatePlanTarget.Key).Result);
        Assert.Equal(Balanced, source.Active);
        Assert.Equal(new[] { Created, Balanced }, calls);
    }

    [Fact]
    public void UltimatePlanTarget_without_scheme_is_SchemeMissing_and_game_session_blocks_it()
    {
        var source = StockSchemes();
        var target = new UltimatePlanTarget(_ => true, source);
        var svc = new SafeActionService(new SafeActionJournal(null), new ISafeActionTarget[] { target });
        Assert.Equal(SafeActionCodes.SchemeMissing, svc.Apply(UltimatePlanTarget.Id).Code);

        source.Schemes.Add(new PowerSchemeInfo(Created, UltimateRaw, null));
        var blocked = new SafeActionService(new SafeActionJournal(null), new ISafeActionTarget[] { target }, () => true);
        Assert.Equal(SafeActionResult.Blocked, blocked.Apply(UltimatePlanTarget.Id).Result);
    }

    // ---------------- HAGS status ----------------

    [Fact]
    public void HagsStatus_reports_pending_restart_when_registry_and_kernel_disagree()
    {
        var hklm = new PcFakeMachineRegistry();
        hklm.Set(HagsTarget.KeyPath, HagsTarget.ValueName, RegValue.Dword(2));
        var s = HagsStatusReader.Read(hklm, new PcFakeGpuProbe(PcFakeGpuProbe.Gpu(supported: true, enabled: false)));
        Assert.True(s.Supported);
        Assert.True(s.RegistryOn);
        Assert.False(s.RunningOn);
        Assert.True(s.PendingRestart);
        Assert.False(s.EffectiveOn);
        Assert.Contains("pendingRestart", s.Evidence());
        Assert.Contains("wddm=3.2", s.Evidence());
    }

    [Fact]
    public void HagsStatus_absent_value_uses_kernel_state_and_ignores_pre_2_7_adapters()
    {
        var hklm = new PcFakeMachineRegistry();
        var s = HagsStatusReader.Read(hklm, new PcFakeGpuProbe(
            PcFakeGpuProbe.Gpu(supported: true, enabled: true, wddm: 3200),
            PcFakeGpuProbe.Gpu(supported: true, enabled: false, wddm: 2600)));
        Assert.Null(s.RegistryOn);
        Assert.True(s.RunningOn);
        Assert.True(s.EffectiveOn);
        Assert.False(s.PendingRestart);
        Assert.Contains("HwSchMode=absent", s.Evidence());

        var old = HagsStatusReader.Read(hklm, new PcFakeGpuProbe(PcFakeGpuProbe.Gpu(supported: true, enabled: true, wddm: 2600)));
        Assert.False(old.Supported);
        Assert.Null(old.RunningOn);

        var unknown = HagsStatusReader.Read(hklm, PcFakeGpuProbe.Unavailable());
        Assert.Null(unknown.Supported);
        Assert.Null(unknown.EffectiveOn);
    }

    [Fact]
    public void HagsItem_from_status_keeps_the_card_id_and_carries_evidence()
    {
        var item = OptimizeStatus.HagsItem(new HagsStatus { RegistryValue = 1, RegistryReadable = true, Supported = true, RunningOn = false, MaxWddmVersion = 2700 });
        Assert.Equal(OptimizeStatus.HagsId, item.Id);
        Assert.False(item.Status);
        Assert.Contains("HwSchMode=1", item.Evidence);
        Assert.Contains("wddm=2.7", item.Evidence);
        Assert.Null(OptimizeStatus.HagsItem(new HagsStatus { Supported = false }).Status);
    }

    // ---------------- HAGS target (elevated, exact undo) ----------------

    [Fact]
    public async Task Hags_on_writes_2_via_broker_and_undo_deletes_the_previously_absent_value()
    {
        var hklm = new PcFakeMachineRegistry();
        var broker = new PcFakeBroker(hklm);
        var probe = new PcFakeGpuProbe(PcFakeGpuProbe.Gpu(supported: true, enabled: false));
        var on = new HagsTarget(true, broker, hklm, probe);
        var off = new HagsTarget(false, broker, hklm, probe);
        Assert.True(on.RequiresElevation);
        Assert.Equal(on.JournalKey, off.JournalKey);
        var svc = new SafeActionService(new SafeActionJournal(null), new ISafeActionTarget[] { on, off });

        Assert.Equal(SafeActionResult.Applied, (await svc.ApplyAsync(HagsTarget.OnId)).Result);
        var op = Assert.Single(Assert.Single(broker.Batches));
        Assert.Equal(ElevatedOpKind.RegSetDword, op.Kind);
        Assert.Equal(ElevationAllowlist.GraphicsDriversKey, op.Target);
        Assert.Equal("HwSchMode", op.Name);
        Assert.Equal("2", op.Value);
        Assert.Equal(2, hklm.Get(HagsTarget.KeyPath, HagsTarget.ValueName)!.AsDword);

        Assert.Equal(SafeActionUndoResult.Undone, (await svc.UndoAsync(HagsTarget.Key)).Result);
        Assert.Equal(ElevatedOpKind.RegDeleteValue, broker.Batches[1][0].Kind);
        Assert.Null(hklm.Get(HagsTarget.KeyPath, HagsTarget.ValueName));
    }

    [Fact]
    public async Task Hags_on_is_a_noop_when_the_kernel_already_runs_it_with_no_registry_value()
    {
        var hklm = new PcFakeMachineRegistry();
        var broker = new PcFakeBroker(hklm);
        var on = new HagsTarget(true, broker, hklm, new PcFakeGpuProbe(PcFakeGpuProbe.Gpu(supported: true, enabled: true)));
        var svc = new SafeActionService(new SafeActionJournal(null), new ISafeActionTarget[] { on });
        Assert.Equal(SafeActionResult.NoOp, (await svc.ApplyAsync(HagsTarget.OnId)).Result);
        Assert.Empty(broker.Batches);
    }

    [Fact]
    public async Task Hags_off_restores_the_exact_previous_dword()
    {
        var hklm = new PcFakeMachineRegistry();
        hklm.Set(HagsTarget.KeyPath, HagsTarget.ValueName, RegValue.Dword(2));
        var broker = new PcFakeBroker(hklm);
        var off = new HagsTarget(false, broker, hklm, new PcFakeGpuProbe(PcFakeGpuProbe.Gpu(supported: true, enabled: true)));
        var svc = new SafeActionService(new SafeActionJournal(null), new ISafeActionTarget[] { off });

        Assert.Equal(SafeActionResult.Applied, (await svc.ApplyAsync(HagsTarget.OffId)).Result);
        Assert.Equal(1, hklm.Get(HagsTarget.KeyPath, HagsTarget.ValueName)!.AsDword);
        Assert.Equal(SafeActionUndoResult.Undone, (await svc.UndoAsync(HagsTarget.OffId)).Result);
        Assert.Equal(2, hklm.Get(HagsTarget.KeyPath, HagsTarget.ValueName)!.AsDword);
    }

    [Fact]
    public void Hags_sync_apply_is_refused_because_it_needs_the_helper()
    {
        var hklm = new PcFakeMachineRegistry();
        var broker = new PcFakeBroker(hklm);
        var on = new HagsTarget(true, broker, hklm, new PcFakeGpuProbe(PcFakeGpuProbe.Gpu(supported: true, enabled: false)));
        var svc = new SafeActionService(new SafeActionJournal(null), new ISafeActionTarget[] { on });
        var r = svc.Apply(HagsTarget.OnId);
        Assert.Equal(SafeActionResult.Failed, r.Result);
        Assert.Equal(SafeActionCodes.NeedsElevation, r.Code);
        Assert.Empty(broker.Batches);
        Assert.Null(hklm.Get(HagsTarget.KeyPath, HagsTarget.ValueName));
    }

    [Fact]
    public void PcTweaks_Create_registers_targets_without_applying_anything()
    {
        var broker = new PcFakeBroker();
        var pc = PcTweaks.Create(null, broker, _ => throw new InvalidOperationException("must not be called"));
        var svc = new SafeActionService(new SafeActionJournal(null), Array.Empty<ISafeActionTarget>());
        pc.Register(svc);
        Assert.Contains(HagsTarget.OnId, svc.ActionIds);
        Assert.Contains(HagsTarget.OffId, svc.ActionIds);
        Assert.Contains(UltimatePlanTarget.Id, svc.ActionIds);
        Assert.Empty(broker.Batches);
        Assert.Empty(svc.Journal.Entries);
    }
}
