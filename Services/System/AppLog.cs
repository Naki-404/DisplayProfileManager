using System.IO;
using System.Reflection;
using System.Text;

namespace Visor.Services;

/// <summary>
/// Process-wide log: an in-memory ring (for the UI) plus a batched file appender.
/// Lines are buffered and written at most a second later — immediately for WARN / ERROR and at
/// exit. Every flush opens the file in append mode, writes the batch and closes it again, so a
/// second process (second instance, <c>--list</c>, <c>--emergency</c>) appending to the same file
/// can never be overwritten by a stale offset. At 2 MB the file is rotated to "Visor.log.1"
/// (two files are kept), so it can never grow without bound. Thread-safe; never throws.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly LinkedList<string> Ring = new();
    private const int RingMax = 500;
    private const long DefaultMaxLogBytes = 2 * 1024 * 1024;
    private const int MaxMessageChars = 2000;
    private const int FlushDelayMs = 1000;
    /// <summary>Lines kept while the file cannot be opened (disk full / locked) — oldest are dropped first.</summary>
    private const int MaxPendingLines = 2000;
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RotateRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static string _path = Path.Combine(ConfigService.DefaultConfigDirectory, ConfigService.LogFileName);
    /// <summary>Lines written to the ring but not yet to the file.</summary>
    private static readonly List<string> Pending = new();
    private static long _maxLogBytes = DefaultMaxLogBytes;
    private static bool _flushScheduled;
    private static DateTime _nextOpenAttemptUtc = DateTime.MinValue;
    private static DateTime _nextRotateAttemptUtc = DateTime.MinValue;
    /// <summary>One-shot (re-armed per write burst) — never ticks while nothing is logged.</summary>
    private static readonly System.Threading.Timer FlushTimer = new(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>%USERPROFILE% (C:\Users\Name) — replaced with "~" in every log line.</summary>
    private static readonly string UserProfile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');

    static AppLog()
    {
        // Last resort for paths that bypass CleanupForExit (Environment.Exit, unhandled crash).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    public static string LogFilePath => _path;

    /// <summary>Rotation threshold in bytes (2 MB; tests lower it).</summary>
    internal static long MaxLogBytes
    {
        get { lock (Gate) return _maxLogBytes; }
        set { lock (Gate) _maxLogBytes = value > 0 ? value : DefaultMaxLogBytes; }
    }

    public static void Configure(string path)
    {
        lock (Gate)
        {
            if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
                FlushLocked();             // lines logged so far belong to the old file
            _path = path;
            _nextOpenAttemptUtc = DateTime.MinValue;
            _nextRotateAttemptUtc = DateTime.MinValue;
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); }
            catch (Exception ex) { DebugWrite("AppLog.Configure: " + ex.Message); }
        }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    public static void Error(Exception ex, string context)
    {
        if (ex == null)
        {
            Error(context);
            return;
        }

        var detail = $"{context}: {ex.GetType().Name}: {ex.Message}";
        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
            detail += " | " + FlattenStack(ex.StackTrace);
        if (ex.InnerException != null)
            detail += $" | inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
        Error(detail);
    }

    public static void Warn(Exception ex, string context)
    {
        if (ex == null)
        {
            Warn(context);
            return;
        }
        Warn($"{context}: {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>One-line startup banner with version and log path.</summary>
    public static void StartupBanner()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        Info($"Visor {ver} starting — log: {_path}");
    }

    /// <summary>Writes buffered lines to disk now (called for WARN/ERROR, by the flush timer and at exit).</summary>
    public static void Flush()
    {
        lock (Gate) FlushLocked();
    }

    /// <summary>Flushes the buffer. The file is never held open, so there is nothing else to release.</summary>
    public static void Shutdown()
    {
        lock (Gate) FlushLocked();
    }

    private static string FlattenStack(string stack)
    {
        var lines = stack.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" <- ", lines.Take(6).Select(l => l.Trim()));
    }

    private static void Write(string level, string msg)
    {
        if (string.IsNullOrEmpty(msg)) msg = "(empty)";
        msg = MaskUserPaths(msg);
        // Avoid dumping huge payloads / paths that could leak secrets into shared logs.
        if (msg.Length > MaxMessageChars)
            msg = msg[..MaxMessageChars] + "…";

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}";
        lock (Gate)
        {
            Ring.AddLast(line);
            while (Ring.Count > RingMax)
                Ring.RemoveFirst();

            Pending.Add(line);
            if (Pending.Count > MaxPendingLines)
                Pending.RemoveRange(0, Pending.Count - MaxPendingLines);

            if (level != "INFO")
                FlushLocked();          // problems must be on disk even if we die next
            else
                ScheduleFlushLocked();
        }
        DebugWrite(line);
    }

    private static void ScheduleFlushLocked()
    {
        if (_flushScheduled) return;
        _flushScheduled = true;
        try { FlushTimer.Change(FlushDelayMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { _flushScheduled = false; }
    }

    /// <summary>
    /// Appends the pending batch: open (append, shared read/write), write, close. The offset is
    /// taken from the file at open time, so lines another process appended meanwhile are kept.
    /// </summary>
    private static void FlushLocked()
    {
        _flushScheduled = false;
        if (Pending.Count == 0) return;
        if (DateTime.UtcNow < _nextOpenAttemptUtc) return;

        long length;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096);
            using (var writer = new StreamWriter(fs, Utf8NoBom, 16 * 1024, leaveOpen: true))
            {
                foreach (var line in Pending)
                    writer.WriteLine(line);
                writer.Flush();
            }
            length = fs.Length;
            Pending.Clear();
        }
        catch (Exception ex)
        {
            // Disk full / file locked — keep the pending lines and retry later. A batch that partly
            // reached the file is re-appended (duplicates are preferable to losing lines).
            DebugWrite("AppLog flush failed: " + ex.Message);
            _nextOpenAttemptUtc = DateTime.UtcNow + OpenRetryDelay;
            return;
        }

        if (length >= _maxLogBytes)
            RotateLocked();
    }

    /// <summary>Visor.log → Visor.log.1 (replacing the previous .1); the next flush creates a fresh file.</summary>
    private static void RotateLocked()
    {
        if (DateTime.UtcNow < _nextRotateAttemptUtc) return;
        try
        {
            File.Move(_path, _path + ".1", overwrite: true);
        }
        catch (Exception ex)
        {
            // Another process holds it — keep appending, try again in a minute.
            DebugWrite("AppLog rotate failed: " + ex.Message);
            _nextRotateAttemptUtc = DateTime.UtcNow + RotateRetryDelay;
        }
    }

    /// <summary>Keeps personal profile paths (and the user name in them) out of shared logs.</summary>
    internal static string MaskUserPaths(string msg)
    {
        if (UserProfile.Length < 4) return msg;
        return msg.Replace(UserProfile, "~", StringComparison.OrdinalIgnoreCase);
    }

    private static void DebugWrite(string line)
    {
        System.Diagnostics.Debug.WriteLine(line);
    }

    public static IReadOnlyList<string> Tail(int lines = 200)
    {
        lock (Gate)
        {
            if (Ring.Count > 0)
            {
                if (Ring.Count <= lines) return Ring.ToArray();
                return Ring.Skip(Ring.Count - lines).ToArray();
            }

            FlushLocked();
            if (!File.Exists(_path)) return Array.Empty<string>();
            return ReadLastLines(_path, lines);
        }
    }

    private static string[] ReadLastLines(string path, int lines)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0) return Array.Empty<string>();

            long take = Math.Min(fs.Length, 256 * 1024);
            fs.Seek(-take, SeekOrigin.End);
            using var reader = new StreamReader(fs);
            var text = reader.ReadToEnd();
            var all = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (all.Length <= lines) return all;
            return all.Skip(all.Length - lines).ToArray();
        }
        catch (Exception ex)
        {
            DebugWrite("AppLog tail failed: " + ex.Message);
            return Array.Empty<string>();
        }
    }
}
