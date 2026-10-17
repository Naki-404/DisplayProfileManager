using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Visor.Services.Elevation;

/// <summary>
/// The fixed list of things the elevated helper may do. Pure and side-effect free: the helper
/// validates the WHOLE batch with it before executing anything, and the broker runs the same check
/// up front so an invalid batch never even shows a UAC prompt.
/// <para>
/// Registry ops are HKLM only (the hive is implicit — a path carrying a hive prefix is rejected),
/// on a handful of exact keys and value names. Paths are compared after a strict shape check
/// (no "..", no "/", no empty segments, no control characters), never after normalisation, so a
/// traversal attempt can only ever be rejected, not "repaired".
/// </para>
/// </summary>
public static class ElevationAllowlist
{
    public const int MaxOpsPerBatch = 32;
    public const int MaxDnsServers = 4;
    public const int MaxStringValueChars = 2048;
    public const int MaxBinaryValueBytes = 64;
    public const int MaxValueNameChars = 255;

    public const string UltimatePerformanceScheme = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    public const string TcpipInterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    public const string MultimediaSystemProfileKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    public const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    public const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupApprovedRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string StartupApprovedRun32Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    public const string StartupApprovedFolderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    // Rejection reasons (stable codes, returned per op).
    public const string ReasonKind = "badKind";
    public const string ReasonPath = "badPath";
    public const string ReasonValueName = "badValueName";
    public const string ReasonValueData = "badValueData";
    public const string ReasonAdapter = "unknownAdapter";
    public const string ReasonDnsServers = "badDnsServers";
    public const string ReasonTcpTarget = "badTcpSetting";
    public const string ReasonTcpValue = "badTcpValue";
    public const string ReasonScheme = "badScheme";
    public const string ReasonBatch = "badBatch";

    private static readonly string[] InterfaceValueNames = { "TcpAckFrequency", "TCPNoDelay", "TcpDelAckTicks" };
    private static readonly string[] SystemProfileValueNames = { "NetworkThrottlingIndex", "SystemResponsiveness" };
    private static readonly string[] GraphicsDriversValueNames = { "HwSchMode" };

