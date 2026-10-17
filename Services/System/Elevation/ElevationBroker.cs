using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Visor.Services.Elevation;

/// <summary>Seam for starting the helper elevated. Tests inject a fake; production uses <see cref="ShellRunAsLauncher"/>.</summary>
public interface IElevatedProcessLauncher
{
    /// <summary>
    /// Starts <paramref name="exePath"/> with <paramref name="args"/> elevated and waits for it to exit.
    /// Throws <see cref="Win32Exception"/> (1223 when the user declines UAC), <see cref="TimeoutException"/>
    /// when it does not exit within <paramref name="timeout"/>.
    /// </summary>
    Task<int> RunElevatedAsync(string exePath, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);
}

/// <summary>ShellExecute "runas" (the only way to get a UAC prompt), hidden window.</summary>
public sealed class ShellRunAsLauncher : IElevatedProcessLauncher
{
    public async Task<int> RunElevatedAsync(string exePath, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            // ShellExecute takes one string; every arg is a path we created (validated: no quotes).
            Arguments = string.Join(" ", args.Select(Quote)),
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.SystemDirectory
        };
        using var p = Process.Start(psi) ?? throw new Win32Exception(ElevationBroker.ErrorLaunchFailed);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Elevated helper did not exit in time");
        }
        try { return p.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    private static string Quote(string a)
    {
        if (a.Contains('"')) throw new ArgumentException("Quotes are not allowed in helper arguments", nameof(a));
        return "\"" + a + "\"";
    }
}

/// <summary>
/// Runs a batch of <see cref="ElevatedOp"/>s in one short-lived elevated helper (<c>Visor.exe --elevated</c>,
/// one UAC prompt per batch). Visor itself never runs elevated. Exchange is file based: the request goes
/// to <c>%LocalAppData%\Visor\elevation\&lt;id&gt;.req.json</c> (folder ACL: current user, Administrators,
/// SYSTEM), the helper writes <c>&lt;id&gt;.res.json</c>, both are deleted afterwards. A batch outside the
/// allowlist is rejected here already — no UAC prompt is shown for something the helper would refuse.
/// </summary>
public sealed class ElevationBroker : IElevationBroker
{
    public const string AppFolderName = "Visor";
    public const string DirectoryName = "elevation";
    public const int ErrorCancelledByUser = 1223;
    internal const int ErrorLaunchFailed = 1155; // ERROR_NO_ASSOCIATION — only used when Process.Start returns null

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly IElevatedProcessLauncher _launcher;
    private readonly string _directory;
    private readonly string? _exePath;
    private readonly Func<string, bool> _adapterExists;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <param name="launcher">Default: ShellExecute runas.</param>
    /// <param name="directory">Exchange folder. Default: %LocalAppData%\Visor\elevation.</param>
    /// <param name="exePath">Helper exe. Default: <see cref="Environment.ProcessPath"/> (Visor.exe).</param>
    /// <param name="adapterExists">Adapter check for the pre-validation. Default: real adapters.</param>
    public ElevationBroker(IElevatedProcessLauncher? launcher = null, string? directory = null, string? exePath = null,
        Func<string, bool>? adapterExists = null, TimeSpan? timeout = null)
    {
        _launcher = launcher ?? new ShellRunAsLauncher();
        _directory = directory ?? DefaultDirectory;
        _exePath = exePath ?? Environment.ProcessPath;
        _adapterExists = adapterExists ?? ElevationEnvironment.AdapterExists;
        _timeout = timeout ?? DefaultTimeout;
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName, DirectoryName);

    public string ExchangeDirectory => _directory;

