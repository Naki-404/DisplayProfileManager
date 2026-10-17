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
/// TCP: <c>netsh int tcp set global k=v</c>. Power: <c>powercfg /duplicatescheme</c>.
/// </summary>
public sealed class SystemElevatedExecutor : IElevatedExecutor
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(20);
    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant);

    private readonly ICommandRunner _runner;

    public SystemElevatedExecutor(ICommandRunner? runner = null)
    {
        _runner = runner ?? SystemCommandRunner.Instance;
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
