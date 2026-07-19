using System.IO;
using System.Text.Json;
using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

public sealed class ConfigService : IDisposable
{
    public const int CurrentVersion = 7;

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

    public void Save(AppConfig config)
    {
        lock (_gate)
        {
            config.ConfigVersion = Math.Max(config.ConfigVersion, CurrentVersion);
            _config = config;
            SaveInternal(config);
        }
        ConfigChanged?.Invoke();
    }

    public void ReloadFromDisk()
    {
        lock (_gate)
        {
            if (!File.Exists(ConfigPath)) return;
            var json = File.ReadAllText(ConfigPath);
            _config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? _config;
        }
        ConfigChanged?.Invoke();
    }

    private void SaveInternal(AppConfig config)
    {
        _ignoreWatcherUntil = DateTime.UtcNow.AddMilliseconds(800);
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
        File.Copy(tmp, ConfigPath, true);
        File.Delete(tmp);
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
        try
        {
            Thread.Sleep(100);
            ReloadFromDisk();
            AppLog.Info("Config reloaded from disk.");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Config reload failed: {ex.Message}");
        }
    }

    private static bool MigrateIfNeeded(AppConfig cfg)
    {
        bool changed = false;

        bool looksLikeSeedList = cfg.Profiles.Count >= 8 &&
            cfg.Profiles.Any(p => p.ProcessName.Contains("VALORANT", StringComparison.OrdinalIgnoreCase)) &&
            cfg.Profiles.Any(p => p.ProcessName.Contains("Minecraft", StringComparison.OrdinalIgnoreCase));

        if (cfg.ConfigVersion < 2 || looksLikeSeedList || cfg.Presets == null || cfg.Presets.Count == 0)
        {
            if (looksLikeSeedList || cfg.ConfigVersion < 2)
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
            // One-time seed only during v2 migration
            EnsureCoreProfiles(cfg);
            cfg.CoreProfilesSeeded = true;
            changed = true;
            AppLog.Info("Migrated config to v2.");
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

        cfg.Ui ??= new UiPreferences();

        foreach (var profile in cfg.Profiles)
        {
            profile.Presets ??= new List<QuickPreset>();
            if (profile.Presets.Count == 0)
            {
                profile.Presets = ClonePresetsForProfile(CreateDefaultPresets(), profile.Id);
                changed = true;
            }
        }

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
        return source.Select(p => new QuickPreset
        {
            Id = profileId + "_" + p.Id,
            Name = p.Name,
            Hotkey = p.Hotkey,
            ApplyResolution = p.ApplyResolution,
            Resolution = p.Resolution,
            ApplyColor = p.ApplyColor,
            Color = p.Color.Clone()
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
                p.Presets = ClonePresetsForProfile(CreateDefaultPresets(), p.Id);
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

    private void TryRestrictDirectoryAcl()
    {
        // profiles.json lives under the current user's AppData (not world-readable).
        // Avoid System.IO.FileSystem.AccessControl dependency; no network export of config.
    }

    private static void TryRestrictFileAcl(string path) { }

    public void Dispose()
    {
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
    }
}
