using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Visor.Models;

namespace Visor.Services;

public sealed class ConfigService : IDisposable
{
    public const int CurrentVersion = 19;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // OverlayLeft/Top used to default to NaN — never write Infinity/NaN as JSON numbers.
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    /// <summary>%AppData%\Visor — single source for config, session snapshot, lock and log.</summary>
    public static string DefaultConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Visor");

    /// <summary>Pre-rename folder (Display Profile Manager). Read once for migration, never modified.</summary>
    public static string LegacyConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DisplayProfileManager");

    public const string LogFileName = "Visor.log";

    public string ConfigDirectory { get; }
    public string ConfigPath { get; }
    public string LogPath { get; }

    /// <summary>Folder read (never modified) for the one-time legacy migration; null = none.</summary>
    private readonly string? _legacyConfigDirectory;

    /// <summary>
    /// Test hook: supplies the "factory" baseline instead of reading the live display / power state.
    /// Null (the app) = capture from the system.
    /// </summary>
    internal static Func<DefaultSettings>? FactoryDefaultsProvider { get; set; }

    /// <summary>False disables the profiles.json FileSystemWatcher (tests call the reload directly).</summary>
    internal bool EnableFileWatcher { get; set; } = true;

    /// <summary>Set when LoadOrCreate recovered from corrupt JSON / backup.</summary>
    public string? LastRecoveryMessage { get; private set; }

    /// <summary>ConfigVersion found on disk before migration (0 for a brand-new config).</summary>
    public int LoadedConfigVersion { get; private set; }

    private readonly object _gate = new();
    private AppConfig _config = new();
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounceTimer;
    /// <summary>SHA-256 of the profiles.json bytes we last wrote or successfully loaded.</summary>
    private byte[]? _knownDiskHash;
    /// <summary>Hash of an externally edited profiles.json that did not parse (warned once, never trusted).</summary>
    private byte[]? _lastBrokenHash;
    /// <summary>Legacy profiles.json to load (read-only) when the folder migration was incomplete.</summary>
    private string? _legacyConfigToImport;
    /// <summary>v19 migration: the factory power plan is captured after a leftover session is restored.</summary>
    private bool _factoryPowerPlanPending;

    public event Action? ConfigChanged;

    public AppConfig Current
    {
        get { lock (_gate) return _config; }
    }

    public ConfigService() : this(DefaultConfigDirectory, LegacyConfigDirectory)
    {
    }

    /// <param name="configDirectory">Folder for profiles.json, backups and the log.</param>
    /// <param name="legacyConfigDirectory">Pre-rename folder to migrate from once (null = skip migration).</param>
    public ConfigService(string configDirectory, string? legacyConfigDirectory = null)
    {
        ConfigDirectory = configDirectory;
        _legacyConfigDirectory = legacyConfigDirectory;
        ConfigPath = Path.Combine(ConfigDirectory, "profiles.json");
        LogPath = Path.Combine(ConfigDirectory, LogFileName);
        Directory.CreateDirectory(ConfigDirectory);
        MigrateLegacyFolder();
    }

    /// <summary>
    /// First Visor start after the rename: copy %AppData%\DisplayProfileManager (config, backups,
    /// session snapshot…) into %AppData%\Visor. The old folder is left untouched; logs are not copied.
    /// </summary>
    private void MigrateLegacyFolder()
    {
        try
        {
            if (File.Exists(ConfigPath)) return;
            if (string.IsNullOrEmpty(_legacyConfigDirectory)) return;
            var oldConfig = Path.Combine(_legacyConfigDirectory, "profiles.json");
            if (!File.Exists(oldConfig)) return;

            // Every file on its own; profiles.json goes LAST — it is the "migration done" marker,
            // so an interrupted copy is retried on the next start.
            int copied = 0, failed = 0;
            foreach (var file in Directory.EnumerateFiles(_legacyConfigDirectory)
                         .OrderBy(f => string.Equals(Path.GetFileName(f), "profiles.json", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "session.lock", StringComparison.OrdinalIgnoreCase))
                    continue;
                bool isMarker = string.Equals(name, "profiles.json", StringComparison.OrdinalIgnoreCase);
                if (isMarker && failed > 0)
                {
                    AppLog.Warn($"Legacy config migration: {failed} file(s) failed — copying profiles.json is retried on the next start.");
                    _legacyConfigToImport = file;
                    return;
                }
                try
                {
                    var dest = Path.Combine(ConfigDirectory, name);
                    File.Copy(file, dest, overwrite: !isMarker);
                    copied++;
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Error($"Legacy config migration: could not copy {name}: {ex.Message}");
                }
            }
            AppLog.Info($"Migrated {copied} file(s) from the Display Profile Manager config folder.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Legacy config folder migration failed: " + ex.Message);
        }
    }

