using System.IO;
using System.Text.Json;
using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

public sealed class ConfigService : IDisposable
{
    public const int CurrentVersion = 18;

        private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // OverlayLeft/Top used to default to NaN — never write Infinity/NaN as JSON numbers.
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public string ConfigDirectory { get; }
    public string ConfigPath { get; }
    public string LogPath { get; }

    /// <summary>Set when LoadOrCreate recovered from corrupt JSON / backup.</summary>
    public string? LastRecoveryMessage { get; private set; }

    private readonly object _gate = new();
    private AppConfig _config = new();
    private FileSystemWatcher? _watcher;
    private DateTime _ignoreWatcherUntil = DateTime.MinValue;
    private System.Threading.Timer? _debounceTimer;

    public event Action? ConfigChanged;

    public AppConfig Current
    {
        get { lock (_gate) return _config; }
    }

    public ConfigService()
    {
        ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DisplayProfileManager");
        ConfigPath = Path.Combine(ConfigDirectory, "profiles.json");
        LogPath = Path.Combine(ConfigDirectory, "DisplayProfileManager.log");
        Directory.CreateDirectory(ConfigDirectory);
        TryRestrictDirectoryAcl();
    }

    public AppConfig LoadOrCreate()
    {
        lock (_gate)
        {
            LastRecoveryMessage = null;
            if (!File.Exists(ConfigPath))
            {
                _config = CreateDefaultConfig();
                SaveInternal(_config);
            }
            else
            {
                _config = TryLoadConfigFile(ConfigPath, out var err) ?? TryRecoverCorrupt(err);
                if (MigrateIfNeeded(_config))
                    SaveInternal(_config);
            }

            EnsureWatcher();
            return _config;
        }
    }

    private AppConfig? TryLoadConfigFile(string path, out string? error)
    {
        error = null;
        try
        {
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (cfg == null)
            {
                error = "null config";
                return null;
            }
            return cfg;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLog.Error($"Config load failed ({path}): {ex.Message}");
            return null;
        }
    }

    private AppConfig TryRecoverCorrupt(string? primaryError)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        string corruptName = $"profiles.corrupt-{stamp}.json";
        string corruptPath = Path.Combine(ConfigDirectory, corruptName);

        try
        {
            if (File.Exists(ConfigPath))
                File.Copy(ConfigPath, corruptPath, true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not quarantine corrupt config: " + ex.Message);
        }

        string bak = ConfigPath + ".bak";
        var fromBak = TryLoadConfigFile(bak, out _);
        if (fromBak != null)
        {
            _config = fromBak;
            SaveInternal(_config);
            LastRecoveryMessage =
                $"Your config file was corrupted. DPM restored the latest backup.\n" +
                $"Original file was saved as {corruptName}.";
            AppLog.Info(LastRecoveryMessage);
            return _config;
        }

        _config = CreateDefaultConfig();
        SaveInternal(_config);
        LastRecoveryMessage =
            $"Your config file was corrupted and no backup was available. DPM created a new config.\n" +
            $"Original file was saved as {corruptName}." +
            (string.IsNullOrWhiteSpace(primaryError) ? "" : $"\n({primaryError})");
        AppLog.Info(LastRecoveryMessage);
        return _config;
    }

    public void Save(AppConfig config) => Save(config, raiseChanged: true);

    /// <param name="raiseChanged">
    /// False for in-app Save (avoids ReloadFromConfig wiping selection).
    /// External file watcher still uses ReloadFromDisk → raiseChanged true.
    /// </param>
    public void Save(AppConfig config, bool raiseChanged)
    {
        lock (_gate)
        {
            config.ConfigVersion = Math.Max(config.ConfigVersion, CurrentVersion);
            _config = config;
            SaveInternal(config);
        }
        if (raiseChanged)
            ConfigChanged?.Invoke();
    }

