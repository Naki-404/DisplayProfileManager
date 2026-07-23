using System.IO;
using System.Text.Json;
using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

/// <summary>Export / import color preset packs (JSON) â€” share between PCs.</summary>
public static class PresetPackService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public sealed class Pack
    {
        public string Name { get; set; } = "Preset pack";
        public string? GameProcess { get; set; }
        public List<QuickPreset> Presets { get; set; } = new();
    }

    public static bool Export(GameProfile profile)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export preset pack",
            Filter = "Preset pack (*.json)|*.json",
            FileName = Sanitize(profile.Name) + "-presets.json"
        };
        if (dlg.ShowDialog() != true) return false;

        var pack = new Pack
        {
            Name = profile.Name + " presets",
            GameProcess = profile.ProcessName,
            Presets = profile.Presets.Select(QuickPreset.CloneOf).ToList()
        };
        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(pack, Json));
        AppLog.Info("Exported preset pack: " + dlg.FileName);
        return true;
    }

    public static int Import(GameProfile profile)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import preset pack",
            Filter = "Preset pack (*.json)|*.json"
        };
        if (dlg.ShowDialog() != true) return 0;

        var pack = JsonSerializer.Deserialize<Pack>(File.ReadAllText(dlg.FileName), Json);
        if (pack?.Presets == null || pack.Presets.Count == 0) return 0;

        profile.Presets ??= new List<QuickPreset>();
        int added = 0;
        foreach (var p in pack.Presets)
        {
            var c = QuickPreset.CloneOf(p);
            c.EnsureDualColorSlots();
            c.Id = profile.Id + "_" + Guid.NewGuid().ToString("N")[..8];
            if (profile.Presets.Any(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase)))
                c.Name += " (import)";
            profile.Presets.Add(c);
            added++;
        }
        AppLog.Info($"Imported {added} presets into {profile.Name}");
        return added;
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "presets" : name;
    }
}

/// <summary>Export / import full game profiles (JSON) â€” share between PCs.</summary>
public static class ProfilePackService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public sealed class Pack
    {
        public string Name { get; set; } = "Profile pack";
        public GameProfile? Profile { get; set; }
    }

    public static bool Export(GameProfile profile)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export profile pack",
            Filter = "Profile pack (*.json)|*.json",
            FileName = Sanitize(profile.Name) + "-profile.json"
        };
        if (dlg.ShowDialog() != true) return false;

        var pack = new Pack
        {
            Name = profile.Name + " profile",
            Profile = CloneProfile(profile)
        };
        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(pack, Json));
        AppLog.Info("Exported profile pack: " + dlg.FileName);
        return true;
    }

    /// <summary>Import a profile pack and return a cloned GameProfile for MainWindow to add. Null if cancelled/empty.</summary>
    public static GameProfile? Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import profile pack",
            Filter = "Profile pack (*.json)|*.json"
        };
        if (dlg.ShowDialog() != true) return null;

        var pack = JsonSerializer.Deserialize<Pack>(File.ReadAllText(dlg.FileName), Json);
        if (pack?.Profile == null || string.IsNullOrWhiteSpace(pack.Profile.ProcessName))
        {
            // Allow bare GameProfile JSON as well
            var bare = JsonSerializer.Deserialize<GameProfile>(File.ReadAllText(dlg.FileName), Json);
            if (bare == null || string.IsNullOrWhiteSpace(bare.ProcessName))
                return null;
            return PrepareImported(bare);
        }

        return PrepareImported(pack.Profile);
    }

    /// <summary>Import and merge into cfg.Profiles. Returns number of profiles added (0 or 1).</summary>
    public static int ImportMerge(AppConfig cfg)
    {
        var imported = Import();
        if (imported == null) return 0;

        if (cfg.Profiles.Any(p =>
                string.Equals(p.ProcessName, imported.ProcessName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Name, imported.Name, StringComparison.OrdinalIgnoreCase)))
        {
            imported.Name += " (import)";
        }

        cfg.Profiles.Add(imported);
        AppLog.Info($"Imported profile '{imported.Name}' ({imported.ProcessName})");
        return 1;
    }

    private static GameProfile PrepareImported(GameProfile src)
    {
        var c = CloneProfile(src);
        c.Id = Guid.NewGuid().ToString("N");
        c.ProcessAliases ??= new List<string>();
        c.Companions ??= new List<CompanionApp>();
        c.Presets ??= new List<QuickPreset>();
        c.Session ??= new SessionExtras();
        foreach (var pr in c.Presets)
        {
            pr.Id = c.Id + "_" + Guid.NewGuid().ToString("N")[..8];
            pr.EnsureDualColorSlots();
        }
        c.EnsureDualColorSlots();
        return c;
    }

    public static GameProfile CloneProfile(GameProfile p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Enabled = p.Enabled,
        ProcessName = p.ProcessName,
        ProcessAliases = (p.ProcessAliases ?? new List<string>()).ToList(),
        ExePath = p.ExePath,
        Resolution = p.Resolution,
        RefreshRate = p.RefreshRate,
        StartupPresetId = p.StartupPresetId,
        ApplyDelaySeconds = p.ApplyDelaySeconds,
        ApplyOnFocus = p.ApplyOnFocus,
        DisplayDevice = p.DisplayDevice,
        PowerPlan = p.PowerPlan,
        ApplyColor = p.ApplyColor,
        ApplyResolution = p.ApplyResolution,
        ApplyPowerPlan = p.ApplyPowerPlan,
        RestoreMode = p.RestoreMode,
        Color = p.Color.Clone(),
        ColorLowLevel = p.ColorLowLevel?.Clone(),
        ColorDriver = p.ColorDriver?.Clone(),
        Session = (p.Session ?? new SessionExtras()).Clone(),
        Companions = (p.Companions ?? new List<CompanionApp>()).Select(c => new CompanionApp
        {
            Path = c.Path,
            Arguments = c.Arguments,
            LaunchMode = c.LaunchMode,
            TaskName = c.TaskName,
            OnStop = c.OnStop,
            StopHotkey = c.StopHotkey,
            DismissDialogs = c.DismissDialogs,
            MinimizeToTray = c.MinimizeToTray
        }).ToList(),
        Presets = (p.Presets ?? new List<QuickPreset>()).Select(QuickPreset.CloneOf).ToList()
    };

    private static string Sanitize(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        return string.IsNullOrWhiteSpace(name) ? "profile" : name;
    }
}
