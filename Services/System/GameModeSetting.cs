using Microsoft.Win32;

namespace Visor.Services;

/// <summary>Minimal registry seam for the Game Mode values (tests inject a dictionary-backed fake).</summary>
public interface IGameModeRegistry
{
    /// <summary>DWORD value or null when the value (or the key) is absent.</summary>
    int? GetDword(string valueName);
    void SetDword(string valueName, int value);
    /// <summary>Deletes the value; no-op when absent.</summary>
    void DeleteValue(string valueName);
}

/// <summary>Exact raw snapshot of both Game Mode DWORDs; null = value absent.</summary>
public readonly record struct GameModeRawState(int? Auto, int? Allow)
{
    /// <summary>Windows semantics: AutoGameModeEnabled wins, then AllowAutoGameMode, absence = on.</summary>
    public bool Enabled => (Auto ?? Allow ?? 1) != 0;

    public string Serialize() => $"auto:{Fmt(Auto)}|allow:{Fmt(Allow)}";

    private static string Fmt(int? v) => v?.ToString() ?? "-";

    public static bool TryParse(string? text, out GameModeRawState state)
    {
        state = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        int? auto = null, allow = null;
        bool sawAuto = false, sawAllow = false;
        foreach (var part in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0) return false;
            string name = part[..colon];
            string val = part[(colon + 1)..];
            int? parsed;
            if (val == "-") parsed = null;
            else if (int.TryParse(val, out int n)) parsed = n;
            else return false;
            if (name.Equals("auto", StringComparison.OrdinalIgnoreCase)) { auto = parsed; sawAuto = true; }
            else if (name.Equals("allow", StringComparison.OrdinalIgnoreCase)) { allow = parsed; sawAllow = true; }
            else return false;
        }
        if (!sawAuto || !sawAllow) return false;
        state = new GameModeRawState(auto, allow);
        return true;
    }
}

/// <summary>
/// Windows Game Mode (Settings → Gaming → Game Mode) via HKCU\Software\Microsoft\GameBar.
/// Writes both <c>AutoGameModeEnabled</c> and <c>AllowAutoGameMode</c> (Win11 dual values).
/// Missing values = enabled (Windows default). No admin required.
/// </summary>
public static class GameModeSetting
{
    public const string ActionId = "gameMode";
    public const string ActionIdOn = "gameMode.on";
    public const string ActionIdOff = "gameMode.off";
    private const string KeyPath = @"Software\Microsoft\GameBar";
    public const string AutoValue = "AutoGameModeEnabled";
    public const string AllowValue = "AllowAutoGameMode";

    private sealed class HkcuRegistry : IGameModeRegistry
    {
        public int? GetDword(string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            var v = key?.GetValue(valueName);
            return v == null ? null : Convert.ToInt32(v);
        }

        public void SetDword(string valueName, int value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath)
                            ?? throw new InvalidOperationException("GameBar key unavailable");
            key.SetValue(valueName, value, RegistryValueKind.DWord);
        }

