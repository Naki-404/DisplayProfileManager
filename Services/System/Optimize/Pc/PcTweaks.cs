using Visor.Services.Elevation;

namespace Visor.Services.Optimize.Pc;

/// <summary>
/// Wiring for the PC tweaks. Nothing here applies anything: it only registers SafeAction targets so the
/// UI can call <see cref="SafeActionService.ApplyAsync"/> / <see cref="SafeActionService.UndoAsync"/> on a click.
/// <code>
/// var pc = PcTweaks.Create(configDir, broker, setPlan: Display.SetPowerPlan);
/// pc.Register(safeActions);                      // hags.on/off, ultimatePlan.on, journaled startup toggles
/// var apps = pc.Startup.List(); pc.Startup.Register(safeActions, apps);
/// await safeActions.ApplyAsync(apps[0].DisableActionId);
/// </code>
/// </summary>
public sealed class PcTweaks
{
    public required StartupAppsService Startup { get; init; }
    public required UltimatePlanService Ultimate { get; init; }
    public required CleanupService Cleanup { get; init; }
    public required IReadOnlyList<ISafeActionTarget> Targets { get; init; }

    /// <param name="configDirectory">Folder for pc-tweaks.json (created Ultimate scheme GUID); null = memory only.</param>
    /// <param name="broker">Elevated helper (HAGS, HKLM startup toggles, creating the Ultimate scheme).</param>
    /// <param name="setPlan">Activates a scheme GUID (DisplayEngine.SetPowerPlan) — no admin.</param>
    public static PcTweaks Create(string? configDirectory, IElevationBroker broker, Func<string, bool> setPlan)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(setPlan);
        var store = new PcTweakStore(configDirectory);
        return new PcTweaks
        {
            Startup = new StartupAppsService(broker: broker),
            Ultimate = new UltimatePlanService(broker: broker, store: store),
            Cleanup = new CleanupService(),
            Targets = new ISafeActionTarget[]
            {
                new HagsTarget(enable: true, broker),
                new HagsTarget(enable: false, broker),
                new UltimatePlanTarget(setPlan, store: store)
            }
        };
    }

    /// <summary>Registers the fixed targets plus targets for journaled startup toggles (undo after restart).</summary>
    public void Register(SafeActionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        foreach (var t in Targets) service.Register(t);
        Startup.RegisterJournaled(service);
    }
}