    public AppConfig LoadOrCreate()
    {
        lock (_gate)
        {
            LastRecoveryMessage = null;
            var legacy = _legacyConfigToImport != null && !File.Exists(ConfigPath)
                ? TryLoadConfigFile(_legacyConfigToImport, out _, out _)
                : null;
            if (legacy != null)
            {
                // Folder migration was incomplete — keep the user's profiles (old file read, not moved).
                LoadedConfigVersion = legacy.ConfigVersion;
                _config = legacy;
                MigrateIfNeeded(_config);
                SaveInternal(_config);
            }
            else if (!File.Exists(ConfigPath))
            {
                LoadedConfigVersion = 0;
                _config = CreateDefaultConfig();
                SaveInternal(_config);
            }
            else
            {
                var loaded = TryLoadConfigFile(ConfigPath, out var err, out var hash);
                if (loaded != null)
                {
                    _knownDiskHash = hash;
                    LoadedConfigVersion = loaded.ConfigVersion;
                    _config = loaded;
                }
                else
                {
                    _config = TryRecoverCorrupt(err);
                    LoadedConfigVersion = _config.ConfigVersion;
                }

                if (MigrateIfNeeded(_config))
                    SaveInternal(_config);
            }
            _factoryPowerPlanPending = _config.FactoryDefaults?.PowerPlan == null && LoadedConfigVersion is > 0 and < 19;

            EnsureWatcher();
            return _config;
        }
    }

    /// <summary>
    /// Finishes the v19 migration once a leftover game session (crash) has been restored:
    /// the factory power plan is captured from the now-real desktop state.
    /// </summary>
    public void CompleteDeferredMigration()
    {
        lock (_gate)
        {
            if (!_factoryPowerPlanPending) return;
            _factoryPowerPlanPending = false;
            _config.FactoryDefaults ??= CaptureFactoryDefaults();
            _config.FactoryDefaults.PowerPlan ??= CaptureActivePowerPlan();
            SaveInternal(_config);
            AppLog.Info($"Factory power plan captured: {_config.FactoryDefaults.PowerPlan ?? "(unknown)"}.");
        }
    }

    private static AppConfig? TryLoadConfigFile(string path, out string? error, out byte[]? hash)
    {
        error = null;
        hash = null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            hash = SHA256.HashData(bytes);
            return ParseConfig(bytes, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLog.Error($"Config load failed ({Path.GetFileName(path)}): {ex.Message}");
            return null;
        }
    }