    public void ReloadFromDisk()
    {
        lock (_gate)
        {
            if (!File.Exists(ConfigPath)) return;
            var loaded = TryLoadConfigFile(ConfigPath, out var err);
            if (loaded == null)
            {
                loaded = TryRecoverCorrupt(err);
            }
            if (MigrateIfNeeded(loaded))
                SaveInternal(loaded);
            _config = loaded;
        }
        ConfigChanged?.Invoke();
    }

    private void SaveInternal(AppConfig config)
    {
        SanitizeForJson(config);
        _ignoreWatcherUntil = DateTime.UtcNow.AddMilliseconds(5000);
        if (config.Ui?.BackupOnSave != false && File.Exists(ConfigPath))
        {
            try
            {
                var bak = ConfigPath + ".bak";
                File.Copy(ConfigPath, bak, true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Config backup failed: " + ex.Message);
            }
        }

        var json = JsonSerializer.Serialize(config, JsonOptions);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        try
        {
            File.Replace(tmp, ConfigPath, null);
        }
        catch
        {
            File.Copy(tmp, ConfigPath, true);
            try { File.Delete(tmp); } catch { }
        }
        TryRestrictFileAcl(ConfigPath);
    }

    /// <summary>JSON cannot encode NaN/Infinity as bare numbers — clear them before serialize.</summary>
    private static void SanitizeForJson(AppConfig config)
    {
        var ui = config.Ui;
        if (ui == null) return;
        if (ui.OverlayLeft is double ol && (double.IsNaN(ol) || double.IsInfinity(ol)))
            ui.OverlayLeft = null;
        if (ui.OverlayTop is double ot && (double.IsNaN(ot) || double.IsInfinity(ot)))
            ui.OverlayTop = null;
        if (double.IsNaN(ui.OverlayPanelOpacity) || double.IsInfinity(ui.OverlayPanelOpacity))
            ui.OverlayPanelOpacity = 0.92;
    }

    private void EnsureWatcher()
    {
        if (_watcher != null) return;
        _watcher = new FileSystemWatcher(ConfigDirectory, "profiles.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
        };
        _watcher.Changed += (_, _) => OnFileChanged();
        _watcher.Created += (_, _) => OnFileChanged();
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileChanged()
    {
        if (DateTime.UtcNow < _ignoreWatcherUntil) return;
        lock (_gate)
        {
            _debounceTimer ??= new System.Threading.Timer(_ =>
            {
                try
                {
                    if (DateTime.UtcNow < _ignoreWatcherUntil) return;
                    ReloadFromDisk();
                    AppLog.Info("Config reloaded from disk (debounced).");
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Config reload failed: {ex.Message}");
                }
            });
            _debounceTimer.Change(500, Timeout.Infinite);
        }
    }

    private static bool MigrateIfNeeded(AppConfig cfg)
    {
        bool changed = false;

        bool looksLikeSeedList = cfg.Profiles.Count >= 8 &&
            cfg.Profiles.Any(p => p.ProcessName.Contains("VALORANT", StringComparison.OrdinalIgnoreCase)) &&
            cfg.Profiles.Any(p => p.ProcessName.Contains("Minecraft", StringComparison.OrdinalIgnoreCase));

        // Only for pre-v2 configs. Do NOT key off empty legacy Presets — after v3 that list is always null.
        if (cfg.ConfigVersion < 2)
        {
            if (looksLikeSeedList)
            {
                var keep = cfg.Profiles
                    .Where(p => p.Companions.Count > 0 || ProcessWatcher.IsProcessRunning(p.ProcessName))
                    .ToList();
                cfg.Profiles = keep;
                AppLog.Info($"Cleared unused seed profiles, kept {keep.Count}.");
            }

            if (cfg.Presets == null || cfg.Presets.Count == 0)
            {
                cfg.Presets = CreateDefaultPresets();
                AppLog.Info("Installed default presets.");
            }

            cfg.FactoryDefaults ??= CaptureFactoryDefaults();
            if (string.IsNullOrWhiteSpace(cfg.FactoryDefaults.Resolution))
                cfg.FactoryDefaults = CaptureFactoryDefaults();

            cfg.FactoryDefaults.Color = ColorSettings.Neutral;
            if (cfg.Defaults.Color == null || cfg.Defaults.Color.Brightness < 0.05 || cfg.Defaults.Color.Brightness > 0.95)
                cfg.Defaults.Color = ColorSettings.Neutral;

            cfg.ConfigVersion = 2;
            cfg.FirstScanDone = false;
            EnsureCoreProfiles(cfg);
            cfg.CoreProfilesSeeded = true;
            changed = true;
            AppLog.Info("Migrated config to v2.");
        }
        else if (looksLikeSeedList && !cfg.CoreProfilesSeeded)
        {
            // One-time cleanup for installs that never got CoreProfilesSeeded
            var keep = cfg.Profiles
                .Where(p => p.Companions.Count > 0 || ProcessWatcher.IsProcessRunning(p.ProcessName))
                .ToList();
            if (keep.Count < cfg.Profiles.Count)
            {
                cfg.Profiles = keep;
                changed = true;
                AppLog.Info($"Cleared unused seed profiles, kept {keep.Count}.");
            }
            cfg.CoreProfilesSeeded = true;
            changed = true;
        }

        if (cfg.ConfigVersion < 3)
        {
            var legacy = cfg.Presets != null && cfg.Presets.Count > 0
                ? cfg.Presets
                : CreateDefaultPresets();

            foreach (var profile in cfg.Profiles)
            {
                profile.Presets ??= new List<QuickPreset>();
                if (profile.Presets.Count == 0)
                    profile.Presets = ClonePresetsForProfile(legacy, profile.Id);
            }

            cfg.Presets = null;
            cfg.ConfigVersion = 3;
            changed = true;
            AppLog.Info("Migrated presets to per-game (v3).");
        }

        if (cfg.ConfigVersion < 4)
        {
            cfg.StartMinimized = false;
            cfg.ConfigVersion = 4;
            changed = true;
            AppLog.Info("Migrated to v4 (open UI on launch).");
        }

        if (cfg.ConfigVersion < 5)
        {
            // Stop re-adding deleted core games; mark seeded without forcing profiles back.
            cfg.CoreProfilesSeeded = true;

            foreach (var p in cfg.Profiles)
            {
                foreach (var c in p.Companions)
                {
                    if (string.Equals(c.OnStop, "hotkeyThenKill", StringComparison.OrdinalIgnoreCase))
                        c.OnStop = "closeThenKill";
                    if (string.Equals(c.StopHotkey, "NumPad9", StringComparison.OrdinalIgnoreCase))
                        c.StopHotkey = null;
                }
            }

            cfg.ConfigVersion = 5;
            changed = true;
            AppLog.Info("Migrated to v5 (security / anti-cheat hardening).");
        }

        if (cfg.ConfigVersion < 6)
        {
            cfg.Ui ??= new UiPreferences();
            // Existing installs: skip wizard, keep defaults
            cfg.Ui.SetupCompleted = true;
            cfg.ConfigVersion = 6;
            changed = true;
            AppLog.Info("Migrated to v6 (UI preferences).");
        }

        if (cfg.ConfigVersion < 7)
        {
            // Legacy global adjust hotkeys removed from the model — keep Toggle/Emergency only.
            cfg.GlobalHotkeys ??= new GlobalHotkeys();
            cfg.ConfigVersion = 7;
            changed = true;
            AppLog.Info("Migrated to v7 (global adjust hotkeys retired).");
        }

        if (cfg.ConfigVersion < 8)
        {
            // New ColorSettings fields (vibrance / driver / lock / shadowLift) use model defaults when missing.
            foreach (var p in cfg.Profiles)
                p.Color?.Clamp();
            cfg.Defaults?.Color?.Clamp();
            cfg.ConfigVersion = 8;
            changed = true;
            AppLog.Info("Migrated to v8 (GPU driver color / vibrance / lock).");
        }

        if (cfg.ConfigVersion < 9)
        {
            foreach (var p in cfg.Profiles)
            {
                p.Color ??= ColorSettings.Neutral;
                // Prefer Low Level when coming from old UseDriverColor-less / GDI-only configs
                if (p.Color.Backend == ColorBackend.Gdi)
                    p.Color.Backend = ColorBackend.LowLevel;
                p.Color.Clamp();

                if (string.Equals(p.ProcessName, "EscapeFromTarkov.exe", StringComparison.OrdinalIgnoreCase))
                {
                    p.ApplyColor = true;
                    p.Color = ColorSettings.FromRivaTuner(0, 10, 1.5);
                    var riva = GameCatalog.CreateTarkovRivaPresets();
                    // Merge: keep user presets, add missing Riva ones by name
                    p.Presets ??= new List<QuickPreset>();
                    foreach (var rp in riva)
                    {
                        if (p.Presets.Any(x => string.Equals(x.Name, rp.Name, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        rp.Id = p.Id + "_" + rp.Id;
                        p.Presets.Add(rp);
                    }
                }
            }

            cfg.Defaults ??= new DefaultSettings();
            cfg.Defaults.Color ??= ColorSettings.Neutral;
            if (cfg.Defaults.Color.Backend == ColorBackend.Gdi)
                cfg.Defaults.Color.Backend = ColorBackend.LowLevel;

            cfg.ConfigVersion = 9;
            changed = true;
            AppLog.Info("Migrated to v9 (color backend + Tarkov Riva presets).");
        }

        if (cfg.ConfigVersion < 10)
        {
            foreach (var p in cfg.Profiles)
                p.Session ??= new SessionExtras();
            cfg.ConfigVersion = 10;
            changed = true;
            AppLog.Info("Migrated to v10 (session extras).");
        }

        if (cfg.ConfigVersion < 11)
        {
            cfg.Defaults ??= new DefaultSettings();
            cfg.Defaults.EnsureDualColorSlots();
            foreach (var p in cfg.Profiles)
            {
                p.EnsureDualColorSlots();
                foreach (var pr in p.Presets ?? new List<QuickPreset>())
                    pr.EnsureDualColorSlots();
            }
            cfg.ConfigVersion = 11;
            changed = true;
            AppLog.Info("Migrated to v11 (per-backend color slots + NVIDIA CP ramp).");
        }

        if (cfg.ConfigVersion < 12)
        {
            cfg.Defaults ??= new DefaultSettings();
            cfg.Defaults.EnsureDualColorSlots();
            cfg.FactoryDefaults ??= CaptureFactoryDefaults();
            cfg.FactoryDefaults.EnsureDualColorSlots();
            foreach (var p in cfg.Profiles)
            {
                p.EnsureDualColorSlots();
                p.Presets ??= new List<QuickPreset>();
                foreach (var pr in p.Presets)
                    pr.EnsureDualColorSlots();
            }
            cfg.ConfigVersion = 12;
            changed = true;
            AppLog.Info("Migrated to v12 (FactoryDefaults dual slots, no empty-preset reseed).");
        }

        if (cfg.ConfigVersion < 13)
        {
            // Bare NumPad0–9 hotkeys steal keys globally and switch Tarkov presets
            // even when the Presets tab looks empty (wrong game selected). Require modifiers.
            int cleared = 0;
            foreach (var p in cfg.Profiles)
            {
                foreach (var pr in p.Presets ?? new List<QuickPreset>())
                {
                    if (IsBareNumPadHotkey(pr.Hotkey))
                    {
                        AppLog.Info($"Cleared unsafe preset hotkey '{pr.Hotkey}' on {p.Name}/{pr.Name}");
                        pr.Hotkey = null;
                        cleared++;
                    }
                }
            }
            cfg.ConfigVersion = 13;
            changed = true;
            AppLog.Info($"Migrated to v13 (cleared {cleared} bare NumPad preset hotkeys).");
        }

        if (cfg.ConfigVersion < 14)
        {
            foreach (var p in cfg.Profiles)
            {
                // Default RestoreMode already PreviousSnapshot for new fields via enum default.
                p.Session ??= new SessionExtras();
            }
            cfg.GlobalHotkeys ??= new GlobalHotkeys();
            cfg.ConfigVersion = 14;
            changed = true;
            AppLog.Info("Migrated to v14 (RestoreMode + extended global hotkeys).");
        }

        if (cfg.ConfigVersion < 15)
        {
            // Color is presets-only — clear misleading profile-level ApplyColor / seed color.
            foreach (var p in cfg.Profiles)
            {
                p.ApplyColor = false;
                // Keep dual slots intact; Neutral baseline is unused by ApplyProfile.
                if (p.Presets == null || p.Presets.Count == 0)
                    p.Color = ColorSettings.Neutral;
            }
            cfg.GlobalHotkeys ??= new GlobalHotkeys();
            cfg.ConfigVersion = 15;
            changed = true;
            AppLog.Info("Migrated to v15 (presets-only color; trim dead global adjust hotkeys).");
        }

        if (cfg.ConfigVersion < 16)
        {
            foreach (var p in cfg.Profiles)
            {
                p.ProcessAliases ??= new List<string>();
                p.Session ??= new SessionExtras();
                // Sync IsolatePrimaryMonitor ← MonitorLayout when layout is set.
                var layout = p.Session.MonitorLayout?.Trim();
                if (!string.IsNullOrWhiteSpace(layout))
                {
                    if (string.Equals(layout, "keepAll", StringComparison.OrdinalIgnoreCase))
                        p.Session.IsolatePrimaryMonitor = false;
                    else if (string.Equals(layout, "isolatePrimary", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(layout, "primaryOnly", StringComparison.OrdinalIgnoreCase))
                        p.Session.IsolatePrimaryMonitor = true;
                }
            }
            cfg.GlobalHotkeys ??= new GlobalHotkeys();
            cfg.ConfigVersion = 16;
            changed = true;
            AppLog.Info("Migrated to v16 (ProcessAliases + MonitorLayout sync).");
        }

        if (cfg.ConfigVersion < 17)
        {
            // Hue / FPS limit / SoftRestore foundations — new fields (ColorSettings.Hue,
            // GameProfile.FpsLimit / SoftRestoreOnAltTab, QuickPreset.FpsLimit,
            // UiPreferences.MuteSoundsInGame) all default safely via the model; no
            // existing data needs to move. Just bump the version so future migrations
            // can rely on their presence.
            cfg.ConfigVersion = 17;
            changed = true;
            AppLog.Info("Migrated to v17 (Hue / FPS limit / SoftRestore foundations).");
        }

        if (cfg.ConfigVersion < 18)
        {
            cfg.Ui ??= new UiPreferences();
            if (cfg.Ui.ZoomFactor < 2 || cfg.Ui.ZoomFactor > 12)
                cfg.Ui.ZoomFactor = 4;
            cfg.GlobalHotkeys ??= new GlobalHotkeys();
            cfg.ConfigVersion = 18;
            changed = true;
            AppLog.Info("Migrated to v18 (center-screen Magnification zoom).");
        }

        cfg.Ui ??= new UiPreferences();

        foreach (var profile in cfg.Profiles)
            profile.Presets ??= new List<QuickPreset>();

        // Strip clearly dangerous companion entries (keep missing .exe paths — user may reinstall)
        foreach (var profile in cfg.Profiles)
        {
            int before = profile.Companions.Count;
            profile.Companions = profile.Companions
                .Where(c =>
                {
                    if (string.Equals(c.LaunchMode, "scheduledTask", StringComparison.OrdinalIgnoreCase))
                        return PathSecurity.IsSafeScheduledTaskName(c.TaskName);
                    return PathSecurity.IsPlausibleExecutablePath(c.Path);
                })
                .ToList();
            if (profile.Companions.Count != before) changed = true;
        }

        cfg.FactoryDefaults ??= CaptureFactoryDefaults();
        return changed;
    }

    private static List<QuickPreset> ClonePresetsForProfile(IEnumerable<QuickPreset> source, string profileId)
    {
        return source.Select(p =>
        {
            var c = QuickPreset.CloneOf(p);
            c.Id = profileId + "_" + p.Id;
            return c;
        }).ToList();
    }

    public static DefaultSettings CaptureFactoryDefaults()
    {
        return new DefaultSettings
        {
            Resolution = DisplayEngine.GetCurrentResolution(),
            PowerPlan = "balanced",
            Color = ColorSettings.Neutral
        };
    }

    public static AppConfig CreateDefaultConfig()
    {
        var factory = CaptureFactoryDefaults();
        return new AppConfig
        {
            ConfigVersion = CurrentVersion,
            CoreProfilesSeeded = true,
            Defaults = factory.Clone(),
            FactoryDefaults = factory.Clone(),
            GlobalHotkeys = new GlobalHotkeys(),
            StartWithWindows = true,
            StartMinimized = false,
            FirstScanDone = false,
            Ui = new UiPreferences { SetupCompleted = false },
            Profiles = GameCatalog.CreateCoreProfiles().Select(p =>
            {
                if (p.Presets == null || p.Presets.Count == 0)
                    p.Presets = ClonePresetsForProfile(CreateDefaultPresets(), p.Id);
                else
                    p.Presets = ClonePresetsForProfile(p.Presets, p.Id);
                return p;
            }).ToList(),
            Presets = null
        };
    }

    /// <summary>Add missing core games — only from UI "Restore my games", not every launch.</summary>
    public static void EnsureCoreProfiles(AppConfig cfg)
    {
        foreach (var core in GameCatalog.CreateCoreProfiles())
        {
            if (cfg.Profiles.Any(p => string.Equals(p.ProcessName, core.ProcessName, StringComparison.OrdinalIgnoreCase)))
                continue;
            cfg.Profiles.Add(core);
        }
        cfg.CoreProfilesSeeded = true;
    }

    public static List<QuickPreset> CreateDefaultPresets() => new()
    {
        new()
        {
            Id = "neutral",
            Name = "Neutral",
            Hotkey = "Ctrl+Alt+NumPad0",
            ApplyColor = true,
            Color = new ColorSettings
            {
                Brightness = 0.5, Contrast = 1.0, Gamma = 1.0, Vibrance = 50,
                Backend = ColorBackend.LowLevel, LockColor = true
            },
            ApplyResolution = false
        },
        new()
        {
            Id = "bright",
            Name = "Bright / flat",
            Hotkey = "Ctrl+Alt+NumPad1",
            ApplyColor = true,
            Color = new ColorSettings { Brightness = 0.62, Contrast = 1.05, Gamma = 0.85, LockColor = true },
            ApplyResolution = false
        },
        new()
        {
            Id = "dark",
            Name = "Dark / punchy",
            Hotkey = "Ctrl+Alt+NumPad2",
            ApplyColor = true,
            Color = new ColorSettings { Brightness = 0.42, Contrast = 1.2, Gamma = 1.15, LockColor = true },
            ApplyResolution = false
        },
        new()
        {
            Id = "res1080",
            Name = "1080p",
            Hotkey = "Ctrl+Alt+NumPad3",
            ApplyResolution = true,
            Resolution = "1920x1080",
            ApplyColor = false,
            Color = ColorSettings.Neutral
        },
        new()
        {
            Id = "res1440",
            Name = "1440p",
            Hotkey = "Ctrl+Alt+NumPad4",
            ApplyResolution = true,
            Resolution = "2560x1440",
            ApplyColor = false,
            Color = ColorSettings.Neutral
        },
    };

    private static bool IsBareNumPadHotkey(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return false;
        var t = hotkey.Trim();
        if (t.Contains('+')) return false;
        return t.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase)
               || (t.Length == 1 && char.IsDigit(t[0]));
    }

    private void TryRestrictDirectoryAcl()
    {
        // profiles.json lives under the current user's AppData (not world-readable).
        // Avoid System.IO.FileSystem.AccessControl dependency; no network export of config.
    }

    private static void TryRestrictFileAcl(string path) { }

    public void Dispose()
    {
        try { _debounceTimer?.Dispose(); } catch { }
        _debounceTimer = null;
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
    }
}
