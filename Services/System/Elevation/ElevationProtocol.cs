using System.Text.Json;
using System.Text.Json.Serialization;

namespace Visor.Services.Elevation;

/// <summary>Stable error codes carried in <see cref="ElevatedBatchResult.ErrorCode"/> / <see cref="ElevatedOpResult.ErrorCode"/>.</summary>
public static class ElevationErrorCodes
{
    /// <summary>The helper started without an elevated token — it refuses to do anything.</summary>
    public const string NotElevated = "notElevated";
    /// <summary>Request file missing, too large, unreadable or not valid JSON / wrong version.</summary>
    public const string BadRequest = "badRequest";
    /// <summary>Request / result paths are not a matching &lt;id&gt;.req.json / &lt;id&gt;.res.json pair.</summary>
    public const string BadPath = "badPath";
    /// <summary>At least one op is outside the allowlist; per-op results carry the reason, nothing ran.</summary>
    public const string Rejected = "rejected";
    /// <summary>Op was valid but not executed because another op of the batch was rejected.</summary>
    public const string NotExecuted = "notExecuted";
    /// <summary>Helper process did not finish within the broker timeout.</summary>
    public const string Timeout = "timeout";
    /// <summary>Helper process could not be started (other than the user declining UAC).</summary>
    public const string LaunchFailed = "launchFailed";
    /// <summary>User clicked "No" on the UAC prompt.</summary>
    public const string Declined = "declined";
    /// <summary>Helper exited without writing a (matching) result file.</summary>
    public const string NoResult = "noResult";
    /// <summary>Elevation is not available (no exe path, not Windows, running under dotnet host).</summary>
    public const string Unsupported = "unsupported";
    public const string Cancelled = "cancelled";
    public const string EmptyBatch = "emptyBatch";

    // Per-op execution failures.
    public const string KeyMissing = "keyMissing";
    public const string AdapterMissing = "adapterMissing";
    public const string WmiFailed = "wmiFailed";
    public const string CommandFailed = "commandFailed";
    public const string ParseFailed = "parseFailed";
    public const string Crashed = "exception";
}

/// <summary>What the unelevated broker writes to <c>&lt;id&gt;.req.json</c>.</summary>
public sealed class ElevationRequest
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    /// <summary>Random id; must equal the file name stem and is echoed in the response.</summary>
    public string RequestId { get; set; } = "";
    public List<ElevatedOp> Ops { get; set; } = new();
}

/// <summary>What the elevated helper writes to <c>&lt;id&gt;.res.json</c>.</summary>
public sealed class ElevationResponse
{
    public int Version { get; set; } = ElevationRequest.CurrentVersion;
    public string RequestId { get; set; } = "";
    public ElevationOutcome Outcome { get; set; }
    public string? ErrorCode { get; set; }
    public List<ElevatedOpResult> Results { get; set; } = new();
}

/// <summary>JSON wire format shared by broker and helper (enums as strings, strict, size-limited).</summary>
public static class ElevationJson
{
    /// <summary>Requests are a handful of ops — anything larger is not ours.</summary>
    public const int MaxFileBytes = 64 * 1024;

    public const string RequestSuffix = ".req.json";
    public const string ResultSuffix = ".res.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        MaxDepth = 8
    };

    public static string Serialize(ElevationRequest request) => JsonSerializer.Serialize(request, Options);

    public static string Serialize(ElevationResponse response) => JsonSerializer.Serialize(response, Options);

    public static bool TryParseRequest(string? json, out ElevationRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxFileBytes) return false;
        try
        {
            var r = JsonSerializer.Deserialize<ElevationRequest>(json, Options);
            if (r == null || r.Version != ElevationRequest.CurrentVersion || r.Ops == null) return false;
            if (r.Ops.Any(o => o == null || o.Target == null)) return false;
            request = r;
            return true;
        }
        catch (JsonException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    public static bool TryParseResponse(string? json, out ElevationResponse? response)
    {
        response = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxFileBytes) return false;
        try
        {
            var r = JsonSerializer.Deserialize<ElevationResponse>(json, Options);
            if (r == null || r.Version != ElevationRequest.CurrentVersion || r.Results == null) return false;
            if (r.Results.Any(x => x == null)) return false;
            response = r;
            return true;
        }
        catch (JsonException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    /// <summary>True when <paramref name="id"/> is a 32-hex "N" GUID (the only accepted file stem).</summary>
    public static bool IsValidRequestId(string? id) =>
        id != null && id.Length == 32 && Guid.TryParseExact(id, "N", out _);
}