    /// <summary>
    /// True when a helper can be started: Windows, running from a real Visor exe (not the dotnet host
    /// or a test runner), and the exe exists.
    /// </summary>
    public bool IsSupported
    {
        get
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(_exePath)) return false;
            string name = Path.GetFileNameWithoutExtension(_exePath);
            if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase))
                return false;
            try { return File.Exists(_exePath); }
            catch (Exception) { return false; }
        }
    }

    public async Task<ElevatedBatchResult> RunAsync(IReadOnlyList<ElevatedOp> ops, CancellationToken ct = default)
    {
        if (ops == null || ops.Count == 0)
            return Fail(ElevationErrorCodes.EmptyBatch);
        if (!IsSupported)
            return Fail(ElevationErrorCodes.Unsupported);

        // Same allowlist as the helper: an invalid batch never reaches a UAC prompt.
        var reasons = ElevationAllowlist.ValidateBatch(ops, _adapterExists);
        if (reasons != null)
        {
            AppLog.Warn("Elevation: batch rejected before launch (" + string.Join(",", reasons.Where(r => r != null)) + ")");
            return new ElevatedBatchResult(ElevationOutcome.Rejected,
                ops.Select((_, i) => new ElevatedOpResult(false, (i < reasons.Count ? reasons[i] : null) ?? ElevationErrorCodes.NotExecuted)).ToList(),
                ElevationErrorCodes.Rejected);
        }

        try { await _oneAtATime.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Fail(ElevationErrorCodes.Cancelled); }
        string id = Guid.NewGuid().ToString("N");
        string reqPath = Path.Combine(_directory, id + ElevationJson.RequestSuffix);
        string resPath = Path.Combine(_directory, id + ElevationJson.ResultSuffix);
        try
        {
            if (!PrepareDirectory())
                return Fail(ElevationErrorCodes.LaunchFailed);
            CleanupStale();

            var request = new ElevationRequest { RequestId = id, Ops = ops.ToList() };
            File.WriteAllText(reqPath, ElevationJson.Serialize(request), new UTF8Encoding(false));

            AppLog.Info($"Elevation: requesting {ops.Count} op(s): " + string.Join(", ", ops.Select(Describe)));
            int exitCode;
            try
            {
                exitCode = await _launcher.RunElevatedAsync(_exePath!, new[] { ElevatedHelperHost.CliSwitch, reqPath, resPath },
                    _timeout, ct).ConfigureAwait(false);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelledByUser)
            {
                AppLog.Info("Elevation: UAC prompt declined by the user.");
                return new ElevatedBatchResult(ElevationOutcome.Declined, Array.Empty<ElevatedOpResult>(), ElevationErrorCodes.Declined);
            }
            catch (Win32Exception ex)
            {
                AppLog.Warn(ex, "Elevation: helper launch");
                return Fail(ElevationErrorCodes.LaunchFailed);
            }
            catch (TimeoutException)
            {
                AppLog.Warn("Elevation: helper timed out after " + _timeout.TotalSeconds + " s.");
                return Fail(ElevationErrorCodes.Timeout);
            }
            catch (OperationCanceledException)
            {
                return Fail(ElevationErrorCodes.Cancelled);
            }

            var result = ReadResult(resPath, id, ops.Count);
            AppLog.Info($"Elevation: helper exit {exitCode}, outcome {result.Outcome}"
                        + (result.ErrorCode != null ? " (" + result.ErrorCode + ")" : "")
                        + (result.Results.Count > 0 ? ", ops " + string.Join(",", result.Results.Select(r => r.Ok ? "ok" : r.ErrorCode ?? "fail")) : ""));
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn(ex, "Elevation: exchange files");
            return Fail(ElevationErrorCodes.LaunchFailed);
        }
        finally
        {
            TryDelete(reqPath);
            TryDelete(resPath);
            _oneAtATime.Release();
        }
    }

    /// <summary>Maps the helper's result file to a batch result (pure apart from the file read; internal for tests).</summary>
    internal static ElevatedBatchResult ReadResult(string resPath, string expectedId, int opCount)
    {
        string? json;
        try { json = File.Exists(resPath) ? File.ReadAllText(resPath, Encoding.UTF8) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { json = null; }
        return MapResponse(json, expectedId, opCount);
    }

    internal static ElevatedBatchResult MapResponse(string? json, string expectedId, int opCount)
    {
        if (!ElevationJson.TryParseResponse(json, out var res)
            || !string.Equals(res!.RequestId, expectedId, StringComparison.OrdinalIgnoreCase))
            return Fail(ElevationErrorCodes.NoResult);
        switch (res.Outcome)
        {
            case ElevationOutcome.Completed:
                return res.Results.Count == opCount
                    ? new ElevatedBatchResult(ElevationOutcome.Completed, res.Results, res.ErrorCode)
                    : Fail(ElevationErrorCodes.NoResult);
            case ElevationOutcome.Rejected:
                return new ElevatedBatchResult(ElevationOutcome.Rejected, res.Results, res.ErrorCode ?? ElevationErrorCodes.Rejected);
            case ElevationOutcome.Declined:
                // Only the broker can observe a declined prompt — a helper claiming it is malformed.
                return Fail(ElevationErrorCodes.NoResult);
            default:
                return new ElevatedBatchResult(ElevationOutcome.Failed, res.Results, res.ErrorCode ?? ElevationErrorCodes.NoResult);
        }
    }

    private static ElevatedBatchResult Fail(string code) =>
        new(ElevationOutcome.Failed, Array.Empty<ElevatedOpResult>(), code);

    private static string Describe(ElevatedOp op) =>
        op.Kind + " " + op.Target + (op.Name != null ? "\\" + op.Name : "") + (op.Value != null ? "=" + op.Value : "");

    /// <summary>Creates the exchange folder and restricts it to the current user, Administrators and SYSTEM.</summary>
    private bool PrepareDirectory()
    {
        try
        {
            var di = Directory.CreateDirectory(_directory);
            if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            try
            {
                var sec = new DirectorySecurity();
                sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                var user = WindowsIdentity.GetCurrent().User;
                if (user != null)
                    sec.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                di.SetAccessControl(sec);
            }
            catch (Exception ex)
            {
                // %LocalAppData% is already per-user; a failed tightening is logged, not fatal.
                AppLog.Warn(ex, "Elevation: folder ACL");
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Elevation: folder");
            return false;
        }
    }

    /// <summary>Leftovers of a crashed run (older than an hour) are removed.</summary>
    private void CleanupStale()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_directory))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromHours(1))
                        File.Delete(f);
                }
                catch (Exception) { /* in use / gone */ }
            }
        }
        catch (Exception) { /* best effort */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AppLog.Warn(ex, "Elevation: delete " + Path.GetFileName(path)); }
    }
}
