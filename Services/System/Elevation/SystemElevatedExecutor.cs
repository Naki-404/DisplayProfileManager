using System.Management;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Visor.Services.Elevation;

/// <summary>Executes ONE already-validated op. The helper never calls it before the whole batch passed the allowlist.</summary>
public interface IElevatedExecutor
{
    ElevatedOpResult Execute(ElevatedOp op);
}

/// <summary>Process / machine facts the helper and broker need (seams for tests).</summary>
public static class ElevationEnvironment
{
    /// <summary>True when this process runs with an elevated (full administrator) token.</summary>
    public static bool IsProcessElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            // Under UAC the filtered token has Administrators as deny-only → IsInRole is false until elevated.
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True when <paramref name="interfaceGuid"/> ("{...}") is the Id of an existing network adapter.</summary>
    public static bool AdapterExists(string interfaceGuid)
    {
        if (!ElevationAllowlist.IsBracedGuid(interfaceGuid)) return false;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Any(n => string.Equals(n.Id, interfaceGuid, StringComparison.OrdinalIgnoreCase));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }
}

/// <summary>
/// The real executor, used only inside the elevated helper process.
/// Registry: HKLM 64-bit view. DNS: WMI <c>Win32_NetworkAdapterConfiguration.SetDNSServerSearchOrder</c>
/// (array = static servers, null = back to DHCP — one API for both directions, no netsh name quoting).
/// TCP: <c>netsh int tcp set global k=v</c>. Power: <c>powercfg /duplicatescheme</c> and <c>powercfg /delete</c>.
/// </summary>
public sealed class SystemElevatedExecutor : IElevatedExecutor
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(20);
    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant);

    /// <summary>Delete-DeliveryOptimizationCache talks to the DO service; leave room under the 60 s broker timeout.</summary>
    internal static readonly TimeSpan DeliveryOptimizationTimeout = TimeSpan.FromSeconds(45);

    private readonly ICommandRunner _runner;
    private readonly ICommandRunner _powerShell;
    private readonly Func<long?> _doCacheBytes;

    /// <param name="runner">System32 console tools (netsh, powercfg).</param>
    /// <param name="powerShell">Windows PowerShell 5.1 runner (<see cref="WindowsPowerShellRunner"/>); tests inject a fake.</param>
    /// <param name="doCacheBytes">Delivery Optimization cache size probe for the freed-bytes figure; tests inject a fake.</param>
    public SystemElevatedExecutor(ICommandRunner? runner = null, ICommandRunner? powerShell = null, Func<long?>? doCacheBytes = null)
    {
        _runner = runner ?? SystemCommandRunner.Instance;
        _powerShell = powerShell ?? WindowsPowerShellRunner.Instance;
        _doCacheBytes = doCacheBytes ?? Optimize.Pc.DeliveryOptimizationCache.ReadBytes;
    }

    public ElevatedOpResult Execute(ElevatedOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        try
        {
            return op.Kind switch
            {
                ElevatedOpKind.RegSetDword or ElevatedOpKind.RegSetString or ElevatedOpKind.RegSetBinary => RegSet(op),
                ElevatedOpKind.RegDeleteValue => RegDelete(op),
                ElevatedOpKind.DnsSetStatic => SetDns(op.Target, op.Value),
                ElevatedOpKind.DnsSetDhcp => SetDns(op.Target, null),
                ElevatedOpKind.TcpGlobalSet => TcpSet(op),
                ElevatedOpKind.PowerDuplicateScheme => PowerDuplicate(op),
                ElevatedOpKind.PowerDeleteScheme => PowerDelete(op),
                ElevatedOpKind.DeliveryOptimizationClear => ClearDeliveryOptimization(),
                _ => new ElevatedOpResult(false, ElevationAllowlist.ReasonKind)
            };
        }
        catch (Exception ex)
        {
            return new ElevatedOpResult(false, ElevationErrorCodes.Crashed, ex.GetType().Name);
        }
    }

    private static RegistryKey OpenHklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    private static ElevatedOpResult RegSet(ElevatedOp op)
    {
        using var hklm = OpenHklm();
        // Interface keys belong to real adapters: never create one. The other allowlisted keys may be
        // missing on a fresh install (StartupApproved\Run) and are created on demand.
        bool mayCreate = !op.Target.StartsWith(ElevationAllowlist.TcpipInterfacesKey + "\\", StringComparison.OrdinalIgnoreCase);
        using var key = hklm.OpenSubKey(op.Target, writable: true)
                        ?? (mayCreate ? hklm.CreateSubKey(op.Target, writable: true) : null);
        if (key == null) return new ElevatedOpResult(false, ElevationErrorCodes.KeyMissing);

        switch (op.Kind)
        {
            case ElevatedOpKind.RegSetDword:
                if (!ElevationAllowlist.TryParseDword(op.Value, out uint dw))
                    return new ElevatedOpResult(false, ElevationAllowlist.ReasonValueData);
                key.SetValue(op.Name!, unchecked((int)dw), RegistryValueKind.DWord);
                break;
            case ElevatedOpKind.RegSetString:
                key.SetValue(op.Name!, op.Value ?? "", RegistryValueKind.String);
                break;
            case ElevatedOpKind.RegSetBinary:
                if (!ElevationAllowlist.TryParseBinary(op.Value, out var bytes))
                    return new ElevatedOpResult(false, ElevationAllowlist.ReasonValueData);
                key.SetValue(op.Name!, bytes, RegistryValueKind.Binary);
                break;
        }
        return new ElevatedOpResult(true);
    }

    private static ElevatedOpResult RegDelete(ElevatedOp op)
    {
        using var hklm = OpenHklm();
        using var key = hklm.OpenSubKey(op.Target, writable: true);
        key?.DeleteValue(op.Name!, throwOnMissingValue: false);   // absent key / value = already deleted
        return new ElevatedOpResult(true);
    }

    private static ElevatedOpResult SetDns(string interfaceGuid, string? csv)
    {
        string[]? servers = null;
        if (csv != null)
        {
            if (!ElevationAllowlist.TryParseDnsServers(csv, out var list))
                return new ElevatedOpResult(false, ElevationAllowlist.ReasonDnsServers);
            servers = list.Select(a => a.ToString()).ToArray();
        }

        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapterConfiguration");
        using var results = searcher.Get();
        foreach (ManagementObject mo in results)
        {
            using (mo)
            {
                if (!string.Equals(mo["SettingID"] as string, interfaceGuid, StringComparison.OrdinalIgnoreCase))
                    continue;
                using var inParams = mo.GetMethodParameters("SetDNSServerSearchOrder");
                // null = clear the static list → Windows falls back to the DHCP-provided servers.
                inParams["DNSServerSearchOrder"] = servers;
                using var outParams = mo.InvokeMethod("SetDNSServerSearchOrder", inParams, null);
                uint rv = Convert.ToUInt32(outParams?["ReturnValue"] ?? 999u, System.Globalization.CultureInfo.InvariantCulture);
                // 0 = done, 1 = done but needs a reboot (never seen for DNS, still a success).
                return rv <= 1
                    ? new ElevatedOpResult(true)
                    : new ElevatedOpResult(false, ElevationErrorCodes.WmiFailed, rv.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return new ElevatedOpResult(false, ElevationErrorCodes.AdapterMissing);
    }

    private ElevatedOpResult TcpSet(ElevatedOp op)
    {
        string target = op.Target.ToLowerInvariant();
        string value = op.Value!.ToLowerInvariant();
        var r = _runner.Run("netsh.exe", new[] { "int", "tcp", "set", "global", target + "=" + value }, ToolTimeout);
        return r.Succeeded
            ? new ElevatedOpResult(true)
            : new ElevatedOpResult(false, r.ExitCode == null ? ElevationErrorCodes.Timeout : ElevationErrorCodes.CommandFailed,
                Trim(r.Output));
    }

    /// <summary><c>powercfg /delete &lt;guid&gt;</c> for a scheme Visor created (allowlist: well-formed, not built-in).</summary>
    private ElevatedOpResult PowerDelete(ElevatedOp op)
    {
        if (!ElevationAllowlist.IsDeletableScheme(op.Value)) return new ElevatedOpResult(false, ElevationAllowlist.ReasonScheme);
        string guid = op.Value!.ToLowerInvariant();
        var r = _runner.Run("powercfg.exe", new[] { "/delete", guid }, ToolTimeout);
        if (!r.Succeeded)
            return new ElevatedOpResult(false, r.ExitCode == null ? ElevationErrorCodes.Timeout : ElevationErrorCodes.CommandFailed,
                Trim(r.Output));
        return new ElevatedOpResult(true);
    }

    private ElevatedOpResult PowerDuplicate(ElevatedOp op)
    {
        string source = ElevationAllowlist.PowerSchemeOf(op).ToLowerInvariant();
        var r = _runner.Run("powercfg.exe", new[] { "/duplicatescheme", source }, ToolTimeout);
        if (!r.Succeeded)
            return new ElevatedOpResult(false, r.ExitCode == null ? ElevationErrorCodes.Timeout : ElevationErrorCodes.CommandFailed,
                Trim(r.Output));
        string? created = ParseDuplicatedScheme(r.Output, source);
        return created == null
            ? new ElevatedOpResult(false, ElevationErrorCodes.ParseFailed, Trim(r.Output))
            : new ElevatedOpResult(true, null, created);
    }

    /// <summary>
    /// Fixed command line, never concatenated with anything: the documented cmdlet, pinned files kept, -Force so it
    /// never waits for a key press. A thrown error ends PowerShell with exit code 1.
    /// </summary>
    public static IReadOnlyList<string> DeliveryOptimizationArguments { get; } = new[]
    {
        "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", DeliveryOptimizationCommand
    };

    public const string DeliveryOptimizationCommand =
        "$ErrorActionPreference = 'Stop'; Delete-DeliveryOptimizationCache -Force | Out-Null";

    private ElevatedOpResult ClearDeliveryOptimization()
    {
        long? before = SafeBytes();
        var r = _powerShell.Run(WindowsPowerShellRunner.ToolName, DeliveryOptimizationArguments, DeliveryOptimizationTimeout);
        if (!r.Succeeded)
            return new ElevatedOpResult(false, r.ExitCode == null ? ElevationErrorCodes.Timeout : ElevationErrorCodes.CommandFailed,
                Trim(r.Output));
        long? after = SafeBytes();
        return before is { } b && after is { } a
            ? new ElevatedOpResult(true, null, Math.Max(0, b - a).ToString(System.Globalization.CultureInfo.InvariantCulture))
            : new ElevatedOpResult(true);
    }

    private long? SafeBytes()
    {
        try { return _doCacheBytes(); }
        catch (Exception) { return null; }
    }

    /// <summary>First GUID in powercfg's (localized) output that is not the source scheme.</summary>
    public static string? ParseDuplicatedScheme(string? output, string sourceGuid)
    {
        if (string.IsNullOrEmpty(output)) return null;
        foreach (Match m in GuidPattern.Matches(output))
        {
            if (!string.Equals(m.Value, sourceGuid, StringComparison.OrdinalIgnoreCase))
                return m.Value.ToLowerInvariant();
        }
        return null;
    }

    private static string Trim(string? s)
    {
        s = (s ?? "").Trim();
        return s.Length <= 300 ? s : s[..300];
    }
}

/// <summary>
/// Runs Windows PowerShell 5.1 from its fixed location (<c>%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe</c>)
/// — only the tool name <see cref="ToolName"/> is accepted, never a search-path lookup. Same rules as
/// <see cref="SystemCommandRunner"/>: hidden, argument list (no shell string building), hard timeout, process tree
/// killed on timeout. Used only inside the elevated helper for <see cref="ElevatedOpKind.DeliveryOptimizationClear"/>.
/// </summary>
public sealed class WindowsPowerShellRunner : ICommandRunner
{
    public const string ToolName = "powershell.exe";
    public static readonly WindowsPowerShellRunner Instance = new();

    public static string FullPath => System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", ToolName);

    public CommandResult Run(string systemTool, IReadOnlyList<string> args, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!string.Equals(systemTool, ToolName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only " + ToolName + " is supported", nameof(systemTool));

        var psi = new System.Diagnostics.ProcessStartInfo(FullPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new System.Diagnostics.Process { StartInfo = psi };
        var sb = new System.Text.StringBuilder();
        var gate = new object();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (gate) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (gate) sb.AppendLine(e.Data); };
        try
        {
            if (!p.Start()) return new CommandResult(null, "");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CommandResult(null, ex.Message);
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 100, int.MaxValue)))
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            lock (gate) return new CommandResult(null, sb.ToString());
        }
        p.WaitForExit(); // flush async readers
        lock (gate) return new CommandResult(p.ExitCode, sb.ToString());
    }
}
