using System.Reflection;
using System.Text.Json;
using Visor.Models;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class SessionSnapshotModelTests
{
    /// <summary>
    /// The options SessionSnapshotService really uses for active-session.json, read from the service
    /// itself so the round-trip below tests the file format that ships (not a copy that could drift).
    /// The field is private; if it is renamed or removed this throws and every test here fails loudly.
    /// </summary>
    private static readonly JsonSerializerOptions Json = ServiceJsonOptions();

    private static JsonSerializerOptions ServiceJsonOptions()
    {
        var field = typeof(SessionSnapshotService).GetField("JsonOptions",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        return field?.GetValue(null) as JsonSerializerOptions
            ?? throw new InvalidOperationException(
                "SessionSnapshotService.JsonOptions (static JsonSerializerOptions) not found - update SessionSnapshotModelTests to the service's new seam.");
    }

    [Fact]
    public void Service_options_write_camel_case_property_names()
    {
        // Guards the assumptions the string assertions below rely on.
        Assert.Same(JsonNamingPolicy.CamelCase, Json.PropertyNamingPolicy);
        Assert.True(Json.WriteIndented);
        Assert.False(Json.PropertyNameCaseInsensitive);
    }

    [Fact]
    public void Round_trip_keeps_change_flags_restore_attempts_and_displays()
    {
        var snap = new ActiveSessionSnapshot
        {
            Version = ActiveSessionSnapshot.CurrentVersion,
            CapturedUtc = new DateTime(2026, 10, 13, 8, 30, 0, DateTimeKind.Utc),
            ProfileId = "tarkov",
            ProfileName = "Escape from Tarkov",
            Displays =
            {
                new SnapshotDisplay { Device = @"\\.\DISPLAY1", Resolution = "2560x1440", RefreshRate = 165, ModeChanged = true, HasGammaRamp = true, GammaRedB64 = "AAA=", GammaGreenB64 = "AQE=", GammaBlueB64 = "AgI=" },
                new SnapshotDisplay { Device = @"\\.\DISPLAY2", Resolution = "1920x1080", RefreshRate = 60, ModeChanged = false }
            },
            PowerPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e",
            PowerPlanChanged = true,
            ToastOriginal = 1,
            ToastChanged = true,
            AutoHdrOriginal = null,
            AutoHdrChanged = false,
            AudioDeviceId = "{0.0.0.00000000}.{abc}",
            AudioConsoleDeviceId = "{0.0.0.00000000}.{def}",
            AudioChanged = true,
            MonitorBrightnessLevels = new List<int?> { 80, null },
            BrightnessChanged = true,
            TopologySaved = true,
            TopologyPathsB64 = "cGF0aHM=",
            TopologyModesB64 = "bW9kZXM=",
            TopologyPathCount = 2,
            TopologyModeCount = 4,
            ScalingSaved = true,
            ScalingDevice = @"\\.\DISPLAY1",
            ScalingFixedOutput = 3,
            ScalingWidth = 1920,
            ScalingHeight = 1080,
            HasDriverColor = true,
            DriverVendor = "nvidia",
            DriverDisplayName = "Display 1",
            DriverVibranceLevel = 50,
            DriverNormalizedVibrance = 0.5f,
            DriverHueAngle = 0,
            FpsLimitChanged = true,
            FpsLimitTarget = "EscapeFromTarkov.exe",
            FpsLimitHadSetting = true,
            FpsLimitWasPredefined = false,
            FpsLimitOriginalValue = 144,
            FpsLimitCreatedProfile = true,
            RestoreAttempts = 2
        };

        var text = JsonSerializer.Serialize(snap, Json);
        var back = JsonSerializer.Deserialize<ActiveSessionSnapshot>(text, Json);

        Assert.NotNull(back);
        Assert.Equal(JsonSerializer.Serialize(snap, Json), JsonSerializer.Serialize(back, Json));
        Assert.Equal(2, back!.Version);
        Assert.Equal(2, back.RestoreAttempts);
        Assert.True(back.PowerPlanChanged);
        Assert.True(back.ToastChanged);
        Assert.Equal(1, back.ToastOriginal);
        Assert.False(back.AutoHdrChanged);
        Assert.Null(back.AutoHdrOriginal);
        Assert.True(back.AudioChanged);
        Assert.True(back.BrightnessChanged);
        Assert.Equal(new List<int?> { 80, null }, back.MonitorBrightnessLevels);
        Assert.True(back.FpsLimitChanged);
        Assert.Equal(144u, back.FpsLimitOriginalValue);
        Assert.Equal(2, back.Displays.Count);
        Assert.True(back.Displays[0].ModeChanged);
        Assert.False(back.Displays[1].ModeChanged);
        Assert.Equal("AQE=", back.Displays[0].GammaGreenB64);
        Assert.Equal(snap.CapturedUtc, back.CapturedUtc);

        // Property names as they appear in active-session.json (what a hand-edited or legacy file must match).
        Assert.Contains("\"version\": 2", text);
        Assert.Contains("\"restoreAttempts\": 2", text);
        Assert.Contains("\"powerPlanChanged\": true", text);
        Assert.Contains("\"displays\": [", text);
        Assert.Contains("\"modeChanged\": true", text);
        Assert.Contains("\"fpsLimitOriginalValue\": 144", text);
        Assert.DoesNotContain("\"RestoreAttempts\"", text);
    }

    [Fact]
    public void Legacy_snapshot_without_flags_deserializes_with_version_0_semantics()
    {
        const string legacy = """
            {
              "capturedUtc": "2025-01-02T03:04:05Z",
              "profileId": "valorant",
              "profileName": "Valorant",
              "resolution": "1920x1080",
              "refreshRate": 144,
              "displayDevice": "\\\\.\\DISPLAY1",
              "gammaRedB64": "AAA=",
              "gammaGreenB64": "AAA=",
              "gammaBlueB64": "AAA=",
              "hasGammaRamp": true,
              "powerPlanGuid": "381b4222-f694-41f0-9685-ff5bb260df2e",
              "toastEnabled": true,
              "autoHdrEnabled": false,
              "nightLightKnown": true,
              "nightLightOn": false,
              "audioDeviceId": "{dev}",
              "monitorBrightness": 70
            }
            """;

        var snap = JsonSerializer.Deserialize<ActiveSessionSnapshot>(legacy, Json);

        Assert.NotNull(snap);
        Assert.Equal(0, snap!.Version);
        Assert.Equal(0, snap.RestoreAttempts);
        Assert.Empty(snap.Displays);

        // Legacy single-display fields survive for the upgrade step.
        Assert.Equal("1920x1080", snap.Resolution);
        Assert.Equal(144, snap.RefreshRate);
        Assert.Equal(@"\\.\DISPLAY1", snap.DisplayDevice);
        Assert.True(snap.HasGammaRamp);
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", snap.PowerPlanGuid);
        Assert.True(snap.ToastEnabled);
        Assert.False(snap.AutoHdrEnabled);
        Assert.Equal("{dev}", snap.AudioDeviceId);
        Assert.Equal(70, snap.MonitorBrightness);

        // No change flags in the file -> all false; the service's UpgradeLegacy decides what to restore.
        Assert.False(snap.PowerPlanChanged);
        Assert.False(snap.ToastChanged);
        Assert.False(snap.AutoHdrChanged);
        Assert.False(snap.AudioChanged);
        Assert.False(snap.BrightnessChanged);
        Assert.False(snap.FpsLimitChanged);
        Assert.Null(snap.ToastOriginal);
        Assert.Null(snap.AutoHdrOriginal);
        Assert.Null(snap.MonitorBrightnessLevels);
        Assert.Null(snap.AudioConsoleDeviceId);
    }

    [Fact]
    public void Pascal_case_property_names_are_not_recognised_by_the_service_options()
    {
        // The service is case-sensitive: a file written with PascalCase keys (e.g. by hand) is treated as empty
        // rather than silently half-read. This pins that behaviour so a future options change is deliberate.
        var snap = JsonSerializer.Deserialize<ActiveSessionSnapshot>(
            """{ "Version": 2, "RestoreAttempts": 5, "PowerPlanChanged": true }""", Json);
        Assert.NotNull(snap);
        Assert.Equal(0, snap!.Version);
        Assert.Equal(0, snap.RestoreAttempts);
        Assert.False(snap.PowerPlanChanged);
    }

    [Fact]
    public void Unknown_properties_and_null_displays_do_not_throw()
    {
        var snap = JsonSerializer.Deserialize<ActiveSessionSnapshot>(
            """{ "version": 2, "futureField": { "x": 1 }, "displays": null, "restoreAttempts": 1 }""", Json);
        Assert.NotNull(snap);
        Assert.Equal(2, snap!.Version);
        Assert.Null(snap.Displays);
        Assert.Equal(1, snap.RestoreAttempts);
    }

    [Fact]
    public void New_snapshot_defaults()
    {
        var snap = new ActiveSessionSnapshot();
        Assert.Equal(0, snap.Version);
        Assert.Equal(2, ActiveSessionSnapshot.CurrentVersion);
        Assert.NotNull(snap.Displays);
        Assert.Equal(0, snap.RestoreAttempts);
        Assert.True(snap.ToastEnabled);
        Assert.True(snap.AutoHdrEnabled);
        Assert.True((DateTime.UtcNow - snap.CapturedUtc).Duration() < TimeSpan.FromMinutes(5));
    }
}
