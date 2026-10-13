namespace Visor.Services;

/// <summary>
/// Safe switch to the High Performance power plan with rollback to the previous scheme GUID.
/// Callers supply apply/read delegates so this stays free of UI / AppServices coupling and tests
/// never touch the real power API.
/// </summary>
public static class PowerPlanSafeAction
{
    public const string ActionId = "powerHighPerf";
    public const string HighPerformanceAlias = "highPerformance";

    public static string? ReadAliasOrGuid()
    {
        var guid = DisplayEngine.GetActivePowerPlanGuid();
        if (guid == null) return null;
        return DisplayEngine.PowerPlanAlias(guid) ?? guid;
    }

    /// <param name="setPlan">Activates a plan alias or GUID; returns true when active afterwards.</param>
    /// <param name="readActiveGuid">Seam: active scheme GUID (default: real power API).</param>
    /// <param name="resolveGuid">Seam: alias → GUID (default: <see cref="DisplayEngine.ResolvePowerPlanGuid"/>).</param>
    /// <param name="isGameSessionActive">Seam: refuse while a game session snapshot owns the plan.</param>
    public static SafeActionSession ApplyHighPerformance(Func<string, bool> setPlan,
        Func<string?>? readActiveGuid = null, Func<string, string?>? resolveGuid = null,
        Func<bool>? isGameSessionActive = null)
    {
        ArgumentNullException.ThrowIfNull(setPlan);
        readActiveGuid ??= DisplayEngine.GetActivePowerPlanGuid;
        resolveGuid ??= DisplayEngine.ResolvePowerPlanGuid;

        var previousGuid = readActiveGuid();
        var session = SafeActionFlow.Begin(ActionId, previousGuid, HighPerformanceAlias);
        if (isGameSessionActive?.Invoke() == true)
        {
            SafeActionFlow.MarkFailed(session, SafeActionCodes.SessionActive);
            return session;
        }
        if (previousGuid == null)
        {
            SafeActionFlow.MarkFailed(session, SafeActionCodes.Unavailable);
            return session;
        }

        var targetGuid = resolveGuid(HighPerformanceAlias);
        if (targetGuid == null)
        {
            SafeActionFlow.MarkFailed(session, SafeActionCodes.SchemeMissing);
            return session;
        }

        if (string.Equals(previousGuid, targetGuid, StringComparison.OrdinalIgnoreCase))
        {
            session.VerifiedValue = HighPerformanceAlias;
            session.State = SafeActionState.Completed;
            return session;
        }

        SafeActionFlow.MarkRunning(session);
        bool wrote;
        try { wrote = setPlan(HighPerformanceAlias); }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Power plan apply");
            wrote = false;
        }

        // Always re-read: the plan may have changed even when setPlan reported failure, and vice versa.
        var verified = readActiveGuid();
        if (verified != null && string.Equals(verified, targetGuid, StringComparison.OrdinalIgnoreCase))
        {
            SafeActionFlow.MarkCompleted(session, HighPerformanceAlias);
            return session;
        }

        var alias = verified == null ? null : (DisplayEngine.PowerPlanAlias(verified) ?? verified);
        SafeActionFlow.MarkApplyFailed(session,
            wrote ? SafeActionCodes.VerifyMismatch : SafeActionCodes.ApplyFailed,
            previousGuid, verified, alias);
        return session;
    }

    /// <param name="confirmedConflict">Restore even when the active plan is no longer what Visor applied.</param>
    public static SafeActionSession Rollback(SafeActionSession session, Func<string, bool> setPlan,
        Func<string?>? readActiveGuid = null, Func<string, string?>? resolveGuid = null,
        bool confirmedConflict = false)
    {
        ArgumentNullException.ThrowIfNull(setPlan);
        readActiveGuid ??= DisplayEngine.GetActivePowerPlanGuid;
        resolveGuid ??= DisplayEngine.ResolvePowerPlanGuid;

        if (session.ActionId != ActionId || string.IsNullOrEmpty(session.PreviousValue)
            || session.State is not (SafeActionState.RollbackAvailable or SafeActionState.RollbackFailed))
        {
            SafeActionFlow.MarkFailed(session, SafeActionCodes.CannotRollback);
            return session;
        }

        var current = readActiveGuid();
        if (current == null)
        {
            session.State = SafeActionState.RollbackFailed;
            session.Error = SafeActionCodes.Unavailable;
            return session;
        }
        if (string.Equals(current, session.PreviousValue, StringComparison.OrdinalIgnoreCase))
        {
            // Already back (e.g. the session snapshot restored it).
            session.Error = null;
            SafeActionFlow.MarkRollback(session, current);
            return session;
        }
        var targetGuid = resolveGuid(session.TargetValue ?? HighPerformanceAlias) ?? session.TargetValue;
        if (!confirmedConflict && !string.Equals(current, targetGuid, StringComparison.OrdinalIgnoreCase))
        {
            session.State = SafeActionState.RollbackFailed;
            session.Error = SafeActionCodes.Conflict;
            return session;
        }

        SafeActionFlow.MarkRunning(session);
        bool wrote;
        try { wrote = setPlan(session.PreviousValue!); }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Power plan rollback");
            wrote = false;
        }
        var verified = readActiveGuid();
        if (!wrote && !string.Equals(verified, session.PreviousValue, StringComparison.OrdinalIgnoreCase))
        {
            session.State = SafeActionState.RollbackFailed;
            session.Error = SafeActionCodes.ApplyFailed;
            session.VerifiedValue = verified;
            return session;
        }
        session.Error = null;
        SafeActionFlow.MarkRollback(session, verified);
        return session;
    }
}

/// <summary><see cref="ISafeActionTarget"/> for the High performance plan. Blocked while a game session owns the plan.</summary>
public sealed class PowerPlanSafeActionTarget : ISafeActionTarget
{
    private readonly Func<string, bool> _setPlan;
    private readonly Func<string?> _readActiveGuid;
    private readonly Func<string, string?> _resolveGuid;

    public PowerPlanSafeActionTarget(Func<string, bool> setPlan,
        Func<string?>? readActiveGuid = null, Func<string, string?>? resolveGuid = null)
    {
        _setPlan = setPlan ?? throw new ArgumentNullException(nameof(setPlan));
        _readActiveGuid = readActiveGuid ?? DisplayEngine.GetActivePowerPlanGuid;
        _resolveGuid = resolveGuid ?? DisplayEngine.ResolvePowerPlanGuid;
    }

    public string ActionId => PowerPlanSafeAction.ActionId;
    public string JournalKey => PowerPlanSafeAction.ActionId;
    public bool BlockedDuringGameSession => true;

    public string? ReadState() => _readActiveGuid()?.ToLowerInvariant();

    public string? ResolveTargetState() => _resolveGuid(PowerPlanSafeAction.HighPerformanceAlias)?.ToLowerInvariant();

    public bool SatisfiesTarget(string state, string targetState) => StatesEqual(state, targetState);

    public bool StatesEqual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public bool WriteState(string state) => _setPlan(state);
}
