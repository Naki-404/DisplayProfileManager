namespace DisplayProfileManager.Models;

/// <summary>
/// Pre-game PC state persisted to active-session.json for crash-safe restore.
/// </summary>
public sealed class ActiveSessionSnapshot
{
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public string? ProfileId { get; set; }
    public string? ProfileName { get; set; }

    public string? Resolution { get; set; }
    public int RefreshRate { get; set; }
    public string? DisplayDevice { get; set; }
    public string? PowerPlanGuid { get; set; }

    /// <summary>Base64 of 256 ushorts (little-endian) per channel.</summary>
    public string? GammaRedB64 { get; set; }
    public string? GammaGreenB64 { get; set; }
    public string? GammaBlueB64 { get; set; }
    public bool HasGammaRamp { get; set; }

    public bool ToastEnabled { get; set; } = true;
    public bool AutoHdrEnabled { get; set; } = true;
    public bool NightLightKnown { get; set; }
    public bool NightLightOn { get; set; }
    public string? AudioDeviceId { get; set; }
    public int? MonitorBrightness { get; set; }

    public bool TopologySaved { get; set; }
    public bool ScalingSaved { get; set; }
    public string? ScalingDevice { get; set; }
    public int? ScalingFixedOutput { get; set; }
    public int? ScalingWidth { get; set; }
    public int? ScalingHeight { get; set; }
}
