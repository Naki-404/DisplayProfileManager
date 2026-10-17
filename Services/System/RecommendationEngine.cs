using System.Globalization;

namespace Visor.Services;

/// <summary>How the UI should act when the user accepts a recommendation.</summary>
public enum RecommendationAction
{
    /// <summary>Informational only — no mutate button.</summary>
    None,
    /// <summary>Resume Visor monitoring (not a Windows SafeAction).</summary>
    ResumeMonitoring,
    /// <summary>Enable Windows Game Mode via <see cref="GameModeSetting"/> SafeAction.</summary>
    SafeActionEnableGameMode,
    /// <summary>Retained for binary compatibility — no rule emits it any more (Visor manages the plan per game).</summary>
    SafeActionHighPerfPower,
    /// <summary>Navigate to System page.</summary>
    OpenSystem,
    /// <summary>Navigate to Network page.</summary>
    OpenNetwork,
    /// <summary>Navigate to Games / library.</summary>
    OpenGames,
    /// <summary>Xbox Game Bar background off via <see cref="Optimize.GameBarSafeActionTarget"/> (SafeActionId = gameBar.off).</summary>
    SafeActionGameBarOff,
    /// <summary>Mouse acceleration off via <see cref="Optimize.MouseAccelerationSafeActionTarget"/> (SafeActionId = mouseAccel.off).</summary>
    SafeActionMouseAccelOff,
    /// <summary>Per-game "disable fullscreen optimizations" — <see cref="Recommendation.SafeActionId"/> embeds the exe path.</summary>
    SafeActionFsoForGame,
    /// <summary>Navigate to the startup-apps list (Optimize → PC). Each toggle there is its own SafeAction.</summary>
    OpenStartupApps,
    /// <summary>Navigate to the cache cleanup panel (Optimize → PC). Nothing is deleted without a click there.</summary>
    OpenCleanup,
    /// <summary>HAGS on via <see cref="Optimize.Pc.HagsTarget"/> (SafeActionId = hags.on) — elevated helper + restart.</summary>
    SafeActionHagsOn
}

public enum RecommendationSeverity
{
    Info,
    Tip,
    Warning
}

/// <summary>One rule result. Titles/bodies are Loc keys (or Loc keys + format args).</summary>
public sealed class Recommendation
{
    public required string Id { get; init; }
    public RecommendationSeverity Severity { get; init; }
    /// <summary>Loc key for a severity badge: rec.severity.info | rec.severity.tip | rec.severity.warning.</summary>
    public string SeverityKey => "rec.severity." + Severity.ToString().ToLowerInvariant();
    public required string TitleKey { get; init; }
    public required string BodyKey { get; init; }
    public string[] BodyArgs { get; init; } = Array.Empty<string>();
    public RecommendationAction Action { get; init; }
    /// <summary>Loc key for the primary button when <see cref="Action"/> is not None.</summary>
    public string? ActionKey { get; init; }

    /// <summary>
    /// Exact <see cref="SafeActionService"/> action id for SafeAction kinds (e.g. "gameBar.off",
    /// "fso.disable|C:\game.exe"); null for navigation / informational recommendations.
    /// </summary>
    public string? SafeActionId { get; init; }

    /// <summary>Global, or the game exe a per-game SafeAction applies to.</summary>
    public SafeActionScope Scope { get; init; } = SafeActionScope.Global;

    /// <summary>Measured facts behind the rule ("temp: 87 °C", "free: 7.2 %") — raw, not localized, never invented.</summary>
    public string[] Evidence { get; init; } = Array.Empty<string>();

    /// <summary>Loc key: what the suggestion gives (never an FPS promise).</summary>
    public string? WhyKey { get; init; }
    /// <summary>Loc key: side effects / what could go wrong.</summary>
    public string? RiskKey { get; init; }
    /// <summary>Loc key: how it is undone (SafeActions) or why there is nothing to undo.</summary>
    public string? UndoKey { get; init; }

    /// <summary>Stable hash of the occurrence (title + args) so a dismissal only hides this exact situation.</summary>
    public string ContextHash => RecommendationDismissals.ContextHash(TitleKey, BodyArgs);

