namespace Visor.Services.Optimize;

/// <summary>
/// Base for <see cref="ISafeActionTarget"/>s whose whole state is a fixed, ordered list of HKCU values.
/// State strings are <see cref="RegistryValueSet"/> JSON (exact kind + data, absent = null), so undo
/// restores or deletes each value exactly as it was. Subclasses only decide the target set and the
/// semantic "already satisfied" check.
/// </summary>
public abstract class RegistrySafeActionTarget : ISafeActionTarget
{
    protected RegistrySafeActionTarget(IOptimizeRegistry? registry)
    {
        Registry = registry ?? HkcuOptimizeRegistry.Instance;
    }

    protected IOptimizeRegistry Registry { get; }

    public abstract string ActionId { get; }
    public virtual string JournalKey => ActionId;
    public virtual bool BlockedDuringGameSession => false;
    public virtual SafeActionScope Scope => SafeActionScope.Global;
    /// <summary>Loc key prefix (see <see cref="ISafeActionTarget.LocPrefix"/>); per-game targets override with a shared prefix.</summary>
    public virtual string LocPrefix => "optimize." + JournalKey;

    /// <summary>The values this target owns, in journal order.</summary>
    protected abstract IReadOnlyList<RegAddress> Addresses { get; }

    /// <summary>Log label for read / write failures.</summary>
    protected abstract string Label { get; }

    /// <summary>True when the snapshot already has the desired effect (semantic — e.g. absent may equal "on").</summary>
    protected abstract bool Satisfies(RegistryValueSet current);

    /// <summary>The set to write given what is there now (lets a target merge with existing data).</summary>
    protected abstract RegistryValueSet BuildTarget(RegistryValueSet current);

    public RegistryValueSet? ReadSet() => RegistryValueSet.Read(Registry, Addresses, Label);

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

    public bool WriteState(string state)
    {
        if (!TryParseOwn(state, out var set)) return false;
        if (!set.Write(Registry, Label)) return false;
        AfterWrite(set);
        return true;
    }

    /// <summary>Hook for targets that must broadcast a change (SPI / WM_SETTINGCHANGE) after the registry write.</summary>
    protected virtual void AfterWrite(RegistryValueSet written) { }

    /// <summary>Parses a state and rejects sets that describe another target's addresses.</summary>
    protected bool TryParseOwn(string? state, out RegistryValueSet set) =>
        RegistryValueSet.TryParse(state, out set) && set.Covers(Addresses);
}