    private static AppConfig? ParseConfig(byte[] bytes, out string? error)
    {
        error = null;
        try
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(bytes, JsonOptions);
            if (cfg == null)
            {
                error = "null config";
                return null;
            }
            NormalizeLoaded(cfg);
            return cfg;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// JSON can carry explicit nulls ("profiles": null, "companions": null, null list items…).
    /// Replace them right after deserialization so nothing downstream has to guard.
    /// </summary>
    private static void NormalizeLoaded(AppConfig cfg)
    {
        cfg.Defaults ??= new DefaultSettings();
        cfg.Defaults.Color ??= ColorSettings.Neutral;
        if (cfg.FactoryDefaults != null)
            cfg.FactoryDefaults.Color ??= ColorSettings.Neutral;
        cfg.GlobalHotkeys ??= new GlobalHotkeys();
        cfg.Ui ??= new UiPreferences();
        cfg.Ui.Locale ??= "en";
        cfg.Ui.Theme ??= "dark";
        cfg.Ui.CustomAccent ??= "#22D3EE";
        cfg.Ui.CustomBackground ??= "#0A0C10";
        if (cfg.Ui.CustomPalette != null)
        {
            bool darkBg = ThemeService.IsDark(ThemeService.Hex(
                string.IsNullOrWhiteSpace(cfg.Ui.CustomPalette.Bg) ? cfg.Ui.CustomBackground : cfg.Ui.CustomPalette.Bg,
                ThemeService.Hex(ThemeService.BrandBackground)));
            // Fields absent from the file (older custom themes) get the status colors that read
            // on this background — the getters alone would always answer with the dark defaults.
            if (!cfg.Ui.CustomPalette.HasExplicitSuccess)
                cfg.Ui.CustomPalette.Success = darkBg ? ThemePalette.DefaultSuccess : "#15803D";
            if (!cfg.Ui.CustomPalette.HasExplicitWarning)
                cfg.Ui.CustomPalette.Warning = darkBg ? ThemePalette.DefaultWarning : "#B45309";
        }
        cfg.Presets?.RemoveAll(p => p == null);
        cfg.Presets?.ForEach(NormalizePreset);

        cfg.Profiles ??= new List<GameProfile>();
        cfg.Profiles.RemoveAll(p => p == null);
        foreach (var p in cfg.Profiles)
        {
            p.Id ??= Guid.NewGuid().ToString("N");
            p.Name ??= "";
            p.ProcessName ??= "";
            p.ProcessAliases ??= new List<string>();
            p.ProcessAliases.RemoveAll(string.IsNullOrWhiteSpace);
            p.Color ??= ColorSettings.Neutral;
            p.Session ??= new SessionExtras();
            p.Companions ??= new List<CompanionApp>();
            p.Companions.RemoveAll(c => c == null);
            foreach (var c in p.Companions)
            {
                c.Path ??= "";
                c.LaunchMode ??= "direct";
                c.OnStop ??= "closeThenKill";
            }
            p.Presets ??= new List<QuickPreset>();
            p.Presets.RemoveAll(x => x == null);
            p.Presets.ForEach(NormalizePreset);
        }
    }

    private static void NormalizePreset(QuickPreset p)
    {
        p.Id ??= Guid.NewGuid().ToString("N");
        p.Name ??= "Preset";
        p.Color ??= ColorSettings.Neutral;
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

        // The corrupt file is not parseable, so SaveInternal will not rotate it into .bak —
        // the good backup survives this recovery.
        string bak = ConfigPath + ".bak";
        var fromBak = File.Exists(bak) ? TryLoadConfigFile(bak, out _, out _) : null;
        if (fromBak != null)
        {
            _config = fromBak;
            SaveInternal(_config);
            LastRecoveryMessage =
                $"Your config file was corrupted. Visor restored the latest backup.\n" +
                $"Original file was saved as {corruptName}.";
            AppLog.Info(LastRecoveryMessage);
            return _config;
        }

        _config = CreateDefaultConfig();
        SaveInternal(_config);
        LastRecoveryMessage =
            $"Your config file was corrupted and no backup was available. Visor created a new config.\n" +
            $"Original file was saved as {corruptName}." +
            (string.IsNullOrWhiteSpace(primaryError) ? "" : $"\n({primaryError})");
        AppLog.Info(LastRecoveryMessage);
        return _config;
    }

    public void Save(AppConfig config) => Save(config, raiseChanged: true);

    /// <param name="raiseChanged">
    /// False for in-app Save (avoids ReloadFromConfig wiping selection).
    /// External edits picked up by the file watcher always raise ConfigChanged.
    /// </param>
    public void Save(AppConfig config, bool raiseChanged)
    {
        lock (_gate)
        {
            NormalizeLoaded(config);
            if (config.ConfigVersion < CurrentVersion)
                config.ConfigVersion = CurrentVersion;
            _config = config;
            SaveInternal(config);
        }
        if (raiseChanged)
            ConfigChanged?.Invoke();
    }

    /// <summary>
    /// Atomic write: serialize to profiles.json.tmp, then File.Replace with the previous file
    /// rotated into .bak — but only when the previous file is a valid config (a corrupt or
    /// half-written file never overwrites a good backup).
    /// </summary>
    private void SaveInternal(AppConfig config)
    {
        SanitizeForJson(config);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(config, JsonOptions);
        var hash = SHA256.HashData(bytes);
        var tmp = ConfigPath + ".tmp";

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        if (!File.Exists(ConfigPath))
        {
            File.Move(tmp, ConfigPath);
            _knownDiskHash = hash;
            return;
        }

        string? backup = null;
        if (config.Ui?.BackupOnSave != false && CurrentFileIsValid())
            backup = ConfigPath + ".bak";

        try
        {
            File.Replace(tmp, ConfigPath, backup, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            AppLog.Warn(ex, "Config File.Replace failed, falling back to copy");
            if (backup != null)
            {
                try { File.Copy(ConfigPath, backup, true); }
                catch (Exception bex) { AppLog.Error("Config backup failed: " + bex.Message); }
            }
            File.Copy(tmp, ConfigPath, true);
            try { File.Delete(tmp); } catch (Exception dex) { AppLog.Warn(dex, "Delete config tmp"); }
        }
        _knownDiskHash = hash;
    }

    /// <summary>True when profiles.json on disk is something we wrote/loaded, or at least parses.</summary>
    private bool CurrentFileIsValid()
    {
        try
        {
            var bytes = File.ReadAllBytes(ConfigPath);
            if (_knownDiskHash != null && SHA256.HashData(bytes).AsSpan().SequenceEqual(_knownDiskHash))
                return true;
            return ParseConfig(bytes, out _) != null;
        }
        catch
        {
            return false;
        }
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
        if (_watcher != null || !EnableFileWatcher) return;
        _watcher = new FileSystemWatcher(ConfigDirectory, "profiles.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName
        };
        _watcher.Changed += (_, _) => OnFileChanged();
        _watcher.Created += (_, _) => OnFileChanged();
        _watcher.Renamed += (_, _) => OnFileChanged();
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileChanged()
    {
        lock (_gate)
        {
            _debounceTimer ??= new System.Threading.Timer(_ =>
            {
                try { ReloadIfChangedExternally(); }
                catch (Exception ex) { AppLog.Error($"Config reload failed: {ex.Message}"); }
            });
            _debounceTimer.Change(500, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Reloads profiles.json when its content differs from what we last wrote/loaded (hash, not a
    /// time window). A file that does not parse (typo while hand-editing) is never replaced —
    /// the in-memory config stays active and a warning is logged.
    /// </summary>
    internal void ReloadIfChangedExternally()
    {
        bool changed;
        lock (_gate)
        {
            if (!File.Exists(ConfigPath)) return;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(ConfigPath); }
            catch (IOException ex)
            {
                // Editor still holds the file — the next change event retries.
                AppLog.Warn(ex, "Config external reload: file busy");
                return;
            }

            var hash = SHA256.HashData(bytes);
            if (_knownDiskHash != null && hash.AsSpan().SequenceEqual(_knownDiskHash))
                return;
            if (_lastBrokenHash != null && hash.AsSpan().SequenceEqual(_lastBrokenHash))
                return; // already warned about this broken revision

            var loaded = ParseConfig(bytes, out var err);
            if (loaded == null)
            {
                // Never trusted as "known good" — CurrentFileIsValid must not rotate it into .bak.
                _lastBrokenHash = hash;
                AppLog.Warn($"profiles.json was edited outside Visor but does not parse ({err}). " +
                            "Keeping the current in-memory config; fix the file to apply your edit.");
                return;
            }

            _knownDiskHash = hash;
            _lastBrokenHash = null;
            if (MigrateIfNeeded(loaded))
                SaveInternal(loaded);
            _config = loaded;
            changed = true;
        }

        if (changed)
        {
            AppLog.Info("Config reloaded from disk (external edit).");
            ConfigChanged?.Invoke();
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

        if (cfg.ConfigVersion < 19)
        {
            // Visor rename. Older installs stored a hard-coded "balanced" power plan that was never
            // the user's choice. Global defaults: null = leave the plan alone. Factory: the real active
            // plan is captured later (CompleteDeferredMigration), after a leftover game session is restored.
            cfg.FactoryDefaults ??= new DefaultSettings { Resolution = CaptureCurrentResolution(), Color = ColorSettings.Neutral };
            if (string.Equals(cfg.FactoryDefaults.PowerPlan, "balanced", StringComparison.OrdinalIgnoreCase))
                cfg.FactoryDefaults.PowerPlan = null;
            if (string.Equals(cfg.Defaults?.PowerPlan, "balanced", StringComparison.OrdinalIgnoreCase))
                cfg.Defaults!.PowerPlan = null;
            cfg.ConfigVersion = 19;
            changed = true;
            AppLog.Info("Migrated to v19 (Visor rename, real factory power plan).");
        }

        cfg.Ui ??= new UiPreferences();

        foreach (var profile in cfg.Profiles)
            profile.Presets ??= new List<QuickPreset>();

        // Companion entries are never deleted here (a network drive may just be offline).
        // Unsafe ones are only reported; CompanionService refuses to launch them.
        foreach (var profile in cfg.Profiles)
        {
            foreach (var c in profile.Companions)
            {
                bool ok = string.Equals(c.LaunchMode, "scheduledTask", StringComparison.OrdinalIgnoreCase)
                    ? PathSecurity.IsSafeScheduledTaskName(c.TaskName)
                    : PathSecurity.IsPlausibleExecutablePath(c.Path);
                if (!ok)
                    AppLog.Warn($"Companion of {profile.Name} is blocked (not a local exe / unsafe task) and will not be launched.");
            }
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

    /// <summary>Current desktop state as the "factory" baseline (resolution + the user's real power plan).</summary>
    public static DefaultSettings CaptureFactoryDefaults()
    {
        if (FactoryDefaultsProvider is { } provider)
            return provider().Clone();
        return new DefaultSettings
        {
            Resolution = DisplayEngine.GetCurrentResolution(),
            PowerPlan = CaptureActivePowerPlan(),
            Color = ColorSettings.Neutral
        };
    }

    private static string CaptureCurrentResolution() =>
        FactoryDefaultsProvider?.Invoke().Resolution ?? DisplayEngine.GetCurrentResolution();

    /// <summary>Active plan as a UI alias when it is a stock plan, otherwise its GUID; null if unreadable.</summary>
    private static string? CaptureActivePowerPlan()
    {
        if (FactoryDefaultsProvider is { } provider)
            return provider().PowerPlan;
        var guid = DisplayEngine.GetActivePowerPlanGuid();
        return guid == null ? null : DisplayEngine.PowerPlanAlias(guid) ?? guid;
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
            StartWithWindows = false, // opt-in (setup wizard / settings)
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
            if (cfg.Profiles.Any(p => ProcessNames.Same(p.ProcessName, core.ProcessName)))
                continue;
            cfg.Profiles.Add(core);
        }
        cfg.CoreProfilesSeeded = true;
    }

    /// <summary>
    /// Seed presets for new profiles. Hotkeys use Ctrl+Alt+digit (no NumPad — TKL keyboards and
    /// laptops don't have one). Existing user configs keep their own hotkeys.
    /// </summary>
    public static List<QuickPreset> CreateDefaultPresets() => new()
    {
        new()
        {
            Id = "neutral",
            Name = "Neutral",
            Hotkey = "Ctrl+Alt+1",
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
            Hotkey = "Ctrl+Alt+2",
            ApplyColor = true,
            Color = new ColorSettings { Brightness = 0.62, Contrast = 1.05, Gamma = 0.85, LockColor = true },
            ApplyResolution = false
        },
        new()
        {
            Id = "dark",
            Name = "Dark / punchy",
            Hotkey = "Ctrl+Alt+3",
            ApplyColor = true,
            Color = new ColorSettings { Brightness = 0.42, Contrast = 1.2, Gamma = 1.15, LockColor = true },
            ApplyResolution = false
        },
        new()
        {
            Id = "res1080",
            Name = "1080p",
            Hotkey = "Ctrl+Alt+4",
            ApplyResolution = true,
            Resolution = "1920x1080",
            ApplyColor = false,
            Color = ColorSettings.Neutral
        },
        new()
        {
            Id = "res1440",
            Name = "1440p",
            Hotkey = "Ctrl+Alt+5",
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

    public void Dispose()
    {
        try { _watcher?.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Config watcher dispose"); }
        _watcher = null;
        lock (_gate)
        {
            try { _debounceTimer?.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Config timer dispose"); }
            _debounceTimer = null;
        }
    }
}
