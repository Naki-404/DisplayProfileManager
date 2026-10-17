using System.IO;
using System.Text;

namespace Visor.Services.Elevation;

/// <summary>
/// Body of <c>Visor.exe --elevated &lt;id&gt;.req.json &lt;id&gt;.res.json</c>. Runs before any GUI, mutex,
/// hook, config or service is created, touches none of them (no AppLog either — the caller logs the
/// result), and exits. Order is fixed: refuse unless elevated → check the path pair → parse →
/// validate EVERY op against <see cref="ElevationAllowlist"/> → only then execute → write the result.
/// Exit code 0 = all ops succeeded, 1 = anything else.
/// </summary>
public sealed class ElevatedHelperHost
{
    public const string CliSwitch = "--elevated";

    private readonly Func<bool> _isElevated;
    private readonly Func<string, bool> _adapterExists;
    private readonly IElevatedExecutor _executor;

    public ElevatedHelperHost(Func<bool> isElevated, Func<string, bool> adapterExists, IElevatedExecutor executor)
    {
        _isElevated = isElevated ?? throw new ArgumentNullException(nameof(isElevated));
        _adapterExists = adapterExists ?? throw new ArgumentNullException(nameof(adapterExists));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <summary>Production entry point (App CLI branch).</summary>
    public static int Run(string requestPath, string resultPath) =>
        new ElevatedHelperHost(ElevationEnvironment.IsProcessElevated, ElevationEnvironment.AdapterExists,
            new SystemElevatedExecutor()).Execute(requestPath, resultPath);

    public int Execute(string requestPath, string resultPath)
    {
        // Paths first: without a valid pair there is nowhere safe to even write a refusal.
        if (!TryCheckPathPair(requestPath, resultPath, out string id))
            return 1;

        var response = new ElevationResponse { RequestId = id };
        try
        {
            if (!SafeInvoke(_isElevated))
            {
                response.Outcome = ElevationOutcome.Failed;
                response.ErrorCode = ElevationErrorCodes.NotElevated;
                WriteResult(resultPath, response);
                return 1;
            }

            string? json = ReadRequest(requestPath);
            if (!ElevationJson.TryParseRequest(json, out var request)
                || !string.Equals(request!.RequestId, id, StringComparison.OrdinalIgnoreCase))
            {
                response.Outcome = ElevationOutcome.Failed;
                response.ErrorCode = ElevationErrorCodes.BadRequest;
                WriteResult(resultPath, response);
                return 1;
            }

            var reasons = ElevationAllowlist.ValidateBatch(request.Ops, _adapterExists);
            if (reasons != null)
            {
                response.Outcome = ElevationOutcome.Rejected;
                response.ErrorCode = ElevationErrorCodes.Rejected;
                for (int i = 0; i < request.Ops.Count; i++)
                {
                    string? reason = i < reasons.Count ? reasons[i] : null;
                    response.Results.Add(new ElevatedOpResult(false, reason ?? ElevationErrorCodes.NotExecuted));
                }
                WriteResult(resultPath, response);
                return 1;
            }

            // Every op is independent and reported on its own; the caller re-reads the real state
            // afterwards, so a partial batch is journaled (and undoable) like any other change.
            foreach (var op in request.Ops)
            {
                ElevatedOpResult r;
                try { r = _executor.Execute(op) ?? new ElevatedOpResult(false, ElevationErrorCodes.Crashed); }
                catch (Exception ex) { r = new ElevatedOpResult(false, ElevationErrorCodes.Crashed, ex.GetType().Name); }
                response.Results.Add(r);
            }
            response.Outcome = ElevationOutcome.Completed;
            WriteResult(resultPath, response);
            return response.Results.All(r => r.Ok) ? 0 : 1;
        }
        catch (Exception ex)
        {
            response.Outcome = ElevationOutcome.Failed;
            response.ErrorCode = ElevationErrorCodes.Crashed;
            response.Results.Clear();
            try { WriteResult(resultPath, response); }
            catch (Exception) { /* nothing else we can do */ }
            System.Diagnostics.Debug.WriteLine("Elevated helper: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// Both paths must be fully qualified, sit in the same "...\Visor\elevation" folder (not a
    /// junction / symlink), and be named &lt;id&gt;.req.json / &lt;id&gt;.res.json with the same 32-hex id.
    /// </summary>
    public static bool TryCheckPathPair(string? requestPath, string? resultPath, out string id)
    {
        id = "";
        if (string.IsNullOrEmpty(requestPath) || string.IsNullOrEmpty(resultPath)) return false;
        if (requestPath.Length > 512 || resultPath.Length > 512) return false;
        if (!Path.IsPathFullyQualified(requestPath) || !Path.IsPathFullyQualified(resultPath)) return false;
        if (requestPath.Contains("..", StringComparison.Ordinal) || resultPath.Contains("..", StringComparison.Ordinal)) return false;
        if (requestPath.StartsWith(@"\\", StringComparison.Ordinal) || resultPath.StartsWith(@"\\", StringComparison.Ordinal)) return false;

        string reqName = Path.GetFileName(requestPath);
        string resName = Path.GetFileName(resultPath);
        if (!reqName.EndsWith(ElevationJson.RequestSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!resName.EndsWith(ElevationJson.ResultSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        string reqId = reqName[..^ElevationJson.RequestSuffix.Length];
        string resId = resName[..^ElevationJson.ResultSuffix.Length];
        if (!ElevationJson.IsValidRequestId(reqId) || !string.Equals(reqId, resId, StringComparison.OrdinalIgnoreCase)) return false;

        string? reqDir = Path.GetDirectoryName(requestPath);
        string? resDir = Path.GetDirectoryName(resultPath);
        if (reqDir == null || resDir == null || !string.Equals(reqDir, resDir, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(Path.GetFileName(reqDir), ElevationBroker.DirectoryName, StringComparison.OrdinalIgnoreCase)) return false;
        string? parent = Path.GetDirectoryName(reqDir);
        if (parent == null || !string.Equals(Path.GetFileName(parent), ElevationBroker.AppFolderName, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            // An elevated writer must not follow a junction planted in place of our folder.
            var dirInfo = new DirectoryInfo(reqDir);
            if (!dirInfo.Exists || dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            var parentInfo = new DirectoryInfo(parent);
            if (parentInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        }
        catch (Exception) { return false; }

        id = reqId.ToLowerInvariant();
        return true;
    }

    private static string? ReadRequest(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > ElevationJson.MaxFileBytes) return null;
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Writes to a fresh temp file (CreateNew — never opens an existing file or link) and renames it
    /// into place without overwrite, so a pre-planted file or link at the result path makes the write
    /// fail instead of redirecting an elevated write elsewhere.
    /// </summary>
    internal static void WriteResult(string resultPath, ElevationResponse response)
    {
        string dir = Path.GetDirectoryName(resultPath)!;
        string tmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(ElevationJson.Serialize(response));
                fs.Write(bytes, 0, bytes.Length);
            }
            File.Move(tmp, resultPath, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { /* best effort */ }
        }
    }

    private static bool SafeInvoke(Func<bool> f)
    {
        try { return f(); }
        catch (Exception) { return false; }
    }
}
