using System.Net;
using Visor.Services;
using Visor.Services.Elevation;
using Visor.Services.NetworkTweaks;
using Visor.Services.Optimize;
using Xunit;

namespace Visor.Tests;

public class NetTweakTargetsTests
{
    private const string Iface = "{3F2504E0-4F89-11D3-9A0C-0305E82C3301}";
    private static readonly string IfaceKey = ElevationAllowlist.TcpipInterfacesKey + "\\" + Iface;
    private const string Mmcss = ElevationAllowlist.MultimediaSystemProfileKey;

    private sealed class Rig
    {
        public readonly FakeMachineRegistry Reg = new();
        public readonly FakeExecutor Exec;
        public readonly FakeBroker Broker;
        public readonly FakeTcp Tcp;
        public int Flushes;
        public readonly SafeActionService Svc;

        public Rig(Func<bool>? session = null)
        {
            Reg.AddKey(IfaceKey).AddKey(Mmcss);
            Exec = new FakeExecutor(Reg);
            Broker = new FakeBroker(Exec);
            Tcp = new FakeTcp(Exec);
            Svc = new SafeActionService(new SafeActionJournal(null),
                NetworkTweakCatalog.CreateStatic(Broker, Reg, Tcp), session,
                id => NetworkTweakCatalog.Resolve(id, Broker, Reg, () => { Flushes++; return true; }));
        }
    }

    /// <summary>TCP globals backed by the fake executor's auto-tuning level.</summary>
    private sealed class FakeTcp : ITcpGlobalReader
    {
        private readonly FakeExecutor _exec;
        public bool Fail;
        public FakeTcp(FakeExecutor exec) => _exec = exec;
        public TcpGlobalInfo? Read() => Fail ? null : new TcpGlobalInfo(_exec.AutoTuning, "disabled", "enabled", "default");
    }

    private static string NagleId => NetworkTweakCatalog.NagleActionId(Iface);
    private static string DnsId(params string[] servers) =>
        NetworkTweakCatalog.DnsActionId(Iface, servers.Select(IPAddress.Parse));

    // ---------------- shared elevation contract ----------------

    [Fact]
    public void Network_targets_require_elevation_and_sync_paths_refuse_them()
    {
        var rig = new Rig();
        foreach (var id in new[] { NagleId, NetworkTweakCatalog.ThrottlingId, NetworkTweakCatalog.AutotuningId, DnsId("1.1.1.1") })
        {
            Assert.True(rig.Svc.FindTarget(id)!.RequiresElevation, id);
            var r = rig.Svc.Apply(id);
            Assert.Equal(SafeActionResult.Failed, r.Result);
            Assert.Equal(SafeActionCodes.NeedsElevation, r.Code);
        }
        Assert.Empty(rig.Broker.Batches);
    }

    [Fact]
    public void Loc_prefixes_are_stable()
    {
        var rig = new Rig();
        Assert.Equal("net.tweak.nagle.title", rig.Svc.FindTarget(NagleId)!.TitleKey);
        Assert.Equal("net.tweak.mmcss.why", rig.Svc.FindTarget(NetworkTweakCatalog.ThrottlingId)!.WhyKey);
        Assert.Equal("net.tweak.autotuning.risk", rig.Svc.FindTarget(NetworkTweakCatalog.AutotuningId)!.RiskKey);
        Assert.Equal("net.tweak.dns.undo", rig.Svc.FindTarget(DnsId("1.1.1.1"))!.UndoKey);
    }

    [Fact]
    public async Task Non_elevated_targets_work_through_ApplyAsync_too()
    {
        var reg = new FakeOptimizeRegistry();
        var svc = new SafeActionService(new SafeActionJournal(null), new ISafeActionTarget[] { new GameBarSafeActionTarget(reg) });
        Assert.Equal(SafeActionResult.Applied, (await svc.ApplyAsync(GameBarSafeActionTarget.Id)).Result);
        Assert.Equal(SafeActionUndoResult.Undone, (await svc.UndoAsync(GameBarSafeActionTarget.Id)).Result);
        Assert.Empty(reg.Values);
    }

