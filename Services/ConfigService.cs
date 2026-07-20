using System.IO;
using System.Text.Json;
using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

public sealed class ConfigService : IDisposable
{
    public const int CurrentVersion = 13;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public string ConfigDirectory { get; }
    public string ConfigPath { get; }
    public string LogPath { get; }

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
            if (!File.Exists(ConfigPath))
            {
                _config = CreateDefaultConfig();
                SaveInternal(_config);
            }
            else
            {
                var json = File.ReadAllText(ConfigPath);
                _config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? CreateDefaultConfig();
                if (MigrateIfNeeded(_config))
                    SaveInternal(_config);
            }

            EnsureWatcher();
            return _config;
        }
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
            var json = File.ReadAllText(ConfigPath);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? _config;
            if (MigrateIfNeeded(loaded))
                SaveInternal(loaded);
            _config = loaded;
        }
        ConfigChanged?.Invoke();
    }

    private void SaveInternal(AppConfig config)
    {
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
            cfg.GlobalHotkeys.ResetColor ??= "Ctrl+Alt+NumPad9";
            if (string.Equals(cfg.GlobalHotkeys.ResetColor, "NumPad9", StringComparison.OrdinalIgnoreCase))
                cfg.GlobalHotkeys.ResetColor = "Ctrl+Alt+NumPad9";

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
            // Global adjust hotkeys are opt-in (empty by default). Clear old stock bindings.
            cfg.GlobalHotkeys ??= new GlobalHotkeys();
            static bool IsStock(string? g, string stock) =>
                string.Equals(g?.Trim(), stock, StringComparison.OrdinalIgnoreCase);
            var hk = cfg.GlobalHotkeys;
            if (IsStock(hk.BrightnessUp, "Ctrl+Alt+Up")) hk.BrightnessUp = null;
            if (IsStock(hk.BrightnessDown, "Ctrl+Alt+Down")) hk.BrightnessDown = null;
            if (IsStock(hk.ContrastUp, "Ctrl+Alt+Right")) hk.ContrastUp = null;
            if (IsStock(hk.ContrastDown, "Ctrl+Alt+Left")) hk.ContrastDown = null;
            if (IsStock(hk.GammaUp, "Ctrl+Alt+Add")) hk.GammaUp = null;
            if (IsStock(hk.GammaDown, "Ctrl+Alt+Subtract")) hk.GammaDown = null;
            if (IsStock(hk.ResetColor, "Ctrl+Alt+NumPad9") || IsStock(hk.ResetColor, "NumPad9"))
                hk.ResetColor = null;
            cfg.ConfigVersion = 7;
            changed = true;
            AppLog.Info("Migrated to v7 (empty global adjust hotkeys by default).");
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
            Color = ColorSettings.Neutral,
            ApplyResolution = false
        },
        new()
        {
            Id = "bright",
            Name = "Bright / flat",
            Hotkey = "Ctrl+Alt+NumPad1",
            ApplyColor = true,
            Color = new ColorSettings { Brightness = 0.62, Contrast = 1.05, Gamma = 0.85 },
            ApplyResolution = false
        },
        new()
        {
            Id = "dark",
            Name = "Dark / punchy",
            Hotkey = "Ctrl+Alt+NumPad2",
            ApplyColor = true,
            Color = new ColorSettings { Brightness = 0.42, Contrast = 1.2, Gamma = 1.15 },
            ApplyResolution = false
        },
        new()
        {
            Id = "res1080",
            Name = "1080p",
            Hotkey = "Ctrl+Alt+NumPad3",
            ApplyResolution = true,
            Resolution = "1920x1080",
            ApplyColor = false
        },
        new()
        {
            Id = "res1440",
            Name = "1440p",
            Hotkey = "Ctrl+Alt+NumPad4",
            ApplyResolution = true,
            Resolution = "2560x1440",
            ApplyColor = false
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
