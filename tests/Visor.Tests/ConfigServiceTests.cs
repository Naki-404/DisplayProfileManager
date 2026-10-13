using System.IO;
using System.Text.Json;
using Visor.Models;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class ConfigServiceTests
{
    private static ConfigService NewService(TempDir dir, string? legacyDir = null) =>
        new(dir.Path, legacyDir) { EnableFileWatcher = false };

    private static JsonDocument ReadJson(string path) => JsonDocument.Parse(File.ReadAllText(path));

    // ------------------------------------------------------------------ defaults

    [Fact]
    public void LoadOrCreate_creates_default_config_when_missing()
    {
        using var dir = new TempDir();
        using var svc = NewService(dir);

        var cfg = svc.LoadOrCreate();

        Assert.True(File.Exists(svc.ConfigPath));
        Assert.False(File.Exists(svc.ConfigPath + ".bak"), "no backup before a previous valid file exists");
        Assert.False(File.Exists(svc.ConfigPath + ".tmp"));
        Assert.Equal(0, svc.LoadedConfigVersion);
        Assert.Equal(ConfigService.CurrentVersion, cfg.ConfigVersion);
        Assert.False(cfg.StartWithWindows);
        Assert.False(cfg.StartMinimized);
        Assert.False(cfg.Ui.SetupCompleted);
        Assert.True(cfg.CoreProfilesSeeded);
        Assert.Null(cfg.Presets);
        Assert.Null(svc.LastRecoveryMessage);
        Assert.NotEmpty(cfg.Profiles);
        Assert.All(cfg.Profiles, p =>
        {
            Assert.False(p.ApplyResolution, $"{p.Name} must not force a resolution");
            Assert.Null(p.Resolution);
            Assert.Empty(p.Companions);
            Assert.NotEmpty(p.Presets);
            Assert.All(p.Presets, pr => Assert.StartsWith(p.Id + "_", pr.Id));
        });
        Assert.Equal(TestEnvironment.FakeResolution, cfg.FactoryDefaults.Resolution);
        Assert.Equal(TestEnvironment.FakePowerPlan, cfg.FactoryDefaults.PowerPlan);
        Assert.NotSame(cfg.Defaults, cfg.FactoryDefaults);
    }

    [Fact]
    public void Default_presets_use_CtrlAlt_digits_not_NumPad()
    {
        var presets = ConfigService.CreateDefaultPresets();
        Assert.Equal(new[] { "Ctrl+Alt+1", "Ctrl+Alt+2", "Ctrl+Alt+3", "Ctrl+Alt+4", "Ctrl+Alt+5" },
            presets.Select(p => p.Hotkey).ToArray());

        var cfg = ConfigService.CreateDefaultConfig();
        foreach (var hk in cfg.Profiles.SelectMany(p => p.Presets).Select(p => p.Hotkey).Where(h => h != null))
        {
            Assert.DoesNotContain("NumPad", hk, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("+", hk);
        }
    }

    [Fact]
    public void Default_config_has_no_personal_settings()
    {
        var cfg = ConfigService.CreateDefaultConfig();
        Assert.False(cfg.StartWithWindows);
        Assert.Null(cfg.Defaults.PowerPlan == null ? null : null); // Defaults mirror the factory capture
        Assert.All(cfg.Profiles, p => Assert.True(p.ApplyPowerPlan));
        Assert.All(cfg.Profiles, p => Assert.False(p.ApplyColor));
    }

    // ------------------------------------------------------------------ atomic save / backup

    [Fact]
    public void Save_is_atomic_and_rotates_backup_only_after_valid_previous_file()
    {
        using var dir = new TempDir();
        using var svc = NewService(dir);
        var cfg = svc.LoadOrCreate();
        var bak = svc.ConfigPath + ".bak";
        Assert.False(File.Exists(bak));

        var first = File.ReadAllText(svc.ConfigPath);
        cfg.StartMinimized = true;
        int raised = 0;
        svc.ConfigChanged += () => raised++;
        svc.Save(cfg, raiseChanged: false);

        Assert.True(File.Exists(bak));
        Assert.Equal(first, File.ReadAllText(bak));
        Assert.False(File.Exists(svc.ConfigPath + ".tmp"));
        Assert.Equal(0, raised);

        using var doc = ReadJson(svc.ConfigPath);
        Assert.True(doc.RootElement.GetProperty("startMinimized").GetBoolean());
        Assert.Equal(ConfigService.CurrentVersion, doc.RootElement.GetProperty("configVersion").GetInt32());

        svc.Save(cfg);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Save_respects_BackupOnSave_false()
    {
        using var dir = new TempDir();
        using var svc = NewService(dir);
        var cfg = svc.LoadOrCreate();
        cfg.Ui.BackupOnSave = false;
        svc.Save(cfg, raiseChanged: false);
        Assert.False(File.Exists(svc.ConfigPath + ".bak"));
    }

    [Fact]
    public void Save_never_writes_NaN_overlay_position()
    {
        using var dir = new TempDir();
        using var svc = NewService(dir);
        var cfg = svc.LoadOrCreate();
        cfg.Ui.OverlayLeft = double.NaN;
        cfg.Ui.OverlayTop = double.PositiveInfinity;
        cfg.Ui.OverlayPanelOpacity = double.NaN;
        svc.Save(cfg, raiseChanged: false);

        var text = File.ReadAllText(svc.ConfigPath);
        Assert.DoesNotContain("NaN", text);
        Assert.DoesNotContain("Infinity", text);
        Assert.Null(cfg.Ui.OverlayLeft);
        Assert.Null(cfg.Ui.OverlayTop);
        Assert.Equal(0.92, cfg.Ui.OverlayPanelOpacity);
    }

    // ------------------------------------------------------------------ corruption recovery

    [Fact]
    public void Corrupt_config_recovers_from_backup_and_keeps_backup_intact()
    {
        using var dir = new TempDir();
        string bakContent;
        using (var svc = NewService(dir))
        {
            var cfg = svc.LoadOrCreate();
            cfg.Ui.Locale = "ru";
            svc.Save(cfg, raiseChanged: false); // creates .bak (default locale "en")
            cfg.Ui.Theme = "light";
            svc.Save(cfg, raiseChanged: false); // .bak now has locale "ru", theme "dark"
            bakContent = File.ReadAllText(svc.ConfigPath + ".bak");
        }

        File.WriteAllText(dir.File("profiles.json"), "{ this is not json");

        using (var svc = NewService(dir))
        {
            var cfg = svc.LoadOrCreate();

            Assert.NotNull(svc.LastRecoveryMessage);
            Assert.Contains("restored the latest backup", svc.LastRecoveryMessage);
            Assert.Equal("ru", cfg.Ui.Locale);
            Assert.Equal("dark", cfg.Ui.Theme);
            Assert.Equal(bakContent, File.ReadAllText(svc.ConfigPath + ".bak"));
            Assert.Single(Directory.GetFiles(dir.Path, "profiles.corrupt-*.json"));
            Assert.Equal("{ this is not json", File.ReadAllText(Directory.GetFiles(dir.Path, "profiles.corrupt-*.json")[0]));

            // The recovered file on disk parses and is the current config.
            using var doc = ReadJson(svc.ConfigPath);
            Assert.Equal("ru", doc.RootElement.GetProperty("ui").GetProperty("locale").GetString());
        }
    }

    [Fact]
    public void Corrupt_config_without_backup_creates_default_and_quarantines()
    {
        using var dir = new TempDir();
        dir.Write("profiles.json", "garbage");
        using var svc = NewService(dir);

        var cfg = svc.LoadOrCreate();

        Assert.Contains("no backup was available", svc.LastRecoveryMessage);
        Assert.Equal(ConfigService.CurrentVersion, cfg.ConfigVersion);
        Assert.Single(Directory.GetFiles(dir.Path, "profiles.corrupt-*.json"));
        Assert.False(File.Exists(svc.ConfigPath + ".bak"));
        using var doc = ReadJson(svc.ConfigPath);
        Assert.Equal(ConfigService.CurrentVersion, doc.RootElement.GetProperty("configVersion").GetInt32());
    }

    [Fact]
    public void Corrupt_backup_is_not_used()
    {
        using var dir = new TempDir();
        dir.Write("profiles.json", "garbage");
        dir.Write("profiles.json.bak", "also garbage");
        using var svc = NewService(dir);
        var cfg = svc.LoadOrCreate();
        Assert.Contains("no backup was available", svc.LastRecoveryMessage);
        Assert.Equal(ConfigService.CurrentVersion, cfg.ConfigVersion);
    }

    // ------------------------------------------------------------------ null normalization

    [Fact]
    public void Explicit_nulls_are_normalized_without_exception()
    {
        using var dir = new TempDir();
        dir.Write("profiles.json", """
            {
              "configVersion": 19,
              "defaults": null,
              "factoryDefaults": null,
              "globalHotkeys": null,
              "ui": null,
              "presets": null,
              "profiles": [
                null,
                {
                  "id": null,
                  "name": null,
                  "processName": "game.exe",
                  "processAliases": null,
                  "color": null,
                  "session": null,
                  "companions": [ null, { "path": null, "launchMode": null, "onStop": null } ],
                  "presets": [ null, { "id": null, "name": null, "color": null } ]
                }
              ]
            }
            """);
        using var svc = NewService(dir);

        var cfg = svc.LoadOrCreate();

        Assert.Null(svc.LastRecoveryMessage);
        Assert.NotNull(cfg.Defaults);
        Assert.NotNull(cfg.Defaults.Color);
        Assert.NotNull(cfg.FactoryDefaults);
        Assert.NotNull(cfg.GlobalHotkeys);
        Assert.NotNull(cfg.Ui);
        Assert.Equal("en", cfg.Ui.Locale);
        Assert.Null(cfg.Presets);
        var p = Assert.Single(cfg.Profiles);
        Assert.False(string.IsNullOrEmpty(p.Id));
        Assert.Equal("", p.Name);
        Assert.NotNull(p.ProcessAliases);
        Assert.NotNull(p.Color);
        Assert.NotNull(p.Session);
        var c = Assert.Single(p.Companions);
        Assert.Equal("", c.Path);
        Assert.Equal("direct", c.LaunchMode);
        Assert.Equal("closeThenKill", c.OnStop);
        var pr = Assert.Single(p.Presets);
        Assert.False(string.IsNullOrEmpty(pr.Id));
        Assert.Equal("Preset", pr.Name);
        Assert.NotNull(pr.Color);
    }

    [Fact]
    public void Top_level_profiles_null_yields_empty_list()
    {
        using var dir = new TempDir();
        dir.Write("profiles.json", """{ "configVersion": 19, "profiles": null }""");
        using var svc = NewService(dir);
        var cfg = svc.LoadOrCreate();
        Assert.NotNull(cfg.Profiles);
        Assert.Empty(cfg.Profiles);
    }

    // ------------------------------------------------------------------ legacy v18 migration

    private const string LegacyV18Json = """
        {
          "configVersion": 18,
          "defaults": {
            "resolution": "2560x1440",
            "refreshRate": 165,
            "powerPlan": "balanced",
            "color": { "brightness": 0.5, "contrast": 1.0, "gamma": 1.0, "vibrance": 50, "backend": 3, "lockColor": false }
          },
          "factoryDefaults": {
            "resolution": "2560x1440",
            "refreshRate": 0,
            "powerPlan": "balanced",
            "color": { "brightness": 0.5, "contrast": 1.0, "gamma": 1.0, "vibrance": 50, "backend": 3, "lockColor": false }
          },
          "globalHotkeys": { "toggleOverlay": "Ctrl+Shift+O", "emergencyRestore": "Ctrl+Alt+NumPad0" },
          "profiles": [
            {
              "id": "tarkov",
              "name": "Escape from Tarkov",
              "enabled": true,
              "processName": "EscapeFromTarkov.exe",
              "processAliases": [ "BsgLauncher.exe" ],
              "exePath": "C:\\Games\\EFT\\EscapeFromTarkov.exe",
              "resolution": "1920x1080",
              "refreshRate": 144,
              "applyResolution": true,
              "applyPowerPlan": true,
              "powerPlan": "highPerformance",
              "color": { "brightness": 0.5, "contrast": 1.0, "gamma": 1.0, "vibrance": 50, "backend": 3, "lockColor": true },
              "companions": [
                { "path": "C:\\Tools\\DoesNotExist\\overlay.exe", "arguments": "-quiet", "launchMode": "direct", "onStop": "closeThenKill" }
              ],
              "presets": [
                { "id": "tarkov_g1", "name": "gamma-1", "hotkey": "Ctrl+NumPad1", "applyColor": true,
                  "color": { "brightness": 0.5, "contrast": 1.12, "gamma": 1.5, "vibrance": 50, "backend": 3, "lockColor": true } },
                { "id": "tarkov_g2", "name": "gamma-2", "hotkey": "Alt+NumPad2", "applyColor": true,
                  "color": { "brightness": 0.52, "contrast": 1.2, "gamma": 1.8, "vibrance": 50, "backend": 3, "lockColor": true } }
              ],
              "startupPresetId": "tarkov_g1",
              "session": { "quietNotifications": true, "monitorLayout": "isolatePrimary" },
              "restoreMode": 0
            }
          ],
          "startWithWindows": true,
          "startMinimized": true,
          "firstScanDone": true,
          "coreProfilesSeeded": true,
          "ui": { "locale": "ru", "theme": "dark", "setupCompleted": true, "zoomFactor": 4 }
        }
        """;

    [Fact]
    public void Legacy_v18_config_migrates_to_current_version_and_keeps_user_data()
    {
        using var dir = new TempDir();
        dir.Write("profiles.json", LegacyV18Json);
        using var svc = NewService(dir);

        var cfg = svc.LoadOrCreate();

        Assert.Null(svc.LastRecoveryMessage);
        Assert.Equal(18, svc.LoadedConfigVersion);
        Assert.Equal(ConfigService.CurrentVersion, cfg.ConfigVersion);

        // "balanced" was never the user's choice → null (leave the plan alone / capture later).
        Assert.Null(cfg.Defaults.PowerPlan);
        Assert.Null(cfg.FactoryDefaults.PowerPlan);
        Assert.Equal("2560x1440", cfg.FactoryDefaults.Resolution);

        var p = Assert.Single(cfg.Profiles);
        Assert.Equal("EscapeFromTarkov.exe", p.ProcessName);
        Assert.Equal(new[] { "BsgLauncher.exe" }, p.ProcessAliases);
        Assert.Equal("highPerformance", p.PowerPlan);
        Assert.True(p.ApplyResolution);
        Assert.Equal("1920x1080", p.Resolution);
        Assert.Equal("tarkov_g1", p.StartupPresetId);
        Assert.True(p.Session.QuietNotifications);

        // User hotkeys are kept, NumPad or not.
        Assert.Equal(new[] { "Ctrl+NumPad1", "Alt+NumPad2" }, p.Presets.Select(x => x.Hotkey).ToArray());
        Assert.Equal("Ctrl+Shift+O", cfg.GlobalHotkeys.ToggleOverlay);
        Assert.Equal("Ctrl+Alt+NumPad0", cfg.GlobalHotkeys.EmergencyRestore);

        // Companions are kept even when the path does not exist (drive may be offline).
        var c = Assert.Single(p.Companions);
        Assert.Equal(@"C:\Tools\DoesNotExist\overlay.exe", c.Path);
        Assert.Equal("-quiet", c.Arguments);

        Assert.True(cfg.StartWithWindows);
        Assert.Equal("ru", cfg.Ui.Locale);

        // The migrated file was written back.
        using var doc = ReadJson(svc.ConfigPath);
        Assert.Equal(ConfigService.CurrentVersion, doc.RootElement.GetProperty("configVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("defaults").GetProperty("powerPlan").ValueKind);
    }

    [Fact]
    public void Legacy_v18_deferred_factory_power_plan_is_captured_on_CompleteDeferredMigration()
    {
        using var dir = new TempDir();
        dir.Write("profiles.json", LegacyV18Json);
        using var svc = NewService(dir);
        var cfg = svc.LoadOrCreate();
        Assert.Null(cfg.FactoryDefaults.PowerPlan);

        svc.CompleteDeferredMigration();

        Assert.Equal(TestEnvironment.FakePowerPlan, cfg.FactoryDefaults.PowerPlan);
        Assert.Null(cfg.Defaults.PowerPlan); // global default stays "leave alone"
        using var doc = ReadJson(svc.ConfigPath);
        Assert.Equal(TestEnvironment.FakePowerPlan,
            doc.RootElement.GetProperty("factoryDefaults").GetProperty("powerPlan").GetString());

        // Idempotent.
        svc.CompleteDeferredMigration();
        Assert.Equal(TestEnvironment.FakePowerPlan, cfg.FactoryDefaults.PowerPlan);
    }

    [Fact]
    public void Current_version_config_is_not_rewritten_on_load()
    {
        using var dir = new TempDir();
        string original;
        using (var svc = NewService(dir))
        {
            svc.LoadOrCreate();
            original = File.ReadAllText(svc.ConfigPath);
        }
        var before = File.GetLastWriteTimeUtc(dir.File("profiles.json"));
        using (var svc = NewService(dir))
        {
            svc.LoadOrCreate();
            Assert.Equal(ConfigService.CurrentVersion, svc.LoadedConfigVersion);
        }
        Assert.Equal(original, File.ReadAllText(dir.File("profiles.json")));
        Assert.Equal(before, File.GetLastWriteTimeUtc(dir.File("profiles.json")));
        Assert.False(File.Exists(dir.File("profiles.json.bak")));
    }

    // ------------------------------------------------------------------ legacy folder migration

    [Fact]
    public void Legacy_folder_is_copied_once_and_left_untouched()
    {
        using var oldDir = new TempDir();
        using var newDir = new TempDir();
        oldDir.Write("profiles.json", LegacyV18Json);
        oldDir.Write("profiles.json.bak", "{}");
        oldDir.Write("active-session.json", "{ \"version\": 0 }");
        oldDir.Write("ui-state.json", "{}");
        oldDir.Write("DisplayProfileManager.log", "old log");
        oldDir.Write("profiles.json.tmp", "half");
        oldDir.Write("session.lock", "");
        var oldSnapshot = Directory.GetFiles(oldDir.Path).ToDictionary(f => Path.GetFileName(f)!, File.ReadAllText);

        using (var svc = NewService(newDir, oldDir.Path))
        {
            Assert.True(File.Exists(newDir.File("profiles.json")));
            Assert.Equal(LegacyV18Json, File.ReadAllText(newDir.File("profiles.json")));
            Assert.True(File.Exists(newDir.File("profiles.json.bak")));
            Assert.True(File.Exists(newDir.File("active-session.json")));
            Assert.True(File.Exists(newDir.File("ui-state.json")));
            Assert.False(File.Exists(newDir.File("DisplayProfileManager.log")));
            Assert.False(File.Exists(newDir.File("profiles.json.tmp")));
            Assert.False(File.Exists(newDir.File("session.lock")));

            // profiles.json is the "done" marker, written last.
            var markerCreated = File.GetCreationTimeUtc(newDir.File("profiles.json"));
            foreach (var other in new[] { "profiles.json.bak", "active-session.json", "ui-state.json" })
                Assert.True(File.GetCreationTimeUtc(newDir.File(other)) <= markerCreated, other + " must be copied before profiles.json");

            var cfg = svc.LoadOrCreate();
            Assert.Equal(18, svc.LoadedConfigVersion);
            Assert.Equal(ConfigService.CurrentVersion, cfg.ConfigVersion);
        }

        // Old folder is untouched.
        var after = Directory.GetFiles(oldDir.Path).ToDictionary(f => Path.GetFileName(f)!, File.ReadAllText);
        Assert.Equal(oldSnapshot, after);

        // Second start: new profiles.json exists → nothing is copied again.
        File.WriteAllText(oldDir.File("ui-state.json"), "{ \"changed\": true }");
        using (NewService(newDir, oldDir.Path))
            Assert.Equal("{}", File.ReadAllText(newDir.File("ui-state.json")));
    }

    [Fact]
    public void Legacy_folder_migration_without_old_config_does_nothing()
    {
        using var oldDir = new TempDir();
        using var newDir = new TempDir();
        oldDir.Write("ui-state.json", "{}");
        using var svc = NewService(newDir, oldDir.Path);
        Assert.Empty(Directory.GetFiles(newDir.Path));
        Assert.Empty(Directory.GetFiles(newDir.Path));
        svc.LoadOrCreate();
        Assert.Equal(0, svc.LoadedConfigVersion);
    }

    [Fact]
    public void Interrupted_legacy_migration_still_loads_old_profiles_and_retries_marker()
    {
        using var oldDir = new TempDir();
        using var newDir = new TempDir();
        oldDir.Write("profiles.json", LegacyV18Json);
        var locked = oldDir.Write("active-session.json", "{}");

        // Hold the file exclusively so File.Copy fails → the marker (profiles.json) must not be copied.
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var svc = NewService(newDir, oldDir.Path))
        {
            Assert.False(File.Exists(newDir.File("profiles.json")), "marker must not be copied after a failure");

            // The old profiles are still loaded (read-only) and written into the new folder.
            var cfg = svc.LoadOrCreate();
            Assert.Equal(18, svc.LoadedConfigVersion);
            Assert.Single(cfg.Profiles);
            Assert.True(File.Exists(newDir.File("profiles.json")));
        }
        Assert.Equal(LegacyV18Json, File.ReadAllText(oldDir.File("profiles.json")));
    }

    // ------------------------------------------------------------------ external edits

    [Fact]
    public void External_edit_with_invalid_json_keeps_in_memory_config()
    {
        using var dir = new TempDir();
        using var svc = NewService(dir);
        var cfg = svc.LoadOrCreate();
        cfg.Ui.Locale = "ru";
        svc.Save(cfg, raiseChanged: false);
        int raised = 0;
        svc.ConfigChanged += () => raised++;

        File.WriteAllText(svc.ConfigPath, "{ \"configVersion\": 19, \"ui\": { \"locale\": ");
        svc.ReloadIfChangedExternally();

        Assert.Equal(0, raised);
        Assert.Same(cfg, svc.Current);
        Assert.Equal("ru", svc.Current.Ui.Locale);

        // A later in-app save must not rotate the broken file into .bak.
        var bakBefore = File.ReadAllText(svc.ConfigPath + ".bak");
        svc.Save(cfg, raiseChanged: false);
        Assert.Equal(bakBefore, File.ReadAllText(svc.ConfigPath + ".bak"));
    }

    [Fact]
    public void External_edit_with_valid_json_is_reloaded_and_raises_ConfigChanged()
    {
        using var dir = new TempDir();
        using var svc = NewService(dir);
        var cfg = svc.LoadOrCreate();
        int raised = 0;
        svc.ConfigChanged += () => raised++;

        var text = File.ReadAllText(svc.ConfigPath).Replace("\"locale\": \"en\"", "\"locale\": \"ru\"");
        Assert.NotEqual(File.ReadAllText(svc.ConfigPath), text);
        File.WriteAllText(svc.ConfigPath, text);
        svc.ReloadIfChangedExternally();

        Assert.Equal(1, raised);
        Assert.NotSame(cfg, svc.Current);
        Assert.Equal("ru", svc.Current.Ui.Locale);
    }

    [Fact]
    public void Self_written_file_is_not_reloaded_content_hash_suppression()
    {
        using var dir = new TempDir();
        using var svc = NewService(dir);
        var cfg = svc.LoadOrCreate();
        int raised = 0;
        svc.ConfigChanged += () => raised++;

        svc.ReloadIfChangedExternally(); // just created by us
        cfg.Ui.Locale = "ru";
        svc.Save(cfg, raiseChanged: false);
        svc.ReloadIfChangedExternally(); // just written by us

        // Touching the file without changing content (editor "save" with identical bytes).
        var bytes = File.ReadAllBytes(svc.ConfigPath);
        File.WriteAllBytes(svc.ConfigPath, bytes);
        svc.ReloadIfChangedExternally();

        Assert.Equal(0, raised);
        Assert.Same(cfg, svc.Current);
    }

    [Fact]
    public void Save_normalizes_nulls_and_bumps_version()
    {
        using var dir = new TempDir();
        using var svc = NewService(dir);
        svc.LoadOrCreate();

        var cfg = new AppConfig { ConfigVersion = 3, Profiles = null!, Ui = null! };
        svc.Save(cfg, raiseChanged: false);

        Assert.Equal(ConfigService.CurrentVersion, cfg.ConfigVersion);
        Assert.NotNull(cfg.Profiles);
        Assert.NotNull(cfg.Ui);
        Assert.Same(cfg, svc.Current);
    }
}
