namespace Visor.Models;

/// <summary>One user-visible Visor action in the activity history ring.</summary>
public sealed class ActivityHistoryEntry
{
    public DateTime Utc { get; set; } = DateTime.UtcNow;

    /// <summary>Stable kind token: profileApplied, presetApplied, sessionRestored, emergency, paused, resumed, leftoverRestored.</summary>
    public string Kind { get; set; } = "";

    public string? ProfileId { get; set; }
    public string? ProfileName { get; set; }
    public string? Detail { get; set; }
    public bool Success { get; set; } = true;
}
