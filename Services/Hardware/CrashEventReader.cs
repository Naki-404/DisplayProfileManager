using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

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

/// <summary>One System-log record with its rendered text (injectable for tests). <see cref="Message"/> null = not renderable.</summary>
public readonly record struct CrashEventRawDetail(DateTime TimeUtc, string Provider, int EventId, int Level, string? Message);

/// <summary>A classified event for the in-app list. <see cref="Message"/> is sanitized (profile paths masked) and truncated.</summary>
public sealed record CrashEventDetail(DateTime TimeUtc, CrashEventKind Kind, string Provider, int EventId, int Level, string Message)
{
    /// <summary>First non-empty line of the message ("" when there is none) — the collapsed row text.</summary>
    public string FirstLine => CrashEventReader.FirstLine(Message);
}

/// <summary>Newest-first event list over a window; <see cref="Reason"/> is set when the log could not be read.</summary>
public sealed record CrashEventList
{
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public IReadOnlyList<CrashEventDetail> Events { get; init; } = Array.Empty<CrashEventDetail>();
    public UnavailableReason Reason { get; init; }
    /// <summary>True when more matching records existed than the requested maximum.</summary>
    public bool Truncated { get; init; }
}

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
            var query = new EventLogQuery("System", PathType.LogName, BuildXPath(win)) { ReverseDirection = true };
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

    // ---------------------------------------------------------------- in-app event list

    public const int DefaultRecentCount = 50;
    /// <summary>Longest message kept per event (characters); longer texts end with "…".</summary>
    public const int MaxMessageChars = 2000;
    public const string ProfileToken = "%USERPROFILE%";

    /// <summary>"X:\Users\name" (any drive, any account) — masked even when it is another account's profile.</summary>
    private static readonly Regex UsersPath = new(@"\b[A-Za-z]:\\Users\\[^\\/:*?""<>|\s]+", RegexOptions.CultureInvariant);
    private static readonly Regex BlankLines = new(@"(\r?\n[ \t]*){3,}", RegexOptions.CultureInvariant);

    /// <summary>
    /// Message as shown / copied: profile paths masked (<paramref name="userProfile"/> exactly, and any
    /// <c>X:\Users\&lt;name&gt;</c>), runs of blank lines collapsed, trimmed, truncated to <paramref name="maxChars"/>.
    /// </summary>
    public static string SanitizeMessage(string? message, string? userProfile, int maxChars = MaxMessageChars)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        string m = message.Replace("\0", "");
        if (!string.IsNullOrEmpty(userProfile) && userProfile.Length > 3)
            m = Regex.Replace(m, Regex.Escape(userProfile.TrimEnd('\\')), ProfileToken.Replace("$", "$$"),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        m = UsersPath.Replace(m, ProfileToken.Replace("$", "$$"));
        m = BlankLines.Replace(m, Environment.NewLine + Environment.NewLine).Trim();
        if (maxChars > 1 && m.Length > maxChars) m = m[..(maxChars - 1)].TrimEnd() + "…";
        return m;
    }

    /// <summary>First non-empty line, trimmed ("" when there is none).</summary>
    public static string FirstLine(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        foreach (var line in message.Split('\n'))
        {
            string t = line.Trim();
            if (t.Length > 0) return t;
        }
        return "";
    }

    /// <summary>
    /// Pure: classified records inside (<paramref name="nowUtc"/> − <paramref name="window"/>, now], newest first,
    /// at most <paramref name="max"/>, messages sanitized. Unclassified records are dropped.
    /// </summary>
    internal static CrashEventList BuildList(IEnumerable<CrashEventRawDetail> records, DateTime nowUtc, TimeSpan window,
        int max, string? userProfile)
    {
        ArgumentNullException.ThrowIfNull(records);
        var from = nowUtc - window;
        var matching = new List<(CrashEventRawDetail Raw, CrashEventKind Kind)>();
        foreach (var r in records)
        {
            if (r.TimeUtc < from || r.TimeUtc > nowUtc) continue;
            var kind = Classify(r.Provider, r.EventId, r.Level);
            if (kind != null) matching.Add((r, kind.Value));
        }
        var ordered = matching.OrderByDescending(x => x.Raw.TimeUtc).ToList();
        int take = Math.Max(0, max);
        var events = ordered.Take(take)
            .Select(x => new CrashEventDetail(x.Raw.TimeUtc, x.Kind, x.Raw.Provider ?? "", x.Raw.EventId, x.Raw.Level,
                SanitizeMessage(x.Raw.Message, userProfile)))
            .ToList();
        return new CrashEventList
        {
            FromUtc = from,
            ToUtc = nowUtc,
            Events = events,
            Reason = UnavailableReason.None,
            Truncated = ordered.Count > take
        };
    }

    /// <summary>
    /// Live read of the newest <paramref name="max"/> crash-related records with their rendered text
    /// (<see cref="EventRecord.FormatDescription()"/>; blocking — call off the UI thread). Never throws.
    /// </summary>
    public static CrashEventList ReadRecent(int max = DefaultRecentCount, TimeSpan? window = null)
    {
        var win = window ?? DefaultWindow;
        var now = DateTime.UtcNow;
        max = Math.Clamp(max, 1, 500);
        var records = new List<CrashEventRawDetail>();
        string? profile;
        try { profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        catch (Exception) { profile = null; }
        try
        {
            var query = new EventLogQuery("System", PathType.LogName, BuildXPath(win)) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            // One extra record only tells "there are more" — its text is never rendered.
            for (int i = 0; i <= max; i++)
            {
                using var e = reader.ReadEvent();
                if (e == null) break;
                var t = e.TimeCreated;
                if (t == null) continue;
                string? text = i < max ? Describe(e) : null;
                records.Add(new CrashEventRawDetail(t.Value.ToUniversalTime(), e.ProviderName ?? "", e.Id, e.Level ?? 4, text));
            }
        }
        catch (EventLogNotFoundException ex)
        {
            AppLog.Info("System event log not found: " + ex.Message);
            return new CrashEventList { FromUtc = now - win, ToUtc = now, Reason = UnavailableReason.NotSupported };
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.Info("System event log access denied: " + ex.Message);
            return new CrashEventList { FromUtc = now - win, ToUtc = now, Reason = UnavailableReason.AccessDenied };
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "System event log (list)");
            return new CrashEventList { FromUtc = now - win, ToUtc = now, Reason = UnavailableReason.QueryFailed };
        }
        return BuildList(records, now, win, max, profile);
    }

    /// <summary>Rendered message in the UI language; null when the provider's message resources are missing.</summary>
    private static string? Describe(EventRecord e)
    {
        try { return e.FormatDescription(); }
        catch (Exception) { return null; }
    }

    private static string BuildXPath(TimeSpan window)
    {
        long ms = (long)window.TotalMilliseconds;
        return "*[System[(" +
               $"(Provider[@Name='{KernelPower}'] and EventID=41) or " +
               $"(Provider[@Name='{Whea}'] and (EventID=17 or EventID=18 or EventID=19)) or " +
               $"(Provider[@Name='{Display}'] and EventID=4101) or " +
               string.Join(" or ", DisplayDrivers.Select(d => $"(Provider[@Name='{d}'] and Level<={LevelError})")) +
               $") and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
    }

    /// <summary>Events of one kind (null = all), order kept.</summary>
    public static IReadOnlyList<CrashEventDetail> Filter(IEnumerable<CrashEventDetail> events, CrashEventKind? kind) =>
        events.Where(e => kind == null || e.Kind == kind.Value).ToList();

    /// <summary>Kinds that occur in <paramref name="events"/>, in enum order (the filter strip shows only these).</summary>
    public static IReadOnlyList<CrashEventKind> KindsPresent(IEnumerable<CrashEventDetail> events)
    {
        var present = events.Select(e => e.Kind).ToHashSet();
        return Enum.GetValues<CrashEventKind>().Where(present.Contains).ToList();
    }

    /// <summary>Loc key of the short kind label: hw.events.kind.unexpectedShutdown, …</summary>
    public static string KindLocKey(CrashEventKind kind)
    {
        string n = kind.ToString();
        return "hw.events.kind." + char.ToLowerInvariant(n[0]) + n[1..];
    }

    /// <summary>EventLog level name (invariant, for the support text).</summary>
    public static string LevelName(int level) => level switch
    {
        1 => "Critical",
        2 => "Error",
        3 => "Warning",
        0 or 4 => "Information",
        5 => "Verbose",
        _ => "Level " + level.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>
    /// Plain-text report for support (invariant English, local time with UTC offset, newest first). Messages are the
    /// already-sanitized ones; nothing else about the machine is included.
    /// </summary>
    public static string BuildSupportReport(IEnumerable<CrashEventDetail> events, DateTime nowUtc, TimeSpan window,
        TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        zone ??= TimeZoneInfo.Local;
        var list = events.OrderByDescending(e => e.TimeUtc).ToList();
        var sb = new StringBuilder();
        sb.Append("Visor - stability events from the Windows System log (last ")
          .Append(((int)Math.Round(window.TotalDays)).ToString(CultureInfo.InvariantCulture))
          .Append(" days), generated ").Append(FormatTime(nowUtc, zone)).AppendLine();
        sb.Append("Events: ").Append(list.Count.ToString(CultureInfo.InvariantCulture)).AppendLine();
        foreach (var e in list)
        {
            sb.AppendLine();
            sb.Append('[').Append(FormatTime(e.TimeUtc, zone)).Append("] ")
              .Append(e.Kind.ToString()).Append(" | ").Append(e.Provider)
              .Append(" | Event ID ").Append(e.EventId.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(LevelName(e.Level)).AppendLine();
            if (e.Message.Length > 0)
            {
                foreach (var line in e.Message.Replace("\r\n", "\n").Split('\n'))
                    sb.Append("    ").AppendLine(line.TrimEnd());
            }
            else sb.AppendLine("    (no description)");
        }
        return sb.ToString();
    }

    private static string FormatTime(DateTime utc, TimeZoneInfo zone)
    {
        var u = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(u, zone);
        var offset = zone.GetUtcOffset(u);
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
               + " UTC" + sign + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }

    private static CrashEventSummary Failed(DateTime now, TimeSpan win, UnavailableReason reason) => new()
    {
        FromUtc = now - win,
        ToUtc = now,
        Buckets = Enum.GetValues<CrashEventKind>().Select(k => new CrashEventBucket(k, 0, null)).ToList(),
        Reason = reason
    };
}
