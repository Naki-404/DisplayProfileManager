using System.IO;
using System.Text.Json;
using Visor.Models;

namespace Visor.Services;

/// <summary>
/// Small durable ring of Visor activity (apply / restore / pause / emergency).
/// Lives next to the config as history.json. Failures are silent — history is cosmetic trust UI.
/// Writes are debounced (≤ 1 per <see cref="SaveDelayMs"/>) and happen off the caller's thread;
/// <see cref="Flush"/> / <see cref="Dispose"/> write synchronously (call on exit).
/// </summary>
public sealed class ActivityHistoryStore : IDisposable
{
    public const int MaxEntries = 100;
    /// <summary>No single kind may hold more than this many of the <see cref="MaxEntries"/> slots (hotkey spam protection).</summary>
    public const int MaxEntriesPerKind = 30;
    public const int SaveDelayMs = 500;
    public const string FileName = "history.json";
    public const string BackupSuffix = ".bak";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly string _path;
    private readonly System.Threading.Timer _timer;
    private readonly EventHandler _processExit;
    private List<ActivityHistoryEntry> _entries = new();
    private bool _dirty;
    private bool _timerArmed;
    private bool _disposed;

    public event Action? Changed;

    public ActivityHistoryStore(string configDirectory)
    {
        _path = Path.Combine(configDirectory, FileName);
        _timer = new System.Threading.Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
        _processExit = (_, _) => Flush();
        AppDomain.CurrentDomain.ProcessExit += _processExit;
        Load();
    }

    public IReadOnlyList<ActivityHistoryEntry> Snapshot()
    {
        lock (_gate)
            return _entries.ToList();
    }

    public IReadOnlyList<ActivityHistoryEntry> Recent(int count)
    {
        if (count <= 0) return Array.Empty<ActivityHistoryEntry>();
        lock (_gate)
        {
            if (_entries.Count == 0) return Array.Empty<ActivityHistoryEntry>();
            int take = Math.Min(count, _entries.Count);
            return _entries.TakeLast(take).Reverse().ToList();
        }
    }

    public void Add(string kind, string? profileId = null, string? profileName = null,
        string? detail = null, bool success = true)
    {
        if (string.IsNullOrWhiteSpace(kind)) return;
        var entry = new ActivityHistoryEntry
        {
            Utc = DateTime.UtcNow,
            Kind = kind.Trim(),
            ProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim(),
            ProfileName = string.IsNullOrWhiteSpace(profileName) ? null : profileName.Trim(),
            Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(),
            Success = success
        };

        lock (_gate)
        {
            _entries.Add(entry);
            Trim(_entries);
            _dirty = true;
            ArmTimerLocked();
        }
        try { Changed?.Invoke(); }
        catch (Exception ex) { AppLog.Warn(ex, "ActivityHistory Changed"); }
    }

    /// <summary>Applies the per-kind and global caps, evicting the oldest entries first.</summary>
    internal static void Trim(List<ActivityHistoryEntry> entries)
    {
        if (entries.Count == 0) return;
        string kind = entries[^1].Kind;
        int ofKind = 0;
        for (int i = 0; i < entries.Count; i++)
            if (string.Equals(entries[i].Kind, kind, StringComparison.OrdinalIgnoreCase)) ofKind++;
        for (int i = 0; i < entries.Count && ofKind > MaxEntriesPerKind; i++)
        {
            if (!string.Equals(entries[i].Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;
            entries.RemoveAt(i);
            i--;
            ofKind--;
        }
        while (entries.Count > MaxEntries)
            entries.RemoveAt(0);
    }

    private void ArmTimerLocked()
    {
        if (_disposed || _timerArmed) return;
        _timerArmed = true;
        try { _timer.Change(SaveDelayMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { _timerArmed = false; }
    }

    private void OnTimer()
    {
        try { Flush(); }
        catch (Exception ex) { AppLog.Warn(ex, "history.json timer flush"); }
    }

    /// <summary>Writes pending entries now (synchronously, on the calling thread). Safe to call repeatedly.</summary>
    public void Flush()
    {
        List<ActivityHistoryEntry>? snapshot;
        lock (_gate)
        {
            _timerArmed = false;
            if (!_dirty) return;
            _dirty = false;
            snapshot = _entries.ToList();
        }
        lock (_writeGate)
            Save(snapshot);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        AppDomain.CurrentDomain.ProcessExit -= _processExit;
        try { _timer.Dispose(); } catch { /* ignore */ }
        Flush();
    }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    _entries = new List<ActivityHistoryEntry>();
                    return;
                }
                var json = File.ReadAllText(_path);
                var list = JsonSerializer.Deserialize<List<ActivityHistoryEntry>>(json, JsonOptions);
                _entries = list ?? new List<ActivityHistoryEntry>();
                if (_entries.Count > MaxEntries)
                    _entries = _entries.TakeLast(MaxEntries).ToList();
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "history.json read");
                try { File.Copy(_path, _path + BackupSuffix, overwrite: true); }
                catch (Exception bex) { AppLog.Warn(bex, "history.json backup"); }
                _entries = new List<ActivityHistoryEntry>();
            }
        }
    }

    private void Save(List<ActivityHistoryEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries, JsonOptions));
            if (!File.Exists(_path))
            {
                File.Move(tmp, _path);
                return;
            }
            try
            {
                File.Replace(tmp, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                AppLog.Warn(ex, "history.json File.Replace failed, falling back to copy");
                File.Copy(tmp, _path, overwrite: true);
                try { File.Delete(tmp); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "history.json write");
        }
    }
}
