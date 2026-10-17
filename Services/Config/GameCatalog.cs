using Visor.Models;
using System.IO;

namespace Visor.Services;

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

    /// <summary>
    /// Starter profiles for a brand-new config. Nothing personal is enabled: no forced resolutions,
    /// no companion tools — only the high-performance power plan while the game runs.
    /// </summary>
    public static List<GameProfile> CreateCoreProfiles()
    {
        return new List<GameProfile>
        {
            new()
            {
                Id = "valorant",
                Name = "Valorant",
                ProcessName = "VALORANT-Win64-Shipping.exe",
                ApplyResolution = false,
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
                ApplyResolution = false,
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
                ApplyResolution = false,
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

        if (!string.IsNullOrWhiteSpace(profile.ExePath)
            && SteamLaunch.TryResolveAppId(profile.ExePath, out int steamId))
            profile.SteamAppId = steamId;

        if (ProcessNames.Same(profile.ProcessName, "EscapeFromTarkov.exe"))
            profile.Presets = CreateTarkovRivaPresets();

        return profile;
    }

    /// <summary>
    /// Classic RivaTuner color schemes (gamma-1 / gamma-2 / Night) converted B/C/G → Visor,
    /// applied via the Low Level backend. Hotkeys: Ctrl+Alt+digit (no NumPad).
    /// </summary>
    public static List<QuickPreset> CreateTarkovRivaPresets() => new()
    {
        new()
        {
            Id = "tarkov_gamma1",
            Name = "gamma-1",
            Hotkey = "Ctrl+Alt+1",
            ApplyColor = true,
            Color = ColorSettings.FromRivaTuner(0, 10, 1.5),
            ApplyResolution = false
        },
        new()
        {
            Id = "tarkov_gamma2",
            Name = "gamma-2",
            Hotkey = "Ctrl+Alt+2",
            ApplyColor = true,
            Color = ColorSettings.FromRivaTuner(-35, 5, 3.0),
            ApplyResolution = false
        },
        new()
        {
            Id = "tarkov_night",
            Name = "Night",
            Hotkey = "Ctrl+Alt+3",
            ApplyColor = true,
            Color = ColorSettings.FromRivaTuner(-16, 5, 3.0),
            ApplyResolution = false
        },
        new()
        {
            Id = "tarkov_neutral",
            Name = "Neutral",
            Hotkey = "Ctrl+Alt+4",
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

    /// <summary>Display / storage form of an exe name (keeps case, ensures ".exe").</summary>
    public static string Normalize(string processName) => ProcessNames.ToExeName(processName);
}
