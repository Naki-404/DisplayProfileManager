namespace Visor.Services;

/// <summary>
/// Tiny apply / undo façade over <see cref="ISafeActionTarget"/>s with a persisted undo journal.
/// Nothing is applied without an explicit call; every apply verifies by re-reading the real value;
/// undo verifies that the setting is still what Visor set before reverting (else <see cref="SafeActionUndoResult.Conflict"/>).
/// </summary>
public sealed class SafeActionService
{
    private readonly Dictionary<string, ISafeActionTarget> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<bool> _isGameSessionActive;
    private readonly Func<string, ISafeActionTarget?>? _resolver;
    private readonly object _gate = new();
    /// <summary>Serialises async (possibly elevated) applies / undos - one UAC batch at a time.</summary>
    private readonly SemaphoreSlim _asyncGate = new(1, 1);

    public SafeActionJournal Journal { get; }

    /// <param name="isGameSessionActive">Seam for the session owner (MainWindow passes <c>() => Monitor.SnapshotActive</c>).</param>
    /// <param name="resolver">
    /// Creates per-game targets on demand from an action id (e.g. <c>fso.disable|C:\game.exe</c>) so a journaled
    /// per-game entry can still be undone after a restart. Resolved targets are cached.
    /// </param>
    public SafeActionService(SafeActionJournal journal, IEnumerable<ISafeActionTarget> targets,
        Func<bool>? isGameSessionActive = null, Func<string, ISafeActionTarget?>? resolver = null)
    {
        Journal = journal ?? throw new ArgumentNullException(nameof(journal));
        ArgumentNullException.ThrowIfNull(targets);
        foreach (var t in targets)
            _targets[t.ActionId] = t;
        _isGameSessionActive = isGameSessionActive ?? (() => false);
        _resolver = resolver;
    }

    /// <summary>
    /// Real targets: Game Mode (HKCU), the High performance power plan, Xbox Game Bar background off,
    /// mouse acceleration off, plus per-game "disable fullscreen optimizations" resolved from the action id.
    /// With <paramref name="elevation"/> also the admin-only network tweaks (MMCSS throttling, TCP auto-tuning,
    /// per-adapter DNS / Nagle) — those apply / undo only via <see cref="ApplyAsync"/> / <see cref="UndoAsync"/>.
    /// </summary>
    public static SafeActionService CreateDefault(string? configDirectory, Func<string, bool> setPlan,
        Func<bool>? isGameSessionActive, Elevation.IElevationBroker? elevation = null)
    {
        ArgumentNullException.ThrowIfNull(setPlan);
        var targets = new List<ISafeActionTarget>
        {
            new GameModeSafeActionTarget(enable: true),
            new GameModeSafeActionTarget(enable: false),
            new PowerPlanSafeActionTarget(setPlan),
            new Optimize.GameBarSafeActionTarget(),
            new Optimize.MouseAccelerationSafeActionTarget()
        };
        if (elevation != null)
            targets.AddRange(NetworkTweaks.NetworkTweakCatalog.CreateStatic(elevation));
        return new SafeActionService(
            new SafeActionJournal(configDirectory),
            targets,
            isGameSessionActive,
            id => ResolvePerGame(id, registry: null)
                  ?? (elevation != null ? NetworkTweaks.NetworkTweakCatalog.Resolve(id, elevation) : null));
    }

    /// <summary>Default per-game resolver: understands <see cref="Optimize.FullscreenOptimizationSafeActionTarget"/> ids.</summary>
    public static ISafeActionTarget? ResolvePerGame(string actionId, Optimize.IOptimizeRegistry? registry)
    {
        var exe = Optimize.FullscreenOptimizationSafeActionTarget.ExePathFromActionId(actionId);
        return exe == null ? null : new Optimize.FullscreenOptimizationSafeActionTarget(exe, registry);
    }

    /// <summary>Registered (static + already resolved) action ids.</summary>
    public IReadOnlyCollection<string> ActionIds
    {
        get { lock (_gate) return _targets.Keys.ToList(); }
    }

