using System.IO;
using Visor.Services.FrameCapture;
using Xunit;

namespace Visor.Tests;

public class FrameCaptureRequestTests
{
    private static FrameCaptureRequest Valid(Action<FrameCaptureRequest>? tweak = null)
    {
        var r = new FrameCaptureRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            PipeName = FrameCaptureRequestJson.NewPipeName(),
            ParentProcessId = 1234,
            ProcessIds = new List<int> { 5678 },
            ExeName = "game.exe",
            SafetyCapSeconds = 3600
        };
        tweak?.Invoke(r);
        return r;
    }

    [Fact]
    public void A_plain_request_is_accepted()
    {
        Assert.Null(FrameCaptureRequestJson.Validate(Valid()));
        Assert.Null(FrameCaptureRequestJson.Validate(Valid(r => r.ExeName = null)));                 // PIDs only
        Assert.Null(FrameCaptureRequestJson.Validate(Valid(r => r.ProcessIds.Clear())));             // exe only
        Assert.Null(FrameCaptureRequestJson.Validate(Valid(r => r.SafetyCapSeconds = 1)));
        Assert.Null(FrameCaptureRequestJson.Validate(Valid(r => r.ProcessIds = Enumerable.Range(100, 32).ToList())));
    }

    public static IEnumerable<object[]> Rejections() => new List<object[]>
    {
        new object[] { "version", (Action<FrameCaptureRequest>)(r => r.Version = 2) },
        new object[] { "id", (Action<FrameCaptureRequest>)(r => r.RequestId = "not-an-id") },
        new object[] { "id", (Action<FrameCaptureRequest>)(r => r.RequestId = Guid.NewGuid().ToString("N").ToUpperInvariant()) },
        new object[] { "pipe", (Action<FrameCaptureRequest>)(r => r.PipeName = "VisorFrames-x") },
        new object[] { "pipe", (Action<FrameCaptureRequest>)(r => r.PipeName = @"..\pipe\VisorFrames-" + Guid.NewGuid().ToString("N")) },
        new object[] { "pipe", (Action<FrameCaptureRequest>)(r => r.PipeName = "OtherApp-" + Guid.NewGuid().ToString("N")) },
        new object[] { "parent", (Action<FrameCaptureRequest>)(r => r.ParentProcessId = 0) },
        new object[] { "parent", (Action<FrameCaptureRequest>)(r => r.ParentProcessId = 4) },
        new object[] { "pids", (Action<FrameCaptureRequest>)(r => r.ProcessIds = new List<int> { 4 }) },
        new object[] { "pids", (Action<FrameCaptureRequest>)(r => r.ProcessIds = new List<int> { 0 }) },
        new object[] { "pids", (Action<FrameCaptureRequest>)(r => r.ProcessIds = new List<int> { -1 }) },
        new object[] { "pids", (Action<FrameCaptureRequest>)(r => r.ProcessIds = new List<int> { 1234 }) },          // the parent itself
        new object[] { "pids", (Action<FrameCaptureRequest>)(r => r.ProcessIds = new List<int> { 77, 77 }) },
        new object[] { "pids", (Action<FrameCaptureRequest>)(r => r.ProcessIds = Enumerable.Range(100, 33).ToList()) },
        new object[] { "exe", (Action<FrameCaptureRequest>)(r => r.ExeName = @"C:\Windows\explorer.exe") },
        new object[] { "exe", (Action<FrameCaptureRequest>)(r => r.ExeName = "game.exe \"--x\"") },
        new object[] { "exe", (Action<FrameCaptureRequest>)(r => r.ExeName = "csrss.exe") },
        new object[] { "target", (Action<FrameCaptureRequest>)(r => { r.ExeName = null; r.ProcessIds.Clear(); }) },
        new object[] { "cap", (Action<FrameCaptureRequest>)(r => r.SafetyCapSeconds = 0) },
        new object[] { "cap", (Action<FrameCaptureRequest>)(r => r.SafetyCapSeconds = 3601) },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void Anything_outside_the_contract_is_rejected(string reason, Action<FrameCaptureRequest> tweak)
    {
        Assert.Equal(reason, FrameCaptureRequestJson.Validate(Valid(tweak)));
    }

    [Fact]
    public void Json_round_trips_and_rejects_unknown_members()
    {
        var r = Valid();
        Assert.True(FrameCaptureRequestJson.TryParse(FrameCaptureRequestJson.Serialize(r), out var back));
        Assert.Equal(r.RequestId, back!.RequestId);
        Assert.Equal(r.ProcessIds, back.ProcessIds);
        Assert.Equal("game.exe", back.ExeName);

        // Strict: an extra member (e.g. something a future version might try to smuggle in) fails the parse.
        string extra = FrameCaptureRequestJson.Serialize(r).TrimEnd('}') + ",\"command\":\"cmd.exe\"}";
        Assert.False(FrameCaptureRequestJson.TryParse(extra, out _));
        Assert.False(FrameCaptureRequestJson.TryParse("", out _));
        Assert.False(FrameCaptureRequestJson.TryParse("not json", out _));
        Assert.False(FrameCaptureRequestJson.TryParse("{\"processIds\":null}", out _));
        Assert.False(FrameCaptureRequestJson.TryParse(new string(' ', 5000) + "{}", out _));   // size cap
    }

    private sealed class Exchange : IDisposable
    {
        public readonly TempDir Dir = new();
        public string Folder { get; }
        public Exchange()
        {
            Folder = Path.Combine(Dir.Path, "Visor", "elevation");
            Directory.CreateDirectory(Folder);
        }
        public string Write(FrameCaptureRequest r, string? name = null)
        {
            var p = Path.Combine(Folder, (name ?? r.RequestId) + FrameCaptureRequestJson.FileSuffix);
            File.WriteAllText(p, FrameCaptureRequestJson.Serialize(r));
            return p;
        }
        public void Dispose() => Dir.Dispose();
    }

    [Fact]
    public void Load_accepts_a_well_formed_request_file()
    {
        using var x = new Exchange();
        var r = Valid();
        var loaded = FrameCaptureRequestJson.Load(x.Write(r), out var reason);
        Assert.NotNull(loaded);
        Assert.Equal("", reason);
        Assert.Equal(r.PipeName, loaded!.PipeName);
    }

    [Fact]
    public void Load_rejects_bad_paths_and_mismatched_ids()
    {
        using var x = new Exchange();
        var r = Valid();

        Assert.Null(FrameCaptureRequestJson.Load(x.Write(r, Guid.NewGuid().ToString("N")), out var reason));   // id ≠ file stem
        Assert.Equal("id", reason);

        Assert.Null(FrameCaptureRequestJson.Load(Path.Combine(x.Folder, r.RequestId + ".req.json"), out reason));
        Assert.Equal("path", reason);
        Assert.Null(FrameCaptureRequestJson.Load(r.RequestId + FrameCaptureRequestJson.FileSuffix, out reason));   // relative
        Assert.Equal("path", reason);
        Assert.Null(FrameCaptureRequestJson.Load(@"\\server\share\Visor\elevation\" + r.RequestId + FrameCaptureRequestJson.FileSuffix, out reason));
        Assert.Equal("path", reason);
        Assert.Null(FrameCaptureRequestJson.Load(Path.Combine(x.Folder, "..", "elevation", r.RequestId + FrameCaptureRequestJson.FileSuffix), out reason));
        Assert.Equal("path", reason);
        Assert.Null(FrameCaptureRequestJson.Load(null, out reason));
        Assert.Equal("path", reason);

        // Right name, wrong folder.
        var other = Path.Combine(x.Dir.Path, "Elsewhere", "elevation");
        Directory.CreateDirectory(other);
        var p = Path.Combine(other, r.RequestId + FrameCaptureRequestJson.FileSuffix);
        File.WriteAllText(p, FrameCaptureRequestJson.Serialize(r));
        Assert.Null(FrameCaptureRequestJson.Load(p, out reason));
        Assert.Equal("path", reason);

        // Valid path, oversized content.
        var big = Path.Combine(x.Folder, r.RequestId + FrameCaptureRequestJson.FileSuffix);
        File.WriteAllText(big, new string(' ', FrameCaptureRequestJson.MaxFileBytes + 1));
        Assert.Null(FrameCaptureRequestJson.Load(big, out reason));
        Assert.Equal("parse", reason);

        // Valid file, invalid content.
        var bad = Valid(b => b.SafetyCapSeconds = 99999);
        Assert.Null(FrameCaptureRequestJson.Load(x.Write(bad), out reason));
        Assert.Equal("cap", reason);
    }

    [Fact]
    public void Pipe_names_follow_the_pattern()
    {
        var n = FrameCaptureRequestJson.NewPipeName();
        Assert.StartsWith("VisorFrames-", n);
        Assert.True(FrameCaptureRequestJson.IsValidPipeName(n));
        Assert.NotEqual(n, FrameCaptureRequestJson.NewPipeName());
        Assert.False(FrameCaptureRequestJson.IsValidPipeName(null));
        Assert.False(FrameCaptureRequestJson.IsValidPipeName("VisorFrames-" + new string('g', 32)));
    }
}
