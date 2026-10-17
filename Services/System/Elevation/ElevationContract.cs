namespace Visor.Services.Elevation;

// Contract between unelevated Visor and its per-action elevated helper ("Visor.exe --elevated").
// Visor itself never runs elevated. Each Apply/Undo that needs HKLM / netsh / powercfg builds a short
// list of ElevatedOps, the broker starts the helper with Verb=runas (one UAC prompt per batch), the
// helper validates every op against a fixed allowlist, executes, and returns one result per op.
// Undo data (exact previous values) is captured BEFORE the batch by the caller and journaled by
// SafeActionService — the helper is stateless.

public enum ElevatedOpKind
{
    /// <summary>Target = full HKLM key path (no hive prefix, e.g. SYSTEM\CurrentControlSet\...), Name = value name, Value = decimal DWORD.</summary>
    RegSetDword,
    /// <summary>Target = HKLM key path, Name = value name, Value = string (REG_SZ).</summary>
    RegSetString,
    /// <summary>Target = HKLM key path, Name = value name. Deleting a value that does not exist succeeds.</summary>
    RegDeleteValue,
    /// <summary>Target = interface GUID ("{...}"), Value = comma-separated IPv4/IPv6 DNS servers (static).</summary>
    DnsSetStatic,
    /// <summary>Target = interface GUID; switch DNS back to DHCP-provided servers.</summary>
    DnsSetDhcp,
    /// <summary>Target = "autotuninglevel" | "ecncapability" | "rss"; Value = setting value (validated per target).</summary>
    TcpGlobalSet,
    /// <summary>Value = source scheme GUID to duplicate (only the Ultimate Performance template is allowed). Output = new GUID.</summary>
    PowerDuplicateScheme,
    /// <summary>Target = HKLM key path, Name = value name, Value = hex bytes (REG_BINARY, e.g. "020000000000000000000000").</summary>
    RegSetBinary,
}

public sealed record ElevatedOp(ElevatedOpKind Kind, string Target, string? Name = null, string? Value = null);

/// <summary>Per-op outcome. Output carries op-specific data (e.g. new power scheme GUID).</summary>
public sealed record ElevatedOpResult(bool Ok, string? ErrorCode = null, string? Output = null);

public enum ElevationOutcome
{
    /// <summary>Helper ran; inspect per-op results.</summary>
    Completed,
    /// <summary>User clicked "No" on the UAC prompt (Win32 1223).</summary>
    Declined,
    /// <summary>A requested op is outside the allowlist — nothing was executed.</summary>
    Rejected,
    /// <summary>Helper could not be started or timed out.</summary>
    Failed,
}

public sealed record ElevatedBatchResult(
    ElevationOutcome Outcome,
    IReadOnlyList<ElevatedOpResult> Results,
    string? ErrorCode = null)
{
    public bool AllOk => Outcome == ElevationOutcome.Completed && Results.Count > 0 && Results.All(r => r.Ok);
}

public interface IElevationBroker
{
    /// <summary>Runs the batch in one elevated helper process (one UAC prompt). Never throws for UAC decline.</summary>
    Task<ElevatedBatchResult> RunAsync(IReadOnlyList<ElevatedOp> ops, CancellationToken ct = default);
}
