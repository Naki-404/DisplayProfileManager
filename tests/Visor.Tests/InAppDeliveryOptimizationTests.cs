using System.IO;
using Visor.Services;
using Visor.Services.Elevation;
using Visor.Services.Optimize.Pc;
using Xunit;

namespace Visor.Tests;

/// <summary>Records console-tool calls; never starts a process.</summary>
internal sealed class RecordingRunner : ICommandRunner
{
    public readonly List<(string Tool, IReadOnlyList<string> Args, TimeSpan Timeout)> Calls = new();
    public CommandResult Result = new(0, "Deleting...\r\nSuccessfully deleted Delivery Optimization cache\r\n");
    public CommandResult Run(string systemTool, IReadOnlyList<string> args, TimeSpan timeout)
    {
        Calls.Add((systemTool, args.ToList(), timeout));
        return Result;
    }
}

/// <summary>Broker fake that validates like the real one and returns a scripted result for the DO op.</summary>
internal sealed class DoFakeBroker : IElevationBroker
{
    public readonly List<IReadOnlyList<ElevatedOp>> Batches = new();
    public ElevationOutcome Outcome = ElevationOutcome.Completed;
    public ElevatedOpResult OpResult = new(true);
    public Action? OnRun;

    public Task<ElevatedBatchResult> RunAsync(IReadOnlyList<ElevatedOp> ops, CancellationToken ct = default)
    {
        Batches.Add(ops);
        if (ElevationAllowlist.ValidateBatch(ops, _ => false) != null)
            return Task.FromResult(new ElevatedBatchResult(ElevationOutcome.Rejected, Array.Empty<ElevatedOpResult>(), ElevationErrorCodes.Rejected));
        if (Outcome != ElevationOutcome.Completed)
            return Task.FromResult(new ElevatedBatchResult(Outcome, Array.Empty<ElevatedOpResult>()));
        OnRun?.Invoke();
        return Task.FromResult(new ElevatedBatchResult(ElevationOutcome.Completed, new[] { OpResult }));
    }
}

public class InAppDeliveryOptimizationTests
{
    private static readonly ElevatedOp Op = DeliveryOptimizationService.ClearOp();

    // ---------------- allowlist ----------------

    [Fact]
    public void Allowlist_accepts_the_parameterless_clear_op()
    {
        Assert.Equal(ElevatedOpKind.DeliveryOptimizationClear, Op.Kind);
        Assert.Equal("", Op.Target);
        Assert.Null(Op.Name);
        Assert.Null(Op.Value);
        Assert.Null(ElevationAllowlist.Validate(Op, _ => false));
        Assert.Null(ElevationAllowlist.ValidateBatch(new[] { Op }, _ => false));
    }

    public static IEnumerable<object[]> ParameterisedClears() => new[]
    {
        new object[] { new ElevatedOp(ElevatedOpKind.DeliveryOptimizationClear, "C:\\Windows") },
        new object[] { new ElevatedOp(ElevatedOpKind.DeliveryOptimizationClear, "", "IncludePinnedFiles") },
        new object[] { new ElevatedOp(ElevatedOpKind.DeliveryOptimizationClear, "", null, "-IncludePinnedFiles") },
        new object[] { new ElevatedOp(ElevatedOpKind.DeliveryOptimizationClear, "", null, "") },
        new object[] { new ElevatedOp(ElevatedOpKind.DeliveryOptimizationClear, " ") },
        new object[] { new ElevatedOp(ElevatedOpKind.DeliveryOptimizationClear, "; Remove-Item C:\\ -Recurse") },
    };

    [Theory]
    [MemberData(nameof(ParameterisedClears))]
    public void Allowlist_rejects_any_parameter_on_the_clear_op(ElevatedOp op)
    {
        Assert.Equal(ElevationAllowlist.ReasonParameters, ElevationAllowlist.Validate(op, _ => true));
        var reasons = ElevationAllowlist.ValidateBatch(new[] { Op, op }, _ => true);
        Assert.NotNull(reasons);
        Assert.Null(reasons![0]);
        Assert.Equal(ElevationAllowlist.ReasonParameters, reasons[1]);
    }

    [Fact]
    public void Clear_op_survives_the_wire_format()
    {
        var json = ElevationJson.Serialize(new ElevationRequest { RequestId = Guid.NewGuid().ToString("N"), Ops = { Op } });
        Assert.Contains("\"deliveryOptimizationClear\"", json);
        Assert.True(ElevationJson.TryParseRequest(json, out var req));
        Assert.Equal(Op, req!.Ops.Single());
    }

    // ---------------- executor ----------------