    /// <summary>Adds or replaces a target (used for per-game targets built from a profile).</summary>
    public void Register(ISafeActionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate) _targets[target.ActionId] = target;
    }

    public ISafeActionTarget? FindTarget(string actionId)
    {
        if (string.IsNullOrEmpty(actionId)) return null;
        lock (_gate)
        {
            if (_targets.TryGetValue(actionId, out var t)) return t;
            if (_resolver == null) return null;
            ISafeActionTarget? resolved;
            try { resolved = _resolver(actionId); }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "SafeAction resolve " + actionId);
                return null;
            }
            if (resolved != null) _targets[resolved.ActionId] = resolved;
            return resolved;
        }
    }

    /// <summary>Journal entries whose target is per-game (for an "undo per game" list in the UI).</summary>
    public IReadOnlyList<SafeActionJournalEntry> PerGameEntries() =>
        Journal.Entries.Where(e => FindTarget(e.ActionId)?.Scope.IsPerGame == true).ToList();

    /// <summary>Current raw state of the target behind <paramref name="actionId"/> (null when unreadable / unknown).</summary>
    public string? ReadState(string actionId) => FindTarget(actionId)?.ReadState();

    /// <summary>True when the setting already satisfies the action's target (apply would be a no-op).</summary>
    public bool IsAtTarget(string actionId)
    {
        var t = FindTarget(actionId);
        if (t == null) return false;
        var cur = t.ReadState();
        var target = t.ResolveTargetState();
        return cur != null && target != null && t.SatisfiesTarget(cur, target);
    }

    public SafeActionOutcome Apply(string actionId)
    {
        var target = FindTarget(actionId);
        if (target == null)
            return new SafeActionOutcome(SafeActionResult.Failed, SafeActionCodes.UnknownAction);

        // Elevated writes wait on a UAC prompt - never on this (usually UI) thread.
        if (target.RequiresElevation)
            return new SafeActionOutcome(SafeActionResult.Failed, SafeActionCodes.NeedsElevation);

        lock (_gate)
        {
            if (target.BlockedDuringGameSession && SafeInvoke(_isGameSessionActive))
                return new SafeActionOutcome(SafeActionResult.Blocked, SafeActionCodes.SessionActive);

            string? previous = target.ReadState();
            if (previous == null)
                return new SafeActionOutcome(SafeActionResult.Failed, SafeActionCodes.Unavailable);

            string? targetState = target.ResolveTargetState();
            if (targetState == null)
                return new SafeActionOutcome(SafeActionResult.Failed, SafeActionCodes.SchemeMissing);

            // Second click at target: nothing written, existing undo entry untouched.
            if (target.SatisfiesTarget(previous, targetState))
                return new SafeActionOutcome(SafeActionResult.NoOp);

            bool wrote;
            try { wrote = target.WriteState(targetState); }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "SafeAction apply " + actionId);
                wrote = false;
            }

            string? after = target.ReadState();
            bool atTarget = after != null && target.SatisfiesTarget(after, targetState);
            if (atTarget)
            {
                Journal.Record(new SafeActionJournalEntry
                {
                    Key = target.JournalKey,
                    ActionId = target.ActionId,
                    Utc = DateTime.UtcNow,
                    PreviousState = previous,
                    TargetState = after!
                });
                return new SafeActionOutcome(SafeActionResult.Applied);
            }

            // Not at target. If the value moved anyway, keep a backup so the user can undo the partial change.
            bool moved = after != null && !target.StatesEqual(after, previous);
            if (moved)
            {
                Journal.Record(new SafeActionJournalEntry
                {
                    Key = target.JournalKey,
                    ActionId = target.ActionId,
                    Utc = DateTime.UtcNow,
                    PreviousState = previous,
                    TargetState = after!
                });
            }
            return new SafeActionOutcome(SafeActionResult.Failed,
                wrote ? SafeActionCodes.VerifyMismatch : SafeActionCodes.ApplyFailed);
        }
    }

    /// <param name="actionId">Action id or its journal key (e.g. "gameMode" covers gameMode.on / gameMode.off).</param>
    public bool CanUndo(string actionId) => FindEntry(actionId) != null;

    /// <param name="actionId">Action id or its journal key.</param>
    /// <param name="confirmedConflict">
    /// True after the user confirmed a <see cref="SafeActionUndoResult.Conflict"/>: restore the journaled
    /// previous state even though the setting no longer matches what Visor applied.
    /// </param>
    public SafeActionUndoOutcome Undo(string actionId, bool confirmedConflict = false)
    {
        lock (_gate)
        {
            var entry = FindEntry(actionId);
            if (entry == null)
                return new SafeActionUndoOutcome(SafeActionUndoResult.Failed, SafeActionCodes.CannotRollback);
            var target = FindTarget(entry.ActionId);
            if (target == null)
                return new SafeActionUndoOutcome(SafeActionUndoResult.Failed, SafeActionCodes.UnknownAction);
            if (target.RequiresElevation)
                return new SafeActionUndoOutcome(SafeActionUndoResult.Failed, SafeActionCodes.NeedsElevation);

            string? current = target.ReadState();
            if (current == null)
                return new SafeActionUndoOutcome(SafeActionUndoResult.Failed, SafeActionCodes.Unavailable);

            if (target.StatesEqual(current, entry.PreviousState))
            {
                // Already back (e.g. the session snapshot restored it) — nothing to write.
                Journal.Remove(entry.Key);
                return new SafeActionUndoOutcome(SafeActionUndoResult.Undone);
            }

            if (!confirmedConflict && !target.StatesEqual(current, entry.TargetState))
                return new SafeActionUndoOutcome(SafeActionUndoResult.Conflict, SafeActionCodes.Conflict);

            bool wrote;
            try { wrote = target.WriteState(entry.PreviousState); }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "SafeAction undo " + entry.ActionId);
                wrote = false;
            }

            string? after = target.ReadState();
            if (after != null && target.StatesEqual(after, entry.PreviousState))
            {
                Journal.Remove(entry.Key);
                return new SafeActionUndoOutcome(SafeActionUndoResult.Undone);
            }
            return new SafeActionUndoOutcome(SafeActionUndoResult.Failed,
                wrote ? SafeActionCodes.RollbackVerifyMismatch : SafeActionCodes.ApplyFailed);
        }
    }

    /// <summary>
    /// Async apply - works for every target and is the only path for <see cref="ISafeActionTarget.RequiresElevation"/>
    /// targets (one UAC prompt per call). Same contract as <see cref="Apply"/>: refuse during a game session,
    /// NoOp at target, re-read to verify, journal exactly what was there before.
    /// <para>
    /// Re-applying over Visor's own earlier change (e.g. DNS Cloudflare, then Google) keeps the ORIGINAL previous
    /// state in the journal, so one undo always returns to what the user had before Visor touched it.
    /// A declined UAC prompt changes nothing and journals nothing.
    /// </para>
    /// <para>
    /// Continuations stay on the caller's context (no ConfigureAwait(false)), so the verify re-read and
    /// <see cref="SafeActionJournal.Changed"/> happen on the calling (UI) thread like the sync path. Await it;
    /// never block on it from the UI thread.
    /// </para>
    /// </summary>
    public async Task<SafeActionOutcome> ApplyAsync(string actionId, CancellationToken ct = default)
    {
        var target = FindTarget(actionId);
        if (target == null)
            return new SafeActionOutcome(SafeActionResult.Failed, SafeActionCodes.UnknownAction);

        await _asyncGate.WaitAsync(ct);
        try
        {
            string? previous;
            string? targetState;
            lock (_gate)
            {
                if (target.BlockedDuringGameSession && SafeInvoke(_isGameSessionActive))
                    return new SafeActionOutcome(SafeActionResult.Blocked, SafeActionCodes.SessionActive);

                previous = target.ReadState();
                if (previous == null)
                    return new SafeActionOutcome(SafeActionResult.Failed, SafeActionCodes.Unavailable);

                targetState = target.ResolveTargetState();
                if (targetState == null)
                    return new SafeActionOutcome(SafeActionResult.Failed, SafeActionCodes.SchemeMissing);

                if (target.SatisfiesTarget(previous, targetState))
                    return new SafeActionOutcome(SafeActionResult.NoOp);
            }

            SafeActionWriteResult write;
            try { write = await target.WriteStateAsync(targetState, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "SafeAction apply " + actionId);
                write = SafeActionWriteResult.Fail(SafeActionCodes.ApplyFailed);
            }

            lock (_gate)
            {
                string? after = target.ReadState();
                bool atTarget = after != null && target.SatisfiesTarget(after, targetState);
                bool moved = after != null && !target.StatesEqual(after, previous);
                if (atTarget || moved)
                {
                    // Chain over Visor's own earlier change: keep the pre-Visor state as the undo point.
                    string journalPrevious = previous;
                    var existing = Journal.Get(target.JournalKey);
                    bool chained = existing != null && target.StatesEqual(previous, existing.TargetState);
                    if (chained)
                        journalPrevious = existing!.PreviousState;

                    if (chained && target.StatesEqual(after!, journalPrevious))
                        Journal.Remove(target.JournalKey);   // back at the original - nothing left to undo
                    else
                        Journal.Record(new SafeActionJournalEntry
                        {
                            Key = target.JournalKey,
                            ActionId = target.ActionId,
                            Utc = DateTime.UtcNow,
                            PreviousState = journalPrevious,
                            TargetState = after!
                        });
                }
                if (atTarget)
                    return new SafeActionOutcome(SafeActionResult.Applied);
                return new SafeActionOutcome(SafeActionResult.Failed,
                    write.Written ? SafeActionCodes.VerifyMismatch : write.Code ?? SafeActionCodes.ApplyFailed);
            }
        }
        finally
        {
            _asyncGate.Release();
        }
    }

    /// <summary>Async undo - see <see cref="Undo"/>; the only undo path for elevated targets.</summary>
    public async Task<SafeActionUndoOutcome> UndoAsync(string actionId, bool confirmedConflict = false,
        CancellationToken ct = default)
    {
        await _asyncGate.WaitAsync(ct);
        try
        {
            SafeActionJournalEntry? entry;
            ISafeActionTarget? target;
            lock (_gate)
            {
                entry = FindEntry(actionId);
                if (entry == null)
                    return new SafeActionUndoOutcome(SafeActionUndoResult.Failed, SafeActionCodes.CannotRollback);
                target = FindTarget(entry.ActionId);
                if (target == null)
                    return new SafeActionUndoOutcome(SafeActionUndoResult.Failed, SafeActionCodes.UnknownAction);

                string? current = target.ReadState();
                if (current == null)
                    return new SafeActionUndoOutcome(SafeActionUndoResult.Failed, SafeActionCodes.Unavailable);

                if (target.StatesEqual(current, entry.PreviousState))
                {
                    Journal.Remove(entry.Key);
                    return new SafeActionUndoOutcome(SafeActionUndoResult.Undone);
                }

                if (!confirmedConflict && !target.StatesEqual(current, entry.TargetState))
                    return new SafeActionUndoOutcome(SafeActionUndoResult.Conflict, SafeActionCodes.Conflict);
            }

            SafeActionWriteResult write;
            try { write = await target.WriteStateAsync(entry.PreviousState, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "SafeAction undo " + entry.ActionId);
                write = SafeActionWriteResult.Fail(SafeActionCodes.ApplyFailed);
            }

            lock (_gate)
            {
                string? after = target.ReadState();
                if (after != null && target.StatesEqual(after, entry.PreviousState))
                {
                    Journal.Remove(entry.Key);
                    return new SafeActionUndoOutcome(SafeActionUndoResult.Undone);
                }
                return new SafeActionUndoOutcome(SafeActionUndoResult.Failed,
                    write.Written ? SafeActionCodes.RollbackVerifyMismatch : write.Code ?? SafeActionCodes.ApplyFailed);
            }
        }
        finally
        {
            _asyncGate.Release();
        }
    }

    private SafeActionJournalEntry? FindEntry(string actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return null;
        var target = FindTarget(actionId);
        return Journal.Get(target?.JournalKey ?? actionId) ?? Journal.Get(actionId);
    }

    private static bool SafeInvoke(Func<bool> f)
    {
        try { return f(); }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "isGameSessionActive");
            return false;
        }
    }
}