    /// <summary>Token to persist when the user dismisses this occurrence (<see cref="RecommendationDismissals.Add"/>).</summary>
    public string DismissKey => Id + "|" + ContextHash;
}

/// <summary>One persisted dismissal: id + occurrence hash + when (expires after <see cref="RecommendationDismissals.Ttl"/>).</summary>
public readonly record struct RecommendationDismissal(string Id, string ContextHash, DateTime Utc)
{
    public bool IsExpired(DateTime nowUtc) => nowUtc - Utc >= RecommendationDismissals.Ttl;

    public bool Matches(string id, string contextHash) =>
        string.Equals(Id, id, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ContextHash, contextHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>"id|hash|yyyy-MM-ddTHH:mm:ssZ" — the format stored in UiState.DismissedRecommendations.</summary>
    public string Serialize() =>
        Id + "|" + ContextHash + "|" + Utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Parses the stored token. Legacy plain ids (no hash / time) are rejected → treated as expired.</summary>
    public static bool TryParse(string? text, out RecommendationDismissal value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('|');
        if (parts.Length != 3) return false;
        if (parts[0].Length == 0 || parts[1].Length == 0) return false;
        if (!DateTime.TryParseExact(parts[2], "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
            return false;
        value = new RecommendationDismissal(parts[0], parts[1], utc);
        return true;
    }
}

/// <summary>Helpers over the raw dismissal list persisted in UiState (strings, see <see cref="RecommendationDismissal.Serialize"/>).</summary>
public static class RecommendationDismissals
{
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(14);

    /// <summary>FNV-1a 32-bit over the occurrence parts (stable across runs, culture-independent).</summary>
    public static string ContextHash(string? titleKey, IReadOnlyList<string>? args)
    {
        unchecked
        {
            const uint prime = 16777619;
            uint h = 2166136261;
            void Mix(string? s)
            {
                foreach (char c in s ?? "")
                {
                    h ^= c;
                    h *= prime;
                }
                h ^= 0x1F;
                h *= prime;
            }
            Mix(titleKey);
            if (args != null)
                foreach (var a in args) Mix(a);
            return h.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Live (unexpired, parseable) dismissals from the stored tokens.</summary>
    public static List<RecommendationDismissal> Parse(IEnumerable<string>? raw, DateTime nowUtc)
    {
        var list = new List<RecommendationDismissal>();
        if (raw == null) return list;
        foreach (var token in raw)
        {
            if (RecommendationDismissal.TryParse(token, out var d) && !d.IsExpired(nowUtc))
                list.Add(d);
        }
        return list;
    }

    public static bool IsDismissed(IEnumerable<string>? raw, string id, string contextHash, DateTime nowUtc) =>
        Parse(raw, nowUtc).Any(d => d.Matches(id, contextHash));

    /// <summary>Records a dismissal for this exact occurrence (replacing an older one) and drops expired / legacy tokens.</summary>
    public static void Add(IList<string> store, Recommendation rec, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(rec);
        var live = Parse(store, nowUtc).Where(d => !d.Matches(rec.Id, rec.ContextHash)).ToList();
        live.Add(new RecommendationDismissal(rec.Id, rec.ContextHash, nowUtc));
        store.Clear();
        foreach (var d in live) store.Add(d.Serialize());
    }

    /// <summary>Removes expired and legacy (plain id) tokens in place. Returns true when anything changed.</summary>
    public static bool Prune(IList<string> store, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(store);
        var live = Parse(store, nowUtc).Select(d => d.Serialize()).ToList();
        if (live.Count == store.Count && live.SequenceEqual(store, StringComparer.Ordinal)) return false;
        store.Clear();
        foreach (var s in live) store.Add(s);
        return true;
    }

    /// <summary>"Show dismissed suggestions again".</summary>
    public static void Reset(IList<string> store)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.Clear();
    }
}

/// <summary>Facts the engine may use. Never invents sensors — callers pass only measured values.</summary>
public sealed class RecommendationContext
{
    public bool MonitoringPaused { get; init; }
    public int ProfileCount { get; init; }
    public bool? GameModeEnabled { get; init; }
    /// <summary>Retained for the System page label; no rule uses it (Visor manages the plan per game).</summary>
    public string? PowerPlanAliasOrGuid { get; init; }
    /// <summary>Retained for compatibility — the Home health block owns conflict reporting; no rule uses it.</summary>
    public bool ConflictApps { get; init; }
    /// <summary>Retained for compatibility — see <see cref="ConflictApps"/>.</summary>
    public bool NightLightOn { get; init; }
    /// <summary>Retained for compatibility — see <see cref="ConflictApps"/>.</summary>
    public IReadOnlyList<string> ConflictAppNames { get; init; } = Array.Empty<string>();
    public bool HasUsableNetworkAdapter { get; init; }
    public bool HasUsableGateway { get; init; }
    /// <summary>Retained for compatibility — the Home health block owns crash reporting; no rule uses it.</summary>
    public bool CrashRestored { get; init; }
    /// <summary>
    /// Raw dismissal tokens from UiState (see <see cref="RecommendationDismissal.Serialize"/>). Legacy plain ids
    /// are ignored (they dismissed forever); expired tokens are ignored.
    /// </summary>
    public IReadOnlySet<string> DismissedIds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Clock seam for expiry.</summary>
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;

    // ---- Real diagnostics (Services\Hardware) — all optional; a null means "not measured", never "fine". ----

    /// <summary>Static inventory (<c>HardwareInventoryService.Cached</c>): GPU driver date / version.</summary>
    public HardwareInventory? Inventory { get; init; }
    /// <summary>Latest 1 Hz snapshot (<c>SensorService.Latest</c>): GPU throttle / temp, RAM, drive free space.</summary>
    public SensorSnapshot? Sensors { get; init; }
    /// <summary>Evaluated health report — used as a fallback when <see cref="Sensors"/> is null.</summary>
    public HealthReport? Health { get; init; }
    /// <summary>Crash-related System-log counts over the last 30 days (<c>CrashEventReader.Read()</c>).</summary>
    public CrashEventSummary? Crashes { get; init; }
    /// <summary>True while a game profile session is active (RAM pressure only matters then).</summary>
    public bool GameRunning { get; init; }
    /// <summary>Xbox Game Bar background features on (<see cref="Optimize.GameBarSafeActionTarget.ReadBackgroundOn"/>); null = unreadable.</summary>
    public bool? GameBarBackgroundOn { get; init; }
    /// <summary>Mouse acceleration on (<see cref="Optimize.MouseAccelerationSafeActionTarget.ReadAccelerationOn"/>); null = unreadable.</summary>
    public bool? MouseAccelerationOn { get; init; }
    /// <summary>Per enabled profile with an exe path: is FSO already disabled (<see cref="Optimize.OptimizeStatus.FsoStatuses"/>).</summary>
    public IReadOnlyList<Optimize.GameFsoStatus> FsoStatuses { get; init; } = Array.Empty<Optimize.GameFsoStatus>();

    // ---- PC tweaks (Services\System\Optimize\Pc) — all optional; null = not measured. ----

    /// <summary>Startup entries (<see cref="Optimize.Pc.StartupAppsService.List"/>).</summary>
    public IReadOnlyList<Optimize.Pc.StartupEntry>? StartupApps { get; init; }
    /// <summary>Last cache scan (<see cref="Optimize.Pc.CleanupService.Scan"/>) — measured bytes only.</summary>
    public Optimize.Pc.CleanupScanResult? CleanupScan { get; init; }
    /// <summary>Drive root holding the cleanable caches ("C:\", <see cref="Optimize.Pc.CleanupService.PrimaryDrive"/>).</summary>
    public string? CleanupDrive { get; init; }
    /// <summary>HAGS registry + kernel state (<see cref="Optimize.Pc.HagsStatusReader.Read"/>).</summary>
    public Optimize.Pc.HagsStatus? Hags { get; init; }
}

/// <summary>
/// Pure recommendation rules over Visor + System + Network signals.
/// Windows mutations are expressed as SafeAction apply kinds only — never auto-applied.
/// Conflicts / crash recovery are deliberately not rules: the Home health block already shows them.
/// </summary>
public static class RecommendationEngine
{
    public const string IdPaused = "visor.paused";
    public const string IdGameModeOff = "system.gameMode.off";
    /// <summary>Retained for compatibility; the rule was removed (Visor manages the power plan per game).</summary>
    public const string IdPowerNotHigh = "system.power.notHigh";
    /// <summary>Retained for compatibility; the rule was removed (Home health block shows conflicts).</summary>
    public const string IdConflicts = "visor.conflicts";
    public const string IdNoGateway = "network.noGateway";
    public const string IdEmptyLibrary = "visor.emptyLibrary";
    /// <summary>Retained for compatibility; the rule was removed (Home health block shows crash recovery).</summary>
    public const string IdCrashRestored = "visor.crashRestored";

    public static IReadOnlyList<Recommendation> Evaluate(RecommendationContext ctx)
    {
        if (ctx == null) return Array.Empty<Recommendation>();
        var dismissals = RecommendationDismissals.Parse(ctx.DismissedIds, ctx.NowUtc);
        var list = new List<Recommendation>();

        void Emit(Recommendation rec)
        {
            string hash = rec.ContextHash;
            if (dismissals.Any(d => d.Matches(rec.Id, hash))) return;
            list.Add(rec);
        }

        if (ctx.MonitoringPaused)
        {
            Emit(new Recommendation
            {
                Id = IdPaused,
                Severity = RecommendationSeverity.Warning,
                TitleKey = "rec.paused.title",
                BodyKey = "rec.paused.body",
                Action = RecommendationAction.ResumeMonitoring,
                ActionKey = "rec.paused.action"
            });
        }

        if (ctx.GameModeEnabled == false)
        {
            Emit(new Recommendation
            {
                Id = IdGameModeOff,
                Severity = RecommendationSeverity.Tip,
                TitleKey = "rec.gameMode.title",
                BodyKey = "rec.gameMode.body",
                Action = RecommendationAction.SafeActionEnableGameMode,
                ActionKey = "rec.gameMode.action"
            });
        }

        if (ctx.HasUsableNetworkAdapter && !ctx.HasUsableGateway)
        {
            Emit(new Recommendation
            {
                Id = IdNoGateway,
                Severity = RecommendationSeverity.Info,
                TitleKey = "rec.noGateway.title",
                BodyKey = "rec.noGateway.body",
                Action = RecommendationAction.OpenNetwork,
                ActionKey = "rec.noGateway.action"
            });
        }

        if (ctx.ProfileCount == 0)
        {
            Emit(new Recommendation
            {
                Id = IdEmptyLibrary,
                Severity = RecommendationSeverity.Tip,
                TitleKey = "rec.emptyLibrary.title",
                BodyKey = "rec.emptyLibrary.body",
                Action = RecommendationAction.OpenGames,
                ActionKey = "rec.emptyLibrary.action"
            });
        }

        EvaluateDiagnostics(ctx, Emit);

        return list
            .OrderByDescending(r => r.Severity)
            .ToList();
    }

    public const string IdGpuThrottle = "rec.gpu.throttle";
    public const string IdDiskLow = "rec.disk.low";
    public const string IdDriverOld = "rec.driver.old";
    public const string IdCrashEvents = "rec.crash.events";
    public const string IdRamHigh = "rec.ram.high";
    public const string IdGameBarOff = "rec.gamebar.off";
    public const string IdMouseAccel = "rec.mouse.accel";
    public const string IdFsoGame = "rec.fso.game";
    public const string IdStartupMany = "rec.startup.many";
    public const string IdCleanupDisk = "rec.cleanup.disk";
    public const string IdHagsOff = "rec.hags.off";

    /// <summary>Enabled startup entries at which the list is worth a look (info only).</summary>
    public const int StartupManyThreshold = 10;
    /// <summary>Measured reclaimable cache bytes below which the cleanup tip is not worth showing.</summary>
    public const long CleanupTipMinBytes = 500L * 1024 * 1024;
    /// <summary>Largest entries listed as evidence for the startup rule.</summary>
    public const int StartupEvidenceMax = 8;

    private const GpuThrottleFlags ThrottleWorthWarning =
        GpuThrottleFlags.Thermal | GpuThrottleFlags.Power | GpuThrottleFlags.InsufficientPower;

    private static string Num(double v, string fmt = "0.#") => v.ToString(fmt, CultureInfo.InvariantCulture);

    /// <summary>Rules over measured diagnostics. Every emitted item carries raw evidence; nothing is inferred from missing data.</summary>
    private static void EvaluateDiagnostics(RecommendationContext ctx, Action<Recommendation> emit)
    {
        // GPU thermal / power throttling observed (live snapshot first, health report as fallback).
        if (ctx.Sensors != null)
        {
            foreach (var g in ctx.Sensors.Gpus)
            {
                if (!g.Throttle.HasValue) continue;
                var flags = g.Throttle.Value!.Value & ThrottleWorthWarning;
                if (flags == GpuThrottleFlags.None) continue;
                var evidence = new List<string> { "gpu: " + g.Name, "throttle: " + flags };
                if (g.TemperatureC.HasValue) evidence.Add("temp: " + g.TemperatureC.Value!.Value + " °C");
                if (g.PowerPercent.HasValue) evidence.Add("power: " + Num(g.PowerPercent.Value!.Value, "0") + " %");
                if (g.CoreClockMhz.HasValue) evidence.Add("core: " + g.CoreClockMhz.Value!.Value + " MHz");
                emit(new Recommendation
                {
                    Id = IdGpuThrottle,
                    Severity = RecommendationSeverity.Warning,
                    TitleKey = "rec.gpu.throttle.title",
                    BodyKey = "rec.gpu.throttle.body",
                    BodyArgs = new[] { g.Name, flags.ToString() },
                    Action = RecommendationAction.OpenSystem,
                    ActionKey = "rec.gpu.throttle.action",
                    Evidence = evidence.ToArray(),
                    WhyKey = "rec.gpu.throttle.why",
                    RiskKey = "rec.gpu.throttle.risk",
                    UndoKey = "rec.none.undo"
                });
            }
        }
        else if (ctx.Health != null)
        {
            foreach (var f in ctx.Health.Findings.Where(f => f.Component == "gpu.throttle" && f.Status >= HealthStatus.Warn))
            {
                emit(new Recommendation
                {
                    Id = IdGpuThrottle,
                    Severity = RecommendationSeverity.Warning,
                    TitleKey = "rec.gpu.throttle.title",
                    BodyKey = "rec.gpu.throttle.body",
                    BodyArgs = new[] { f.Subject ?? "", f.Detail ?? "" },
                    Action = RecommendationAction.OpenSystem,
                    ActionKey = "rec.gpu.throttle.action",
                    Evidence = new[] { "gpu: " + (f.Subject ?? "?"), "throttle: " + (f.Detail ?? "?") },
                    WhyKey = "rec.gpu.throttle.why",
                    RiskKey = "rec.gpu.throttle.risk",
                    UndoKey = "rec.none.undo"
                });
            }
        }

        // Disk < 10 % free (per volume).
        if (ctx.Sensors != null)
        {
            foreach (var d in ctx.Sensors.Drives)
            {
                if (d.TotalBytes <= 0) continue;
                double free = d.FreePercent;
                if (free >= HealthEvaluator.DiskFreeWarnPercent) continue;
                emit(new Recommendation
                {
                    Id = IdDiskLow,
                    Severity = RecommendationSeverity.Warning,
                    TitleKey = "rec.disk.low.title",
                    BodyKey = "rec.disk.low.body",
                    BodyArgs = new[] { d.Name, Num(Math.Floor(free), "0") },
                    Action = RecommendationAction.OpenSystem,
                    ActionKey = "rec.disk.low.action",
                    Evidence = new[]
                    {
                        "drive: " + d.Name,
                        "free: " + Num(free) + " %",
                        "free: " + Num(d.FreeBytes / (1024.0 * 1024 * 1024)) + " GB",
                        "total: " + Num(d.TotalBytes / (1024.0 * 1024 * 1024)) + " GB"
                    },
                    WhyKey = "rec.disk.low.why",
                    RiskKey = "rec.disk.low.risk",
                    UndoKey = "rec.none.undo"
                });
            }
        }

        // GPU driver older than 12 months — info only, never a download.
        if (ctx.Inventory != null)
        {
            foreach (var g in ctx.Inventory.Gpus)
            {
                if (g.DriverDate is not { } date || !HealthEvaluator.IsDriverOld(date, ctx.NowUtc)) continue;
                int months = Math.Max(0, (ctx.NowUtc.Year - date.Year) * 12 + ctx.NowUtc.Month - date.Month);
                emit(new Recommendation
                {
                    Id = IdDriverOld,
                    Severity = RecommendationSeverity.Info,
                    TitleKey = "rec.driver.old.title",
                    BodyKey = "rec.driver.old.body",
                    BodyArgs = new[] { g.Name, g.DriverVersion ?? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), months.ToString(CultureInfo.InvariantCulture) },
                    Action = RecommendationAction.None,
                    Evidence = new[]
                    {
                        "gpu: " + g.Name,
                        "driver: " + (g.DriverVersion ?? "?"),
                        "date: " + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        "age: " + months + " mo"
                    },
                    WhyKey = "rec.driver.old.why",
                    RiskKey = "rec.driver.old.risk",
                    UndoKey = "rec.none.undo"
                });
            }
        }

        // WHEA / Kernel-Power events in the window.
        if (ctx.Crashes != null && ctx.Crashes.Reason == UnavailableReason.None)
        {
            int shutdowns = ctx.Crashes.Count(CrashEventKind.UnexpectedShutdown);
            int wheaFatal = ctx.Crashes.Count(CrashEventKind.WheaFatal);
            int wheaCorrected = ctx.Crashes.Count(CrashEventKind.WheaCorrected);
            int serious = shutdowns + wheaFatal + (wheaCorrected >= HealthEvaluator.WheaCorrectedWarnCount ? wheaCorrected : 0);
            if (serious > 0)
            {
                int days = Math.Max(1, (int)Math.Round((ctx.Crashes.ToUtc - ctx.Crashes.FromUtc).TotalDays));
                var evidence = new List<string> { "window: " + days + " d" };
                if (shutdowns > 0) evidence.Add("kernelPower41: " + shutdowns);
                if (wheaFatal > 0) evidence.Add("wheaFatal: " + wheaFatal);
                if (wheaCorrected > 0) evidence.Add("wheaCorrected: " + wheaCorrected);
                var last = new[] { ctx.Crashes.Last(CrashEventKind.UnexpectedShutdown), ctx.Crashes.Last(CrashEventKind.WheaFatal) }
                    .Where(d => d != null).Max();
                if (last != null) evidence.Add("last: " + last.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                emit(new Recommendation
                {
                    Id = IdCrashEvents,
                    Severity = RecommendationSeverity.Warning,
                    TitleKey = "rec.crash.events.title",
                    BodyKey = "rec.crash.events.body",
                    BodyArgs = new[] { serious.ToString(CultureInfo.InvariantCulture), days.ToString(CultureInfo.InvariantCulture) },
                    Action = RecommendationAction.OpenSystem,
                    ActionKey = "rec.crash.events.action",
                    Evidence = evidence.ToArray(),
                    WhyKey = "rec.crash.events.why",
                    RiskKey = "rec.crash.events.risk",
                    UndoKey = "rec.none.undo"
                });
            }
        }

        // RAM ≥ 90 % while a game runs.
        if (ctx.GameRunning && ctx.Sensors?.RamUsedPercent.HasValue == true)
        {
            double p = ctx.Sensors.RamUsedPercent.Value!.Value;
            if (p >= HealthEvaluator.RamUsedWarnPercent)
            {
                emit(new Recommendation
                {
                    Id = IdRamHigh,
                    Severity = RecommendationSeverity.Warning,
                    TitleKey = "rec.ram.high.title",
                    BodyKey = "rec.ram.high.body",
                    Action = RecommendationAction.OpenSystem,
                    ActionKey = "rec.ram.high.action",
                    Evidence = new[]
                    {
                        "used: " + Num(p, "0") + " %",
                        "used: " + Num(ctx.Sensors.RamUsedBytes / (1024.0 * 1024 * 1024)) + " GB",
                        "total: " + Num(ctx.Sensors.RamTotalBytes / (1024.0 * 1024 * 1024)) + " GB"
                    },
                    WhyKey = "rec.ram.high.why",
                    RiskKey = "rec.ram.high.risk",
                    UndoKey = "rec.none.undo"
                });
            }
        }

        // Xbox Game Bar background on while game profiles exist → SafeAction.
        if (ctx.GameBarBackgroundOn == true && ctx.ProfileCount > 0)
        {
            emit(new Recommendation
            {
                Id = IdGameBarOff,
                Severity = RecommendationSeverity.Tip,
                TitleKey = "rec.gamebar.off.title",
                BodyKey = "rec.gamebar.off.body",
                Action = RecommendationAction.SafeActionGameBarOff,
                ActionKey = "rec.gamebar.off.action",
                SafeActionId = Optimize.GameBarSafeActionTarget.Id,
                Evidence = new[] { "profiles: " + ctx.ProfileCount, "gameBarBackground: on" },
                WhyKey = "optimize.gameBar.off.why",
                RiskKey = "optimize.gameBar.off.risk",
                UndoKey = "optimize.gameBar.off.undo"
            });
        }

        // Mouse acceleration on → SafeAction.
        if (ctx.MouseAccelerationOn == true)
        {
            emit(new Recommendation
            {
                Id = IdMouseAccel,
                Severity = RecommendationSeverity.Tip,
                TitleKey = "rec.mouse.accel.title",
                BodyKey = "rec.mouse.accel.body",
                Action = RecommendationAction.SafeActionMouseAccelOff,
                ActionKey = "rec.mouse.accel.action",
                SafeActionId = Optimize.MouseAccelerationSafeActionTarget.Id,
                Evidence = new[] { "mouseAcceleration: on" },
                WhyKey = "optimize.mouseAccel.off.why",
                RiskKey = "optimize.mouseAccel.off.risk",
                UndoKey = "optimize.mouseAccel.off.undo"
            });
        }

        // Fullscreen optimizations still on for an enabled game profile with an exe → per-game SafeAction.
        foreach (var s in ctx.FsoStatuses)
        {
            if (s == null || s.FsoDisabled != false || string.IsNullOrWhiteSpace(s.ExePath)) continue;
            emit(new Recommendation
            {
                Id = IdFsoGame,
                Severity = RecommendationSeverity.Tip,
                TitleKey = "rec.fso.game.title",
                BodyKey = "rec.fso.game.body",
                BodyArgs = new[] { string.IsNullOrWhiteSpace(s.ProfileName) ? s.ExePath : s.ProfileName },
                Action = RecommendationAction.SafeActionFsoForGame,
                ActionKey = "rec.fso.game.action",
                SafeActionId = Optimize.FullscreenOptimizationSafeActionTarget.ActionIdFor(s.ExePath),
                Scope = SafeActionScope.PerGame(s.ExePath),
                Evidence = new[] { "exe: " + s.ExePath, "fso: enabled" },
                WhyKey = "optimize.fso.why",
                RiskKey = "optimize.fso.risk",
                UndoKey = "optimize.fso.undo"
            });
        }

        EvaluatePcTweaks(ctx, emit);
    }

    private static string Size(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? Num(bytes / (1024.0 * 1024 * 1024)) + " GB"
            : Num(bytes / (1024.0 * 1024), "0") + " MB";

    /// <summary>
    /// Startup list, cache cleanup and HAGS rules. The Ultimate Performance plan is deliberately not a rule:
    /// Visor already sets a plan per game, and a permanent "more power" nag would be noise.
    /// </summary>
    private static void EvaluatePcTweaks(RecommendationContext ctx, Action<Recommendation> emit)
    {
        // Many programs start with Windows — info only, evidence = names + file sizes (no invented "impact").
        if (ctx.StartupApps != null)
        {
            var enabled = ctx.StartupApps.Where(e => e != null && e.Enabled).ToList();
            if (enabled.Count >= StartupManyThreshold)
            {
                var evidence = new List<string> { "enabled: " + enabled.Count + "/" + ctx.StartupApps.Count };
                evidence.AddRange(enabled
                    .OrderByDescending(e => e.FileSizeBytes ?? -1)
                    .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Take(StartupEvidenceMax)
                    .Select(e => e.DisplayName + ": " + (e.FileSizeBytes is { } b ? Size(b) : "?")));
                emit(new Recommendation
                {
                    Id = IdStartupMany,
                    Severity = RecommendationSeverity.Info,
                    TitleKey = "rec.startup.many.title",
                    BodyKey = "rec.startup.many.body",
                    BodyArgs = new[] { enabled.Count.ToString(CultureInfo.InvariantCulture) },
                    Action = RecommendationAction.OpenStartupApps,
                    ActionKey = "rec.startup.many.action",
                    Evidence = evidence.ToArray(),
                    WhyKey = "rec.startup.many.why",
                    RiskKey = "rec.startup.many.risk",
                    UndoKey = "optimize.startup.undo"
                });
            }
        }

        // Low space on the cache drive AND measured cache bytes worth emptying → Tip to the cleanup panel.
        // Bytes stay in the evidence (not the body args) so a re-scan does not resurface a dismissed tip.
        if (ctx.CleanupScan != null && ctx.Sensors != null && !string.IsNullOrEmpty(ctx.CleanupDrive)
            && ctx.CleanupScan.ReclaimableBytes >= CleanupTipMinBytes)
        {
            var drive = ctx.Sensors.Drives.FirstOrDefault(d => d.TotalBytes > 0 && SameDrive(d.Name, ctx.CleanupDrive));
            if (drive != null && drive.FreePercent < HealthEvaluator.DiskFreeWarnPercent)
            {
                var evidence = new List<string>
                {
                    "drive: " + drive.Name,
                    "free: " + Num(drive.FreePercent) + " %",
                    "reclaimable: " + Size(ctx.CleanupScan.ReclaimableBytes)
                };
                evidence.AddRange(ctx.CleanupScan.Categories
                    .Where(c => !c.InfoOnly && (c.Bytes ?? 0) > 0)
                    .OrderByDescending(c => c.Bytes)
                    .Select(c => c.Category + ": " + Size(c.Bytes!.Value)));
                emit(new Recommendation
                {
                    Id = IdCleanupDisk,
                    Severity = RecommendationSeverity.Tip,
                    TitleKey = "rec.cleanup.disk.title",
                    BodyKey = "rec.cleanup.disk.body",
                    BodyArgs = new[] { drive.Name },
                    Action = RecommendationAction.OpenCleanup,
                    ActionKey = "rec.cleanup.disk.action",
                    Evidence = evidence.ToArray(),
                    WhyKey = "optimize.cleanup.why",
                    RiskKey = "optimize.cleanup.risk",
                    UndoKey = "optimize.cleanup.undo"
                });
            }
        }

        // HAGS off on a GPU whose driver reports support (WDDM >= 2.7 caps read) — info; admin + restart.
        if (ctx.Hags is { } h && h.Supported == true && h.MaxWddmVersion >= 2700
            && h.RunningOn == false && h.RegistryOn != true && !h.PendingRestart)
        {
            emit(new Recommendation
            {
                Id = IdHagsOff,
                Severity = RecommendationSeverity.Info,
                TitleKey = "rec.hags.off.title",
                BodyKey = "rec.hags.off.body",
                Action = RecommendationAction.SafeActionHagsOn,
                ActionKey = "rec.hags.off.action",
                SafeActionId = Optimize.Pc.HagsTarget.OnId,
                Evidence = h.Evidence(),
                WhyKey = "optimize.hagsMode.why",
                RiskKey = "optimize.hagsMode.risk",
                UndoKey = "optimize.hagsMode.undo"
            });
        }
    }

    /// <summary>"C:" vs "C:\" vs "c:\" — same volume letter.</summary>
    private static bool SameDrive(string? a, string? b)
    {
        static char? Letter(string? s)
        {
            s = s?.Trim();
            return s is { Length: >= 2 } && s[1] == ':' && char.IsAsciiLetter(s[0]) ? char.ToUpperInvariant(s[0]) : null;
        }
        var x = Letter(a);
        return x != null && x == Letter(b);
    }

    /// <summary>Clears every dismissal so all suggestions show again.</summary>
    public static void ResetDismissed(IList<string> store) => RecommendationDismissals.Reset(store);

    /// <summary>Plans that already meet the "performance" tip — do not recommend downgrading Ultimate.</summary>
    public static bool IsPerformanceClassPlan(string? aliasOrGuid) =>
        aliasOrGuid is not null
        && (string.Equals(aliasOrGuid, PowerPlanSafeAction.HighPerformanceAlias, StringComparison.OrdinalIgnoreCase)
            || string.Equals(aliasOrGuid, "ultimate", StringComparison.OrdinalIgnoreCase));
}
