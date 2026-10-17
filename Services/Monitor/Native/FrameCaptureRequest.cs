using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Visor.Services.Elevation;

namespace Visor.Services.FrameCapture;

/// <summary>
/// What the elevated host may do: capture present events of these processes and stream them to
/// this pipe, for at most <see cref="SafetyCapSeconds"/>. Written by unelevated Visor to
/// <c>%LocalAppData%\Visor\elevation\&lt;id&gt;.cap.json</c>; nothing in it names a file to write,
/// a command to run or a registry key — the host has no such capability to begin with.
/// </summary>
public sealed class FrameCaptureRequest
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    /// <summary>32-hex id; must equal the request file's stem.</summary>
    public string RequestId { get; set; } = "";
    /// <summary><c>VisorFrames-&lt;32 hex&gt;</c>.</summary>
    public string PipeName { get; set; } = "";
    /// <summary>The unelevated Visor process: owns the pipe; the host stops when it exits.</summary>
    public int ParentProcessId { get; set; }
    /// <summary>Target PIDs resolved by Visor (≤ <see cref="FrameCaptureRequestJson.MaxProcessIds"/>).</summary>
    public List<int> ProcessIds { get; set; } = new();
    /// <summary>Plain exe name ("game.exe"); the host re-resolves it to PIDs. Optional when PIDs are given.</summary>
    public string? ExeName { get; set; }
    public int SafetyCapSeconds { get; set; } = FrameCaptureRequestJson.MaxSafetyCapSeconds;
}

/// <summary>JSON format, path rules and validation of <see cref="FrameCaptureRequest"/> (shared by both sides).</summary>
public static class FrameCaptureRequestJson
{
    public const string FileSuffix = ".cap.json";
    public const string PipePrefix = "VisorFrames-";
    public const int MaxFileBytes = 4 * 1024;
    public const int MaxProcessIds = 32;
    public const int MaxSafetyCapSeconds = 3600;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 4
    };

    public static string NewPipeName() => PipePrefix + Guid.NewGuid().ToString("N");

    public static bool IsValidPipeName(string? name) =>
        name != null && name.Length == PipePrefix.Length + 32
        && name.StartsWith(PipePrefix, StringComparison.Ordinal)
        && IsLowerHex32(name.AsSpan(PipePrefix.Length));

    public static string Serialize(FrameCaptureRequest r) => JsonSerializer.Serialize(r, Options);

    public static bool TryParse(string? json, out FrameCaptureRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxFileBytes) return false;
        try
        {
            var r = JsonSerializer.Deserialize<FrameCaptureRequest>(json, Options);
            if (r == null || r.ProcessIds == null || r.RequestId == null || r.PipeName == null) return false;
            request = r;
            return true;
        }
        catch (JsonException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    /// <summary>Null when acceptable, otherwise a short reason ("version", "id", "pipe", "parent", "pids", "exe", "target", "cap").</summary>
    public static string? Validate(FrameCaptureRequest? r)
    {
        if (r == null) return "null";
        if (r.Version != FrameCaptureRequest.CurrentVersion) return "version";
        if (!ElevationJson.IsValidRequestId(r.RequestId) || !IsLowerHex32(r.RequestId)) return "id";
        if (!IsValidPipeName(r.PipeName)) return "pipe";
        if (r.ParentProcessId <= 4) return "parent";
        if (r.ProcessIds == null || r.ProcessIds.Count > MaxProcessIds) return "pids";
        // 0 = Idle, 4 = System: never a game, and the System PID would make the kernel provider noisy.
        if (r.ProcessIds.Any(p => p <= 4 || p == r.ParentProcessId)) return "pids";
        if (r.ProcessIds.Distinct().Count() != r.ProcessIds.Count) return "pids";
        if (r.ExeName != null && (!ProcessNames.IsPlainExeName(r.ExeName) || ProcessNames.IsProtectedSystemProcess(r.ExeName)))
            return "exe";
        if (r.ProcessIds.Count == 0 && r.ExeName == null) return "target";
        if (r.SafetyCapSeconds < 1 || r.SafetyCapSeconds > MaxSafetyCapSeconds) return "cap";
        return null;
    }

    /// <summary>Path rules for the request file (same folder rules as the <c>--elevated</c> helper).</summary>
    public static bool TryCheckRequestPath(string? path, out string id) =>
        ElevationExchangeDirectory.TryCheckExchangeFile(path, FileSuffix, out id);

    /// <summary>Reads a request file: size-capped, not a reparse point. Null on any problem.</summary>
    public static string? ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return null;
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Full host-side check: path → read → parse → id match → validate. Null request + reason on refusal.</summary>
    public static FrameCaptureRequest? Load(string? path, out string reason)
    {
        if (!TryCheckRequestPath(path, out string id)) { reason = "path"; return null; }
        string? json = ReadFile(path!);
        if (!TryParse(json, out var r)) { reason = "parse"; return null; }
        if (!string.Equals(r!.RequestId, id, StringComparison.Ordinal)) { reason = "id"; return null; }
        reason = Validate(r) ?? "";
        return reason.Length == 0 ? r : null;
    }

    private static bool IsLowerHex32(ReadOnlySpan<char> s)
    {
        if (s.Length != 32) return false;
        foreach (char c in s)
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
}
