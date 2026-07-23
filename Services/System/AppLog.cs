using System.IO;
using System.Reflection;

namespace DisplayProfileManager.Services;

public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly LinkedList<string> Ring = new();
    private const int RingMax = 500;
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private const int MaxMessageChars = 2000;

    private static string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DisplayProfileManager",
        "DisplayProfileManager.log");

    public static string LogFilePath => _path;

    public static void Configure(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
        Info($"Display Profile Manager {ver} starting — log: {_path}");
    }

    private static string FlattenStack(string stack)
    {
        var lines = stack.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" <- ", lines.Take(6).Select(l => l.Trim()));
    }

    private static void Write(string level, string msg)
    {
        if (string.IsNullOrEmpty(msg)) msg = "(empty)";
        // Avoid dumping huge payloads / paths that could leak secrets into shared logs.
        if (msg.Length > MaxMessageChars)
            msg = msg[..MaxMessageChars] + "…";

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}";
        lock (Gate)
        {
            Ring.AddLast(line);
            while (Ring.Count > RingMax)
                Ring.RemoveFirst();

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, line + Environment.NewLine);
                TryTrimLogFile();
            }
            catch
            {
                /* disk full / locked — keep ring only */
            }
        }
        DebugWrite(line);
    }

    private static void DebugWrite(string line)
    {
        System.Diagnostics.Debug.WriteLine(line);
    }

    private static void TryTrimLogFile()
    {
        try
        {
            var fi = new FileInfo(_path);
            if (!fi.Exists || fi.Length < MaxLogBytes) return;
            var lines = File.ReadAllLines(_path);
            var keep = lines.Skip(Math.Max(0, lines.Length - 800)).ToArray();
            File.WriteAllLines(_path, keep);
        }
        catch { }
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
        catch
        {
            return Array.Empty<string>();
        }
    }
}
