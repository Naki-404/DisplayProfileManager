using System.IO;
using System.Text.Json;

namespace Visor.Services;

/// <summary>One journaled apply: exactly what was there before, and what Visor set.</summary>
public sealed class SafeActionJournalEntry
{
    /// <summary>Undo slot (see <see cref="ISafeActionTarget.JournalKey"/>).</summary>
    public string Key { get; set; } = "";
    /// <summary>Action that produced this entry (e.g. gameMode.on).</summary>
    public string ActionId { get; set; } = "";
    public DateTime Utc { get; set; }
    /// <summary>Exact raw state before apply (target-specific serialization; includes "value absent").</summary>
    public string PreviousState { get; set; } = "";
    /// <summary>Raw state Visor applied and verified.</summary>
    public string TargetState { get; set; } = "";
}

/// <summary>
/// Persisted undo journal for SafeActions (safeactions.json next to the config). One entry per
/// undo slot; survives restarts. Writes are atomic (tmp + replace) like <see cref="ActivityHistoryStore"/>.
/// </summary>
public sealed class SafeActionJournal
{
    public const string FileName = "safeactions.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly object _gate = new();
    private readonly string? _path;
    private List<SafeActionJournalEntry> _entries = new();

    public event Action? Changed;

    /// <param name="configDirectory">Folder for the journal file; null keeps the journal in memory only (tests).</param>
    public SafeActionJournal(string? configDirectory)
    {
        _path = string.IsNullOrWhiteSpace(configDirectory) ? null : Path.Combine(configDirectory, FileName);
        Load();
    }

    public IReadOnlyList<SafeActionJournalEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public SafeActionJournalEntry? Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        lock (_gate)
            return _entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds or replaces the entry for <c>entry.Key</c>.</summary>
    public void Record(SafeActionJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Key)) throw new ArgumentException("Key required", nameof(entry));
        lock (_gate)
        {
            _entries.RemoveAll(e => string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
            _entries.Add(entry);
            SaveLocked();
        }
        RaiseChanged();
    }

    public bool Remove(string key)
    {
        bool removed;
        lock (_gate)
        {
            removed = _entries.RemoveAll(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) SaveLocked();
        }
        if (removed) RaiseChanged();
        return removed;
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { AppLog.Warn(ex, "SafeActionJournal Changed"); }
    }

    private void Load()
    {
        if (_path == null) return;
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return;
                var list = JsonSerializer.Deserialize<List<SafeActionJournalEntry>>(File.ReadAllText(_path), JsonOptions);
                _entries = (list ?? new List<SafeActionJournalEntry>())
                    .Where(e => !string.IsNullOrWhiteSpace(e.Key)
                                && !string.IsNullOrWhiteSpace(e.ActionId)
                                && !string.IsNullOrEmpty(e.PreviousState)
                                && !string.IsNullOrEmpty(e.TargetState))
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, FileName + " read");
                try { File.Copy(_path, _path + ".bak", overwrite: true); } catch { /* ignore */ }
                _entries = new List<SafeActionJournalEntry>();
            }
        }
    }

    private void SaveLocked()
    {
        if (_path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, JsonOptions));
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
                AppLog.Warn(ex, FileName + " File.Replace failed, falling back to copy");
                File.Copy(tmp, _path, overwrite: true);
                try { File.Delete(tmp); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, FileName + " write");
        }
    }
}
