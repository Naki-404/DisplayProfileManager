namespace Visor.Services.Optimize;

/// <summary>
/// Xbox Game Bar background features off (HKCU only, no admin):
/// <list type="bullet">
/// <item><c>HKCU\Software\Microsoft\GameBar\UseNexusForGameBarEnabled</c> = 0 — the Xbox / Guide button no longer launches Game Bar.</item>
/// <item><c>HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR\AppCaptureEnabled</c> = 0 — "Record game clips, screenshots and broadcast using Game bar" off.</item>
/// <item><c>HKCU\System\GameConfigStore\GameDVR_Enabled</c> = 0 — Game DVR background recording off.</item>
/// </list>
/// Windows treats an absent value as enabled, so "absent" is journaled and restored as "delete the value".
/// </summary>
public sealed class GameBarSafeActionTarget : RegistrySafeActionTarget
{
    public const string Id = "gameBar.off";

    public const string GameBarKey = @"Software\Microsoft\GameBar";
    public const string GameDvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    public const string GameConfigStoreKey = @"System\GameConfigStore";

    public static readonly RegAddress UseNexus = new(GameBarKey, "UseNexusForGameBarEnabled");
    public static readonly RegAddress AppCapture = new(GameDvrKey, "AppCaptureEnabled");
    public static readonly RegAddress GameDvrEnabled = new(GameConfigStoreKey, "GameDVR_Enabled");

    private static readonly RegAddress[] All = { UseNexus, AppCapture, GameDvrEnabled };

    public GameBarSafeActionTarget(IOptimizeRegistry? registry = null) : base(registry) { }

    public override string ActionId => Id;
    protected override IReadOnlyList<RegAddress> Addresses => All;
    protected override string Label => "Game Bar background";

    /// <summary>Absent = enabled (Windows default); any non-zero DWORD = enabled.</summary>
    public static bool IsEnabled(RegValue? value) => (value?.AsDword ?? 1) != 0;

    /// <summary>True when at least one of the three background features is still on.</summary>
    public static bool AnyBackgroundOn(RegistryValueSet set) =>
        All.Any(a => IsEnabled(set.Get(a)));

    protected override bool Satisfies(RegistryValueSet current) => !AnyBackgroundOn(current);

    protected override RegistryValueSet BuildTarget(RegistryValueSet current)
    {
        var set = new RegistryValueSet();
        foreach (var a in All) set.Add(a, RegValue.Dword(0));
        return set;
    }

    /// <summary>Status for the UI / recommendations: true when any Game Bar background feature is on; null when unreadable.</summary>
    public static bool? ReadBackgroundOn(IOptimizeRegistry? registry = null)
    {
        var set = RegistryValueSet.Read(registry ?? HkcuOptimizeRegistry.Instance, All, "Game Bar background");
        return set == null ? null : AnyBackgroundOn(set);
    }
}
