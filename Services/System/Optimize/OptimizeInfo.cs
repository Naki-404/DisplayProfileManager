using Visor.Models;

namespace Visor.Services.Optimize;

/// <summary>Why an Optimize card has no Apply button.</summary>
public enum OptimizeInfoKind
{
    /// <summary>Needs HKLM / elevation — changed only through the elevated helper (one UAC prompt per change).</summary>
    RequiresAdmin,
    /// <summary>Already handled per game by the profile system (power plan) — read-only status.</summary>
    ManagedByProfile,
    /// <summary>Deliberately left to the Windows dialog (see the Loc "why" text).</summary>
    ManualOnly
}

/// <summary>
/// A read-only Optimize card: title / why / risk keys like a SafeAction, plus a tri-state status and
/// evidence strings for the UI. Never has an action.
/// </summary>
public sealed record OptimizeInfoItem
{
    public required string Id { get; init; }
    public required OptimizeInfoKind Kind { get; init; }
    /// <summary>Loc key prefix: {LocPrefix}.title / .why / .risk / .status.on / .status.off / .status.unknown.</summary>
    public string LocPrefix => "optimize." + Id;
    public string TitleKey => LocPrefix + ".title";
    public string WhyKey => LocPrefix + ".why";
    public string RiskKey => LocPrefix + ".risk";
    /// <summary>true = feature on, false = off, null = not readable / not applicable.</summary>
    public bool? Status { get; init; }
    public string StatusKey => LocPrefix + ".status." + (Status switch { true => "on", false => "off", null => "unknown" });
    /// <summary>Raw numbers / names behind the status (not localized).</summary>
    public string[] Evidence { get; init; } = Array.Empty<string>();
}

/// <summary>Read-only "managed by profile" summary of the per-game power plan feature.</summary>
public sealed record PowerPlanManagedStatus(int EnabledProfiles, int ProfilesWithPlan, IReadOnlyList<string> Plans)
{
    public bool Managed => ProfilesWithPlan > 0;
}

/// <summary>Per-profile fullscreen-optimization status fed to the recommendation engine.</summary>
public sealed record GameFsoStatus(string ProfileName, string ExePath, bool? FsoDisabled);

/// <summary>Read-only status readers behind the Optimize cards (pure given an injected registry / profiles).</summary>
public static class OptimizeStatus
{
    public const string HagsId = "hags";
    public const string PowerPerGameId = "powerPerGame";
    public const string VisualEffectsId = "visualEffects";
    /// <summary>In-app visual effects action (<see cref="Pc.VisualEffectsSafeActionTarget"/>, journal key / Loc prefix "optimize.visualFx").</summary>
    public const string VisualFxId = Pc.VisualEffectsSafeActionTarget.Key;
    /// <summary>Delivery Optimization cache row in Cleanup (<see cref="Pc.DeliveryOptimizationService"/>, Loc prefix "optimize.doCache").</summary>
    public const string DeliveryOptimizationId = "doCache";
    /// <summary>PC tweak panels (Services\System\Optimize\Pc) — Loc prefix "optimize.{id}".</summary>
    public const string StartupAppsId = "startupApps";
    public const string CleanupId = "cleanup";
    public const string UltimatePlanId = "ultimatePlan";

    public const string HagsKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    public const string HagsValue = "HwSchMode";

    /// <summary>Hardware-accelerated GPU scheduling: HKLM HwSchMode 2 = on, 1 = off; absent / other = unknown. Read-only.</summary>
    public static bool? ReadHags(IOptimizeRegistry? registry = null)
    {
        try
        {
            var v = (registry ?? HkcuOptimizeRegistry.Instance).GetMachineValue(HagsKey, HagsValue);
            return v?.AsDword switch { 2 => true, 1 => false, _ => null };
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "HAGS read");
            return null;
        }
    }

    public static OptimizeInfoItem HagsItem(IOptimizeRegistry? registry = null)
    {
        bool? on = ReadHags(registry);
        return new OptimizeInfoItem
        {
            Id = HagsId,
            Kind = OptimizeInfoKind.RequiresAdmin,
            Status = on,
            Evidence = on == null ? Array.Empty<string>() : new[] { HagsValue + "=" + (on.Value ? 2 : 1) }
        };
    }

    /// <summary>
    /// HAGS card from the combined status (<see cref="Pc.HagsStatusReader.Read"/>): Status = what runs now
    /// (kernel), falling back to the registry request; evidence carries HwSchMode, running state, WDDM
    /// version and "pendingRestart" when the registry asks for a state the kernel does not run yet.
    /// Changing it is the elevated <see cref="Pc.HagsTarget"/> (hags.on / hags.off).
    /// </summary>
    public static OptimizeInfoItem HagsItem(Pc.HagsStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new OptimizeInfoItem
        {
            Id = HagsId,
            Kind = OptimizeInfoKind.RequiresAdmin,
            Status = status.Supported == false ? null : status.EffectiveOn,
            Evidence = status.Evidence()
        };
    }

    public static PowerPlanManagedStatus PowerPlanManaged(IEnumerable<GameProfile>? profiles)
    {
        var list = (profiles ?? Enumerable.Empty<GameProfile>()).Where(p => p != null).ToList();
        int enabled = list.Count(p => p.Enabled);
        var withPlan = list.Where(p => p.Enabled && p.ApplyPowerPlan && !string.IsNullOrWhiteSpace(p.PowerPlan)).ToList();
        var plans = withPlan.Select(p => p.PowerPlan!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        return new PowerPlanManagedStatus(enabled, withPlan.Count, plans);
    }

    public static OptimizeInfoItem PowerPerGameItem(IEnumerable<GameProfile>? profiles)
    {
        var s = PowerPlanManaged(profiles);
        return new OptimizeInfoItem
        {
            Id = PowerPerGameId,
            Kind = OptimizeInfoKind.ManagedByProfile,
            Status = s.Managed,
            Evidence = new[] { "profiles=" + s.ProfilesWithPlan + "/" + s.EnabledProfiles }
                .Concat(s.Plans.Select(p => "plan=" + p)).ToArray()
        };
    }

    /// <summary>
    /// Legacy read-only card from before visual effects became an in-app action. The Graphics section now renders
    /// <see cref="Pc.VisualEffectsSafeActionTarget"/> (<see cref="VisualFxId"/>) instead; kept for the info-card API.
    /// </summary>
    public static OptimizeInfoItem VisualEffectsItem() => new()
    {
        Id = VisualEffectsId,
        Kind = OptimizeInfoKind.ManualOnly,
        Status = null
    };

    /// <summary>Every read-only card in display order.</summary>
    public static IReadOnlyList<OptimizeInfoItem> InfoItems(IEnumerable<GameProfile>? profiles, IOptimizeRegistry? registry = null) =>
        new[] { PowerPerGameItem(profiles), HagsItem(registry), VisualEffectsItem() };

    /// <summary>FSO status for each enabled profile that has an exe path (duplicates by path collapsed).</summary>
    public static IReadOnlyList<GameFsoStatus> FsoStatuses(IEnumerable<GameProfile>? profiles, IOptimizeRegistry? registry = null)
    {
        var result = new List<GameFsoStatus>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in profiles ?? Enumerable.Empty<GameProfile>())
        {
            if (p == null || !p.Enabled || string.IsNullOrWhiteSpace(p.ExePath)) continue;
            string exe = p.ExePath.Trim();
            if (!seen.Add(exe)) continue;
            result.Add(new GameFsoStatus(p.Name ?? "", exe, FullscreenOptimizationSafeActionTarget.ReadDisabledFor(exe, registry)));
        }
        return result;
    }
}
