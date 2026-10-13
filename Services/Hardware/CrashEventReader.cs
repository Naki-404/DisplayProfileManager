using System.Diagnostics.Eventing.Reader;

namespace Visor.Services;

/// <summary>One System-log record reduced to what the classifier needs (injectable for tests).</summary>
public readonly record struct CrashEventRecord(DateTime TimeUtc, string Provider, int EventId, int Level);

public enum CrashEventKind
{
    /// <summary>Kernel-Power 41: the machine rebooted without a clean shutdown (power loss, hard hang, BSOD).</summary>
    UnexpectedShutdown,
    /// <summary>WHEA-Logger 18: fatal hardware error (machine check).</summary>
    WheaFatal,
    /// <summary>WHEA-Logger 17 / 19: corrected hardware error (PCIe / machine check) — survivable, noisy on some boards.</summary>
    WheaCorrected,
    /// <summary>Display 4101: the display driver stopped responding and was reset (TDR).</summary>
    DisplayTdr,
    /// <summary>Error-level events from the NVIDIA / AMD kernel-mode driver providers (nvlddmkm, amdkmdag).</summary>
    DisplayDriverError
}

public sealed record CrashEventBucket(CrashEventKind Kind, int Count, DateTime? LastUtc);

/// <summary>Counts per kind over a window; <see cref="Reason"/> is set when the log could not be read.</summary>
public sealed record CrashEventSummary
{
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public IReadOnlyList<CrashEventBucket> Buckets { get; init; } = Array.Empty<CrashEventBucket>();
    public UnavailableReason Reason { get; init; }

    public int Count(CrashEventKind kind) => Buckets.FirstOrDefault(b => b.Kind == kind)?.Count ?? 0;
    public DateTime? Last(CrashEventKind kind) => Buckets.FirstOrDefault(b => b.Kind == kind)?.LastUtc;
    public int Total => Buckets.Sum(b => b.Count);
}

/// <summary>
/// Reads crash-related records from the Windows <c>System</c> log via <see cref="EventLogReader"/> —
/// readable by standard users (the Security log is not; it is never touched). Read-only; the classifier
/// and the summary are pure.
/// </summary>
public static class CrashEventReader
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

    private const string KernelPower = "Microsoft-Windows-Kernel-Power";
    private const string Whea = "Microsoft-Windows-WHEA-Logger";
    private const string Display = "Display";
    private static readonly string[] DisplayDrivers = ["nvlddmkm", "amdkmdag", "amdkmdap", "igfx", "igfxn"];
    /// <summary>EventLog levels: 1 Critical, 2 Error, 3 Warning, 4 Information.</summary>
    private const int LevelError = 2;

    /// <summary>Which bucket a record belongs to, or null when it is noise for our purposes.</summary>
    internal static CrashEventKind? Classify(string? provider, int eventId, int level)
    {
        if (string.IsNullOrEmpty(provider)) return null;
        if (provider.Equals(KernelPower, StringComparison.OrdinalIgnoreCase))
            return eventId == 41 ? CrashEventKind.UnexpectedShutdown : null;
        if (provider.Equals(Whea, StringComparison.OrdinalIgnoreCase))
            return eventId switch
            {
                18 => CrashEventKind.WheaFatal,
                17 or 19 => CrashEventKind.WheaCorrected,
                _ => null
            };
        if (provider.Equals(Display, StringComparison.OrdinalIgnoreCase))
            return eventId == 4101 ? CrashEventKind.DisplayTdr : null;
        foreach (var d in DisplayDrivers)
            if (provider.Equals(d, StringComparison.OrdinalIgnoreCase))
                return level is >= 1 and <= LevelError ? CrashEventKind.DisplayDriverError : null;
        return null;
    }

    /// <summary>Pure: counts classified records inside (<paramref name="nowUtc"/> − <paramref name="window"/>, now].</summary>
    internal static CrashEventSummary Summarize(IEnumerable<CrashEventRecord> records, DateTime nowUtc, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(records);
        var from = nowUtc - window;
        var counts = new Dictionary<CrashEventKind, (int Count, DateTime? Last)>();
        foreach (var r in records)
        {
            if (r.TimeUtc < from || r.TimeUtc > nowUtc) continue;
            var kind = Classify(r.Provider, r.EventId, r.Level);
            if (kind == null) continue;
            counts.TryGetValue(kind.Value, out var c);
            counts[kind.Value] = (c.Count + 1, c.Last == null || r.TimeUtc > c.Last ? r.TimeUtc : c.Last);
        }
        var buckets = Enum.GetValues<CrashEventKind>()
            .Select(k => counts.TryGetValue(k, out var c) ? new CrashEventBucket(k, c.Count, c.Last) : new CrashEventBucket(k, 0, null))
            .ToList();
        return new CrashEventSummary { FromUtc = from, ToUtc = nowUtc, Buckets = buckets, Reason = UnavailableReason.None };
    }

    /// <summary>Live read of the System log (blocking, ~100 ms – a few s on a noisy log; call off the UI thread). Never throws.</summary>
    public static CrashEventSummary Read(TimeSpan? window = null)
    {
        var win = window ?? DefaultWindow;
        var now = DateTime.UtcNow;
        var records = new List<CrashEventRecord>();
        try
        {
            long ms = (long)win.TotalMilliseconds;
            string xpath =
                "*[System[(" +
                $"(Provider[@Name='{KernelPower}'] and EventID=41) or " +
                $"(Provider[@Name='{Whea}'] and (EventID=17 or EventID=18 or EventID=19)) or " +
                $"(Provider[@Name='{Display}'] and EventID=4101) or " +
                string.Join(" or ", DisplayDrivers.Select(d => $"(Provider[@Name='{d}'] and Level<={LevelError})")) +
                $") and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
            var query = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            for (int i = 0; i < 20000; i++)
            {
                using var e = reader.ReadEvent();
                if (e == null) break;
                var t = e.TimeCreated;
                if (t == null) continue;
                records.Add(new CrashEventRecord(t.Value.ToUniversalTime(), e.ProviderName ?? "", e.Id, e.Level ?? 4));
            }
        }
        catch (EventLogNotFoundException ex)
        {
            AppLog.Info("System event log not found: " + ex.Message);
            return Failed(now, win, UnavailableReason.NotSupported);
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.Info("System event log access denied: " + ex.Message);
            return Failed(now, win, UnavailableReason.AccessDenied);
        }
        catch (EventLogException ex)
        {
            AppLog.Warn(ex, "System event log");
            return Failed(now, win, UnavailableReason.QueryFailed);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "System event log");
            return Failed(now, win, UnavailableReason.QueryFailed);
        }

        var summary = Summarize(records, now, win);
        AppLog.Info($"Crash events ({win.TotalDays:0}d): shutdowns={summary.Count(CrashEventKind.UnexpectedShutdown)}, " +
                    $"whea fatal={summary.Count(CrashEventKind.WheaFatal)}, whea corrected={summary.Count(CrashEventKind.WheaCorrected)}, " +
                    $"tdr={summary.Count(CrashEventKind.DisplayTdr)}, driver errors={summary.Count(CrashEventKind.DisplayDriverError)}");
        return summary;
    }

    private static CrashEventSummary Failed(DateTime now, TimeSpan win, UnavailableReason reason) => new()
    {
        FromUtc = now - win,
        ToUtc = now,
        Buckets = Enum.GetValues<CrashEventKind>().Select(k => new CrashEventBucket(k, 0, null)).ToList(),
        Reason = reason
    };
}
