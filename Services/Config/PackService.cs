using System.IO;
using System.Text.Json;
using Visor.Models;

namespace Visor.Services;

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

    /// <summary>A built-in preset pack shown in the Pack Gallery (bundled JSON or fallback).</summary>
    public sealed class BundledPack
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "Preset pack";
        public string? Subtitle { get; set; }
        public List<QuickPreset> Presets { get; set; } = new();
    }

    /// <summary>Raw JSON shape for bundled packs — flattened preset fields (see Assets/Packs/*.json).</summary>
    private sealed class BundledPackDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Subtitle { get; set; }
        public List<BundledPresetDto> Presets { get; set; } = new();
    }

    private sealed class BundledPresetDto
    {
        public string? Name { get; set; }
        public string? Hotkey { get; set; }
        public bool ApplyColor { get; set; } = true;
        public double Brightness { get; set; } = 0.5;
        public double Contrast { get; set; } = 1.0;
        public double Gamma { get; set; } = 1.0;
        public int Vibrance { get; set; } = 50;
        public double ShadowLift { get; set; }
        public int Hue { get; set; }
        public string? Backend { get; set; }
        public bool LockColor { get; set; } = true;
        public bool ApplyResolution { get; set; }
        public string? Resolution { get; set; }
    }

    /// <summary>
    /// Loads built-in packs from Assets/Packs/*.json (copied next to the exe). Falls back to the
    /// hardcoded <see cref="GameCatalog"/> creators when the folder is missing or empty/unparsable,
    /// so the Pack Gallery is never empty.
    /// </summary>
    public static List<BundledPack> LoadBundledPacks()
    {
        var result = new List<BundledPack>();
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Packs");
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.json")
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var dto = JsonSerializer.Deserialize<BundledPackDto>(File.ReadAllText(file), Json);
                        if (dto == null || string.IsNullOrWhiteSpace(dto.Name)) continue;
                        result.Add(ConvertDto(dto));
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error($"Bundled pack parse failed ({file}): {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("LoadBundledPacks: " + ex.Message);
        }

        if (result.Count == 0)
            result.AddRange(FallbackPacks());

        return result;
    }

    private static BundledPack ConvertDto(BundledPackDto dto) => new()
    {
        Id = string.IsNullOrWhiteSpace(dto.Id) ? Guid.NewGuid().ToString("N") : dto.Id!,
        Name = dto.Name!,
        Subtitle = dto.Subtitle,
        Presets = dto.Presets.Select(ConvertPresetDto).ToList()
    };

    private static QuickPreset ConvertPresetDto(BundledPresetDto p)
    {
        var color = new ColorSettings
        {
            Brightness = p.Brightness,
            Contrast = p.Contrast,
            Gamma = p.Gamma,
            Vibrance = p.Vibrance,
            ShadowLift = p.ShadowLift,
            Hue = p.Hue,
            Backend = ParseBackend(p.Backend),
            LockColor = p.LockColor
        };
        color.Clamp();

        var preset = new QuickPreset
        {
            Name = string.IsNullOrWhiteSpace(p.Name) ? "Preset" : p.Name!,
            Hotkey = string.IsNullOrWhiteSpace(p.Hotkey) ? null : p.Hotkey,
            ApplyColor = p.ApplyColor,
            Color = color,
            ApplyResolution = p.ApplyResolution,
            Resolution = p.Resolution
        };
        preset.EnsureDualColorSlots();
        return preset;
    }

    private static ColorBackend ParseBackend(string? backend) => (backend?.Trim().ToLowerInvariant()) switch
    {
        "driver" => ColorBackend.Driver,
        "nvidia" => ColorBackend.Nvidia,
        "amd" => ColorBackend.Amd,
        "gdi" => ColorBackend.Gdi,
        _ => ColorBackend.LowLevel
    };

    private static List<BundledPack> FallbackPacks() => new()
    {
        new BundledPack
        {
            Id = "tarkov-riva",
            Name = "Tarkov Riva presets",
            Subtitle = "gamma-1 / gamma-2 / Night / Neutral",
            Presets = GameCatalog.CreateTarkovRivaPresets()
        },
        new BundledPack
        {
            Id = "valorant-stretch",
            Name = "Valorant stretch",
            Subtitle = "Stretch resolutions + Vibrance+",
            Presets = GameCatalog.CreateValorantStretchPresets()
        },
        new BundledPack
        {
            Id = "pubg-stretch",
            Name = "PUBG stretch",
            Subtitle = "2560×1440 / 1920×1080 stretch helpers",
            Presets = GameCatalog.CreatePubgStretchPresets()
        }
    };

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
            Presets = (profile.Presets ?? new List<QuickPreset>())
                .Where(p => p != null)
                .Select(QuickPreset.CloneOf)
                .ToList()
        };
        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(pack, Json));
        AppLog.Info("Exported preset pack: " + dlg.FileName);
        return true;
    }

    /// <summary>Import presets into <paramref name="profile"/>. Returns the number added (0 on cancel / invalid file).</summary>
    public static int Import(GameProfile profile)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import preset pack",
            Filter = "Preset pack (*.json)|*.json"
        };
        if (dlg.ShowDialog() != true) return 0;

        Pack? pack;
        try
        {
            pack = JsonSerializer.Deserialize<Pack>(File.ReadAllText(dlg.FileName), Json);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Preset pack import (unreadable file)");
            return 0;
        }
        if (pack?.Presets == null || pack.Presets.Count == 0) return 0;

        profile.Presets ??= new List<QuickPreset>();
        int added = 0;
        foreach (var p in pack.Presets)
        {
            if (p == null) continue;
            p.Name ??= "Preset";
            p.Color ??= ColorSettings.Neutral;
            var c = QuickPreset.CloneOf(p);
            c.Color.Clamp();
            c.EnsureDualColorSlots();
            c.Id = profile.Id + "_" + Guid.NewGuid().ToString("N")[..8];
            if (profile.Presets.Any(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase)))
                c.Name += " (import)";
            if (!string.IsNullOrWhiteSpace(c.Hotkey) && !HotkeyIsFree(c.Hotkey!, profile))
            {
                AppLog.Info($"Imported preset '{c.Name}': hotkey {c.Hotkey} cleared (invalid or already in use).");
                c.Hotkey = null;
            }
            profile.Presets.Add(c);
            added++;
        }
        AppLog.Info($"Imported {added} presets into {profile.Name}");
        return added;
    }

    /// <summary>Valid gesture not used by a global hotkey or another preset of this game.</summary>
    private static bool HotkeyIsFree(string gesture, GameProfile profile)
    {
        if (!HotkeyService.TryParseGesture(gesture, out uint mods, out uint vk)) return false;

        bool Same(string? other) =>
            !string.IsNullOrWhiteSpace(other)
            && HotkeyService.TryParseGesture(other!, out uint m, out uint v)
            && m == mods && v == vk;

        var g = Visor.App.Services?.Config?.Current?.GlobalHotkeys;
        if (g != null && (Same(g.ToggleOverlay) || Same(g.EmergencyRestore) || Same(g.NextPreset)
                          || Same(g.PreviousPreset) || Same(g.CompareAb) || Same(g.ToggleZoom)))
            return false;
        return !(profile.Presets ?? new List<QuickPreset>()).Any(p => Same(p.Hotkey));
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "presets" : name;
    }
}

