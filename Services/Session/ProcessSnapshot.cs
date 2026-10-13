using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Visor.Services;

/// <summary>
/// Cheap process-list enumeration: <c>EnumProcesses</c> for the PID list plus
/// <c>NtQuerySystemInformation(SystemProcessIdInformation)</c> for each image name — the same
/// kernel query .NET's own <see cref="System.Diagnostics.Process"/> uses for names. No
/// <see cref="System.Diagnostics.Process"/> objects, no process handles opened (anti-cheat
/// friendly), no per-process allocations: a full scan of ~300 processes costs about 0.1 ms
/// (measured on the dev PC — 50× less than Process.GetProcessesByName and a Toolhelp32 snapshot).
/// Names are exposed in <see cref="ProcessNames.Key"/> form (file name without ".exe"; casing is
/// irrelevant because every set here is OrdinalIgnoreCase).
/// </summary>
internal static class ProcessSnapshot
{
    private const int SystemProcessIdInformation = 88;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int NameBufferBytes = 4096; // NT path of the image; longer paths are skipped

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcesses([Out] uint[] lpidProcess, uint cb, out uint lpcbNeeded);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, ref SYSTEM_PROCESS_ID_INFORMATION info, int length, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_ID_INFORMATION
    {
        public IntPtr ProcessId;
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    // Scans are short (~0.1 ms) and come from a few threads (poll, UI, companions): one shared
    // set of buffers under a lock beats per-call allocation.
    private static readonly object Gate = new();
    private static uint[] _pids = new uint[1024];
    private static readonly char[] NameChars = new char[NameBufferBytes / 2];
    private static readonly IntPtr NameBuffer = Marshal.AllocHGlobal(NameBufferBytes); // lives for the process
    private static DateTime _lastFailureLogUtc = DateTime.MinValue;

    /// <summary>Return true to keep enumerating.</summary>
    public delegate bool Visitor<in TState>(TState state, uint pid, ReadOnlySpan<char> exeKey);

    /// <summary>
    /// Visits every running process whose image name is readable (pid 0 / the idle process are
    /// not). Returns false when the PID list itself could not be read (callers must then treat
    /// the result as "unknown", never as "nothing is running").
    /// </summary>
    public static bool ForEach<TState>(TState state, Visitor<TState> visit)
    {
        lock (Gate)
        {
            int count = EnumPidsLocked();
            if (count < 0) return false;
            for (int i = 0; i < count; i++)
            {
                uint pid = _pids[i];
                var key = QueryExeKeyLocked(pid);
                if (key.Length == 0) continue;
                if (!visit(state, pid, key))
                    break;
            }
            return true;
        }
    }

    /// <summary>PID list into the shared buffer (grown as needed). -1 on failure.</summary>
    private static int EnumPidsLocked()
    {
        while (true)
        {
            if (!EnumProcesses(_pids, (uint)(_pids.Length * sizeof(uint)), out uint needed))
            {
                LogFailure("EnumProcesses", Marshal.GetLastWin32Error());
                return -1;
            }
            int count = (int)(needed / sizeof(uint));
            if (count < _pids.Length) return count;
            _pids = new uint[_pids.Length * 2]; // buffer was full — may have been truncated
        }
    }

    /// <summary>
    /// Image file name of <paramref name="pid"/> (no directory, no ".exe"), as a span over the
    /// shared buffer — valid until the next query. Empty when unknown.
    /// </summary>
    private static ReadOnlySpan<char> QueryExeKeyLocked(uint pid)
    {
        if (pid == 0) return ReadOnlySpan<char>.Empty;
        var info = new SYSTEM_PROCESS_ID_INFORMATION
        {
            ProcessId = (IntPtr)pid,
            Length = 0,
            MaximumLength = NameBufferBytes,
            Buffer = NameBuffer
        };
        int status = NtQuerySystemInformation(SystemProcessIdInformation, ref info, Unsafe.SizeOf<SYSTEM_PROCESS_ID_INFORMATION>(), out _);
        if (status != 0 || info.Length == 0) return ReadOnlySpan<char>.Empty; // exited, or STATUS_INFO_LENGTH_MISMATCH (path too long)
        int chars = info.Length / 2;
        Marshal.Copy(NameBuffer, NameChars, 0, chars);
        ReadOnlySpan<char> path = NameChars.AsSpan(0, chars);
        int slash = path.LastIndexOf('\\');
        if (slash >= 0) path = path[(slash + 1)..];
        path = path.Trim();
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        return path;
    }

    /// <summary>
    /// Adds to <paramref name="result"/> the members of <paramref name="watchKeys"/> (their stored
    /// string instances — no new strings) that have at least one running instance.
    /// Both sets must use <see cref="StringComparer.OrdinalIgnoreCase"/>.
    /// </summary>
    /// <returns>False when the process list could not be read (result untouched).</returns>
    public static bool RunningKeys(HashSet<string> watchKeys, HashSet<string> result)
    {
        if (watchKeys.Count == 0) return true;
        var lookup = watchKeys.GetAlternateLookup<ReadOnlySpan<char>>();
        return ForEach((lookup, result, remaining: watchKeys.Count), static (s, _, key) =>
        {
            if (!s.lookup.TryGetValue(key, out var stored)) return true;
            if (s.result.Add(stored) && s.result.Count >= s.remaining)
                return false; // every watched exe is already running — stop early
            return true;
        });
    }

    /// <summary>True when at least one process runs with this key (ProcessNames.Key form).</summary>
    public static bool IsRunning(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        // StrongBox carries the result out of a static lambda (no closure allocation per call).
        var box = new StrongBox<bool>();
        ForEach((key, box), static (s, _, k) =>
        {
            if (!k.Equals(s.key, StringComparison.OrdinalIgnoreCase)) return true;
            s.box.Value = true;
            return false;
        });
        return box.Value;
    }

    /// <summary>True when the process with <paramref name="pid"/> has one of <paramref name="keys"/> (OrdinalIgnoreCase set). One kernel query, no enumeration.</summary>
    public static bool PidHasKey(uint pid, HashSet<string> keys)
    {
        if (pid == 0 || keys.Count == 0) return false;
        lock (Gate)
        {
            var key = QueryExeKeyLocked(pid);
            return key.Length > 0 && keys.GetAlternateLookup<ReadOnlySpan<char>>().Contains(key);
        }
    }

    /// <summary>Exe key of one process, or null when it is not running / unreadable. One kernel query.</summary>
    public static string? TryGetExeKey(uint pid)
    {
        if (pid == 0) return null;
        lock (Gate)
        {
            var key = QueryExeKeyLocked(pid);
            return key.Length > 0 ? key.ToString() : null;
        }
    }

    /// <summary>PIDs of every process whose exe key equals <paramref name="key"/>.</summary>
    public static void PidsOf(string key, HashSet<int> result)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        ForEach((key, result), static (s, pid, k) =>
        {
            if (k.Equals(s.key, StringComparison.OrdinalIgnoreCase))
                s.result.Add((int)pid);
            return true;
        });
    }

    /// <summary>Distinct exe file names ("name.exe") of all running processes — UI discovery only.</summary>
    public static SortedSet<string> AllExeNames()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        ForEach(set, static (s, _, key) =>
        {
            s.Add(string.Concat(key, ".exe"));
            return true;
        });
        return set;
    }

    private static void LogFailure(string api, int error)
    {
        var now = DateTime.UtcNow;
        if (now - _lastFailureLogUtc < TimeSpan.FromMinutes(1)) return;
        _lastFailureLogUtc = now;
        AppLog.Warn($"Process list unavailable: {api} error {error}.");
    }
}
