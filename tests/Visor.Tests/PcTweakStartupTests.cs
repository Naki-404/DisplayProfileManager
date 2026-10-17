using System.IO;
using Visor.Services;
using Visor.Services.Elevation;
using Visor.Services.Optimize;
using Visor.Services.Optimize.Pc;
using Xunit;

namespace Visor.Tests;

public class PcTweakStartupTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string UserRunApproved = StartupApproved.ApprovedKey(StartupLocation.UserRun);
    private static readonly string UserFolderApproved = StartupApproved.ApprovedKey(StartupLocation.UserStartupFolder);
    private static readonly string MachineRunApproved = StartupApproved.ApprovedKey(StartupLocation.MachineRun);

    // ---------------- StartupApproved bytes ----------------

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("020000000000000000000000", true)]
    [InlineData("060000000000000000000000", true)]
    [InlineData("03000000A8FBAD861D81DC01", false)] // real HKCU value written by Task Manager
    [InlineData("07000000E8DE3A1FBC82DC01", false)] // real HKLM SecurityHealth value
    [InlineData("01", false)]
    public void StartupApproved_low_bit_means_disabled(string? hex, bool enabled)
    {
        byte[]? bytes = hex == null ? null : Convert.FromHexString(hex);
        Assert.Equal(enabled, StartupApproved.IsEnabled(bytes));
    }

    [Fact]
    public void StartupApproved_absent_and_foreign_kinds_are_enabled()
    {
        Assert.True(StartupApproved.IsEnabled((RegValue?)null));
        Assert.True(StartupApproved.IsEnabled(RegValue.Dword(3)));
        Assert.False(StartupApproved.IsEnabled(RegValue.Binary(new byte[] { 3, 0, 0, 0 })));
    }

    [Fact]
    public void EnabledBytes_and_DisabledBytes_match_task_manager_layout()
    {
        Assert.Equal("020000000000000000000000", Convert.ToHexString(StartupApproved.EnabledBytes()));
        var d = StartupApproved.DisabledBytes(Now);
        Assert.Equal(12, d.Length);
        Assert.Equal(new byte[] { 3, 0, 0, 0 }, d[..4]);
        Assert.Equal(Now, StartupApproved.DisabledAtUtc(d));
        Assert.Null(StartupApproved.DisabledAtUtc(StartupApproved.EnabledBytes()));
    }

    // ---------------- action ids ----------------

    [Theory]
    [InlineData(true, StartupLocation.UserRun, "Discord")]
    [InlineData(false, StartupLocation.MachineRun, "Riot Vanguard")]
    [InlineData(false, StartupLocation.UserStartupFolder, "odd|name.lnk")]
    public void ActionId_round_trips(bool enable, StartupLocation loc, string name)
    {
        string id = StartupToggle.ActionIdFor(enable, loc, name);
        Assert.True(StartupToggle.TryParseActionId(id, out bool e, out var l, out var n));
        Assert.Equal(enable, e);
        Assert.Equal(loc, l);
        Assert.Equal(name, n);
        Assert.StartsWith(StartupToggle.JournalPrefix, StartupToggle.JournalKeyFor(loc, name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("startup.disable|")]
    [InlineData("startup.disable|UserRun|")]
    [InlineData("startup.disable|Nowhere|x")]
    [InlineData("startup.disable|7|x")]
    [InlineData("gameMode.on")]
    public void ActionId_rejects_garbage(string? id) =>
        Assert.False(StartupToggle.TryParseActionId(id, out _, out _, out _));

    // ---------------- command parsing ----------------

    [Theory]
    [InlineData("\"D:\\Steam\\steam.exe\" -silent", "D:\\Steam\\steam.exe")]
    [InlineData("D:\\Riot Games\\Riot Client\\RiotClientServices.exe --launch-background-mode", "D:\\Riot Games\\Riot Client\\RiotClientServices.exe")]
    [InlineData("C:\\WINDOWS\\system32\\SecurityHealthSystray.exe", "C:\\WINDOWS\\system32\\SecurityHealthSystray.exe")]
    [InlineData("rundll32.exe C:\\x\\y.dll,Entry", "rundll32.exe")]
    [InlineData("C:\\tools\\run.bat /q", "C:\\tools\\run.bat")]
    [InlineData("  \"C:\\a b\\c.exe\"", "C:\\a b\\c.exe")]
    public void ExtractExecutable_handles_quoted_and_unquoted_commands(string command, string expected) =>
        Assert.Equal(expected, StartupCommand.ExtractExecutable(command, s => s));

    [Fact]
    public void ExtractExecutable_expands_environment_variables_through_the_seam()
    {
        string? exe = StartupCommand.ExtractExecutable("%LOCALAPPDATA%\\Discord\\Update.exe --processStart Discord.exe",
            s => s.Replace("%LOCALAPPDATA%", "C:\\Users\\U\\AppData\\Local"));
        Assert.Equal("C:\\Users\\U\\AppData\\Local\\Discord\\Update.exe", exe);
        Assert.Null(StartupCommand.ExtractExecutable("   ", s => s));
    }

    [Theory]
    [InlineData("C:\\x.exe", true)]
    [InlineData("\\\\srv\\share\\x.exe", false)]
    [InlineData("x.exe", false)]
    [InlineData("C:x.exe", false)]
    [InlineData("C:\\x.exe:ads", false)]
    public void IsLocalAbsolute_rejects_network_and_relative_paths(string path, bool ok) =>
        Assert.Equal(ok, StartupCommand.IsLocalAbsolute(path));

    // ---------------- listing ----------------

    private static (StartupAppsService Svc, PcFakeStartupReader Reader, PcFakeFileProbe Probe) Fixture(TempDir dir, IElevationBroker? broker = null,
        FakeOptimizeRegistry? hkcu = null, PcFakeMachineRegistry? hklm = null)
    {
        var reader = new PcFakeStartupReader()
            .Add(false, StartupApproved.UserRunKey, "Steam", RegValue.String("\"D:\\Steam\\steam.exe\" -silent"))
            .Add(false, StartupApproved.UserRunKey, "Discord", RegValue.String("\"C:\\U\\Discord\\Update.exe\" --processStart Discord.exe"))
            .Add(false, UserRunApproved, "Steam", RegValue.Binary(Convert.FromHexString("03000000F723248A1D81DC01")))
            .Add(true, StartupApproved.MachineRunKey, "SecurityHealth", RegValue.String("C:\\WINDOWS\\system32\\SecurityHealthSystray.exe"))
            .Add(true, MachineRunApproved, "SecurityHealth", RegValue.Binary(Convert.FromHexString("07000000E8DE3A1FBC82DC01")))
            .Add(true, StartupApproved.MachineRun32Key, "SunJavaUpdateSched", RegValue.String("\"C:\\Java\\jusched.exe\""));
        var probe = new PcFakeFileProbe();
        probe.Files["D:\\Steam\\steam.exe"] = (4_500_000, "Valve Corporation");
        probe.Files["C:\\U\\Discord\\Update.exe"] = (1_200_000, "GitHub");
        var user = Path.Combine(dir.Path, "user");
        var common = Path.Combine(dir.Path, "common");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(common);
        var lnk = Path.Combine(user, "AutoResolution.lnk");
        File.WriteAllText(lnk, "not a real link — the fake probe resolves it");
        File.WriteAllText(Path.Combine(user, "desktop.ini"), "[.ShellClassInfo]");
        File.WriteAllText(Path.Combine(common, "tool.bat"), "@echo off");
        probe.Links[lnk] = ("C:\\Tools\\autores.exe", "/fast");
        probe.Files["C:\\Tools\\autores.exe"] = (64_000, null);
        var svc = new StartupAppsService(reader, probe, user, common, hkcu ?? new FakeOptimizeRegistry(), broker, hklm, () => Now);
        return (svc, reader, probe);
    }

    [Fact]
    public void List_reads_every_source_with_state_publisher_and_size()
    {
        using var dir = new TempDir();
        var (svc, _, _) = Fixture(dir, broker: new PcFakeBroker());
        var list = svc.List();

        Assert.Equal(6, list.Count);
        var steam = list.Single(e => e.Name == "Steam");
        Assert.False(steam.Enabled);
        Assert.Equal("03000000F723248A1D81DC01", steam.ApprovedHex);
        Assert.Equal("Valve Corporation", steam.Publisher);
        Assert.Equal(4_500_000, steam.FileSizeBytes);
        Assert.True(steam.CanToggle);
        Assert.False(steam.RequiresElevation);

        var discord = list.Single(e => e.Name == "Discord");
        Assert.True(discord.Enabled);           // absent StartupApproved value
        Assert.Null(discord.ApprovedHex);

        var health = list.Single(e => e.Name == "SecurityHealth");
        Assert.Equal(StartupLocation.MachineRun, health.Location);
        Assert.False(health.Enabled);
        Assert.True(health.RequiresElevation);
        Assert.True(health.CanToggle);

        var java = list.Single(e => e.Name == "SunJavaUpdateSched");
        Assert.Equal(StartupToggleBlock.NotInHelperAllowlist, java.ToggleBlock);
        Assert.Null(java.FileSizeBytes);        // probe knows nothing → no invented size

        var link = list.Single(e => e.Location == StartupLocation.UserStartupFolder);
        Assert.Equal("AutoResolution.lnk", link.Name);
        Assert.Equal("AutoResolution", link.DisplayName);
        Assert.Equal("C:\\Tools\\autores.exe", link.ExecutablePath);
        Assert.Contains("/fast", link.Command);
        Assert.Equal(64_000, link.FileSizeBytes);

        var bat = list.Single(e => e.Location == StartupLocation.CommonStartupFolder);
        Assert.Equal(StartupToggleBlock.NotInHelperAllowlist, bat.ToggleBlock);
        Assert.DoesNotContain(list, e => e.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase));

        var summary = StartupAppsService.Summarize(list);
        Assert.Equal(6, summary.Total);
        Assert.Equal(4, summary.Enabled);
    }

    [Fact]
    public void HKLM_entries_are_read_only_without_a_broker()
    {
        using var dir = new TempDir();
        var (svc, _, _) = Fixture(dir, broker: null);
        var health = svc.List().Single(e => e.Name == "SecurityHealth");
        Assert.Equal(StartupToggleBlock.NoBroker, health.ToggleBlock);
        Assert.Null(svc.CreateTarget(health.EnableActionId));
        Assert.Null(svc.CreateTarget(StartupToggle.ActionIdFor(false, StartupLocation.MachineRun32, "x")));
        Assert.Null(svc.CreateTarget(StartupToggle.ActionIdFor(false, StartupLocation.CommonStartupFolder, "x.lnk")));
        Assert.Null(svc.CreateTarget("nonsense"));
    }

    // ---------------- HKCU toggles (journaled, exact undo) ----------------

    [Fact]
    public void Disable_absent_value_then_undo_deletes_it_again()
    {
        var reg = new FakeOptimizeRegistry();
        var addr = new RegAddress(UserRunApproved, "Discord");
        var target = new StartupToggleTarget(false, StartupLocation.UserRun, "Discord", reg, () => Now);
        var svc = new SafeActionService(new SafeActionJournal(null), new ISafeActionTarget[] { target });

        Assert.Equal(SafeActionResult.Applied, svc.Apply(target.ActionId).Result);
        var written = reg.Get(addr)!;
        Assert.Equal(RegValueKind.Binary, written.Kind);
        Assert.Equal(Convert.ToHexString(StartupApproved.DisabledBytes(Now)), written.Data);
        Assert.Single(reg.Values); // only the StartupApproved value — the Run value is never touched

        Assert.Equal(SafeActionResult.NoOp, svc.Apply(target.ActionId).Result);
        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(target.ActionId).Result);
        Assert.False(reg.Has(addr));
        Assert.False(svc.CanUndo(target.JournalKey));
    }

    [Fact]
    public void Enable_disabled_entry_and_undo_restores_the_exact_original_bytes()
    {
        var reg = new FakeOptimizeRegistry();
        var addr = new RegAddress(UserRunApproved, "Steam");
        reg.Set(addr, RegValue.Binary(Convert.FromHexString("03000000F723248A1D81DC01")));
        var on = new StartupToggleTarget(true, StartupLocation.UserRun, "Steam", reg, () => Now);
        var off = new StartupToggleTarget(false, StartupLocation.UserRun, "Steam", reg, () => Now);
        Assert.Equal(on.JournalKey, off.JournalKey);
        Assert.Equal("optimize.startup", on.LocPrefix);
        var svc = new SafeActionService(new SafeActionJournal(null), new ISafeActionTarget[] { on, off });

        Assert.Equal(SafeActionResult.Applied, svc.Apply(on.ActionId).Result);
        Assert.Equal("020000000000000000000000", reg.Get(addr)!.Data);
        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(off.ActionId).Result); // shared journal slot
        Assert.Equal("03000000F723248A1D81DC01", reg.Get(addr)!.Data);
    }

    [Fact]
    public void StartupFolder_toggle_uses_the_StartupFolder_approval_key()
    {
        var reg = new FakeOptimizeRegistry();
        var target = new StartupToggleTarget(false, StartupLocation.UserStartupFolder, "AutoResolution.lnk", reg, () => Now);
        var svc = new SafeActionService(new SafeActionJournal(null), new ISafeActionTarget[] { target });
        Assert.Equal(SafeActionResult.Applied, svc.Apply(target.ActionId).Result);
        Assert.True(reg.Has(new RegAddress(UserFolderApproved, "AutoResolution.lnk")));
    }

    [Fact]
    public void Undo_after_restart_works_through_RegisterJournaled()
    {
        using var dir = new TempDir();
        var reg = new FakeOptimizeRegistry();
        var (svcA, _, _) = Fixture(dir, broker: null, hkcu: reg);
        var safeA = new SafeActionService(new SafeActionJournal(dir.Path), Array.Empty<ISafeActionTarget>());
        var discord = svcA.List().Single(e => e.Name == "Discord");
        svcA.Register(safeA, new[] { discord });
        Assert.Equal(SafeActionResult.Applied, safeA.Apply(discord.DisableActionId).Result);

        // "Restart": fresh SafeActionService over the same journal file, nothing registered yet.
        var safeB = new SafeActionService(new SafeActionJournal(dir.Path), Array.Empty<ISafeActionTarget>());
        Assert.Equal(SafeActionUndoResult.Failed, safeB.Undo(discord.JournalKey).Result);
        var (svcB, _, _) = Fixture(dir, broker: null, hkcu: reg);
        Assert.Equal(1, svcB.RegisterJournaled(safeB));
        Assert.Equal(SafeActionUndoResult.Undone, safeB.Undo(discord.JournalKey).Result);
        Assert.False(reg.Has(new RegAddress(UserRunApproved, "Discord")));
    }

    // ---------------- HKLM toggles through the elevated helper ----------------

    [Fact]
    public async Task HKLM_disable_goes_through_the_broker_with_an_allowlisted_binary_op_and_undoes_exactly()
    {
        var hklm = new PcFakeMachineRegistry();
        var broker = new PcFakeBroker(hklm);
        var target = new ElevatedStartupToggleTarget(false, "Riot Vanguard", broker, hklm, () => Now);
        Assert.True(target.RequiresElevation);
        var svc = new SafeActionService(new SafeActionJournal(null), new ISafeActionTarget[] { target });

        Assert.Equal(SafeActionResult.Applied, (await svc.ApplyAsync(target.ActionId)).Result);
        var op = Assert.Single(Assert.Single(broker.Batches));
        Assert.Equal(ElevatedOpKind.RegSetBinary, op.Kind);
        Assert.Equal(ElevationAllowlist.StartupApprovedRunKey, op.Target);
        Assert.Equal("Riot Vanguard", op.Name);
        Assert.Equal(Convert.ToHexString(StartupApproved.DisabledBytes(Now)), op.Value);
        Assert.Null(ElevationAllowlist.ValidateBatch(broker.Batches[0], _ => true));

        Assert.Equal(SafeActionUndoResult.Undone, (await svc.UndoAsync(target.ActionId)).Result);
        Assert.Equal(ElevatedOpKind.RegDeleteValue, broker.Batches[1][0].Kind);
        Assert.Null(hklm.Get(ElevationAllowlist.StartupApprovedRunKey, "Riot Vanguard"));
    }

    [Fact]
    public async Task HKLM_toggle_declined_UAC_changes_and_journals_nothing()
    {
        var hklm = new PcFakeMachineRegistry();
        var broker = new PcFakeBroker(hklm) { Outcome = ElevationOutcome.Declined };
        var target = new ElevatedStartupToggleTarget(false, "Bdagent", broker, hklm, () => Now);
        var svc = new SafeActionService(new SafeActionJournal(null), new ISafeActionTarget[] { target });

        var r = await svc.ApplyAsync(target.ActionId);
        Assert.Equal(SafeActionResult.Failed, r.Result);
        Assert.Equal(SafeActionCodes.ElevationDeclined, r.Code);
        Assert.Empty(svc.Journal.Entries);
        Assert.Empty(hklm.Values);
    }
}