/// <summary>Export / import full game profiles (JSON) — share between PCs.</summary>
/// <remarks>
/// Packs are untrusted input: import keeps only display / preset / session settings. Companion
/// apps (paths, arguments, scheduled tasks) and the exe path are stripped, and the watched
/// process names must be plain exe names that are not core Windows processes.
/// </remarks>
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

        var copy = profile.Clone();
        // Machine-local details are never imported anyway — don't leak them into shared files.
        copy.Companions = new List<CompanionApp>();
        copy.ExePath = null;
        copy.DisplayDevice = null;
        copy.Session.AudioDeviceId = null;

        var pack = new Pack
        {
            Name = profile.Name + " profile",
            Profile = copy
        };
        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(pack, Json));
        AppLog.Info("Exported profile pack: " + dlg.FileName);
        return true;
    }

    /// <summary>Import a profile pack and return a sanitized GameProfile for MainWindow to add. Null if cancelled/invalid.</summary>
    public static GameProfile? Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import profile pack",
            Filter = "Profile pack (*.json)|*.json"
        };
        if (dlg.ShowDialog() != true) return null;

        try
        {
            var text = File.ReadAllText(dlg.FileName);
            var pack = JsonSerializer.Deserialize<Pack>(text, Json);
            var src = pack?.Profile;
            if (src == null || string.IsNullOrWhiteSpace(src.ProcessName))
            {
                // Allow bare GameProfile JSON as well
                src = JsonSerializer.Deserialize<GameProfile>(text, Json);
                if (src == null || string.IsNullOrWhiteSpace(src.ProcessName))
                    return null;
            }
            return PrepareImported(src);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Profile pack import (unreadable file)");
            return null;
        }
    }

    private static GameProfile? PrepareImported(GameProfile src)
    {
        // Untrusted JSON may contain nulls anywhere — make Clone() safe first.
        src.Name ??= "Imported profile";
        src.ProcessAliases ??= new List<string>();
        src.Companions ??= new List<CompanionApp>();
        src.Presets ??= new List<QuickPreset>();
        src.Presets.RemoveAll(p => p == null);
        foreach (var p in src.Presets)
        {
            p.Name ??= "Preset";
            p.Color ??= ColorSettings.Neutral;
        }
        src.Session ??= new SessionExtras();
        src.Color ??= ColorSettings.Neutral;

        if (!IsAcceptableProcessName(src.ProcessName, out var why))
        {
            AppLog.Warn($"Profile pack rejected: process name {why}.");
            return null;
        }

        var stripped = new List<string>();
        int companions = src.Companions.Count(c => c != null);
        if (companions > 0) stripped.Add($"{companions} companion app(s)");
        if (!string.IsNullOrWhiteSpace(src.ExePath)) stripped.Add("exe path");
        src.Companions = new List<CompanionApp>();
        src.ExePath = null;

        var aliases = new List<string>();
        foreach (var a in src.ProcessAliases)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            if (IsAcceptableProcessName(a, out var aliasWhy))
                aliases.Add(ProcessNames.ToExeName(a));
            else
                stripped.Add($"alias ({aliasWhy})");
        }
        src.ProcessAliases = aliases;

        var c = src.Clone();
        c.Id = Guid.NewGuid().ToString("N");
        c.ProcessName = ProcessNames.ToExeName(c.ProcessName);

        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedGestures = new HashSet<(uint, uint)>();
        foreach (var pr in c.Presets)
        {
            if (!string.IsNullOrWhiteSpace(pr.Hotkey)
                && (!HotkeyService.TryParseGesture(pr.Hotkey!, out uint m, out uint v) || !usedGestures.Add((m, v))))
            {
                stripped.Add($"hotkey {pr.Hotkey} of '{pr.Name}' (invalid or duplicate)");
                pr.Hotkey = null;
            }
            var newId = c.Id + "_" + Guid.NewGuid().ToString("N")[..8];
            if (!string.IsNullOrEmpty(pr.Id)) idMap[pr.Id] = newId;
            pr.Id = newId;
            pr.Color.Clamp();
            pr.EnsureDualColorSlots();
        }
        c.StartupPresetId = c.StartupPresetId != null && idMap.TryGetValue(c.StartupPresetId, out var mapped)
            ? mapped
            : null;
        c.EnsureDualColorSlots();

        AppLog.Info(stripped.Count == 0
            ? $"Imported profile pack '{c.Name}' ({c.ProcessName})."
            : $"Imported profile pack '{c.Name}' ({c.ProcessName}); stripped: {string.Join(", ", stripped)}.");
        return c;
    }

    private static bool IsAcceptableProcessName(string? name, out string why)
    {
        why = "";
        if (!ProcessNames.IsPlainExeName(name))
        {
            why = "is not a plain exe file name";
            return false;
        }
        if (ProcessNames.IsProtectedSystemProcess(name))
        {
            why = $"'{name}' is a Windows system process";
            return false;
        }
        return true;
    }

    private static string Sanitize(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        return string.IsNullOrWhiteSpace(name) ? "profile" : name;
    }
}