    /// <summary>netsh int tcp set global targets and their allowed values ("experimental" auto-tuning is deliberately excluded).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> TcpGlobalValues =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["autotuninglevel"] = new[] { "normal", "disabled", "highlyrestricted", "restricted" },
            ["ecncapability"] = new[] { "enabled", "disabled", "default" },
            ["rss"] = new[] { "enabled", "disabled" },
        };

    private enum KeyClass { None, TcpipInterface, SystemProfile, GraphicsDrivers, Run, StartupApprovedRun }

    /// <summary>
    /// Validates every op. Returns null when the whole batch is allowed, else one reason per op
    /// (null for ops that were fine on their own).
    /// </summary>
    /// <param name="adapterExists">Seam: true when an interface GUID ("{...}") belongs to an existing adapter.</param>
    public static IReadOnlyList<string?>? ValidateBatch(IReadOnlyList<ElevatedOp>? ops, Func<string, bool> adapterExists)
    {
        ArgumentNullException.ThrowIfNull(adapterExists);
        if (ops == null || ops.Count == 0 || ops.Count > MaxOpsPerBatch)
        {
            int n = ops?.Count ?? 0;
            return n == 0 ? new string?[] { ReasonBatch } : Enumerable.Repeat<string?>(ReasonBatch, n).ToList();
        }
        var reasons = new string?[ops.Count];
        bool any = false;
        for (int i = 0; i < ops.Count; i++)
        {
            reasons[i] = Validate(ops[i], adapterExists);
            any |= reasons[i] != null;
        }
        return any ? reasons : null;
    }

    /// <summary>Null when <paramref name="op"/> is allowed, else a rejection reason code.</summary>
    public static string? Validate(ElevatedOp? op, Func<string, bool> adapterExists)
    {
        ArgumentNullException.ThrowIfNull(adapterExists);
        if (op == null || op.Target == null) return ReasonBatch;
        if (!Enum.IsDefined(op.Kind)) return ReasonKind;
        return op.Kind switch
        {
            ElevatedOpKind.RegSetDword or ElevatedOpKind.RegSetString or ElevatedOpKind.RegSetBinary
                or ElevatedOpKind.RegDeleteValue => ValidateRegistry(op),
            ElevatedOpKind.DnsSetStatic => ValidateDns(op, adapterExists, isStatic: true),
            ElevatedOpKind.DnsSetDhcp => ValidateDns(op, adapterExists, isStatic: false),
            ElevatedOpKind.TcpGlobalSet => ValidateTcp(op),
            ElevatedOpKind.PowerDuplicateScheme => ValidatePower(op),
            _ => ReasonKind
        };
    }

    // ---------------- registry ----------------

    private static string? ValidateRegistry(ElevatedOp op)
    {
        var cls = ClassifyKey(op.Target);
        if (cls == KeyClass.None) return ReasonPath;
        if (!IsSafeValueName(op.Name)) return ReasonValueName;
        string name = op.Name!;

        // Which kinds each key accepts.
        bool kindOk = cls switch
        {
            KeyClass.TcpipInterface or KeyClass.SystemProfile or KeyClass.GraphicsDrivers =>
                op.Kind is ElevatedOpKind.RegSetDword or ElevatedOpKind.RegDeleteValue,
            KeyClass.Run => op.Kind is ElevatedOpKind.RegSetString or ElevatedOpKind.RegDeleteValue,
            KeyClass.StartupApprovedRun => op.Kind is ElevatedOpKind.RegSetBinary or ElevatedOpKind.RegDeleteValue,
            _ => false
        };
        if (!kindOk) return ReasonKind;

        // Which value names each key accepts (Run / StartupApproved: any safe name).
        string[]? names = cls switch
        {
            KeyClass.TcpipInterface => InterfaceValueNames,
            KeyClass.SystemProfile => SystemProfileValueNames,
            KeyClass.GraphicsDrivers => GraphicsDriversValueNames,
            _ => null
        };
        if (names != null && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) return ReasonValueName;

        return op.Kind switch
        {
            ElevatedOpKind.RegSetDword => TryParseDword(op.Value, out _) ? null : ReasonValueData,
            ElevatedOpKind.RegSetString => IsSafeString(op.Value) ? null : ReasonValueData,
            ElevatedOpKind.RegSetBinary => TryParseBinary(op.Value, out _) ? null : ReasonValueData,
            ElevatedOpKind.RegDeleteValue => op.Value == null ? null : ReasonValueData,
            _ => ReasonKind
        };
    }

    private static KeyClass ClassifyKey(string? path)
    {
        if (!IsWellFormedKeyPath(path)) return KeyClass.None;
        string p = path!;
        if (Eq(p, MultimediaSystemProfileKey)) return KeyClass.SystemProfile;
        if (Eq(p, GraphicsDriversKey)) return KeyClass.GraphicsDrivers;
        if (Eq(p, RunKey)) return KeyClass.Run;
        // Run / Run32 / StartupFolder approval flags share one shape: binary flag bytes or delete.
        if (Eq(p, StartupApprovedRunKey) || Eq(p, StartupApprovedRun32Key) || Eq(p, StartupApprovedFolderKey))
            return KeyClass.StartupApprovedRun;
        string prefix = TcpipInterfacesKey + "\\";
        if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string leaf = p[prefix.Length..];
            if (IsBracedGuid(leaf)) return KeyClass.TcpipInterface;
        }
        return KeyClass.None;
    }

    /// <summary>Relative HKLM path: segments separated by single backslashes, no hive prefix, no dots-only segments.</summary>
    internal static bool IsWellFormedKeyPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 255) return false;
        if (path.Contains('/') || path.Contains(':')) return false;
        foreach (char c in path)
            if (char.IsControl(c)) return false;
        var segments = path.Split('\\');
        foreach (var s in segments)
        {
            if (s.Length == 0) return false;                     // leading / trailing / double backslash
            if (s.Trim().Length != s.Length) return false;       // padded segments
            if (s.Trim('.').Length == 0) return false;           // ".", "..", "..."
        }
        string first = segments[0];
        if (first.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase)
            || first.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
            || first.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
            || first.Equals("HKU", StringComparison.OrdinalIgnoreCase)
            || first.Equals("HKCR", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Registry", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool IsSafeValueName(string? name)
    {
        // Empty name = the key's default value — never ours.
        if (string.IsNullOrEmpty(name) || name.Length > MaxValueNameChars) return false;
        foreach (char c in name)
            if (char.IsControl(c)) return false;
        return true;
    }

    private static bool IsSafeString(string? value)
    {
        if (value == null || value.Length > MaxStringValueChars) return false;
        foreach (char c in value)
            if (c == '\0' || (char.IsControl(c) && c != '\t')) return false;
        return true;
    }

    /// <summary>Decimal DWORD: unsigned (0..4294967295) or signed (-2147483648..-1, journal form of e.g. 0xFFFFFFFF).</summary>
    public static bool TryParseDword(string? text, out uint value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text) || text.Length > 11) return false;
        if (uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return true;
        if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int signed) && signed < 0)
        {
            value = unchecked((uint)signed);
            return true;
        }
        return false;
    }

    public static bool TryParseBinary(string? hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (hex == null || hex.Length == 0 || hex.Length % 2 != 0 || hex.Length > MaxBinaryValueBytes * 2) return false;
        foreach (char c in hex)
            if (!Uri.IsHexDigit(c)) return false;
        bytes = Convert.FromHexString(hex);
        return true;
    }

    // ---------------- DNS ----------------

    private static string? ValidateDns(ElevatedOp op, Func<string, bool> adapterExists, bool isStatic)
    {
        if (op.Name != null) return ReasonValueName;
        if (!IsBracedGuid(op.Target)) return ReasonAdapter;
        if (isStatic)
        {
            if (!TryParseDnsServers(op.Value, out _)) return ReasonDnsServers;
        }
        else if (!string.IsNullOrEmpty(op.Value)) return ReasonDnsServers;

        bool exists;
        try { exists = adapterExists(op.Target); }
        catch { exists = false; }
        return exists ? null : ReasonAdapter;
    }

    /// <summary>
    /// "a.b.c.d,e.f.g.h" → 1..4 canonical IPv4 unicast addresses (the WMI DNS API is IPv4 only).
    /// Non-canonical spellings ("1", "001.1.1.1", "0x01010101") are rejected rather than reinterpreted.
    /// </summary>
    public static bool TryParseDnsServers(string? csv, out IReadOnlyList<IPAddress> servers)
    {
        servers = Array.Empty<IPAddress>();
        if (string.IsNullOrWhiteSpace(csv) || csv.Length > 4 * 16) return false;
        var parts = csv.Split(',');
        if (parts.Length == 0 || parts.Length > MaxDnsServers) return false;
        var list = new List<IPAddress>(parts.Length);
        foreach (var raw in parts)
        {
            string s = raw.Trim();
            if (!IsCanonicalIPv4(s, out var ip)) return false;
            if (list.Contains(ip!)) return false;
            list.Add(ip!);
        }
        servers = list;
        return true;
    }

    public static bool IsCanonicalIPv4(string? s, out IPAddress? ip)
    {
        ip = null;
        if (string.IsNullOrEmpty(s) || s.Length > 15) return false;
        if (!IPAddress.TryParse(s, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork) return false;
        if (!string.Equals(parsed.ToString(), s, StringComparison.Ordinal)) return false;
        var b = parsed.GetAddressBytes();
        // 0.0.0.0/8, multicast, reserved and broadcast are never DNS servers. Loopback and private
        // ranges stay allowed: undo must be able to restore e.g. a local DNS proxy on 127.0.0.1.
        if (b[0] == 0 || b[0] >= 224) return false;
        ip = parsed;
        return true;
    }

    // ---------------- TCP / power ----------------

    private static string? ValidateTcp(ElevatedOp op)
    {
        if (op.Name != null) return ReasonValueName;
        if (!TcpGlobalValues.TryGetValue(op.Target, out var allowed)) return ReasonTcpTarget;
        if (op.Value == null || !allowed.Contains(op.Value, StringComparer.OrdinalIgnoreCase)) return ReasonTcpValue;
        return null;
    }

    private static string? ValidatePower(ElevatedOp op)
    {
        if (op.Name != null) return ReasonValueName;
        string? scheme = string.IsNullOrEmpty(op.Value) ? op.Target : op.Value;
        if (!string.Equals(scheme, UltimatePerformanceScheme, StringComparison.OrdinalIgnoreCase)) return ReasonScheme;
        if (op.Target.Length != 0 && !string.Equals(op.Target, UltimatePerformanceScheme, StringComparison.OrdinalIgnoreCase))
            return ReasonScheme;
        return null;
    }

    /// <summary>The scheme GUID a valid <see cref="ElevatedOpKind.PowerDuplicateScheme"/> op duplicates.</summary>
    public static string PowerSchemeOf(ElevatedOp op) => string.IsNullOrEmpty(op.Value) ? op.Target : op.Value;

    // ---------------- helpers ----------------

    /// <summary>"{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}" exactly (the form Windows uses for interface keys).</summary>
    public static bool IsBracedGuid(string? s) =>
        s != null && s.Length == 38 && Guid.TryParseExact(s, "B", out _);

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
