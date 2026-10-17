using System.ComponentModel;
using System.Globalization;
using System.IO;
using Visor.Services.Elevation;
using Visor.Services.Optimize;
using Xunit;

namespace Visor.Tests;

/// <summary>Dictionary-backed read-only HKLM fake (the network targets never touch the real hive in tests).</summary>
internal sealed class FakeMachineRegistry : IMachineRegistryReader
{
    public readonly Dictionary<string, RegValue> Values = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase);
    public bool FailReads;

    private static string K(string keyPath, string valueName) => keyPath + "\\\\" + valueName;

    public FakeMachineRegistry AddKey(string keyPath) { Keys.Add(keyPath); return this; }
    public FakeMachineRegistry Set(string keyPath, string name, RegValue v) { Keys.Add(keyPath); Values[K(keyPath, name)] = v; return this; }
    public void Delete(string keyPath, string name) => Values.Remove(K(keyPath, name));
    public RegValue? Get(string keyPath, string name) => Values.TryGetValue(K(keyPath, name), out var v) ? v : null;

    public RegValue? GetValue(string keyPath, string valueName)
    {
        if (FailReads) throw new UnauthorizedAccessException("read");
        return Get(keyPath, valueName);
    }

    public bool KeyExists(string keyPath)
    {
        if (FailReads) throw new UnauthorizedAccessException("read");
        return Keys.Contains(keyPath);
    }
}

/// <summary>Executes validated ops against the fakes (what the real helper does against Windows).</summary>
internal sealed class FakeExecutor : IElevatedExecutor
{
    private readonly FakeMachineRegistry _reg;
    public readonly List<ElevatedOp> Executed = new();
    public string AutoTuning = "disabled";
    /// <summary>Op kinds that report failure without changing anything.</summary>
    public readonly HashSet<ElevatedOpKind> FailKinds = new();
    /// <summary>Writes "succeed" but do not stick (policy).</summary>
    public bool IgnoreWrites;

    public FakeExecutor(FakeMachineRegistry? reg = null) => _reg = reg ?? new FakeMachineRegistry();

    public ElevatedOpResult Execute(ElevatedOp op)
    {
        Executed.Add(op);
        if (FailKinds.Contains(op.Kind)) return new ElevatedOpResult(false, ElevationErrorCodes.CommandFailed);
        if (IgnoreWrites) return new ElevatedOpResult(true);
        switch (op.Kind)
        {
            case ElevatedOpKind.RegSetDword:
                Assert.True(ElevationAllowlist.TryParseDword(op.Value, out uint dw));
                _reg.Set(op.Target, op.Name!, RegValue.Dword(unchecked((int)dw)));
                break;
            case ElevatedOpKind.RegSetString:
                _reg.Set(op.Target, op.Name!, RegValue.String(op.Value!));
                break;
            case ElevatedOpKind.RegSetBinary:
                _reg.Set(op.Target, op.Name!, new RegValue(RegValueKind.Binary, op.Value!.ToUpperInvariant()));
                break;
            case ElevatedOpKind.RegDeleteValue:
                _reg.Delete(op.Target, op.Name!);
                break;
            case ElevatedOpKind.DnsSetStatic:
                _reg.Set(ElevationAllowlist.TcpipInterfacesKey + "\\" + op.Target, "NameServer", RegValue.String(op.Value!));
                break;
            case ElevatedOpKind.DnsSetDhcp:
                // Windows leaves an empty NameServer behind when switching back to DHCP.
                _reg.Set(ElevationAllowlist.TcpipInterfacesKey + "\\" + op.Target, "NameServer", RegValue.String(""));
                break;
            case ElevatedOpKind.TcpGlobalSet:
                if (op.Target == "autotuninglevel") AutoTuning = op.Value!;
                break;
            case ElevatedOpKind.PowerDuplicateScheme:
                return new ElevatedOpResult(true, null, "11111111-2222-3333-4444-555555555555");
        }
        return new ElevatedOpResult(true);
    }
}

/// <summary>
/// In-process broker: validates with the real allowlist and executes via <see cref="FakeExecutor"/>,
/// or simulates a declined / failed UAC prompt. Nothing is elevated.
/// </summary>
internal sealed class FakeBroker : IElevationBroker
{
    public readonly FakeExecutor Executor;
    public readonly List<IReadOnlyList<ElevatedOp>> Batches = new();
    public ElevationOutcome? ForceOutcome;
    public Func<string, bool> AdapterExists = _ => true;

