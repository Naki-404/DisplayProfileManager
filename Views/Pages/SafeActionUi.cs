using System.IO;
using Visor.Services;
using Visor.Services.NetworkTweaks;
using Visor.Services.Optimize;
using Visor.Services.Optimize.Pc;

namespace Visor;

/// <summary>Options for <see cref="SafeActionUi.ApplyAsync"/>.</summary>
internal sealed record SafeApplyOptions
{
    /// <summary>Show the confirm dialog (why / risk / undo note, plus the UAC note for elevated targets).</summary>
    public bool Confirm { get; init; } = true;
    /// <summary>Card title for the dialog / toast. Default: <see cref="SafeActionUi.TitleFor"/>.</summary>
    public string? Title { get; init; }
    public string? WhyKey { get; init; }
    public string? RiskKey { get; init; }
    /// <summary>Toast after a verified apply. Default: "Applied: {title}".</summary>
    public string? OkToast { get; init; }
    /// <summary>Second toast line after a verified apply (reconnect / restart needed).</summary>
    public string? AfterNote { get; init; }
    /// <summary>Toast carries an Undo button (startup toggles and other one-click switches).</summary>
    public bool OfferUndo { get; init; }
    public string? HistoryDetail { get; init; }
}

/// <summary>
/// The one apply / undo flow for every SafeAction button (Optimize, Network, recommendations): optional
/// confirm → <see cref="SafeActionService.ApplyAsync"/> / <see cref="SafeActionService.UndoAsync"/> (always the
/// async path — elevated targets wait on a UAC prompt, never on the UI thread) → localized toast → history.
/// Codes are shown through <c>safeAction.code.*</c>, never raw. Pages re-read real state afterwards.
/// </summary>
internal static class SafeActionUi
{
    /// <summary>Raised on the UI thread after a verified apply (true) or undo (false), with the journal key.</summary>
    public static event Action<string, bool>? Completed;

    private static SafeActionService Safe => App.Services.SafeActions;

    /// <summary>True when the target behind <paramref name="actionId"/> goes through the elevated helper.</summary>
    public static bool NeedsElevation(string actionId) => Safe.FindTarget(actionId)?.RequiresElevation == true;

    /// <summary>Localized reason for a <see cref="SafeActionCodes"/> code.</summary>
    public static string CodeText(string? code) => Loc.T(SafeActionCodes.LocKey(code));

    /// <summary>Toast text for a failed / refused action. A declined UAC prompt is not an error.</summary>
    public static string FailText(string? code) =>
        code == SafeActionCodes.ElevationDeclined ? CodeText(code) : Loc.Tf("system.safe.fail", CodeText(code));

    /// <summary>Localized card title for an action id or journal key (per-entry ids format their subject in).</summary>
    public static string TitleFor(string actionId)
    {
        var exe = FullscreenOptimizationSafeActionTarget.ExePathFromActionId(actionId);
        if (exe != null) return Loc.Tf("optimize.fso.title", Path.GetFileName(exe));
        var target = Safe.FindTarget(actionId);
        string id = target?.ActionId ?? actionId;
        if (StartupToggle.TryParseActionId(id, out _, out _, out var name))
            return Loc.Tf("optimize.startup.journalTitle", StartupDisplayName(name));
        if (DnsSafeActionTarget.TryParseActionId(id, out _, out var servers))
            return Loc.Tf("net.tweak.dns.journalTitle", string.Join(", ", servers));
        return Loc.T(target?.TitleKey ?? "optimize." + actionId + ".title");
    }

    /// <summary>Title for a history detail (an action id), or null when no known target owns it.</summary>
    public static string? TryTitleFor(string actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return null;
        bool known = FullscreenOptimizationSafeActionTarget.ExePathFromActionId(actionId) != null
                     || StartupToggle.TryParseActionId(actionId, out _, out _, out _)
                     || Safe.FindTarget(actionId) != null;
        return known ? TitleFor(actionId) : null;
    }

    /// <summary>"Discord.lnk" → "Discord"; Run value names stay as they are.</summary>
    public static string StartupDisplayName(string name) =>
        name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(name) : name;

