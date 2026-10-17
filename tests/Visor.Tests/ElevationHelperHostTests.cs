using System.IO;
using Visor.Services.Elevation;
using Xunit;

namespace Visor.Tests;

public class ElevationHelperHostTests
{
    private const string Mmcss = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    private sealed class Pair : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly string Id = Guid.NewGuid().ToString("N");
        public string Folder { get; }
        public string Req => Path.Combine(Folder, Id + ".req.json");
        public string Res => Path.Combine(Folder, Id + ".res.json");

        public Pair()
        {
            Folder = Path.Combine(Dir.Path, "Visor", "elevation");
            Directory.CreateDirectory(Folder);
        }

        public void WriteRequest(params ElevatedOp[] ops) =>
            File.WriteAllText(Req, ElevationJson.Serialize(new ElevationRequest { RequestId = Id, Ops = ops.ToList() }));

        public ElevationResponse ReadResponse()
        {
            Assert.True(ElevationJson.TryParseResponse(File.ReadAllText(Res), out var r));
            return r!;
        }

        public void Dispose() => Dir.Dispose();
    }

    private static ElevatedHelperHost Host(FakeExecutor exec, bool elevated = true) => new(() => elevated, _ => true, exec);

    [Fact]
    public void Refuses_when_not_elevated_and_executes_nothing()
    {
        using var p = new Pair();
        p.WriteRequest(new ElevatedOp(ElevatedOpKind.RegSetDword, Mmcss, "SystemResponsiveness", "10"));
        var exec = new FakeExecutor();

        Assert.Equal(1, Host(exec, elevated: false).Execute(p.Req, p.Res));
        Assert.Empty(exec.Executed);
        var r = p.ReadResponse();
        Assert.Equal(ElevationOutcome.Failed, r.Outcome);
        Assert.Equal(ElevationErrorCodes.NotElevated, r.ErrorCode);
        Assert.Equal(p.Id, r.RequestId);
    }

    [Fact]
    public void Validates_whole_batch_before_executing_any_op()
    {
        using var p = new Pair();
        p.WriteRequest(
            new ElevatedOp(ElevatedOpKind.RegSetDword, Mmcss, "SystemResponsiveness", "10"),
            new ElevatedOp(ElevatedOpKind.RegSetDword, @"SYSTEM\CurrentControlSet\Control\Lsa", "LimitBlankPasswordUse", "0"));
        var exec = new FakeExecutor();

        Assert.Equal(1, Host(exec).Execute(p.Req, p.Res));
        Assert.Empty(exec.Executed);   // the valid first op did NOT run
        var r = p.ReadResponse();
        Assert.Equal(ElevationOutcome.Rejected, r.Outcome);
        Assert.Equal(ElevationErrorCodes.NotExecuted, r.Results[0].ErrorCode);
        Assert.Equal(ElevationAllowlist.ReasonPath, r.Results[1].ErrorCode);
    }

    [Fact]
    public void Executes_all_ops_and_reports_each()
    {
        using var p = new Pair();
        p.WriteRequest(
            new ElevatedOp(ElevatedOpKind.RegSetDword, Mmcss, "SystemResponsiveness", "10"),
            new ElevatedOp(ElevatedOpKind.TcpGlobalSet, "autotuninglevel", null, "normal"),
            new ElevatedOp(ElevatedOpKind.PowerDuplicateScheme, "", null, ElevationAllowlist.UltimatePerformanceScheme));
        var exec = new FakeExecutor();
        exec.FailKinds.Add(ElevatedOpKind.TcpGlobalSet);

        Assert.Equal(1, Host(exec).Execute(p.Req, p.Res));    // one op failed → exit 1
        Assert.Equal(3, exec.Executed.Count);                  // ... but every op was attempted
        var r = p.ReadResponse();
        Assert.Equal(ElevationOutcome.Completed, r.Outcome);
        Assert.True(r.Results[0].Ok);
        Assert.False(r.Results[1].Ok);
        Assert.Equal("11111111-2222-3333-4444-555555555555", r.Results[2].Output);
    }

    [Fact]
    public void Success_exit_code_is_zero()
    {
        using var p = new Pair();
        p.WriteRequest(new ElevatedOp(ElevatedOpKind.RegDeleteValue, Mmcss, "SystemResponsiveness"));
        Assert.Equal(0, Host(new FakeExecutor()).Execute(p.Req, p.Res));
        Assert.True(p.ReadResponse().Results.Single().Ok);
    }

    [Fact]
    public void Bad_json_or_mismatched_request_id_is_a_bad_request()
    {
        using var p = new Pair();
        File.WriteAllText(p.Req, "{ this is not json");
        var exec = new FakeExecutor();
        Assert.Equal(1, Host(exec).Execute(p.Req, p.Res));
        Assert.Equal(ElevationErrorCodes.BadRequest, p.ReadResponse().ErrorCode);

        File.Delete(p.Res);
        File.WriteAllText(p.Req, ElevationJson.Serialize(new ElevationRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Ops = { new ElevatedOp(ElevatedOpKind.RegDeleteValue, Mmcss, "SystemResponsiveness") }
        }));
        Assert.Equal(1, Host(exec).Execute(p.Req, p.Res));
        Assert.Equal(ElevationErrorCodes.BadRequest, p.ReadResponse().ErrorCode);
        Assert.Empty(exec.Executed);
    }

    [Fact]
    public void Does_not_overwrite_a_preexisting_result_file()
    {
        using var p = new Pair();
        p.WriteRequest(new ElevatedOp(ElevatedOpKind.RegDeleteValue, Mmcss, "SystemResponsiveness"));
        File.WriteAllText(p.Res, "planted");
        Host(new FakeExecutor()).Execute(p.Req, p.Res);
        Assert.Equal("planted", File.ReadAllText(p.Res));
        Assert.Empty(Directory.EnumerateFiles(p.Folder, "*.tmp"));   // no temp leftovers
    }

    [Fact]
    public void Path_pair_rules()
    {
        using var p = new Pair();
        Assert.True(ElevatedHelperHost.TryCheckPathPair(p.Req, p.Res, out var id));
        Assert.Equal(p.Id, id);

        string other = Guid.NewGuid().ToString("N");
        Assert.False(ElevatedHelperHost.TryCheckPathPair(p.Req, Path.Combine(p.Folder, other + ".res.json"), out _));
        Assert.False(ElevatedHelperHost.TryCheckPathPair(p.Req, p.Req, out _));
        Assert.False(ElevatedHelperHost.TryCheckPathPair("x.req.json", "x.res.json", out _));
        Assert.False(ElevatedHelperHost.TryCheckPathPair(Path.Combine(p.Folder, "..", "elevation", p.Id + ".req.json"), p.Res, out _));
        Assert.False(ElevatedHelperHost.TryCheckPathPair(Path.Combine(p.Folder, "abc.req.json"), Path.Combine(p.Folder, "abc.res.json"), out _));
        Assert.False(ElevatedHelperHost.TryCheckPathPair(@"\\server\share\Visor\elevation\" + p.Id + ".req.json",
            @"\\server\share\Visor\elevation\" + p.Id + ".res.json", out _));

        // Same names in a folder that is not ...\Visor\elevation
        string elsewhere = Path.Combine(p.Dir.Path, "Temp");
        Directory.CreateDirectory(elsewhere);
        Assert.False(ElevatedHelperHost.TryCheckPathPair(Path.Combine(elsewhere, p.Id + ".req.json"),
            Path.Combine(elsewhere, p.Id + ".res.json"), out _));

        // Invalid pair → the helper writes nothing at all.
        var exec = new FakeExecutor();
        Assert.Equal(1, Host(exec).Execute(Path.Combine(elsewhere, p.Id + ".req.json"), Path.Combine(elsewhere, p.Id + ".res.json")));
        Assert.False(File.Exists(Path.Combine(elsewhere, p.Id + ".res.json")));
    }

    [Fact]
    public void Request_and_response_json_round_trip_with_string_enums()
    {
        var req = new ElevationRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Ops =
            {
                new ElevatedOp(ElevatedOpKind.RegSetBinary, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Tool", "0300"),
                new ElevatedOp(ElevatedOpKind.DnsSetDhcp, "{3F2504E0-4F89-11D3-9A0C-0305E82C3301}"),
            }
        };
        string json = ElevationJson.Serialize(req);
        Assert.Contains("\"regSetBinary\"", json);
        Assert.Contains("\"dnsSetDhcp\"", json);
        Assert.True(ElevationJson.TryParseRequest(json, out var back));
        Assert.Equal(req.RequestId, back!.RequestId);
        Assert.Equal(req.Ops, back.Ops);

        var res = new ElevationResponse
        {
            RequestId = req.RequestId,
            Outcome = ElevationOutcome.Completed,
            Results = { new ElevatedOpResult(true, null, "guid"), new ElevatedOpResult(false, "keyMissing") }
        };
        Assert.True(ElevationJson.TryParseResponse(ElevationJson.Serialize(res), out var rb));
        Assert.Equal(res.Results, rb!.Results);
        Assert.Equal(ElevationOutcome.Completed, rb.Outcome);

        // Integer enums, wrong versions, null ops and oversize payloads are refused.
        Assert.False(ElevationJson.TryParseRequest(json.Replace("\"regSetBinary\"", "6"), out _));
        Assert.False(ElevationJson.TryParseRequest(json.Replace("\"version\":1", "\"version\":2"), out _));
        Assert.False(ElevationJson.TryParseRequest("{\"version\":1,\"requestId\":\"a\",\"ops\":[null]}", out _));
        Assert.False(ElevationJson.TryParseRequest(new string(' ', ElevationJson.MaxFileBytes + 1), out _));
        Assert.False(ElevationJson.TryParseRequest(json.Replace("\"dnsSetDhcp\"", "\"formatDisk\""), out _));
    }
}