    public FakeBroker(FakeExecutor executor) => Executor = executor;

    public Task<ElevatedBatchResult> RunAsync(IReadOnlyList<ElevatedOp> ops, CancellationToken ct = default)
    {
        Batches.Add(ops);
        if (ForceOutcome is ElevationOutcome forced and not ElevationOutcome.Completed)
            return Task.FromResult(new ElevatedBatchResult(forced, Array.Empty<ElevatedOpResult>(), forced.ToString()));
        var reasons = ElevationAllowlist.ValidateBatch(ops, AdapterExists);
        if (reasons != null)
            return Task.FromResult(new ElevatedBatchResult(ElevationOutcome.Rejected,
                reasons.Select(r => new ElevatedOpResult(false, r ?? ElevationErrorCodes.NotExecuted)).ToList(), ElevationErrorCodes.Rejected));
        var results = ops.Select(Executor.Execute).ToList();
        return Task.FromResult(new ElevatedBatchResult(ElevationOutcome.Completed, results));
    }
}

/// <summary>Launcher fake: never starts a process. Either throws like ShellExecute would, or runs the helper host in-process.</summary>
internal sealed class FakeLauncher : IElevatedProcessLauncher
{
    public Func<string, IReadOnlyList<string>, int>? Run;
    public Exception? Throw;
    public readonly List<IReadOnlyList<string>> Calls = new();
    public string? SeenRequestJson;

    public Task<int> RunElevatedAsync(string exePath, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add(args);
        if (args.Count == 3 && File.Exists(args[1])) SeenRequestJson = File.ReadAllText(args[1]);
        if (Throw != null) throw Throw;
        return Task.FromResult(Run?.Invoke(exePath, args) ?? 0);
    }
}

public class ElevationBrokerTests
{
    private static readonly string Iface = "{3F2504E0-4F89-11D3-9A0C-0305E82C3301}";
    private static string IfaceKey => ElevationAllowlist.TcpipInterfacesKey + "\\" + Iface;

    /// <summary>TempDir\Visor\elevation + a dummy "Visor.exe" (never executed).</summary>
    private sealed class Env : IDisposable
    {
        public readonly TempDir Dir = new();
        public string Exchange => Path.Combine(Dir.Path, "Visor", "elevation");
        public string Exe { get; }

        public Env()
        {
            Exe = Dir.Write("Visor.exe", "not a real exe");
        }

        public ElevationBroker Broker(FakeLauncher launcher, TimeSpan? timeout = null) =>
            new(launcher, Exchange, Exe, _ => true, timeout ?? TimeSpan.FromSeconds(5));

        public void Dispose() => Dir.Dispose();
    }

    private static readonly ElevatedOp[] NagleOps =
    {
        new(ElevatedOpKind.RegSetDword, IfaceKey, "TcpAckFrequency", "1"),
        new(ElevatedOpKind.RegSetDword, IfaceKey, "TCPNoDelay", "1"),
    };

    [Fact]
    public async Task Uac_declined_1223_maps_to_Declined_and_cleans_up_files()
    {
        using var env = new Env();
        var launcher = new FakeLauncher { Throw = new Win32Exception(1223) };
        var r = await env.Broker(launcher).RunAsync(NagleOps);

        Assert.Equal(ElevationOutcome.Declined, r.Outcome);
        Assert.Equal(ElevationErrorCodes.Declined, r.ErrorCode);
        Assert.False(r.AllOk);
        Assert.Single(launcher.Calls);
        Assert.Equal(ElevatedHelperHost.CliSwitch, launcher.Calls[0][0]);
        Assert.NotNull(launcher.SeenRequestJson);                 // request existed while the helper ran
        Assert.Empty(Directory.EnumerateFiles(env.Exchange));     // ... and is gone afterwards
    }

