using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Visor.Services.Elevation;

namespace Visor.Services.Optimize.Pc;

/// <summary>One installed power scheme: GUID, raw registry FriendlyName (language-neutral resource form) and display name.</summary>
public sealed record PowerSchemeInfo(string Guid, string? RawFriendlyName, string? FriendlyName);

/// <summary>Read-only seam over the installed power schemes. Tests inject a fake.</summary>
public interface IPowerSchemeSource
{
    IReadOnlyList<PowerSchemeInfo> Enumerate();
    string? ActiveGuid();
}

/// <summary>
/// Real source: <c>PowerEnumerate</c> (only schemes Windows lists — the hidden Ultimate template is not one),
/// the raw <c>FriendlyName</c> from HKLM (readable without admin) and <c>PowerReadFriendlyName</c>.
/// </summary>
public sealed class WindowsPowerSchemeSource : IPowerSchemeSource
{
    public static readonly WindowsPowerSchemeSource Instance = new();

    private const uint ACCESS_SCHEME = 16;
    private const uint ERROR_NO_MORE_ITEMS = 259;
    private const string SchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupGuid,
        uint accessFlags, uint index, [Out] byte[]? buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid,
        IntPtr powerSettingGuid, [Out] byte[]? buffer, ref uint bufferSize);

    public IReadOnlyList<PowerSchemeInfo> Enumerate()
    {
        var list = new List<PowerSchemeInfo>();
        try
        {
            for (uint i = 0; i < 64; i++)
            {
                var buf = new byte[16];
                uint size = (uint)buf.Length;
                uint rc = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, i, buf, ref size);
                if (rc == ERROR_NO_MORE_ITEMS) break;
                if (rc != 0) break;
                var g = new Guid(buf);
                list.Add(new PowerSchemeInfo(g.ToString("D"), ReadRawName(g), ReadFriendlyName(g)));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Power scheme enumerate");
        }
        return list;
    }

    public string? ActiveGuid() => DisplayEngine.GetActivePowerPlanGuid();

    private static string? ReadRawName(Guid g)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(SchemesKey + "\\" + g.ToString("D"), writable: false);
            return key?.GetValue("FriendlyName", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadFriendlyName(Guid g)
    {
        try
        {
            uint size = 0;
            if (PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size == 0 || size > 4096)
                return null;
            var buf = new byte[size];
            if (PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0) return null;
            return System.Text.Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Pure detection of the Ultimate Performance scheme among installed schemes.</summary>
public static class UltimatePlan
{
    /// <summary>The hidden template Windows duplicates (<c>powercfg /duplicatescheme e9a42b02-…</c>); copies get a new GUID.</summary>
    public const string TemplateGuid = ElevationAllowlist.UltimatePerformanceScheme;

    // Duplicates keep the template's language-neutral FriendlyName "@%SystemRoot%\system32\powrprof.dll,-19,Ultimate Performance".
    private static readonly Regex RawNamePattern = new(@"powrprof\.dll,\s*-19(\s*,|\s*$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Display names seen on localized Windows (fallback when the raw name is unreadable).</summary>
    public static readonly IReadOnlyList<string> KnownDisplayNames = new[]
    {
        "Ultimate Performance",
        "Максимальная производительность",
        "Ultimative Leistung",
        "Performances optimales",
        "Máximo rendimiento",
        "Prestazioni eccellenti",
        "Najwyższa wydajność",
        "Desempenho Máximo",
        "Максимальна продуктивність"
    };

    public static bool IsUltimate(PowerSchemeInfo s)
    {
        if (s == null) return false;
        if (string.Equals(s.Guid, TemplateGuid, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(s.RawFriendlyName) && RawNamePattern.IsMatch(s.RawFriendlyName)) return true;
        string? name = s.FriendlyName?.Trim();
        return !string.IsNullOrEmpty(name)
               && KnownDisplayNames.Any(n => string.Equals(n, name, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>
    /// GUID of an installed Ultimate Performance scheme: the one Visor created (when still installed) first,
    /// then any other match; null when none is installed.
    /// </summary>
    public static string? FindInstalled(IEnumerable<PowerSchemeInfo>? schemes, string? createdByVisor = null)
    {
        var list = (schemes ?? Enumerable.Empty<PowerSchemeInfo>()).Where(s => s != null).ToList();
        if (!string.IsNullOrEmpty(createdByVisor))
        {
            var mine = list.FirstOrDefault(s => string.Equals(s.Guid, createdByVisor, StringComparison.OrdinalIgnoreCase));
            if (mine != null) return mine.Guid.ToLowerInvariant();
        }
        return list.FirstOrDefault(IsUltimate)?.Guid.ToLowerInvariant();
    }
}

public enum UltimateCreateStatus
{
    /// <summary>Visor created the scheme just now (GUID stored in pc-tweaks.json).</summary>
    Created,
    /// <summary>An Ultimate Performance scheme already exists — nothing created.</summary>
    AlreadyInstalled,
    /// <summary>User declined UAC — nothing changed.</summary>
    Declined,
    /// <summary>Helper refused / failed, or the new scheme could not be found afterwards.</summary>
    Failed,
    /// <summary>No elevation broker in this build.</summary>
    Unavailable
}

/// <summary>Result of <see cref="UltimatePlanService.CreateAsync"/>; <see cref="Code"/> is a <see cref="SafeActionCodes"/> code on failure.</summary>
public sealed record UltimateCreateResult(UltimateCreateStatus Status, string? Guid = null, string? Code = null);

/// <summary>
/// Detects / creates the Ultimate Performance scheme. Creating is an explicit, separate click: one
/// elevated <see cref="ElevatedOpKind.PowerDuplicateScheme"/> op (the helper allowlists only the
/// template GUID). Activating it is the <see cref="UltimatePlanTarget"/> SafeAction (no admin), undone
/// by switching back to the previous plan. The created scheme is never deleted automatically.
/// </summary>
public sealed class UltimatePlanService
{
    private readonly IPowerSchemeSource _source;
    private readonly IElevationBroker? _broker;
    private readonly PcTweakStore _store;
    private readonly Func<DateTime> _utcNow;

    public UltimatePlanService(IPowerSchemeSource? source = null, IElevationBroker? broker = null,
        PcTweakStore? store = null, Func<DateTime>? utcNow = null)
    {
        _source = source ?? WindowsPowerSchemeSource.Instance;
        _broker = broker;
        _store = store ?? new PcTweakStore(null);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public PcTweakStore Store => _store;

    public string? FindInstalled()
    {
        try { return UltimatePlan.FindInstalled(_source.Enumerate(), _store.CreatedUltimateGuid); }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Ultimate plan detect");
            return null;
        }
    }

    /// <summary>True when the active scheme is an Ultimate Performance scheme.</summary>
    public bool? IsActive()
    {
        try
        {
            var active = _source.ActiveGuid();
            if (active == null) return null;
            var installed = FindInstalled();
            return installed != null && string.Equals(active, installed, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Duplicates the hidden template via the elevated helper (one UAC prompt). Never throws.</summary>
    public async Task<UltimateCreateResult> CreateAsync(CancellationToken ct = default)
    {
        var existing = FindInstalled();
        if (existing != null) return new UltimateCreateResult(UltimateCreateStatus.AlreadyInstalled, existing);
        if (_broker == null) return new UltimateCreateResult(UltimateCreateStatus.Unavailable, Code: SafeActionCodes.ElevationFailed);

        ElevatedBatchResult r;
        try
        {
            r = await _broker.RunAsync(new[]
            {
                new ElevatedOp(ElevatedOpKind.PowerDuplicateScheme, UltimatePlan.TemplateGuid, Value: UltimatePlan.TemplateGuid)
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Ultimate plan create");
            return new UltimateCreateResult(UltimateCreateStatus.Failed, Code: SafeActionCodes.ElevationFailed);
        }

        switch (r.Outcome)
        {
            case ElevationOutcome.Declined:
                return new UltimateCreateResult(UltimateCreateStatus.Declined, Code: SafeActionCodes.ElevationDeclined);
            case ElevationOutcome.Rejected:
                return new UltimateCreateResult(UltimateCreateStatus.Failed, Code: SafeActionCodes.ElevationRejected);
            case ElevationOutcome.Failed:
                return new UltimateCreateResult(UltimateCreateStatus.Failed, Code: SafeActionCodes.ElevationFailed);
        }

        var op = r.Results.Count > 0 ? r.Results[0] : null;
        if (op == null || !op.Ok || !Guid.TryParse(op.Output, out var created))
        {
            AppLog.Warn("Ultimate plan create: helper returned " + (op?.ErrorCode ?? "no result"));
            return new UltimateCreateResult(UltimateCreateStatus.Failed, Code: SafeActionCodes.ApplyFailed);
        }

        string guid = created.ToString("D");
        // Verify the new scheme really exists before remembering it.
        bool listed;
        try
        {
            listed = _source.Enumerate().Any(s => string.Equals(s.Guid, guid, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { listed = false; }
        if (!listed)
        {
            AppLog.Warn("Ultimate plan create: " + guid + " not listed afterwards.");
            return new UltimateCreateResult(UltimateCreateStatus.Failed, guid, SafeActionCodes.VerifyMismatch);
        }
        _store.SetCreatedUltimate(guid, _utcNow());
        AppLog.Info("Ultimate Performance plan created: " + guid);
        return new UltimateCreateResult(UltimateCreateStatus.Created, guid);
    }
}

/// <summary>
/// Activates the installed Ultimate Performance scheme (no admin; <c>PowerSetActiveScheme</c> via the
/// same delegate as the per-game power plan). State = active scheme GUID; undo switches back to the
/// exact previous GUID. Resolves to null (→ <see cref="SafeActionCodes.SchemeMissing"/>) until the scheme
/// exists — creating it is <see cref="UltimatePlanService.CreateAsync"/>. Blocked while a game session
/// owns the power plan.
/// </summary>
public sealed class UltimatePlanTarget : ISafeActionTarget
{
    public const string Id = "ultimatePlan.on";
    public const string Key = "ultimatePlan";

    private readonly Func<string, bool> _setPlan;
    private readonly IPowerSchemeSource _source;
    private readonly Func<string?> _createdGuid;

    public UltimatePlanTarget(Func<string, bool> setPlan, IPowerSchemeSource? source = null, PcTweakStore? store = null)
    {
        _setPlan = setPlan ?? throw new ArgumentNullException(nameof(setPlan));
        _source = source ?? WindowsPowerSchemeSource.Instance;
        _createdGuid = store == null ? () => null : () => store.CreatedUltimateGuid;
    }

    public string ActionId => Id;
    public string JournalKey => Key;
    public bool BlockedDuringGameSession => true;
    public string LocPrefix => "optimize." + Key;

    public string? ReadState() => _source.ActiveGuid()?.ToLowerInvariant();

    public string? ResolveTargetState()
    {
        try { return UltimatePlan.FindInstalled(_source.Enumerate(), _createdGuid()); }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Ultimate plan resolve");
            return null;
        }
    }

    public bool SatisfiesTarget(string state, string targetState) => StatesEqual(state, targetState);

    public bool StatesEqual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public bool WriteState(string state) => Guid.TryParse(state, out _) && _setPlan(state);
}