    // ---------------- Nagle ----------------

    [Fact]
    public async Task Nagle_apply_writes_both_values_in_one_batch_and_undo_deletes_absent_ones()
    {
        var rig = new Rig();
        var r = await rig.Svc.ApplyAsync(NagleId);
        Assert.Equal(SafeActionResult.Applied, r.Result);
        Assert.Single(rig.Broker.Batches);                           // one UAC prompt
        Assert.Equal(2, rig.Broker.Batches[0].Count);
        Assert.Equal(1, rig.Reg.Get(IfaceKey, "TcpAckFrequency")!.AsDword);
        Assert.Equal(1, rig.Reg.Get(IfaceKey, "TCPNoDelay")!.AsDword);

        var entry = rig.Svc.Journal.Get(NagleId)!;
        Assert.True(RegistryValueSet.TryParse(entry.PreviousState, out var prev));
        Assert.All(prev.Items, kv => Assert.Null(kv.Value));

        Assert.Equal(SafeActionResult.NoOp, (await rig.Svc.ApplyAsync(NagleId)).Result);
        Assert.Single(rig.Broker.Batches);                           // second click: no prompt

        Assert.Equal(SafeActionUndoResult.Undone, (await rig.Svc.UndoAsync(NagleId)).Result);
        Assert.Null(rig.Reg.Get(IfaceKey, "TcpAckFrequency"));
        Assert.Null(rig.Reg.Get(IfaceKey, "TCPNoDelay"));
        Assert.All(rig.Broker.Batches[1], op => Assert.Equal(ElevatedOpKind.RegDeleteValue, op.Kind));
        Assert.False(rig.Svc.CanUndo(NagleId));
    }

    [Fact]
    public async Task Nagle_undo_restores_exact_previous_values()
    {
        var rig = new Rig();
        rig.Reg.Set(IfaceKey, "TcpAckFrequency", RegValue.Dword(2));      // something else set it earlier
        Assert.Equal(SafeActionResult.Applied, (await rig.Svc.ApplyAsync(NagleId)).Result);
        Assert.Equal(SafeActionUndoResult.Undone, (await rig.Svc.UndoAsync(NagleId)).Result);
        Assert.Equal(2, rig.Reg.Get(IfaceKey, "TcpAckFrequency")!.AsDword);
        Assert.Null(rig.Reg.Get(IfaceKey, "TCPNoDelay"));
    }

    [Fact]
    public async Task Nagle_on_missing_interface_key_is_unavailable_without_prompt()
    {
        var rig = new Rig();
        rig.Reg.Keys.Remove(IfaceKey);
        var r = await rig.Svc.ApplyAsync(NagleId);
        Assert.Equal(SafeActionCodes.Unavailable, r.Code);
        Assert.Empty(rig.Broker.Batches);
    }

    [Fact]
    public async Task Declined_uac_changes_nothing_and_journals_nothing()
    {
        var rig = new Rig();
        rig.Broker.ForceOutcome = ElevationOutcome.Declined;
        var r = await rig.Svc.ApplyAsync(NagleId);
        Assert.Equal(SafeActionResult.Failed, r.Result);
        Assert.Equal(SafeActionCodes.ElevationDeclined, r.Code);
        Assert.False(rig.Svc.CanUndo(NagleId));
        Assert.Empty(rig.Exec.Executed);
    }

    [Fact]
    public async Task Failed_or_rejected_helper_maps_to_codes()
    {
        var rig = new Rig();
        rig.Broker.ForceOutcome = ElevationOutcome.Failed;
        Assert.Equal(SafeActionCodes.ElevationFailed, (await rig.Svc.ApplyAsync(NagleId)).Code);
        rig.Broker.ForceOutcome = ElevationOutcome.Rejected;
        Assert.Equal(SafeActionCodes.ElevationRejected, (await rig.Svc.ApplyAsync(NagleId)).Code);
    }

