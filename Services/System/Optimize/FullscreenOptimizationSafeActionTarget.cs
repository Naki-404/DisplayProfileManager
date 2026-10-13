namespace Visor.Services.Optimize;

/// <summary>
/// "Disable fullscreen optimizations" for one exe — the same thing the Compatibility tab writes:
/// <c>HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers</c>, value name = full exe path
/// (REG_SZ), data = space-separated tokens such as <c>~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE</c>.
/// Apply merges our token into whatever is there (other tokens kept, <c>~</c> prefix kept or added when the
/// value is new); undo restores the exact previous data or deletes the value when Visor created it.
/// Takes effect at the game's next launch. Per-game scope; the action id embeds the exe path.
/// </summary>
public sealed class FullscreenOptimizationSafeActionTarget : RegistrySafeActionTarget
{
    public const string IdPrefix = "fso.disable|";
    public const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    public const string Token = "DISABLEDXMAXIMIZEDWINDOWEDMODE";
    private const string TildePrefix = "~";

    private readonly RegAddress[] _addresses;

    public FullscreenOptimizationSafeActionTarget(string exePath, IOptimizeRegistry? registry = null) : base(registry)
    {
        if (string.IsNullOrWhiteSpace(exePath)) throw new ArgumentException("exe path required", nameof(exePath));
        ExePath = exePath.Trim();
        _addresses = new[] { new RegAddress(LayersKey, ExePath) };
    }

    public string ExePath { get; }

    public static string ActionIdFor(string exePath) => IdPrefix + (exePath ?? "").Trim();

    /// <summary>Parses "fso.disable|C:\path\game.exe"; null for any other id.</summary>
    public static string? ExePathFromActionId(string? actionId)
    {
        if (actionId == null || !actionId.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var path = actionId[IdPrefix.Length..].Trim();
        return path.Length == 0 ? null : path;
    }

    public override string ActionId => ActionIdFor(ExePath);
    public override SafeActionScope Scope => SafeActionScope.PerGame(ExePath);
    /// <summary>Shared Loc prefix for every per-game instance (the exe is a format arg).</summary>
    public override string LocPrefix => "optimize.fso";
    protected override IReadOnlyList<RegAddress> Addresses => _addresses;
    protected override string Label => "Fullscreen optimizations " + ExePath;

    /// <summary>True when the layers data already contains our token (case-insensitive, whole token).</summary>
    public static bool HasToken(string? layers) =>
        Tokens(layers).Any(t => t.Equals(Token, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds the token to existing data, keeping other tokens and the leading "~" when present (added for a new value).</summary>
    public static string MergeToken(string? layers)
    {
        var tokens = Tokens(layers).ToList();
        if (tokens.Any(t => t.Equals(Token, StringComparison.OrdinalIgnoreCase)))
            return layers!.Trim();
        if (tokens.Count == 0)
            return TildePrefix + " " + Token;
        tokens.Add(Token);
        return string.Join(' ', tokens);
    }

    /// <summary>Removes only our token; null when nothing else remains (the value should then be deleted).</summary>
    public static string? RemoveToken(string? layers)
    {
        var rest = Tokens(layers).Where(t => !t.Equals(Token, StringComparison.OrdinalIgnoreCase)).ToList();
        if (rest.Count == 0 || (rest.Count == 1 && rest[0] == TildePrefix)) return null;
        return string.Join(' ', rest);
    }

    private static IEnumerable<string> Tokens(string? layers) =>
        (layers ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    protected override bool Satisfies(RegistryValueSet current)
    {
        var v = current.Get(_addresses[0]);
        return v != null && v.Kind == RegValueKind.String && HasToken(v.Data);
    }

    protected override RegistryValueSet BuildTarget(RegistryValueSet current)
    {
        var v = current.Get(_addresses[0]);
        string existing = v?.Kind == RegValueKind.String ? v.Data : "";
        return new RegistryValueSet().Add(_addresses[0], RegValue.String(MergeToken(existing)));
    }

    /// <summary>Status for the UI / recommendations: true when FSO is disabled for the exe; null when unreadable.</summary>
    public static bool? ReadDisabledFor(string exePath, IOptimizeRegistry? registry = null)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        try
        {
            var v = (registry ?? HkcuOptimizeRegistry.Instance).GetValue(LayersKey, exePath.Trim());
            return v != null && v.Kind == RegValueKind.String && HasToken(v.Data);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Fullscreen optimizations read");
            return null;
        }
    }
}
