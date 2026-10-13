using Visor.Models;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class ProfilePackImportTests
{
    private const string UntrustedPack = """
        {
          "name": "Shared pack",
          "profile": {
            "id": "attacker-id",
            "name": "Shared game",
            "processName": "game",
            "processAliases": [ "launcher.exe", "C:\\evil\\x.exe", "..\\up.exe", "helper", "", "script.bat" ],
            "exePath": "C:\\Games\\game.exe",
            "displayDevice": "\\\\.\\DISPLAY2",
            "startupPresetId": "p2",
            "companions": [
              { "path": "C:\\evil\\payload.exe", "arguments": "-x", "launchMode": "direct" },
              { "path": "", "launchMode": "scheduledTask", "taskName": "Evil" }
            ],
            "presets": [
              { "id": "p1", "name": "One", "hotkey": "Ctrl+Alt+1", "color": { "brightness": 0.6, "contrast": 1.1, "gamma": 1.2 } },
              { "id": "p2", "name": "Two", "hotkey": "Ctrl+Alt+1", "color": { "brightness": 9, "contrast": -1, "gamma": 1 } },
              { "id": "p3", "name": "Three", "hotkey": "NotAKey+Zzz", "color": null },
              { "id": "p4", "name": "Four", "hotkey": "Shift+F5" }
            ],
            "session": { "audioDeviceId": "{dev}", "switchAudioDevice": true }
          }
        }
        """;

    [Fact]
    public void Import_strips_companions_and_exe_path()
    {
        var p = ProfilePackService.ImportFromJson(UntrustedPack);
        Assert.NotNull(p);
        Assert.Empty(p!.Companions);
        Assert.Null(p.ExePath);
        Assert.Equal("game.exe", p.ProcessName);
        Assert.Equal("Shared game", p.Name);
        Assert.NotEqual("attacker-id", p.Id);
    }

    [Fact]
    public void Import_drops_aliases_with_paths_or_bad_extensions()
    {
        var p = ProfilePackService.ImportFromJson(UntrustedPack)!;
        Assert.Equal(new[] { "launcher.exe", "helper.exe" }, p.ProcessAliases);
    }

    [Fact]
    public void Import_clears_duplicate_and_invalid_hotkeys_keeps_first()
    {
        var p = ProfilePackService.ImportFromJson(UntrustedPack)!;
        var byName = p.Presets.ToDictionary(x => x.Name);
        Assert.Equal("Ctrl+Alt+1", byName["One"].Hotkey);
        Assert.Null(byName["Two"].Hotkey);   // duplicate of One
        Assert.Null(byName["Three"].Hotkey); // unparsable
        Assert.Equal("Shift+F5", byName["Four"].Hotkey);
    }

    [Fact]
    public void Import_reissues_preset_ids_and_remaps_startup_preset()
    {
        var p = ProfilePackService.ImportFromJson(UntrustedPack)!;
        Assert.Equal(4, p.Presets.Count);
        Assert.All(p.Presets, pr => Assert.StartsWith(p.Id + "_", pr.Id));
        Assert.Equal(4, p.Presets.Select(x => x.Id).Distinct().Count());
        Assert.DoesNotContain(p.Presets, pr => pr.Id is "p1" or "p2" or "p3" or "p4");

        var two = p.Presets.Single(x => x.Name == "Two");
        Assert.Equal(two.Id, p.StartupPresetId);
    }

    [Fact]
    public void Import_clamps_colors_and_fills_dual_slots()
    {
        var p = ProfilePackService.ImportFromJson(UntrustedPack)!;
        var two = p.Presets.Single(x => x.Name == "Two");
        Assert.Equal(1.0, two.Color.Brightness);
        Assert.Equal(0.0, two.Color.Contrast);
        Assert.All(p.Presets, pr =>
        {
            Assert.NotNull(pr.Color);
            Assert.NotNull(pr.ColorLowLevel);
            Assert.NotNull(pr.ColorDriver);
        });
        Assert.NotNull(p.ColorLowLevel);
        Assert.NotNull(p.ColorDriver);
    }

    [Fact]
    public void Import_rejects_system_process_names()
    {
        Assert.Null(ProfilePackService.ImportFromJson("""{ "profile": { "name": "x", "processName": "explorer.exe" } }"""));
        Assert.Null(ProfilePackService.ImportFromJson("""{ "profile": { "name": "x", "processName": "SVCHOST" } }"""));
        Assert.Null(ProfilePackService.ImportFromJson("""{ "profile": { "name": "x", "processName": "C:\\Games\\game.exe" } }"""));
        Assert.Null(ProfilePackService.ImportFromJson("""{ "profile": { "name": "x", "processName": "game.bat" } }"""));
    }

    [Fact]
    public void Import_accepts_bare_GameProfile_json()
    {
        var p = ProfilePackService.ImportFromJson("""{ "name": "Bare", "processName": "Bare.exe", "presets": [ { "name": "A" } ] }""");
        Assert.NotNull(p);
        Assert.Equal("Bare", p!.Name);
        Assert.Equal("Bare.exe", p.ProcessName);
        Assert.Single(p.Presets);
    }

    [Fact]
    public void Import_returns_null_for_unreadable_or_empty_input()
    {
        Assert.Null(ProfilePackService.ImportFromJson("not json"));
        Assert.Null(ProfilePackService.ImportFromJson("{}"));
        Assert.Null(ProfilePackService.ImportFromJson("""{ "profile": null }"""));
        Assert.Null(ProfilePackService.ImportFromJson("""{ "profile": { "name": "x", "processName": "" } }"""));
    }

    [Fact]
    public void Import_without_startup_preset_mapping_clears_it()
    {
        var p = ProfilePackService.ImportFromJson("""
            { "profile": { "name": "x", "processName": "g.exe", "startupPresetId": "missing",
              "presets": [ { "id": "other", "name": "A" } ] } }
            """);
        Assert.NotNull(p);
        Assert.Null(p!.StartupPresetId);
    }

    [Fact]
    public void Import_tolerates_null_list_items_and_missing_fields()
    {
        var p = ProfilePackService.ImportFromJson("""
            { "profile": { "name": null, "processName": "g.exe", "processAliases": null, "companions": null,
              "presets": [ null, { "name": null, "color": null } ], "session": null, "color": null } }
            """);
        Assert.NotNull(p);
        Assert.Equal("Imported profile", p!.Name);
        var pr = Assert.Single(p.Presets);
        Assert.Equal("Preset", pr.Name);
        Assert.NotNull(p.Session);
    }
}