    [Fact]
    public async Task Write_that_does_not_stick_is_verify_mismatch()
    {
        var rig = new Rig();
        rig.Exec.IgnoreWrites = true;
        var r = await rig.Svc.ApplyAsync(NagleId);
        Assert.Equal(SafeActionCodes.VerifyMismatch, r.Code);
        Assert.False(rig.Svc.CanUndo(NagleId));
    }

    [Fact]
    public async Task Undo_detects_outside_change_as_conflict()
    {
        var rig = new Rig();
        await rig.Svc.ApplyAsync(NagleId);
        rig.Reg.Set(IfaceKey, "TCPNoDelay", RegValue.Dword(0));          // user / other tool changed it
        var u = await rig.Svc.UndoAsync(NagleId);
        Assert.Equal(SafeActionUndoResult.Conflict, u.Result);
        Assert.Single(rig.Broker.Batches);
        Assert.Equal(SafeActionUndoResult.Undone, (await rig.Svc.UndoAsync(NagleId, confirmedConflict: true)).Result);
        Assert.Null(rig.Reg.Get(IfaceKey, "TCPNoDelay"));
    }

    [Fact]
    public void Nagle_action_id_parsing()
    {
        Assert.True(NagleSafeActionTarget.TryParseActionId("tcp.nagle.off|" + Iface.ToLowerInvariant(), out var g));
        Assert.Equal(Iface, g);
        Assert.False(NagleSafeActionTarget.TryParseActionId("tcp.nagle.off|Ethernet", out _));
        Assert.False(NagleSafeActionTarget.TryParseActionId("tcp.nagle.off|" + Iface + @"\..\x", out _));
        Assert.Null(NetworkTweakCatalog.Resolve("gameMode.on", new FakeBroker(new FakeExecutor())));
    }

    // ---------------- MMCSS network throttling ----------------

    [Fact]
    public async Task Throttling_apply_sets_ffffffff_and_10_and_undo_restores_defaults_exactly()
    {
        var rig = new Rig();
        rig.Reg.Set(Mmcss, "NetworkThrottlingIndex", RegValue.Dword(10));
        rig.Reg.Set(Mmcss, "SystemResponsiveness", RegValue.Dword(20));

        Assert.Equal(SafeActionResult.Applied, (await rig.Svc.ApplyAsync(NetworkTweakCatalog.ThrottlingId)).Result);
        Assert.Equal(-1, rig.Reg.Get(Mmcss, "NetworkThrottlingIndex")!.AsDword);
        Assert.Equal(10, rig.Reg.Get(Mmcss, "SystemResponsiveness")!.AsDword);
        Assert.Equal("4294967295", rig.Broker.Batches[0][0].Value);         // unsigned on the wire

        Assert.Equal(SafeActionUndoResult.Undone, (await rig.Svc.UndoAsync(NetworkTweakCatalog.ThrottlingId)).Result);
        Assert.Equal(10, rig.Reg.Get(Mmcss, "NetworkThrottlingIndex")!.AsDword);
        Assert.Equal(20, rig.Reg.Get(Mmcss, "SystemResponsiveness")!.AsDword);
    }

    [Fact]
    public async Task Throttling_keeps_a_lower_responsiveness_and_is_noop_when_done()
    {
        var rig = new Rig();
        rig.Reg.Set(Mmcss, "NetworkThrottlingIndex", RegValue.Dword(-1));
        rig.Reg.Set(Mmcss, "SystemResponsiveness", RegValue.Dword(0));      // Windows treats 0 as 10
        Assert.True(rig.Svc.IsAtTarget(NetworkTweakCatalog.ThrottlingId));
        Assert.Equal(SafeActionResult.NoOp, (await rig.Svc.ApplyAsync(NetworkTweakCatalog.ThrottlingId)).Result);

        rig.Reg.Set(Mmcss, "NetworkThrottlingIndex", RegValue.Dword(10));
        Assert.Equal(SafeActionResult.Applied, (await rig.Svc.ApplyAsync(NetworkTweakCatalog.ThrottlingId)).Result);
        Assert.Equal(0, rig.Reg.Get(Mmcss, "SystemResponsiveness")!.AsDword);   // not raised to 10
    }

