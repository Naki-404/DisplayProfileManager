using System.IO;
using System.Text.Json;
using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

/// <summary>Export / import color preset packs (JSON) — share between PCs.</summary>
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