    /// <summary>Confirm text: why, risk, the undo note and — for elevated targets — the UAC note.</summary>
    public static string ConfirmMessage(string actionId, string? whyKey, string? riskKey)
    {
        var target = Safe.FindTarget(actionId);
        var lines = new List<string>();
        string? why = whyKey ?? target?.WhyKey;
        string? risk = riskKey ?? target?.RiskKey;
        if (why != null) lines.Add(Loc.T(why));
        if (risk != null) lines.Add(Loc.T("optimize.ui.risk") + ": " + Loc.T(risk));
        lines.Add(Loc.T("optimize.ui.confirm.undoNote"));
        if (target?.RequiresElevation == true) lines.Add(Loc.T("net.tweak.status.uacHint") + ".");
        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// Confirm (optional) → apply → toast. Returns the outcome, or null when the user cancelled the confirm.
    /// Never throws; never blocks the UI thread.
    /// </summary>
    public static async Task<SafeActionOutcome?> ApplyAsync(IShellHost host, string actionId, SafeApplyOptions? options = null)
    {
        options ??= new SafeApplyOptions();
        var target = Safe.FindTarget(actionId);
        if (target == null)
        {
            host.ShowToast(FailText(SafeActionCodes.UnknownAction));
            return new SafeActionOutcome(SafeActionResult.Failed, SafeActionCodes.UnknownAction);
        }
        string title = options.Title ?? TitleFor(actionId);
        if (options.Confirm
            && !ThemedDialog.Confirm(host.Window, ConfirmMessage(actionId, options.WhyKey, options.RiskKey), title, Loc.T("btn.apply")))
            return null;

        SafeActionOutcome r;
        try
        {
            // Thread pool: the pre-read / verify re-read can run netsh or registry reads, and the elevated write waits on
            // a UAC prompt — none of it may run on the UI thread. Nothing subscribes to Journal.Changed on the UI side.
            r = await Task.Run(() => Safe.ApplyAsync(actionId));
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "SafeAction apply " + actionId);
            r = new SafeActionOutcome(SafeActionResult.Failed, SafeActionCodes.ApplyFailed);
        }

        switch (r.Result)
        {
            case SafeActionResult.Applied:
            {
                App.Services.History.Add("safeAction", detail: options.HistoryDetail ?? actionId);
                string key = target.JournalKey;
                string msg = options.OkToast ?? Loc.Tf("optimize.ui.toast.applied", title);
                if (!string.IsNullOrEmpty(options.AfterNote)) msg += "\n" + options.AfterNote;
                if (options.OfferUndo && Safe.CanUndo(key))
                    host.ShowUndoToast(msg, () => _ = UndoAsync(host, key));
                else
                    host.ShowToast(msg);
                RaiseCompleted(key, true);
                break;
            }
            case SafeActionResult.NoOp:
                host.ShowToast(Loc.T("safeAction.noop"));
                break;
            default:
                host.ShowToast(FailText(r.Code));
                break;
        }
        return r;
    }

    /// <summary>Restores the journaled previous state; a value changed outside Visor asks before forcing.</summary>
    public static async Task<bool> UndoAsync(IShellHost host, string actionIdOrKey, string? historyDetail = null)
    {
        if (!Safe.CanUndo(actionIdOrKey))
        {
            host.ShowToast(CodeText(SafeActionCodes.CannotRollback));
            return false;
        }
        string key = Safe.FindTarget(actionIdOrKey)?.JournalKey ?? actionIdOrKey;
        string undoneAction = Safe.Journal.Get(key)?.ActionId ?? actionIdOrKey;
        SafeActionUndoOutcome r;
        try
        {
            r = await Task.Run(() => Safe.UndoAsync(actionIdOrKey));
            if (r.Result == SafeActionUndoResult.Conflict)
            {
                if (!ThemedDialog.Confirm(host.Window,
                        Loc.T("safeAction.undo.conflict"),
                        Loc.T("system.rollback"),
                        Loc.T("safeAction.undo.force"),
                        danger: true))
                    return false;
                r = await Task.Run(() => Safe.UndoAsync(actionIdOrKey, confirmedConflict: true));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "SafeAction undo " + actionIdOrKey);
            r = new SafeActionUndoOutcome(SafeActionUndoResult.Failed, SafeActionCodes.ApplyFailed);
        }

        if (r.Result == SafeActionUndoResult.Undone)
        {
            App.Services.History.Add("safeAction", detail: historyDetail ?? undoneAction + ".undo");
            host.ShowToast(Loc.T("system.rollback.ok"));
            RaiseCompleted(key, false);
            return true;
        }
        host.ShowToast(FailText(r.Code));
        return false;
    }

    private static void RaiseCompleted(string key, bool applied)
    {
        try { Completed?.Invoke(key, applied); }
        catch (Exception ex) { AppLog.Warn(ex, "SafeAction completed handler"); }
    }
}