    [Fact]
    public void Throttling_state_rejects_foreign_journal_and_reads_absent_values()
    {
        var rig = new Rig();
        var t = rig.Svc.FindTarget(NetworkTweakCatalog.ThrottlingId)!;
        string? state = t.ReadState();
        Assert.NotNull(state);
        Assert.True(RegistryValueSet.TryParse(state, out var set));
        Assert.All(set.Items, kv => Assert.Null(kv.Value));
        var nagleState = rig.Svc.FindTarget(NagleId)!.ReadState()!;
        Assert.False(t.StatesEqual(nagleState, nagleState));     // another target's journal is not ours
        rig.Reg.FailReads = true;
        Assert.Null(t.ReadState());
    }

    // ---------------- TCP auto-tuning ----------------

    [Fact]
    public async Task Autotuning_restores_normal_and_undo_sets_previous_level()
    {
        var rig = new Rig();
        rig.Exec.AutoTuning = "highlyrestricted";
        Assert.Equal(SafeActionResult.Applied, (await rig.Svc.ApplyAsync(NetworkTweakCatalog.AutotuningId)).Result);
        Assert.Equal("normal", rig.Exec.AutoTuning);
        Assert.Equal(new ElevatedOp(ElevatedOpKind.TcpGlobalSet, "autotuninglevel", null, "normal"), rig.Broker.Batches[0].Single());
        Assert.Equal("highlyrestricted", rig.Svc.Journal.Get(NetworkTweakCatalog.AutotuningId)!.PreviousState);

        Assert.Equal(SafeActionUndoResult.Undone, (await rig.Svc.UndoAsync(NetworkTweakCatalog.AutotuningId)).Result);
        Assert.Equal("highlyrestricted", rig.Exec.AutoTuning);
    }

    [Fact]
    public async Task Autotuning_normal_is_noop_experimental_and_unknown_are_unavailable()
    {
        var rig = new Rig();
        rig.Exec.AutoTuning = "normal";
        Assert.Equal(SafeActionResult.NoOp, (await rig.Svc.ApplyAsync(NetworkTweakCatalog.AutotuningId)).Result);
        rig.Exec.AutoTuning = "experimental";
        Assert.Equal(SafeActionCodes.Unavailable, (await rig.Svc.ApplyAsync(NetworkTweakCatalog.AutotuningId)).Code);
        rig.Tcp.Fail = true;
        Assert.Equal(SafeActionCodes.Unavailable, (await rig.Svc.ApplyAsync(NetworkTweakCatalog.AutotuningId)).Code);
        Assert.Empty(rig.Broker.Batches);
    }

    // ---------------- DNS ----------------

    [Fact]
    public async Task Dns_from_dhcp_to_static_and_back()
    {
        var rig = new Rig();                                                  // NameServer absent = DHCP
        string id = DnsId("1.1.1.1", "1.0.0.1");
        Assert.Equal(SafeActionResult.Applied, (await rig.Svc.ApplyAsync(id)).Result);
        Assert.Equal("1.1.1.1,1.0.0.1", rig.Reg.Get(IfaceKey, "NameServer")!.Data);
        Assert.Equal(new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, null, "1.1.1.1,1.0.0.1"), rig.Broker.Batches[0].Single());
        Assert.Equal(1, rig.Flushes);

        var entry = rig.Svc.Journal.Get("dns|" + Iface)!;
        Assert.Equal("dhcp", entry.PreviousState);
        Assert.Equal("static:1.1.1.1,1.0.0.1", entry.TargetState);

