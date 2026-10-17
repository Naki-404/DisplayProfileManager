using System.Globalization;
using Microsoft.Win32;
using Visor.Services.Optimize;

namespace Visor.Services.Elevation;

/// <summary>
/// Read-only HKLM seam (64-bit view). Reading these keys needs no admin rights; writing always goes
/// through the elevated helper. Tests inject a dictionary-backed fake.
/// </summary>
public interface IMachineRegistryReader
{
    /// <summary>The value exactly as stored, or null when the value (or its key) is absent.</summary>
    RegValue? GetValue(string keyPath, string valueName);

    bool KeyExists(string keyPath);
}

public sealed class MachineRegistryReader : IMachineRegistryReader
{
    public static readonly MachineRegistryReader Instance = new();

    private static RegistryKey OpenHklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public RegValue? GetValue(string keyPath, string valueName)
    {
        using var hklm = OpenHklm();
        using var key = hklm.OpenSubKey(keyPath, writable: false);
        if (key == null) return null;
        var raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw == null) return null;
        return key.GetValueKind(valueName) switch
        {
            RegistryValueKind.DWord => RegValue.Dword(Convert.ToInt32(raw, CultureInfo.InvariantCulture)),
            RegistryValueKind.String => RegValue.String(raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture) ?? ""),
            RegistryValueKind.Binary => RegValue.Binary(raw as byte[] ?? Array.Empty<byte>()),
            _ => new RegValue(RegValueKind.Other, Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "")
        };
    }

    public bool KeyExists(string keyPath)
    {
        using var hklm = OpenHklm();
        using var key = hklm.OpenSubKey(keyPath, writable: false);
        return key != null;
    }
}

/// <summary>Maps elevation outcomes to <see cref="SafeActionCodes"/> and registry snapshots to ops.</summary>
public static class ElevatedWrites
{
    /// <summary>Ops that turn the live values into <paramref name="set"/> exactly (absent → delete). Null when a value kind cannot be written.</summary>
    public static IReadOnlyList<ElevatedOp>? OpsFor(RegistryValueSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var ops = new List<ElevatedOp>(set.Items.Count);
        foreach (var (addr, value) in set.Items)
        {
            if (value == null)
            {
                ops.Add(new ElevatedOp(ElevatedOpKind.RegDeleteValue, addr.KeyPath, addr.ValueName));
                continue;
            }
            switch (value.Kind)
            {
                case RegValueKind.Dword:
                    int? dw = value.AsDword;
                    if (dw == null) return null;
                    ops.Add(new ElevatedOp(ElevatedOpKind.RegSetDword, addr.KeyPath, addr.ValueName,
                        unchecked((uint)dw.Value).ToString(CultureInfo.InvariantCulture)));
                    break;
                case RegValueKind.String:
                    ops.Add(new ElevatedOp(ElevatedOpKind.RegSetString, addr.KeyPath, addr.ValueName, value.Data));
                    break;
                case RegValueKind.Binary:
                    ops.Add(new ElevatedOp(ElevatedOpKind.RegSetBinary, addr.KeyPath, addr.ValueName, value.Data));
                    break;
                default:
                    return null; // opaque kinds are snapshot-only
            }
        }
        return ops;
    }

    /// <summary>Success, or the <see cref="SafeActionCodes"/> code that explains why the batch did not run / failed.</summary>
    public static SafeActionWriteResult ToWriteResult(ElevatedBatchResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return r.Outcome switch
        {
            ElevationOutcome.Completed => r.AllOk
                ? SafeActionWriteResult.Ok
                : SafeActionWriteResult.Fail(SafeActionCodes.ApplyFailed),
            ElevationOutcome.Declined => SafeActionWriteResult.Fail(SafeActionCodes.ElevationDeclined),
            ElevationOutcome.Rejected => SafeActionWriteResult.Fail(SafeActionCodes.ElevationRejected),
            _ => SafeActionWriteResult.Fail(SafeActionCodes.ElevationFailed)
        };
    }