        public void DeleteValue(string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    private static readonly IGameModeRegistry Default = new HkcuRegistry();

    /// <summary>Both DWORDs exactly as stored; null on registry failure.</summary>
    public static GameModeRawState? ReadRaw(IGameModeRegistry? registry = null)
    {
        registry ??= Default;
        try
        {
            return new GameModeRawState(registry.GetDword(AutoValue), registry.GetDword(AllowValue));
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Game Mode read");
            return null;
        }
    }

    /// <summary>True/false when readable; null on registry failure.</summary>
    public static bool? Read(IGameModeRegistry? registry = null) => ReadRaw(registry)?.Enabled;

    /// <summary>Writes both DWORD values and re-reads to verify (null = write/read failure).</summary>
    public static bool? Write(bool enabled, IGameModeRegistry? registry = null)
    {
        registry ??= Default;
        if (!WriteRaw(new GameModeRawState(enabled ? 1 : 0, enabled ? 1 : 0), registry)) return null;
        return Read(registry);
    }

    /// <summary>Restores an exact snapshot: sets present values, deletes absent ones.</summary>
    public static bool WriteRaw(GameModeRawState state, IGameModeRegistry? registry = null)
    {
        registry ??= Default;
        try
        {
            if (state.Auto is int a) registry.SetDword(AutoValue, a); else registry.DeleteValue(AutoValue);
            if (state.Allow is int l) registry.SetDword(AllowValue, l); else registry.DeleteValue(AllowValue);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Game Mode write");
            return false;
        }
    }

    public static SafeActionSession Apply(bool enable, IGameModeRegistry? registry = null)
    {
        registry ??= Default;
        var previousRaw = ReadRaw(registry);
        var session = SafeActionFlow.Begin(ActionId,
            previousRaw?.Serialize(),
            enable.ToString().ToLowerInvariant());
        if (previousRaw == null)
        {
            SafeActionFlow.MarkFailed(session, SafeActionCodes.Unavailable);
            return session;
        }
        if (previousRaw.Value.Enabled == enable)
        {
            session.VerifiedValue = enable.ToString().ToLowerInvariant();
            session.State = SafeActionState.Completed;
            return session;
        }

        SafeActionFlow.MarkRunning(session);
        bool wrote = WriteRaw(new GameModeRawState(enable ? 1 : 0, enable ? 1 : 0), registry);
        var afterRaw = ReadRaw(registry);
        if (afterRaw != null && afterRaw.Value.Enabled == enable)
        {
            SafeActionFlow.MarkCompleted(session, enable.ToString().ToLowerInvariant());
            return session;
        }
        // Re-read says the value did not reach the target: never report success.
        SafeActionFlow.MarkApplyFailed(session,
            wrote ? SafeActionCodes.VerifyMismatch : SafeActionCodes.ApplyFailed,
            previousRaw.Value.Serialize(), afterRaw?.Serialize(),
            afterRaw?.Enabled.ToString().ToLowerInvariant());
        return session;
    }

    /// <param name="confirmedConflict">Restore even when the current value is not what Visor applied.</param>
    public static SafeActionSession Rollback(SafeActionSession session, IGameModeRegistry? registry = null,
        bool confirmedConflict = false)
    {
        registry ??= Default;
        if (session.ActionId != ActionId || string.IsNullOrEmpty(session.PreviousValue)
            || session.State is not (SafeActionState.RollbackAvailable or SafeActionState.RollbackFailed))
        {
            SafeActionFlow.MarkFailed(session, SafeActionCodes.CannotRollback);
            return session;
        }
        if (!TryParseBackup(session.PreviousValue, out var prev))
        {
            SafeActionFlow.MarkFailed(session, SafeActionCodes.BadBackup);
            return session;
        }

        var current = ReadRaw(registry);
        if (current == null)
        {
            session.State = SafeActionState.RollbackFailed;
            session.Error = SafeActionCodes.Unavailable;
            return session;
        }
        if (!confirmedConflict && bool.TryParse(session.TargetValue, out bool target)
            && current.Value.Enabled != target && current.Value != prev)
        {
            session.State = SafeActionState.RollbackFailed;
            session.Error = SafeActionCodes.Conflict;
            return session;
        }

        SafeActionFlow.MarkRunning(session);
        if (!WriteRaw(prev, registry))
        {
            session.State = SafeActionState.RollbackFailed;
            session.Error = SafeActionCodes.ApplyFailed;
            return session;
        }
        var verified = ReadRaw(registry);
        session.VerifiedValue = verified?.Serialize();
        if (verified != null && verified.Value == prev)
        {
            session.State = SafeActionState.Completed;
            session.Error = null;
        }
        else
        {
            session.State = SafeActionState.RollbackFailed;
            session.Error = SafeActionCodes.RollbackVerifyMismatch;
        }
        return session;
    }

    private static bool TryParseBackup(string text, out GameModeRawState state)
    {
        if (GameModeRawState.TryParse(text, out state)) return true;
        // Pre-journal sessions stored "true"/"false".
        if (bool.TryParse(text, out bool b))
        {
            state = new GameModeRawState(b ? 1 : 0, b ? 1 : 0);
            return true;
        }
        return false;
    }
}

/// <summary><see cref="ISafeActionTarget"/> for Game Mode on/off. Both share the "gameMode" undo slot.</summary>
public sealed class GameModeSafeActionTarget : ISafeActionTarget
{
    private readonly IGameModeRegistry? _registry;

    public GameModeSafeActionTarget(bool enable, IGameModeRegistry? registry = null)
    {
        Enable = enable;
        _registry = registry;
    }

    public bool Enable { get; }
    public string ActionId => Enable ? GameModeSetting.ActionIdOn : GameModeSetting.ActionIdOff;
    public string JournalKey => GameModeSetting.ActionId;
    public bool BlockedDuringGameSession => false;

    public string? ReadState() => GameModeSetting.ReadRaw(_registry)?.Serialize();

    public string? ResolveTargetState() => new GameModeRawState(Enable ? 1 : 0, Enable ? 1 : 0).Serialize();

    public bool SatisfiesTarget(string state, string targetState) =>
        GameModeRawState.TryParse(state, out var s)
        && GameModeRawState.TryParse(targetState, out var t)
        && s.Enabled == t.Enabled;

    public bool StatesEqual(string a, string b) =>
        GameModeRawState.TryParse(a, out var x) && GameModeRawState.TryParse(b, out var y) && x == y;

    public bool WriteState(string state) =>
        GameModeRawState.TryParse(state, out var s) && GameModeSetting.WriteRaw(s, _registry);
}
