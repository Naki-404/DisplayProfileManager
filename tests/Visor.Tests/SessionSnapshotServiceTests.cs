using System.IO;
using System.Text.Json;
using Visor.Models;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

/// <summary>
/// File side of the session snapshot service (persist / load / quarantine / retry counting /
/// pending FPS undo). The DisplayEngine is constructed but never asked to touch the display —
/// only members that do pure file work are exercised.
/// </summary>
public class SessionSnapshotServiceTests
{
    private static SessionSnapshotService NewService(TempDir dir) =>
        new(new DisplayEngine(dir.Path), dir.Path);

    private static string Serialize(ActiveSessionSnapshot snap) =>
        JsonSerializer.Serialize(snap, SessionSnapshotService.SerializerOptions);

    private static ActiveSessionSnapshot FullSnapshot() => new()
    {
        Version = ActiveSessionSnapshot.CurrentVersion,
        CapturedUtc = new DateTime(2026, 10, 13, 8, 30, 0, DateTimeKind.Utc),
        ProfileId = "tarkov",
        ProfileName = "Escape from Tarkov",
        Displays =
        {
            new SnapshotDisplay { Device = @"\\.\DISPLAY1", Resolution = "2560x1440", RefreshRate = 165, ModeChanged = true, HasGammaRamp = true, GammaRedB64 = "AAA=", GammaGreenB64 = "AQE=", GammaBlueB64 = "AgI=" },
            new SnapshotDisplay { Device = @"\\.\DISPLAY2", Resolution = "1920x1080", RefreshRate = 60 }
        },
        PowerPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e",
        PowerPlanChanged = true,
        ToastOriginal = 1,
        ToastChanged = true,
        AudioConsoleDeviceId = "{0.0.0.00000000}.{def}",
        AudioChanged = true,
        MonitorBrightnessLevels = new List<int?> { 80, null },
        BrightnessChanged = true,
        HasDriverColor = true,
        DriverVendor = "nvidia",
        DriverDisplayName = "Display 1",
        DriverVibranceLevel = 50,
        DriverNormalizedVibrance = 0.5f,
        FpsLimitChanged = true,
        FpsLimitTarget = "EscapeFromTarkov.exe",
        FpsLimitHadSetting = true,
        FpsLimitOriginalValue = 144,
        FpsLimitCreatedProfile = true,
        RestoreAttempts = 1
    };

    // ------------------------------------------------------------------ round trip

    [Fact]
    public void Persist_then_Current_round_trips_through_the_service_options()
    {
        using var dir = new TempDir();
        var svc = NewService(dir);
        var snap = FullSnapshot();

        svc.Persist(snap);

        Assert.True(File.Exists(svc.SnapshotPath));
        Assert.False(File.Exists(svc.SnapshotPath + ".tmp"), "temp file must be replaced atomically");
        var text = File.ReadAllText(svc.SnapshotPath);
        Assert.Contains("\"powerPlanChanged\": true", text);   // camelCase + indented, as the service writes it
        Assert.Contains("\"restoreAttempts\": 1", text);

        // A fresh instance (no in-memory copy) reads the file back.
        var reloaded = NewService(dir).Current;
        Assert.NotNull(reloaded);
        Assert.Equal(Serialize(snap), Serialize(reloaded!));
        Assert.Equal(2, reloaded!.Displays.Count);
        Assert.True(reloaded.Displays[0].ModeChanged);
        Assert.Equal(144u, reloaded.FpsLimitOriginalValue);
        Assert.Equal(snap.CapturedUtc, reloaded.CapturedUtc);
    }

    [Fact]
    public void Current_is_null_and_HasSnapshot_false_without_a_file()
    {
        using var dir = new TempDir();
        var svc = NewService(dir);

        Assert.False(svc.HasSnapshot);
        Assert.Null(svc.Current);
    }

    [Fact]
    public void IsCurrent_and_PersistIfCurrent_track_the_live_snapshot_only()
    {
        using var dir = new TempDir();
        var svc = NewService(dir);
        var live = FullSnapshot();
        var stale = FullSnapshot();
        svc.Persist(live);

        Assert.True(svc.IsCurrent(live));
        Assert.False(svc.IsCurrent(stale));

        svc.Clear();
        Assert.False(svc.HasSnapshot);
        svc.PersistIfCurrent(live);                   // session ended — must not resurrect the file
        Assert.False(File.Exists(svc.SnapshotPath));
    }

    // ------------------------------------------------------------------ corrupt file

    [Fact]
    public void Corrupt_snapshot_is_quarantined_and_reported_as_absent()
    {
        using var dir = new TempDir();
        var svc = NewService(dir);
        File.WriteAllText(svc.SnapshotPath, "{ \"version\": 2, \"displays\": [ not json");
        Assert.True(svc.HasSnapshot);

        var snap = svc.Current;

        Assert.Null(snap);
        Assert.False(File.Exists(svc.SnapshotPath));
        var quarantined = Directory.GetFiles(dir.Path, "active-session.json.corrupt-*");
        Assert.Single(quarantined);
        Assert.Contains("not json", File.ReadAllText(quarantined[0]));
        Assert.False(svc.HasSnapshot);
    }

    [Fact]
    public void Empty_json_document_is_quarantined_too()
    {
        using var dir = new TempDir();
        var svc = NewService(dir);
        File.WriteAllText(svc.SnapshotPath, "null");

        Assert.Null(svc.Current);
        Assert.False(File.Exists(svc.SnapshotPath));
        Assert.Single(Directory.GetFiles(dir.Path, "active-session.json.corrupt-*"));
    }

