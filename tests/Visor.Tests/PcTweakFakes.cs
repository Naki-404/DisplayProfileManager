using System.Globalization;
using Visor.Services.Elevation;
using Visor.Services.Optimize;
using Visor.Services.Optimize.Pc;

namespace Visor.Tests;

/// <summary>Dictionary-backed HKLM reader for the PC tweak tests (never touches the real hive).</summary>
internal sealed class PcFakeMachineRegistry : IMachineRegistryReader
{
    public readonly Dictionary<string, RegValue> Values = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase);

    private static string K(string key, string name) => key + "\\" + name;

    public RegValue? Get(string key, string name) => Values.TryGetValue(K(key, name), out var v) ? v : null;
    public void Set(string key, string name, RegValue v) { Values[K(key, name)] = v; Keys.Add(key); }
    public void Delete(string key, string name) => Values.Remove(K(key, name));

    public RegValue? GetValue(string keyPath, string valueName) => Get(keyPath, valueName);
    public bool KeyExists(string keyPath) => Keys.Contains(keyPath);
}

/// <summary>
/// Fake elevated helper: validates every batch with the real allowlist (like the broker does) and
/// executes registry ops against <see cref="PcFakeMachineRegistry"/>. <see cref="Outcome"/> simulates UAC decline etc.
/// </summary>
internal sealed class PcFakeBroker : IElevationBroker
{
    private readonly PcFakeMachineRegistry? _registry;

    public PcFakeBroker(PcFakeMachineRegistry? registry = null) => _registry = registry;

    public readonly List<IReadOnlyList<ElevatedOp>> Batches = new();
    public ElevationOutcome Outcome = ElevationOutcome.Completed;
    /// <summary>Output for PowerDuplicateScheme ops (the "new" scheme GUID).</summary>
    public string? DuplicateOutput;
    /// <summary>Called after a successful duplicate (lets a test make the new scheme appear).</summary>
    public Action<string>? OnDuplicate;

    public Task<ElevatedBatchResult> RunAsync(IReadOnlyList<ElevatedOp> ops, CancellationToken ct = default)
    {
        Batches.Add(ops);
        if (Outcome != ElevationOutcome.Completed)
            return Task.FromResult(new ElevatedBatchResult(Outcome, Array.Empty<ElevatedOpResult>()));
        if (ElevationAllowlist.ValidateBatch(ops, _ => true) != null)
            return Task.FromResult(new ElevatedBatchResult(ElevationOutcome.Rejected, Array.Empty<ElevatedOpResult>()));
        var results = new List<ElevatedOpResult>();
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case ElevatedOpKind.RegSetDword:
                    ElevationAllowlist.TryParseDword(op.Value, out uint dw);
                    _registry?.Set(op.Target, op.Name!, RegValue.Dword(unchecked((int)dw)));
                    results.Add(new ElevatedOpResult(true));
                    break;
                case ElevatedOpKind.RegSetBinary:
                    _registry?.Set(op.Target, op.Name!, RegValue.Binary(Convert.FromHexString(op.Value!)));
                    results.Add(new ElevatedOpResult(true));
                    break;
                case ElevatedOpKind.RegDeleteValue:
                    _registry?.Delete(op.Target, op.Name!);
                    results.Add(new ElevatedOpResult(true));
                    break;
                case ElevatedOpKind.PowerDuplicateScheme:
                    if (DuplicateOutput == null) results.Add(new ElevatedOpResult(false, "commandFailed"));
                    else
                    {
                        OnDuplicate?.Invoke(DuplicateOutput);
                        results.Add(new ElevatedOpResult(true, null, DuplicateOutput));
                    }
                    break;
                default:
                    results.Add(new ElevatedOpResult(false, "unsupported"));
                    break;
            }
        }
        return Task.FromResult(new ElevatedBatchResult(ElevationOutcome.Completed, results));
    }
}

/// <summary>Fake kernel GPU-scheduling probe.</summary>
internal sealed class PcFakeGpuProbe : IGpuSchedulingProbe
{
    public IReadOnlyList<GpuSchedulingCaps>? Caps;
    public PcFakeGpuProbe(params GpuSchedulingCaps[] caps) => Caps = caps;
    public static PcFakeGpuProbe Unavailable() => new() { Caps = null };
    public IReadOnlyList<GpuSchedulingCaps>? Query() => Caps;

    public static GpuSchedulingCaps Gpu(bool supported, bool enabled, int wddm = 3200) =>
        new("1" + wddm.ToString(CultureInfo.InvariantCulture), wddm, supported, enabled, EnabledByDefault: false);
}

/// <summary>Fake power-scheme list + active scheme.</summary>
internal sealed class PcFakePowerSource : IPowerSchemeSource
{
    public readonly List<PowerSchemeInfo> Schemes = new();
    public string? Active;
    public IReadOnlyList<PowerSchemeInfo> Enumerate() => Schemes.ToList();
    public string? ActiveGuid() => Active;
}

/// <summary>Fake Run / StartupApproved reader for both hives.</summary>
internal sealed class PcFakeStartupReader : IStartupRegistryReader
{
    private readonly Dictionary<(bool, string), List<(string Name, RegValue Value)>> _keys = new();

    public PcFakeStartupReader Add(bool machine, string key, string name, RegValue value)
    {
        var k = (machine, key.ToLowerInvariant());
        if (!_keys.TryGetValue(k, out var list)) _keys[k] = list = new();
        list.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        list.Add((name, value));
        return this;
    }

    public IReadOnlyList<string> GetValueNames(bool machine, string keyPath) =>
        _keys.TryGetValue((machine, keyPath.ToLowerInvariant()), out var l) ? l.Select(x => x.Name).ToList() : Array.Empty<string>();

    public RegValue? GetValue(bool machine, string keyPath, string valueName) =>
        _keys.TryGetValue((machine, keyPath.ToLowerInvariant()), out var l)
            ? l.FirstOrDefault(x => string.Equals(x.Name, valueName, StringComparison.OrdinalIgnoreCase)).Value
            : null;
}

/// <summary>Fake file facts: size / publisher per path, shortcut targets per .lnk path.</summary>
internal sealed class PcFakeFileProbe : IStartupFileProbe
{
    public readonly Dictionary<string, (long? Size, string? Company)> Files = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, (string Target, string Arguments)> Links = new(StringComparer.OrdinalIgnoreCase);

    public (long? Size, string? Company) Describe(string path) =>
        Files.TryGetValue(path, out var f) ? f : (null, null);

    public (string Target, string Arguments)? ResolveShortcut(string lnkPath) =>
        Links.TryGetValue(lnkPath, out var t) ? t : null;
}