        Assert.Equal(SafeActionUndoResult.Undone, (await rig.Svc.UndoAsync(id)).Result);
        Assert.Equal(new ElevatedOp(ElevatedOpKind.DnsSetDhcp, Iface), rig.Broker.Batches[1].Single());
        Assert.Equal("", rig.Reg.Get(IfaceKey, "NameServer")!.Data);        // empty NameServer = DHCP again
        Assert.Equal(2, rig.Flushes);
    }

    [Fact]
    public async Task Dns_switching_twice_keeps_the_original_for_undo()
    {
        var rig = new Rig();
        rig.Reg.Set(IfaceKey, "NameServer", RegValue.String("192.168.1.53"));   // user's own static DNS
        Assert.Equal(SafeActionResult.Applied, (await rig.Svc.ApplyAsync(DnsId("1.1.1.1", "1.0.0.1"))).Result);
        Assert.Equal(SafeActionResult.Applied, (await rig.Svc.ApplyAsync(DnsId("8.8.8.8", "8.8.4.4"))).Result);

        var entry = rig.Svc.Journal.Get("dns|" + Iface)!;
        Assert.Equal("static:192.168.1.53", entry.PreviousState);
        Assert.Equal("static:8.8.8.8,8.8.4.4", entry.TargetState);
        Assert.True(rig.Svc.CanUndo(DnsId("1.1.1.1", "1.0.0.1")));          // same slot, any server choice

        Assert.Equal(SafeActionUndoResult.Undone, (await rig.Svc.UndoAsync("dns|" + Iface)).Result);
        Assert.Equal("192.168.1.53", rig.Reg.Get(IfaceKey, "NameServer")!.Data);
        Assert.Null(rig.Svc.Journal.Get("dns|" + Iface));
    }

    [Fact]
    public async Task Dns_switching_back_to_the_original_clears_the_undo_slot()
    {
        var rig = new Rig();
        rig.Reg.Set(IfaceKey, "NameServer", RegValue.String("9.9.9.9"));
        await rig.Svc.ApplyAsync(DnsId("1.1.1.1"));
        Assert.Equal(SafeActionResult.Applied, (await rig.Svc.ApplyAsync(DnsId("9.9.9.9"))).Result);
        Assert.Null(rig.Svc.Journal.Get("dns|" + Iface));
    }

    [Fact]
    public async Task Dns_unrestorable_previous_value_blocks_apply()
    {
        var rig = new Rig();
        rig.Reg.Set(IfaceKey, "NameServer", RegValue.String("1.1.1.1,garbage"));
        var r = await rig.Svc.ApplyAsync(DnsId("8.8.8.8"));
        Assert.Equal(SafeActionCodes.Unavailable, r.Code);
        Assert.Empty(rig.Broker.Batches);

        rig.Reg.Set(IfaceKey, "NameServer", RegValue.Dword(1));            // wrong kind
        Assert.Equal(SafeActionCodes.Unavailable, (await rig.Svc.ApplyAsync(DnsId("8.8.8.8"))).Code);
    }

    [Fact]
    public async Task Dns_unknown_adapter_is_rejected_by_the_allowlist()
    {
        var rig = new Rig();
        rig.Broker.AdapterExists = _ => false;
        var r = await rig.Svc.ApplyAsync(DnsId("8.8.8.8"));
        Assert.Equal(SafeActionCodes.ElevationRejected, r.Code);
        Assert.Empty(rig.Exec.Executed);
        Assert.Equal(0, rig.Flushes);
    }

    [Fact]
    public void Dns_target_constructor_validates_inputs()
    {
        var broker = new FakeBroker(new FakeExecutor());
        Assert.Throws<ArgumentException>(() => new DnsSafeActionTarget("Ethernet", new[] { IPAddress.Parse("1.1.1.1") }, broker));
        Assert.Throws<ArgumentException>(() => new DnsSafeActionTarget(Iface, Array.Empty<IPAddress>(), broker));
        Assert.Throws<ArgumentException>(() => new DnsSafeActionTarget(Iface, new[] { IPAddress.IPv6Loopback }, broker));
        Assert.Throws<ArgumentException>(() => new NagleSafeActionTarget(@"{x}\..", broker));
    }

    [Fact]
    public async Task Session_block_applies_only_to_targets_that_ask_for_it()
    {
        var rig = new Rig(session: () => true);
        Assert.Equal(SafeActionResult.Applied, (await rig.Svc.ApplyAsync(NagleId)).Result);   // network tweaks are not session-owned
    }
}