    [Fact]
    public void Legacy_snapshot_is_upgraded_on_load()
    {
        using var dir = new TempDir();
        var svc = NewService(dir);
        File.WriteAllText(svc.SnapshotPath, """
            {
              "profileId": "valorant",
              "profileName": "Valorant",
              "resolution": "1920x1080",
              "refreshRate": 144,
              "displayDevice": "\\\\.\\DISPLAY1",
              "powerPlanGuid": "381b4222-f694-41f0-9685-ff5bb260df2e",
              "toastEnabled": true,
              "monitorBrightness": 70
            }
            """);

        var snap = svc.Current;

        Assert.NotNull(snap);
        Assert.Equal(ActiveSessionSnapshot.CurrentVersion, snap!.Version);
        var d = Assert.Single(snap.Displays);
        Assert.Equal(@"\\.\DISPLAY1", d.Device);
        Assert.Equal("1920x1080", d.Resolution);
        Assert.True(d.ModeChanged);
        Assert.True(snap.PowerPlanChanged);
        Assert.True(snap.ToastChanged);
        Assert.Equal(1, snap.ToastOriginal);
        Assert.True(snap.BrightnessChanged);
        Assert.Equal(new List<int?> { 70 }, snap.MonitorBrightnessLevels);
    }

    // ------------------------------------------------------------------ retry counting

    [Fact]
    public void Finish_with_failure_increments_RestoreAttempts_and_gives_up_after_three()
    {
        using var dir = new TempDir();
        var svc = NewService(dir);
        var snap = FullSnapshot();
        snap.RestoreAttempts = 0;
        svc.Persist(snap);

        // Each failed restore step keeps the snapshot (for Emergency Restore / next start) and counts.
        Assert.False(svc.Finish(snap, ok: false));
        Assert.Equal(1, snap.RestoreAttempts);
        Assert.True(File.Exists(svc.SnapshotPath));
        Assert.Contains("\"restoreAttempts\": 1", File.ReadAllText(svc.SnapshotPath));

        Assert.False(svc.Finish(snap, ok: false));
        Assert.Equal(2, snap.RestoreAttempts);
        Assert.True(File.Exists(svc.SnapshotPath));
        Assert.Equal(2, NewService(dir).Current!.RestoreAttempts);

        // Third failure: give up — the snapshot is discarded, nothing is retried forever.
        Assert.False(svc.Finish(snap, ok: false));
        Assert.Equal(3, snap.RestoreAttempts);
        Assert.False(File.Exists(svc.SnapshotPath));
        Assert.False(svc.HasSnapshot);
        Assert.Null(svc.Current);
    }

    [Fact]
    public void Finish_with_success_deletes_the_snapshot_without_counting()
    {
        using var dir = new TempDir();
        var svc = NewService(dir);
        var snap = FullSnapshot();
        snap.RestoreAttempts = 0;
        svc.Persist(snap);

        Assert.True(svc.Finish(snap, ok: true));

        Assert.Equal(0, snap.RestoreAttempts);
        Assert.False(File.Exists(svc.SnapshotPath));
        Assert.False(svc.HasSnapshot);
    }

    // ------------------------------------------------------------------ pending FPS undo

    [Fact]
    public void SavePendingFps_keeps_only_the_fps_undo_and_LoadPendingFps_reads_it_back()
    {
        using var dir = new TempDir();
        var svc = NewService(dir);
        var snap = FullSnapshot();

        svc.SavePendingFps(snap);

        Assert.True(svc.HasPendingFps);
        var pending = svc.LoadPendingFps();
        Assert.NotNull(pending);
        Assert.True(pending!.FpsLimitChanged);
        Assert.Equal("EscapeFromTarkov.exe", pending.FpsLimitTarget);
        Assert.Equal(144u, pending.FpsLimitOriginalValue);
        Assert.True(pending.FpsLimitCreatedProfile);
        Assert.Equal("tarkov", pending.ProfileId);
        Assert.Empty(pending.Displays);                 // display state is never part of the undo file
        Assert.False(pending.PowerPlanChanged);
    }

    [Fact]
    public void SavePendingFps_writes_nothing_when_no_cap_was_applied()
    {
        using var dir = new TempDir();
        var svc = NewService(dir);
        var snap = FullSnapshot();
        snap.FpsLimitChanged = false;

        svc.SavePendingFps(snap);

        Assert.False(svc.HasPendingFps);
        Assert.Null(svc.LoadPendingFps());
    }

    [Fact]
    public void ClearPendingFps_for_a_snapshot_only_deletes_a_matching_target()
    {
        using var dir = new TempDir();
        var svc = NewService(dir);
        var snap = FullSnapshot();
        svc.SavePendingFps(snap);

        var other = FullSnapshot();
        other.FpsLimitTarget = "SomeOtherGame.exe";
        svc.ClearPendingFps(other);
        Assert.True(svc.HasPendingFps);               // another session's undo is left for its retry

        var same = FullSnapshot();
        same.FpsLimitTarget = "escapefromtarkov.EXE"; // case-insensitive
        svc.ClearPendingFps(same);
        Assert.False(svc.HasPendingFps);
    }

    [Fact]
    public void Unreadable_pending_fps_file_is_dropped()
    {
        using var dir = new TempDir();
        var svc = NewService(dir);
        File.WriteAllText(dir.File("pending-fps-restore.json"), "{ broken");

        Assert.True(svc.HasPendingFps);
        Assert.Null(svc.LoadPendingFps());
        Assert.False(svc.HasPendingFps);
    }
}
