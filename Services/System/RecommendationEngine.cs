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
    OpenGames
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

        return list
            .OrderByDescending(r => r.Severity)
            .ToList();
    }

    /// <summary>Clears every dismissal so all suggestions show again.</summary>
    public static void ResetDismissed(IList<string> store) => RecommendationDismissals.Reset(store);

    /// <summary>Plans that already meet the "performance" tip — do not recommend downgrading Ultimate.</summary>
    public static bool IsPerformanceClassPlan(string? aliasOrGuid) =>
        aliasOrGuid is not null
        && (string.Equals(aliasOrGuid, PowerPlanSafeAction.HighPerformanceAlias, StringComparison.OrdinalIgnoreCase)
            || string.Equals(aliasOrGuid, "ultimate", StringComparison.OrdinalIgnoreCase));
}