    [Fact]
    public async Task Other_launch_errors_and_timeouts_map_to_Failed()
    {
        using var env = new Env();
        var r1 = await env.Broker(new FakeLauncher { Throw = new Win32Exception(2) }).RunAsync(NagleOps);
        Assert.Equal(ElevationOutcome.Failed, r1.Outcome);
        Assert.Equal(ElevationErrorCodes.LaunchFailed, r1.ErrorCode);

        var r2 = await env.Broker(new FakeLauncher { Throw = new TimeoutException() }).RunAsync(NagleOps);
        Assert.Equal(ElevationOutcome.Failed, r2.Outcome);
        Assert.Equal(ElevationErrorCodes.Timeout, r2.ErrorCode);
        Assert.Empty(Directory.EnumerateFiles(env.Exchange));
    }

    [Fact]
    public async Task Helper_exits_without_result_maps_to_Failed_noResult()
    {
        using var env = new Env();
        var r = await env.Broker(new FakeLauncher { Run = (_, _) => 0 }).RunAsync(NagleOps);
        Assert.Equal(ElevationOutcome.Failed, r.Outcome);
        Assert.Equal(ElevationErrorCodes.NoResult, r.ErrorCode);
    }

    [Fact]
    public async Task Invalid_batch_is_rejected_before_any_UAC_prompt()
    {
        using var env = new Env();
        var launcher = new FakeLauncher();
        var r = await env.Broker(launcher).RunAsync(new[]
        {
            NagleOps[0],
            new ElevatedOp(ElevatedOpKind.RegSetDword, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "DefaultTTL", "64"),
        });
        Assert.Equal(ElevationOutcome.Rejected, r.Outcome);
        Assert.Empty(launcher.Calls);
        Assert.Equal(2, r.Results.Count);
        Assert.Equal(ElevationErrorCodes.NotExecuted, r.Results[0].ErrorCode);
        Assert.Equal(ElevationAllowlist.ReasonPath, r.Results[1].ErrorCode);
    }