    [Fact]
    public void Executor_runs_the_fixed_documented_cmdlet_through_Windows_PowerShell()
    {
        var ps = new RecordingRunner();
        var tools = new RecordingRunner();
        var sizes = new Queue<long?>(new long?[] { 5_000, 1_200 });
        var exec = new SystemElevatedExecutor(tools, ps, () => sizes.Dequeue());

        var r = exec.Execute(Op);

        Assert.True(r.Ok);
        Assert.Equal("3800", r.Output);
        Assert.Empty(tools.Calls);
        var call = Assert.Single(ps.Calls);
        Assert.Equal("powershell.exe", call.Tool);
        Assert.Equal(new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", SystemElevatedExecutor.DeliveryOptimizationCommand },
            call.Args);
        Assert.Equal("$ErrorActionPreference = 'Stop'; Delete-DeliveryOptimizationCache -Force | Out-Null",
            SystemElevatedExecutor.DeliveryOptimizationCommand);
        Assert.DoesNotContain("IncludePinnedFiles", string.Join(" ", call.Args));
        Assert.DoesNotContain("ExecutionPolicy", string.Join(" ", call.Args));
        Assert.True(call.Timeout > TimeSpan.Zero && call.Timeout < ElevationBroker.DefaultTimeout);
    }

    [Fact]
    public void Executor_reports_success_without_a_number_when_sizes_are_unknown()
    {
        var exec = new SystemElevatedExecutor(new RecordingRunner(), new RecordingRunner(), () => null);
        var r = exec.Execute(Op);
        Assert.True(r.Ok);
        Assert.Null(r.Output);
    }

    [Fact]
    public void Executor_clamps_a_growing_cache_to_zero_freed()
    {
        var sizes = new Queue<long?>(new long?[] { 100, 300 });
        var r = new SystemElevatedExecutor(new RecordingRunner(), new RecordingRunner(), () => sizes.Dequeue()).Execute(Op);
        Assert.Equal("0", r.Output);
    }

    [Fact]
    public void Executor_maps_cmdlet_failure_and_timeout()
    {
        var ps = new RecordingRunner { Result = new CommandResult(1, "Delete-DeliveryOptimizationCache : File is not initialized.") };
        var failed = new SystemElevatedExecutor(new RecordingRunner(), ps, () => 1).Execute(Op);
        Assert.False(failed.Ok);
        Assert.Equal(ElevationErrorCodes.CommandFailed, failed.ErrorCode);
        Assert.Contains("not initialized", failed.Output);

        ps.Result = new CommandResult(null, "");
        var timedOut = new SystemElevatedExecutor(new RecordingRunner(), ps, () => 1).Execute(Op);
        Assert.False(timedOut.Ok);
        Assert.Equal(ElevationErrorCodes.Timeout, timedOut.ErrorCode);
    }

    [Fact]
    public void Executor_survives_a_throwing_size_probe()
    {
        var r = new SystemElevatedExecutor(new RecordingRunner(), new RecordingRunner(), () => throw new InvalidOperationException()).Execute(Op);
        Assert.True(r.Ok);
        Assert.Null(r.Output);
    }

