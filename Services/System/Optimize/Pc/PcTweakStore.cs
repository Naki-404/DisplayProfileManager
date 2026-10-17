using System.IO;
using System.Text.Json;

namespace Visor.Services.Optimize.Pc;

/// <summary>
/// Tiny persisted facts the PC tweaks need across restarts (pc-tweaks.json next to the config):
/// currently only the GUID of the Ultimate Performance scheme Visor created, so detection does not
/// depend on the (localizable / renamable) plan name. Atomic writes (tmp + move). Null directory =
/// in-memory only (tests).
/// </summary>
public sealed class PcTweakStore
{
    public const string FileName = "pc-tweaks.json";

    private sealed class Data
    {
        public string? CreatedUltimateGuid { get; set; }
        public DateTime? CreatedUltimateUtc { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();
    private readonly string? _path;
    private Data _data = new();

    public PcTweakStore(string? configDirectory)
    {
        _path = string.IsNullOrWhiteSpace(configDirectory) ? null : Path.Combine(configDirectory, FileName);
        Load();
    }

    public string? CreatedUltimateGuid
    {
        get { lock (_gate) return _data.CreatedUltimateGuid; }
    }

    public DateTime? CreatedUltimateUtc
    {
        get { lock (_gate) return _data.CreatedUltimateUtc; }
    }

    public void SetCreatedUltimate(string guid, DateTime utc)
    {
        if (!Guid.TryParse(guid, out var g)) throw new ArgumentException("GUID expected", nameof(guid));
        lock (_gate)
        {
            _data.CreatedUltimateGuid = g.ToString("D");
            _data.CreatedUltimateUtc = utc;
            Save();
        }
    }

    /// <summary>Forgets the created scheme (after Visor removed it, or when it no longer exists).</summary>
    public void ClearCreatedUltimate()
    {
        lock (_gate)
        {
            if (_data.CreatedUltimateGuid == null && _data.CreatedUltimateUtc == null) return;
            _data.CreatedUltimateGuid = null;
            _data.CreatedUltimateUtc = null;
            Save();
        }
    }

    private void Load()
    {
        if (_path == null || !File.Exists(_path)) return;
        try
        {
            var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path), JsonOptions);
            if (d != null)
            {
                if (d.CreatedUltimateGuid != null && !Guid.TryParse(d.CreatedUltimateGuid, out _))
                    d.CreatedUltimateGuid = null;
                _data = d;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "PC tweaks store load");
        }
    }

    private void Save()
    {
        if (_path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "PC tweaks store save");
        }
    }
}
