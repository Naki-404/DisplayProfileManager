namespace Visor.Services;

/// <summary>Lifecycle of a reversible system change (Diagnose → Apply → Verify → Restore).</summary>
public enum SafeActionState
{
    Pending,
    Running,
    Completed,
    Failed,
    RollbackAvailable,
    RollbackFailed
}

/// <summary>Outcome of <see cref="SafeActionService.Apply"/>.</summary>
public enum SafeActionResult
{
    /// <summary>State changed and verified; an undo entry was journaled.</summary>
    Applied,
    /// <summary>Already at the target — nothing written, journal untouched.</summary>
    NoOp,
    /// <summary>Write failed or verify mismatch. See <see cref="SafeActionOutcome.Code"/>.</summary>
    Failed,
    /// <summary>Refused up-front (e.g. a game session owns the setting). See <see cref="SafeActionOutcome.Code"/>.</summary>
    Blocked
}

/// <summary>Outcome of <see cref="SafeActionService.Undo"/>.</summary>
public enum SafeActionUndoResult
{
    Undone,
    /// <summary>Current state differs from what Visor applied — ask the user before forcing.</summary>
    Conflict,
    Failed
}

/// <summary>
/// Localizable error / reason codes (never raw text). UI: <c>Loc.T(SafeActionCodes.LocKey(code))</c>.
/// </summary>
public static class SafeActionCodes
{
    public const string Unavailable = "unavailable";
    public const string SchemeMissing = "schemeMissing";
    public const string ApplyFailed = "applyFailed";
    public const string VerifyMismatch = "verifyMismatch";
    public const string RollbackVerifyMismatch = "rollbackVerifyMismatch";
    public const string CannotRollback = "cannotRollback";
    public const string BadBackup = "badBackup";
    public const string SessionActive = "sessionActive";
    public const string UnknownAction = "unknownAction";
    public const string Conflict = "conflict";

    public const string LocPrefix = "safeAction.code.";

    public static string LocKey(string? code) =>
        LocPrefix + (string.IsNullOrEmpty(code) ? "unknown" : code);
}

public readonly record struct SafeActionOutcome(SafeActionResult Result, string? Code = null)
{
    public bool Succeeded => Result is SafeActionResult.Applied or SafeActionResult.NoOp;
}

public readonly record struct SafeActionUndoOutcome(SafeActionUndoResult Result, string? Code = null);

/// <summary>
/// A reversible setting the <see cref="SafeActionService"/> can apply and restore. State is an opaque
/// string the target can serialize and parse itself (exact raw previous state, incl. "value absent").
/// </summary>
public interface ISafeActionTarget
{
    /// <summary>Stable id passed to <see cref="SafeActionService.Apply"/>.</summary>
    string ActionId { get; }

    /// <summary>Journal key — several action ids may share one undo slot (e.g. gameMode.on / gameMode.off → gameMode).</summary>
    string JournalKey { get; }

    /// <summary>True when applying must be refused while a game session snapshot owns the setting.</summary>
    bool BlockedDuringGameSession { get; }

    /// <summary>Exact current raw state, or null when unreadable.</summary>
    string? ReadState();

    /// <summary>Raw target state, or null when the target cannot be resolved (e.g. scheme not installed).</summary>
    string? ResolveTargetState();

    /// <summary>True when <paramref name="state"/> already satisfies the target (semantic, not byte equality).</summary>
    bool SatisfiesTarget(string state, string targetState);

    bool StatesEqual(string a, string b);

    /// <summary>Writes an arbitrary raw state (apply or restore). The caller re-reads to verify regardless of the return.</summary>
    bool WriteState(string state);
}

/// <summary>
/// One recorded SafeAction attempt. Pure data — apply/rollback live in the setting helpers
/// so unit tests can drive the state machine without touching the registry or power APIs.
/// </summary>
public sealed class SafeActionSession
{
    public required string ActionId { get; init; }
    public SafeActionState State { get; set; } = SafeActionState.Pending;
    public string? PreviousValue { get; set; }
    public string? TargetValue { get; set; }
    public string? VerifiedValue { get; set; }
    /// <summary>A <see cref="SafeActionCodes"/> code (localizable), never raw text.</summary>
    public string? Error { get; set; }

    public bool CanRollback =>
        (State is SafeActionState.RollbackAvailable or SafeActionState.RollbackFailed)
        && !string.IsNullOrEmpty(PreviousValue);
}

/// <summary>Pure state transitions for SafeAction sessions (unit-testable).</summary>
public static class SafeActionFlow
{
    public static SafeActionSession Begin(string actionId, string? previous, string target)
    {
        return new SafeActionSession
        {
            ActionId = actionId,
            State = SafeActionState.Pending,
            PreviousValue = previous,
            TargetValue = target
        };
    }

    public static void MarkRunning(SafeActionSession session) =>
        session.State = SafeActionState.Running;

    public static void MarkCompleted(SafeActionSession session, string? verified)
    {
        session.VerifiedValue = verified;
        bool matched = string.Equals(verified, session.TargetValue, StringComparison.OrdinalIgnoreCase);
        if (!matched)
        {
            // Apply may already have taken effect — keep Undo when we have a backup.
            session.Error ??= SafeActionCodes.VerifyMismatch;
            session.State = string.IsNullOrEmpty(session.PreviousValue)
                ? SafeActionState.Failed
                : SafeActionState.RollbackAvailable;
            return;
        }
        session.State = string.IsNullOrEmpty(session.PreviousValue)
            ? SafeActionState.Completed
            : SafeActionState.RollbackAvailable;
        session.Error = null;
    }

    public static void MarkFailed(SafeActionSession session, string errorCode)
    {
        session.State = SafeActionState.Failed;
        session.Error = errorCode;
    }

    /// <summary>
    /// Write failed (or verify mismatched) after an apply. When the value actually moved from
    /// <paramref name="previousRaw"/> the backup is still valid → RollbackAvailable; otherwise Failed.
    /// </summary>
    public static void MarkApplyFailed(SafeActionSession session, string errorCode,
        string? previousRaw, string? currentRaw, string? verifiedLabel = null)
    {
        session.Error = errorCode;
        session.VerifiedValue = verifiedLabel ?? currentRaw;
        bool changed = currentRaw != null && previousRaw != null
            && !string.Equals(previousRaw, currentRaw, StringComparison.OrdinalIgnoreCase);
        session.State = changed && !string.IsNullOrEmpty(session.PreviousValue)
            ? SafeActionState.RollbackAvailable
            : SafeActionState.Failed;
    }

    public static void MarkRollback(SafeActionSession session, string? verified)
    {
        session.VerifiedValue = verified;
        bool matched = string.Equals(verified, session.PreviousValue, StringComparison.OrdinalIgnoreCase);
        session.State = matched ? SafeActionState.Completed : SafeActionState.RollbackFailed;
        if (!matched)
            session.Error ??= SafeActionCodes.RollbackVerifyMismatch;
        else
            session.Error = null;
    }
}