    [Fact]
    public void PowerShell_runner_only_accepts_its_own_tool_and_uses_the_fixed_System32_path()
    {
        Assert.Throws<ArgumentException>(() => WindowsPowerShellRunner.Instance.Run("cmd.exe", Array.Empty<string>(), TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => WindowsPowerShellRunner.Instance.Run(@"..\powershell.exe", Array.Empty<string>(), TimeSpan.FromSeconds(1)));
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), WindowsPowerShellRunner.FullPath);
    }

    [Fact]
    public void Helper_host_accepts_and_executes_the_clear_op_end_to_end()
    {
        using var dir = new TempDir();
        string folder = Path.Combine(dir.Path, "Visor", "elevation");
        Directory.CreateDirectory(folder);
        string id = Guid.NewGuid().ToString("N");
        string req = Path.Combine(folder, id + ".req.json"), res = Path.Combine(folder, id + ".res.json");
        File.WriteAllText(req, ElevationJson.Serialize(new ElevationRequest { RequestId = id, Ops = { Op } }));
        var ps = new RecordingRunner();
        var sizes = new Queue<long?>(new long?[] { 900, 0 });
        var host = new ElevatedHelperHost(() => true, _ => false, new SystemElevatedExecutor(new RecordingRunner(), ps, () => sizes.Dequeue()));

        Assert.Equal(0, host.Execute(req, res));

        Assert.True(ElevationJson.TryParseResponse(File.ReadAllText(res), out var r));
        Assert.Equal(ElevationOutcome.Completed, r!.Outcome);
        Assert.True(r.Results.Single().Ok);
        Assert.Equal("900", r.Results.Single().Output);
        Assert.Single(ps.Calls);
    }

    // ---------------- service ----------------

    [Fact]
    public async Task Clear_sends_exactly_one_parameterless_op_and_uses_the_helper_measurement()
    {
        var broker = new DoFakeBroker { OpResult = new ElevatedOpResult(true, null, "123456") };
        var svc = new DeliveryOptimizationService(broker, () => new DeliveryOptimizationStatus(999_999, DeliveryOptimizationSizeReason.None));

        var r = await svc.ClearAsync();

        Assert.Equal(DeliveryOptimizationCleanStatus.Cleaned, r.Status);
        Assert.Equal(123456, r.FreedBytes);
        var batch = Assert.Single(broker.Batches);
        Assert.Equal(Op, Assert.Single(batch));
    }

    [Fact]
    public async Task Clear_falls_back_to_before_minus_after_when_the_helper_could_not_measure()
    {
        long current = 50_000;
        var broker = new DoFakeBroker { OnRun = () => current = 8_000 };
        var svc = new DeliveryOptimizationService(broker, () => new DeliveryOptimizationStatus(current, DeliveryOptimizationSizeReason.None));

        var r = await svc.ClearAsync();

        Assert.Equal(DeliveryOptimizationCleanStatus.Cleaned, r.Status);
        Assert.Equal(42_000, r.FreedBytes);
    }

    [Fact]
    public async Task Clear_reports_unknown_freed_bytes_when_the_size_needs_admin()
    {
        var svc = new DeliveryOptimizationService(new DoFakeBroker(),
            () => new DeliveryOptimizationStatus(null, DeliveryOptimizationSizeReason.NeedsAdmin));
        var r = await svc.ClearAsync();
        Assert.Equal(DeliveryOptimizationCleanStatus.Cleaned, r.Status);
        Assert.Null(r.FreedBytes);
    }

    [Theory]
    [InlineData(ElevationOutcome.Declined, DeliveryOptimizationCleanStatus.Declined, SafeActionCodes.ElevationDeclined)]
    [InlineData(ElevationOutcome.Failed, DeliveryOptimizationCleanStatus.Failed, SafeActionCodes.ElevationFailed)]
    public async Task Clear_maps_broker_outcomes(ElevationOutcome outcome, DeliveryOptimizationCleanStatus status, string code)
    {
        var svc = new DeliveryOptimizationService(new DoFakeBroker { Outcome = outcome },
            () => new DeliveryOptimizationStatus(10, DeliveryOptimizationSizeReason.None));
        var r = await svc.ClearAsync();
        Assert.Equal(status, r.Status);
        Assert.Equal(code, r.Code);
        Assert.Null(r.FreedBytes);
    }

    [Fact]
    public async Task Clear_maps_a_failed_op_to_applyFailed()
    {
        var svc = new DeliveryOptimizationService(new DoFakeBroker { OpResult = new ElevatedOpResult(false, ElevationErrorCodes.CommandFailed) },
            () => new DeliveryOptimizationStatus(10, DeliveryOptimizationSizeReason.None));
        var r = await svc.ClearAsync();
        Assert.Equal(DeliveryOptimizationCleanStatus.Failed, r.Status);
        Assert.Equal(SafeActionCodes.ApplyFailed, r.Code);
    }

    [Fact]
    public void Read_never_throws()
    {
        var svc = new DeliveryOptimizationService(new DoFakeBroker(), () => throw new UnauthorizedAccessException());
        var st = svc.Read();
        Assert.Null(st.CacheBytes);
        Assert.Equal(DeliveryOptimizationSizeReason.Unavailable, st.Reason);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("42", 42L)]
    [InlineData("-5", null)]
    [InlineData("12 bytes", null)]
    public void ParseFreed(string? output, long? expected) =>
        Assert.Equal(expected, DeliveryOptimizationService.ParseFreed(output));

    [Fact]
    public void Freed_needs_both_sides_and_never_goes_negative()
    {
        Assert.Null(DeliveryOptimizationCache.Freed(null, 1));
        Assert.Null(DeliveryOptimizationCache.Freed(1, null));
        Assert.Equal(7, DeliveryOptimizationCache.Freed(10, 3));
        Assert.Equal(0, DeliveryOptimizationCache.Freed(3, 10));
    }

    [Fact]
    public void Folder_size_of_a_missing_folder_is_unknown_not_zero()
    {
        using var dir = new TempDir();
        Assert.Null(DeliveryOptimizationCache.ReadFolderBytes(Path.Combine(dir.Path, "nope"), out bool denied));
        Assert.False(denied);
        File.WriteAllBytes(Path.Combine(dir.Path, "a.bin"), new byte[10]);
        Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));
        File.WriteAllBytes(Path.Combine(dir.Path, "sub", "b.bin"), new byte[5]);
        Assert.Equal(15, DeliveryOptimizationCache.ReadFolderBytes(dir.Path, out _));
    }

    [Fact]
    public void Cleanup_label_points_to_the_in_app_row_not_to_Settings()
    {
        string key = CleanupService.CategoryLocKey(CleanupCategory.DeliveryOptimization);
        Assert.Equal("optimize.cleanup.cat.doCache", key);
        Assert.True(Loc.Has(key));
        var before = Loc.Locale;
        try
        {
            foreach (var locale in new[] { "en", "ru" })
            {
                Loc.SetLocale(locale);
                Assert.DoesNotContain("→", Loc.T(key));
            }
        }
        finally
        {
            Loc.SetLocale(before);
        }
        foreach (var k in new[] { "optimize.doCache.size", "optimize.doCache.sizeNeedsAdmin", "optimize.doCache.clean",
                     "optimize.doCache.note", "optimize.doCache.confirm", "optimize.doCache.result" })
            Assert.True(Loc.Has(k), k);
    }
}
