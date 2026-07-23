using DisplayProfileManager.Models;
using System.IO;

namespace DisplayProfileManager.Services;

/// <summary>Known game executables used only for discovery — never auto-added as dead profiles.</summary>
public static class GameCatalog
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VALORANT-Win64-Shipping.exe"] = "Valorant",
        ["TslGame.exe"] = "PUBG",
        ["aces.exe"] = "War Thunder",
        ["EscapeFromTarkov.exe"] = "Escape from Tarkov",
        ["cs2.exe"] = "Counter-Strike 2",
        ["csgo.exe"] = "CS:GO",
        ["r5apex.exe"] = "Apex Legends",
        ["Overwatch.exe"] = "Overwatch",
        ["GTA5.exe"] = "GTA V",
        ["RainbowSix.exe"] = "Rainbow Six Siege",
        ["FortniteClient-Win64-Shipping.exe"] = "Fortnite",
        ["Fortnite.exe"] = "Fortnite",
        ["Minecraft.exe"] = "Minecraft",
        ["League of Legends.exe"] = "League of Legends",
        ["LeagueClient.exe"] = "League Client",
        ["RiotClientServices.exe"] = "Riot Client",
        ["destiny2.exe"] = "Destiny 2",
        ["Witcher3.exe"] = "The Witcher 3",
        ["Cyberpunk2077.exe"] = "Cyberpunk 2077",
        ["eldenring.exe"] = "Elden Ring",
        ["hlvr.exe"] = "Half-Life: Alyx",
        ["re8.exe"] = "Resident Evil Village",
        ["DeadByDaylight-Win64-Shipping.exe"] = "Dead by Daylight",
        ["RocketLeague.exe"] = "Rocket League",
        ["Dota2.exe"] = "Dota 2",
        ["bfv.exe"] = "Battlefield V",
        ["bf1.exe"] = "Battlefield 1",
        ["cod.exe"] = "Call of Duty",
        ["ModernWarfare.exe"] = "Modern Warfare",
    };

    public static List<GameProfile> CreateCoreProfiles()
    {
        var riva = @"C:\Program Files (x86)\RivaTuner v2.24 MSI Master Overclocking Arena 2009 edition\RivaTuner.exe";
        return new List<GameProfile>
        {
            new()
            {
                Id = "valorant",
                Name = "Valorant",
                ProcessName = "VALORANT-Win64-Shipping.exe",
                Resolution = "1568x1080",
                ApplyResolution = true,
                ApplyColor = false,
                ApplyPowerPlan = true,
                PowerPlan = "highPerformance",
                Color = ColorSettings.Neutral,
                Presets = CreateValorantStretchPresets()
            },
            new()
            {
                Id = "pubg",
                Name = "PUBG",
                ProcessName = "TslGame.exe",
                Resolution = "2560x1440",
                ApplyResolution = true,
                ApplyColor = false,
                ApplyPowerPlan = true,
                PowerPlan = "highPerformance",
                Color = ColorSettings.Neutral,
                Presets = CreatePubgStretchPresets()
            },
            new()
            {
                Id = "warthunder",
                Name = "War Thunder",
                ProcessName = "aces.exe",
                Resolution = "2560x1440",
                ApplyResolution = true,
                ApplyColor = false,
                ApplyPowerPlan = true,
                PowerPlan = "highPerformance",
                Color = ColorSettings.Neutral
            },
            new()
            {
                Id = "tarkov",
                Name = "Escape from Tarkov",
                ProcessName = "EscapeFromTarkov.exe",
                ApplyResolution = false,
                ApplyColor = false,
                ApplyPowerPlan = true,
                PowerPlan = "highPerformance",
                Color = ColorSettings.Neutral,
                Companions = File.Exists(riva)
                    ? new List<CompanionApp>
                    {
                        new()
                        {
                            Path = riva,
                            LaunchMode = "scheduledTask",
                            TaskName = "GameResolution-RivaTuner",
                            OnStop = "closeThenKill",
                            StopHotkey = null,
                            DismissDialogs = true,
                            MinimizeToTray = true
                        }
                    }
                    : new List<CompanionApp>(),
                Presets = CreateTarkovRivaPresets()
            },
        };
    }

    public static string? GetFriendlyName(string processName)
    {
        var n = Normalize(processName);
        return Map.TryGetValue(n, out var name) ? name : null;
    }

    public static bool IsKnown(string processName) => Map.ContainsKey(Normalize(processName));

    public static IReadOnlyDictionary<string, string> All => Map;

    public static List<(string Process, string Name)> FindRunningKnown()
    {
        var result = new List<(string, string)>();
        foreach (var exe in ProcessWatcher.GetRunningExeNames())
        {
            if (Map.TryGetValue(exe, out var name))
                result.Add((exe, name));
        }
        return result;
    }

    public static GameProfile CreateProfileSkeleton(string process, string? name = null, string? exePath = null)
    {
        var riva = @"C:\Program Files (x86)\RivaTuner v2.24 MSI Master Overclocking Arena 2009 edition\RivaTuner.exe";
        var friendly = name ?? GetFriendlyName(process) ?? Path.GetFileNameWithoutExtension(process);

        var profile = new GameProfile
        {
            Name = friendly,
            ProcessName = Normalize(process),
            ExePath = string.IsNullOrWhiteSpace(exePath) ? null : exePath,
            Enabled = true,
            ApplyResolution = false,
            ApplyColor = false,
            ApplyPowerPlan = true,
            PowerPlan = "highPerformance",
            Color = ColorSettings.Neutral
        };

        if (string.Equals(profile.ProcessName, "EscapeFromTarkov.exe", StringComparison.OrdinalIgnoreCase))
        {
            profile.ApplyColor = false;
            profile.Color = ColorSettings.Neutral;
            profile.Presets = CreateTarkovRivaPresets();
            if (File.Exists(riva))
            {
                profile.Companions.Add(new CompanionApp
                {
                    Path = riva,
                    LaunchMode = "scheduledTask",
                    TaskName = "GameResolution-RivaTuner",
                    OnStop = "closeThenKill",
                    StopHotkey = null,
                    DismissDialogs = true,
                    MinimizeToTray = true
                });
            }
        }

        return profile;
    }

    /// <summary>
    /// Color schemes from the user's RivaTuner (gamma-1 / gamma-2 / Night),
    /// converted B/C/G → DPM and applied via Low Level backend.
    /// </summary>
    public static List<QuickPreset> CreateTarkovRivaPresets() => new()
    {
        new()
        {
            Id = "tarkov_gamma1",
            Name = "gamma-1",
            Hotkey = "Ctrl+Alt+NumPad1",
            ApplyColor = true,
            Color = ColorSettings.FromRivaTuner(0, 10, 1.5),
            ApplyResolution = false
        },
        new()
        {
            Id = "tarkov_gamma2",
            Name = "gamma-2",
            Hotkey = "Ctrl+Alt+NumPad2",
            ApplyColor = true,
            Color = ColorSettings.FromRivaTuner(-35, 5, 3.0),
            ApplyResolution = false
        },
        new()
        {
            Id = "tarkov_night",
            Name = "Night",
            Hotkey = "Ctrl+Alt+NumPad3",
            ApplyColor = true,
            Color = ColorSettings.FromRivaTuner(-16, 5, 3.0),
            ApplyResolution = false
        },
        new()
        {
            Id = "tarkov_neutral",
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
    };

    /// <summary>Stretch resolution helpers + one vibrance color preset for Valorant seed.</summary>
    public static List<QuickPreset> CreateValorantStretchPresets() => new()
    {
        StretchRes("valorant_1920x1080", "1920×1080 stretch", "1920x1080"),
        StretchRes("valorant_1728x1080", "1728×1080 stretch", "1728x1080"),
        StretchRes("valorant_1620x1080", "1620×1080 stretch", "1620x1080"),
        StretchRes("valorant_1440x1080", "1440×1080 stretch", "1440x1080"),
        new()
        {
            Id = "valorant_vibrance",
            Name = "Vibrance+",
            ApplyResolution = false,
            ApplyColor = true,
            Color = new ColorSettings
            {
                Brightness = 0.5, Contrast = 1.05, Gamma = 1.0, Vibrance = 80,
                Backend = ColorBackend.Driver, LockColor = true
            }
        },
    };

    /// <summary>Optional stretch helpers for PUBG seed.</summary>
    public static List<QuickPreset> CreatePubgStretchPresets() => new()
    {
        StretchRes("pubg_2560x1440", "2560×1440 stretch", "2560x1440"),
        StretchRes("pubg_1920x1080", "1920×1080 stretch", "1920x1080"),
    };

    private static QuickPreset StretchRes(string id, string name, string resolution) => new()
    {
        Id = id,
        Name = name,
        ApplyResolution = true,
        Resolution = resolution,
        ApplyColor = false,
        Color = ColorSettings.Neutral
    };

    public static string Normalize(string processName)
    {
        processName = processName.Trim();
        if (!processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            processName += ".exe";
        return processName;
    }
}