    [Fact]
    public async Task Empty_batch_and_unsupported_host_never_launch()
    {
        using var env = new Env();
        var launcher = new FakeLauncher();
        Assert.Equal(ElevationErrorCodes.EmptyBatch, (await env.Broker(launcher).RunAsync(Array.Empty<ElevatedOp>())).ErrorCode);

        var noExe = new ElevationBroker(launcher, env.Exchange, Path.Combine(env.Dir.Path, "missing.exe"), _ => true);
        Assert.False(noExe.IsSupported);
        Assert.Equal(ElevationErrorCodes.Unsupported, (await noExe.RunAsync(NagleOps)).ErrorCode);

        var dotnetHost = new ElevationBroker(launcher, env.Exchange, env.Dir.Write("dotnet.exe", "x"), _ => true);
        Assert.False(dotnetHost.IsSupported);
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public async Task Full_round_trip_through_helper_host_with_fake_executor()
    {
        using var env = new Env();
        var reg = new FakeMachineRegistry().AddKey(IfaceKey);
        var exec = new FakeExecutor(reg);
        var host = new ElevatedHelperHost(() => true, _ => true, exec);
        var launcher = new FakeLauncher { Run = (_, a) => host.Execute(a[1], a[2]) };

        var r = await env.Broker(launcher).RunAsync(NagleOps);

        Assert.Equal(ElevationOutcome.Completed, r.Outcome);
        Assert.True(r.AllOk);
        Assert.Equal(2, r.Results.Count);
        Assert.Equal(1, reg.Get(IfaceKey, "TcpAckFrequency")!.AsDword);
        Assert.Equal(1, reg.Get(IfaceKey, "TCPNoDelay")!.AsDword);
        Assert.Empty(Directory.EnumerateFiles(env.Exchange));
    }

    [Fact]
    public async Task Helper_that_is_not_elevated_reports_failure_and_executes_nothing()
    {
        using var env = new Env();
        var exec = new FakeExecutor();
        var host = new ElevatedHelperHost(() => false, _ => true, exec);
        var launcher = new FakeLauncher { Run = (_, a) => host.Execute(a[1], a[2]) };

        var r = await env.Broker(launcher).RunAsync(NagleOps);
        Assert.Equal(ElevationOutcome.Failed, r.Outcome);
        Assert.Equal(ElevationErrorCodes.NotElevated, r.ErrorCode);
        Assert.Empty(exec.Executed);
    }

    [Fact]
    public void MapResponse_rejects_foreign_ids_wrong_counts_and_fake_declines()
    {
        string id = Guid.NewGuid().ToString("N");
        string Json(string rid, ElevationOutcome o, int n) => ElevationJson.Serialize(new ElevationResponse
        {
            RequestId = rid, Outcome = o,
            Results = Enumerable.Range(0, n).Select(_ => new ElevatedOpResult(true)).ToList()
        });

        Assert.Equal(ElevationOutcome.Completed, ElevationBroker.MapResponse(Json(id, ElevationOutcome.Completed, 2), id, 2).Outcome);
        Assert.Equal(ElevationErrorCodes.NoResult, ElevationBroker.MapResponse(Json(Guid.NewGuid().ToString("N"), ElevationOutcome.Completed, 2), id, 2).ErrorCode);
        Assert.Equal(ElevationErrorCodes.NoResult, ElevationBroker.MapResponse(Json(id, ElevationOutcome.Completed, 1), id, 2).ErrorCode);
        Assert.Equal(ElevationOutcome.Failed, ElevationBroker.MapResponse(Json(id, ElevationOutcome.Declined, 0), id, 2).Outcome);
        Assert.Equal(ElevationOutcome.Rejected, ElevationBroker.MapResponse(Json(id, ElevationOutcome.Rejected, 2), id, 2).Outcome);
        Assert.Equal(ElevationErrorCodes.NoResult, ElevationBroker.MapResponse("{not json", id, 2).ErrorCode);
        Assert.Equal(ElevationErrorCodes.NoResult, ElevationBroker.MapResponse(null, id, 2).ErrorCode);
    }

    [Fact]
    public void ToWriteResult_maps_outcomes_to_safe_action_codes()
    {
        Assert.True(ElevatedWrites.ToWriteResult(new ElevatedBatchResult(ElevationOutcome.Completed, new[] { new ElevatedOpResult(true) })).Written);
        Assert.Equal(Visor.Services.SafeActionCodes.ApplyFailed,
            ElevatedWrites.ToWriteResult(new ElevatedBatchResult(ElevationOutcome.Completed, new[] { new ElevatedOpResult(false) })).Code);
        Assert.Equal(Visor.Services.SafeActionCodes.ElevationDeclined,
            ElevatedWrites.ToWriteResult(new ElevatedBatchResult(ElevationOutcome.Declined, Array.Empty<ElevatedOpResult>())).Code);
        Assert.Equal(Visor.Services.SafeActionCodes.ElevationRejected,
            ElevatedWrites.ToWriteResult(new ElevatedBatchResult(ElevationOutcome.Rejected, Array.Empty<ElevatedOpResult>())).Code);
        Assert.Equal(Visor.Services.SafeActionCodes.ElevationFailed,
            ElevatedWrites.ToWriteResult(new ElevatedBatchResult(ElevationOutcome.Failed, Array.Empty<ElevatedOpResult>())).Code);
    }

    [Fact]
    public void OpsFor_maps_values_and_absence_exactly()
    {
        var set = new RegistrySetBuilder()
            .Add(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", RegValue.Dword(-1))
            .Add(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", null)
            .Add(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Tool", RegValue.String("C:\\t.exe"))
            .Add(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Tool", RegValue.Binary(new byte[] { 3, 0, 0, 0 }))
            .Set;
        var ops = ElevatedWrites.OpsFor(set)!;
        Assert.Equal(4, ops.Count);
        Assert.Equal(new ElevatedOp(ElevatedOpKind.RegSetDword, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            "NetworkThrottlingIndex", uint.MaxValue.ToString(CultureInfo.InvariantCulture)), ops[0]);
        Assert.Equal(ElevatedOpKind.RegDeleteValue, ops[1].Kind);
        Assert.Null(ops[1].Value);
        Assert.Equal(ElevatedOpKind.RegSetString, ops[2].Kind);
        Assert.Equal(new ElevatedOp(ElevatedOpKind.RegSetBinary, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Tool", "03000000"), ops[3]);
        Assert.Null(ElevationAllowlist.ValidateBatch(ops, _ => true));

        var opaque = new RegistrySetBuilder().Add("SOFTWARE\\X", "Y", new RegValue(RegValueKind.Other, "q")).Set;
        Assert.Null(ElevatedWrites.OpsFor(opaque));
    }

    private sealed class RegistrySetBuilder
    {
        public readonly RegistryValueSet Set = new();
        public RegistrySetBuilder Add(string key, string name, RegValue? v) { Set.Add(new RegAddress(key, name), v); return this; }
    }
}