    /// <summary>Runs <paramref name="ops"/> through the broker, never throwing.</summary>
    public static async Task<SafeActionWriteResult> RunAsync(IElevationBroker broker, IReadOnlyList<ElevatedOp>? ops,
        CancellationToken ct, string label)
    {
        ArgumentNullException.ThrowIfNull(broker);
        if (ops == null || ops.Count == 0) return SafeActionWriteResult.Fail(SafeActionCodes.ApplyFailed);
        try
        {
            var r = await broker.RunAsync(ops, ct).ConfigureAwait(false);
            return ToWriteResult(r);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, label + " elevated write");
            return SafeActionWriteResult.Fail(SafeActionCodes.ElevationFailed);
        }
    }
}

/// <summary>
/// Base for HKLM targets: state is a <see cref="RegistryValueSet"/> (exact kind + data, absent = null)
/// read without admin rights; every write — apply and undo — is one elevated batch through
/// <see cref="IElevationBroker"/> (one UAC prompt). <see cref="SafeActionService.ApplyAsync"/> re-reads to verify.
/// </summary>
public abstract class ElevatedRegistrySafeActionTarget : ISafeActionTarget
{
    protected ElevatedRegistrySafeActionTarget(IElevationBroker broker, IMachineRegistryReader? registry)
    {
        Broker = broker ?? throw new ArgumentNullException(nameof(broker));
        Registry = registry ?? MachineRegistryReader.Instance;
    }

    protected IElevationBroker Broker { get; }
    protected IMachineRegistryReader Registry { get; }

    public abstract string ActionId { get; }
    public virtual string JournalKey => ActionId;
    public virtual bool BlockedDuringGameSession => false;
    public virtual SafeActionScope Scope => SafeActionScope.Global;
    public abstract string LocPrefix { get; }
    public bool RequiresElevation => true;

    /// <summary>The HKLM values this target owns, in journal order.</summary>
    protected abstract IReadOnlyList<RegAddress> Addresses { get; }

    /// <summary>Key that must exist for the target to apply (e.g. the adapter's interface key). Null = none.</summary>
    protected virtual string? RequiredKey => null;

    protected abstract string Label { get; }

    protected abstract bool Satisfies(RegistryValueSet current);

    protected abstract RegistryValueSet BuildTarget(RegistryValueSet current);

    public RegistryValueSet? ReadSet()
    {
        try
        {
            if (RequiredKey != null && !Registry.KeyExists(RequiredKey)) return null;
            var set = new RegistryValueSet();
            foreach (var a in Addresses)
                set.Add(a, Registry.GetValue(a.KeyPath, a.ValueName));
            return set;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, Label + " read");
            return null;
        }
    }

    public string? ReadState() => ReadSet()?.Serialize();

    public string? ResolveTargetState()
    {
        var current = ReadSet();
        return current == null ? null : BuildTarget(current).Serialize();
    }

    public bool SatisfiesTarget(string state, string targetState) =>
        TryParseOwn(state, out var s) && Satisfies(s);

    public bool StatesEqual(string a, string b) =>
        TryParseOwn(a, out var x) && TryParseOwn(b, out var y) && x.EqualsExactly(y);

    /// <summary>Synchronous writes would block on a UAC prompt — elevated targets only write via <see cref="WriteStateAsync"/>.</summary>
    public bool WriteState(string state) => false;

    public Task<SafeActionWriteResult> WriteStateAsync(string state, CancellationToken ct)
    {
        if (!TryParseOwn(state, out var set))
            return Task.FromResult(SafeActionWriteResult.Fail(SafeActionCodes.BadBackup));
        return ElevatedWrites.RunAsync(Broker, ElevatedWrites.OpsFor(set), ct, Label);
    }

    protected bool TryParseOwn(string? state, out RegistryValueSet set) =>
        RegistryValueSet.TryParse(state, out set) && set.Covers(Addresses);
}
