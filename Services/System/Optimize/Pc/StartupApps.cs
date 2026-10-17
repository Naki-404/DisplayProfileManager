using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Visor.Services.Elevation;

namespace Visor.Services.Optimize.Pc;

/// <summary>Where a startup entry is registered. Each source maps to one <c>StartupApproved</c> key.</summary>
public enum StartupLocation
{
    /// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run → HKCU StartupApproved\Run (no admin).</summary>
    UserRun,
    /// <summary>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run → HKLM StartupApproved\Run (elevated helper).</summary>
    MachineRun,
    /// <summary>HKLM\SOFTWARE\WOW6432Node\...\Run → HKLM StartupApproved\Run32 (elevated helper).</summary>
    MachineRun32,
    /// <summary>shell:startup → HKCU StartupApproved\StartupFolder (no admin).</summary>
    UserStartupFolder,
    /// <summary>shell:common startup → HKLM StartupApproved\StartupFolder (elevated helper).</summary>
    CommonStartupFolder
}

/// <summary>Why an entry has no enable / disable toggle.</summary>
public enum StartupToggleBlock
{
    None,
    /// <summary>No elevation broker was supplied (HKLM toggles impossible in this build / test).</summary>
    NoBroker
}

/// <summary>One program Windows starts at sign-in. Size is the only "impact" Visor shows — no invented scores.</summary>
public sealed record StartupEntry
{
    public required StartupLocation Location { get; init; }
    /// <summary>Run value name, or the file name in the Startup folder (e.g. "Discord.lnk") — the StartupApproved value name.</summary>
    public required string Name { get; init; }
    /// <summary>Name for the list (Run value name, or the file name without extension).</summary>
    public required string DisplayName { get; init; }
    /// <summary>Raw command line (Run) or "target args" of a shortcut / the file path.</summary>
    public required string Command { get; init; }
    /// <summary>Executable the command starts, when it could be determined (local paths only).</summary>
    public string? ExecutablePath { get; init; }
    /// <summary>FileVersionInfo.CompanyName of <see cref="ExecutablePath"/>; null when unknown.</summary>
    public string? Publisher { get; init; }
    /// <summary>Size of <see cref="ExecutablePath"/> in bytes; null when the file is missing / unreadable.</summary>
    public long? FileSizeBytes { get; init; }
    public bool Enabled { get; init; }
    /// <summary>Exact StartupApproved bytes as hex, or null when the value is absent (= enabled).</summary>
    public string? ApprovedHex { get; init; }
    public StartupToggleBlock ToggleBlock { get; init; }
    public bool CanToggle => ToggleBlock == StartupToggleBlock.None;
    /// <summary>True when toggling goes through the elevated helper (one UAC prompt).</summary>
    public bool RequiresElevation => StartupApproved.IsMachine(Location);
    public string DisableActionId => StartupToggle.ActionIdFor(false, Location, Name);
    public string EnableActionId => StartupToggle.ActionIdFor(true, Location, Name);
    public string JournalKey => StartupToggle.JournalKeyFor(Location, Name);
}

/// <summary>
/// Pure helpers for the Explorer <c>StartupApproved</c> REG_BINARY values (the switch Task Manager's
/// Startup tab writes). Verified on Windows 11: <c>02 00…00</c> (12 bytes) = enabled, <c>03 00 00 00</c> +
/// FILETIME of the change = disabled (07 / 06 also occur — the low bit is the "disabled" flag).
/// An absent value means enabled.
/// </summary>
public static class StartupApproved
{
    public const string UserBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    public const string MachineBase = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    public const string UserRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string MachineRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    public const string MachineRun32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsMachine(StartupLocation location) =>
        location is StartupLocation.MachineRun or StartupLocation.MachineRun32 or StartupLocation.CommonStartupFolder;

    /// <summary>StartupApproved key (relative to its hive) for a location.</summary>
    public static string ApprovedKey(StartupLocation location) => location switch
    {
        StartupLocation.UserRun => UserBase + @"\Run",
        StartupLocation.UserStartupFolder => UserBase + @"\StartupFolder",
        StartupLocation.MachineRun => ElevationAllowlist.StartupApprovedRunKey,
        StartupLocation.MachineRun32 => ElevationAllowlist.StartupApprovedRun32Key,
        StartupLocation.CommonStartupFolder => ElevationAllowlist.StartupApprovedFolderKey,
        _ => throw new ArgumentOutOfRangeException(nameof(location))
    };

    /// <summary>Absent / empty = enabled; otherwise the low bit of the first byte means disabled.</summary>
    public static bool IsEnabled(byte[]? data) => data == null || data.Length == 0 || (data[0] & 1) == 0;

    public static bool IsEnabled(RegValue? value)
    {
        if (value == null) return true;
        if (value.Kind != RegValueKind.Binary) return true; // foreign kind: Explorer ignores it → treated as enabled
        return IsEnabled(Convert.FromHexString(value.Data));
    }

    /// <summary>What Task Manager writes on "Enable": 02 followed by 11 zero bytes.</summary>
    public static byte[] EnabledBytes()
    {
        var b = new byte[12];
        b[0] = 0x02;
        return b;
    }

    /// <summary>What Task Manager writes on "Disable": 03 00 00 00 + FILETIME (UTC, little-endian) of the change.</summary>
    public static byte[] DisabledBytes(DateTime utc)
    {
        var b = new byte[12];
        b[0] = 0x03;
        long ft = utc.Kind == DateTimeKind.Local ? utc.ToFileTimeUtc() : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToFileTimeUtc();
        BitConverter.GetBytes(ft).CopyTo(b, 4);
        return b;
    }

    /// <summary>The disable time a disabled value carries (bytes 4..11), or null.</summary>
    public static DateTime? DisabledAtUtc(byte[]? data)
    {
        if (data == null || data.Length < 12 || IsEnabled(data)) return null;
        long ft = BitConverter.ToInt64(data, 4);
        if (ft <= 0) return null;
        try { return DateTime.FromFileTimeUtc(ft); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}

/// <summary>Action-id grammar for the per-entry toggles: <c>startup.disable|UserRun|Discord</c>.</summary>
public static class StartupToggle
{
    public const string DisablePrefix = "startup.disable|";
    public const string EnablePrefix = "startup.enable|";
    public const string JournalPrefix = "startup|";
    /// <summary>All toggles share one Loc prefix (the entry name is a format arg, not part of the key).</summary>
    public const string LocPrefix = "optimize.startup";

    public static string ActionIdFor(bool enable, StartupLocation location, string name) =>
        (enable ? EnablePrefix : DisablePrefix) + location + "|" + name;

    public static string JournalKeyFor(StartupLocation location, string name) => JournalPrefix + location + "|" + name;

    public static bool TryParseActionId(string? actionId, out bool enable, out StartupLocation location, out string name)
    {
        enable = false;
        location = default;
        name = "";
        if (string.IsNullOrEmpty(actionId)) return false;
        string rest;
        if (actionId.StartsWith(DisablePrefix, StringComparison.OrdinalIgnoreCase)) rest = actionId[DisablePrefix.Length..];
        else if (actionId.StartsWith(EnablePrefix, StringComparison.OrdinalIgnoreCase))
        {
            enable = true;
            rest = actionId[EnablePrefix.Length..];
        }
        else return false;
        int bar = rest.IndexOf('|');
        if (bar <= 0 || bar == rest.Length - 1) return false;
        if (!Enum.TryParse(rest[..bar], ignoreCase: false, out location) || !Enum.IsDefined(location)) return false;
        name = rest[(bar + 1)..];
        return name.Length > 0 && name.IndexOf('\0') < 0;
    }
}

/// <summary>Pure command-line helpers for Run values.</summary>
public static class StartupCommand
{
    /// <summary>
    /// Executable of a Run command: the quoted first token, else everything up to the first ".exe"
    /// followed by whitespace / end (unquoted paths with spaces), else the first whitespace token.
    /// Environment variables are expanded with <paramref name="expand"/> (default: the process environment).
    /// </summary>
    public static string? ExtractExecutable(string? command, Func<string, string>? expand = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        expand ??= Environment.ExpandEnvironmentVariables;
        string s = expand(command.Trim()).Trim();
        if (s.Length == 0) return null;
        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            string q = end > 1 ? s[1..end] : s[1..];
            return q.Trim().Length == 0 ? null : q.Trim();
        }
        int idx = 0;
        while (true)
        {
            int exe = s.IndexOf(".exe", idx, StringComparison.OrdinalIgnoreCase);
            if (exe < 0) break;
            int after = exe + 4;
            if (after == s.Length || char.IsWhiteSpace(s[after]) || s[after] == ',')
                return s[..after];
            idx = after;
        }
        int ws = 0;
        while (ws < s.Length && !char.IsWhiteSpace(s[ws])) ws++;
        return s[..ws];
    }

    /// <summary>"C:\…" on a local drive (no UNC / device paths) — the only paths Visor stats or reads version info from.</summary>
    public static bool IsLocalAbsolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 4) return false;
        if (!char.IsAsciiLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/')) return false;
        if (path.IndexOf(':', 2) >= 0) return false;
        return path.IndexOfAny(Path.GetInvalidPathChars()) < 0;
    }
}

/// <summary>Read-only registry seam for listing Run / StartupApproved values in either hive.</summary>
public interface IStartupRegistryReader
{
    IReadOnlyList<string> GetValueNames(bool machine, string keyPath);

    /// <summary>The value as stored, or null when absent. REG_SZ / REG_EXPAND_SZ come back as <see cref="RegValueKind.String"/> (unexpanded).</summary>
    RegValue? GetValue(bool machine, string keyPath, string valueName);
}

/// <summary>Real reader: HKCU and HKLM (64-bit view), read-only.</summary>
public sealed class WindowsStartupRegistryReader : IStartupRegistryReader
{
    public static readonly WindowsStartupRegistryReader Instance = new();

    private static RegistryKey? Open(bool machine, string keyPath)
    {
        using var root = RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
        return root.OpenSubKey(keyPath, writable: false);
    }

    public IReadOnlyList<string> GetValueNames(bool machine, string keyPath)
    {
        using var key = Open(machine, keyPath);
        return key == null ? Array.Empty<string>() : key.GetValueNames().Where(n => n.Length > 0).ToArray();
    }

    public RegValue? GetValue(bool machine, string keyPath, string valueName)
    {
        using var key = Open(machine, keyPath);
        if (key == null) return null;
        var raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw == null) return null;
        return key.GetValueKind(valueName) switch
        {
            RegistryValueKind.DWord => RegValue.Dword(Convert.ToInt32(raw, CultureInfo.InvariantCulture)),
            RegistryValueKind.String or RegistryValueKind.ExpandString =>
                RegValue.String(raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture) ?? ""),
            RegistryValueKind.Binary => RegValue.Binary(raw as byte[] ?? Array.Empty<byte>()),
            _ => new RegValue(RegValueKind.Other, Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "")
        };
    }
}

/// <summary>File facts for the list. Tests inject a fake; nothing here writes.</summary>
public interface IStartupFileProbe
{
    /// <summary>Size + CompanyName of a local file; nulls when missing / unreadable.</summary>
    (long? Size, string? Company) Describe(string path);

    /// <summary>Target path + arguments of a .lnk (raw, env vars expanded), or null.</summary>
    (string Target, string Arguments)? ResolveShortcut(string lnkPath);
}

public sealed class WindowsStartupFileProbe : IStartupFileProbe
{
    public static readonly WindowsStartupFileProbe Instance = new();

    public (long? Size, string? Company) Describe(string path)
    {
        try
        {
            string p = path;
            if (!Path.IsPathRooted(p) && p.IndexOfAny(new[] { '\\', '/' }) < 0)
                p = Path.Combine(Environment.SystemDirectory, p); // "ctfmon.exe" style entries
            if (!StartupCommand.IsLocalAbsolute(p)) return (null, null);
            var root = Path.GetPathRoot(p);
            if (!string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network) return (null, null);
            var fi = new FileInfo(p);
            if (!fi.Exists) return (null, null);
            string? company = null;
            try
            {
                company = FileVersionInfo.GetVersionInfo(fi.FullName).CompanyName?.Trim();
                if (string.IsNullOrEmpty(company)) company = null;
            }
            catch (Exception) { /* no version resource */ }
            return (fi.Length, company);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    public (string Target, string Arguments)? ResolveShortcut(string lnkPath)
    {
        object? link = null;
        try
        {
            link = new ShellLinkCoClass();
            ((IPersistFile)link).Load(lnkPath, 0 /* STGM_READ */);
            var sl = (IShellLinkW)link;
            var path = new StringBuilder(1024);
            sl.GetPath(path, path.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH: no resolution, no network */);
            var args = new StringBuilder(2048);
            sl.GetArguments(args, args.Capacity);
            string target = Environment.ExpandEnvironmentVariables(path.ToString());
            return target.Length == 0 ? null : (target, args.ToString());
        }
        catch (Exception ex)
        {
            AppLog.Warn("Startup shortcut " + Path.GetFileName(lnkPath) + ": " + ex.Message);
            return null;
        }
        finally
        {
            if (link != null) Marshal.ReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    }
}

/// <summary>
/// HKCU StartupApproved toggle (Run or StartupFolder value) — no admin. State is the exact value
/// (bytes or absent), so undo restores it byte-for-byte or deletes it. Run entries are never touched.
/// </summary>
public sealed class StartupToggleTarget : RegistrySafeActionTarget
{
    private readonly RegAddress[] _addresses;
    private readonly Func<DateTime> _utcNow;

    public StartupToggleTarget(bool enable, StartupLocation location, string name,
        IOptimizeRegistry? registry = null, Func<DateTime>? utcNow = null) : base(registry)
    {
        if (StartupApproved.IsMachine(location)) throw new ArgumentException("HKCU locations only", nameof(location));
        ArgumentException.ThrowIfNullOrEmpty(name);
        Enable = enable;
        Location = location;
        Name = name;
        _addresses = new[] { new RegAddress(StartupApproved.ApprovedKey(location), name) };
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool Enable { get; }
    public StartupLocation Location { get; }
    public string Name { get; }

    public override string ActionId => StartupToggle.ActionIdFor(Enable, Location, Name);
    public override string JournalKey => StartupToggle.JournalKeyFor(Location, Name);
    public override string LocPrefix => StartupToggle.LocPrefix;
    protected override IReadOnlyList<RegAddress> Addresses => _addresses;
    protected override string Label => "Startup approval " + Name;

    protected override bool Satisfies(RegistryValueSet current) =>
        StartupApproved.IsEnabled(current.Get(_addresses[0])) == Enable;

    protected override RegistryValueSet BuildTarget(RegistryValueSet current) =>
        new RegistryValueSet().Add(_addresses[0],
            RegValue.Binary(Enable ? StartupApproved.EnabledBytes() : StartupApproved.DisabledBytes(_utcNow())));
}

/// <summary>
/// HKLM StartupApproved toggle (Run, Run32 or the all-users StartupFolder value) through the elevated
/// helper (one UAC prompt per apply / undo). All three keys are in <see cref="ElevationAllowlist"/>.
/// </summary>
public sealed class ElevatedStartupToggleTarget : ElevatedRegistrySafeActionTarget
{
    private readonly RegAddress[] _addresses;
    private readonly Func<DateTime> _utcNow;

    /// <summary>HKLM Run entry (the original shape).</summary>
    public ElevatedStartupToggleTarget(bool enable, string name, IElevationBroker broker,
        IMachineRegistryReader? registry = null, Func<DateTime>? utcNow = null)
        : this(enable, StartupLocation.MachineRun, name, broker, registry, utcNow) { }

    /// <param name="location">A machine location: <see cref="StartupLocation.MachineRun"/>,
    /// <see cref="StartupLocation.MachineRun32"/> or <see cref="StartupLocation.CommonStartupFolder"/>.</param>
    public ElevatedStartupToggleTarget(bool enable, StartupLocation location, string name, IElevationBroker broker,
        IMachineRegistryReader? registry = null, Func<DateTime>? utcNow = null) : base(broker, registry)
    {
        if (!StartupApproved.IsMachine(location)) throw new ArgumentException("HKLM locations only", nameof(location));
        ArgumentException.ThrowIfNullOrEmpty(name);
        Enable = enable;
        Location = location;
        Name = name;
        _addresses = new[] { new RegAddress(StartupApproved.ApprovedKey(location), name) };
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool Enable { get; }
    public StartupLocation Location { get; }
    public string Name { get; }

    public override string ActionId => StartupToggle.ActionIdFor(Enable, Location, Name);
    public override string JournalKey => StartupToggle.JournalKeyFor(Location, Name);
    public override string LocPrefix => StartupToggle.LocPrefix;
    protected override IReadOnlyList<RegAddress> Addresses => _addresses;
    protected override string Label => "Startup approval (HKLM " + Location + ") " + Name;

    protected override bool Satisfies(RegistryValueSet current) =>
        StartupApproved.IsEnabled(current.Get(_addresses[0])) == Enable;

    protected override RegistryValueSet BuildTarget(RegistryValueSet current) =>
        new RegistryValueSet().Add(_addresses[0],
            RegValue.Binary(Enable ? StartupApproved.EnabledBytes() : StartupApproved.DisabledBytes(_utcNow())));
}

/// <summary>Counts for the recommendation engine.</summary>
public sealed record StartupSummary(int Total, int Enabled, IReadOnlyList<StartupEntry> EnabledEntries);

/// <summary>
/// Lists sign-in programs (HKCU / HKLM Run, WOW6432Node Run, user + common Startup folders) with their
/// StartupApproved state, and builds the per-entry enable / disable SafeAction targets. Never deletes
/// a Run value or a Startup-folder file — disabling only flips the StartupApproved flag (exactly what
/// Task Manager does), journaled with the exact previous bytes.
/// </summary>
public sealed class StartupAppsService
{
    private readonly IStartupRegistryReader _reader;
    private readonly IStartupFileProbe _probe;
    private readonly string? _userFolder;
    private readonly string? _commonFolder;
    private readonly IOptimizeRegistry? _hkcu;
    private readonly IElevationBroker? _broker;
    private readonly IMachineRegistryReader? _hklm;
    private readonly Func<DateTime>? _utcNow;

    /// <param name="userStartupFolder">Default: <c>shell:startup</c>. Pass "" to skip.</param>
    /// <param name="commonStartupFolder">Default: <c>shell:common startup</c>. Pass "" to skip.</param>
    /// <param name="broker">Needed only for HKLM toggles (Run, Run32, all-users Startup folder); null → those entries are read-only.</param>
    public StartupAppsService(IStartupRegistryReader? reader = null, IStartupFileProbe? probe = null,
        string? userStartupFolder = null, string? commonStartupFolder = null,
        IOptimizeRegistry? hkcu = null, IElevationBroker? broker = null, IMachineRegistryReader? hklm = null,
        Func<DateTime>? utcNow = null)
    {
        _reader = reader ?? WindowsStartupRegistryReader.Instance;
        _probe = probe ?? WindowsStartupFileProbe.Instance;
        _userFolder = userStartupFolder ?? SafeFolder(Environment.SpecialFolder.Startup);
        _commonFolder = commonStartupFolder ?? SafeFolder(Environment.SpecialFolder.CommonStartup);
        _hkcu = hkcu;
        _broker = broker;
        _hklm = hklm;
        _utcNow = utcNow;
    }

    private static string? SafeFolder(Environment.SpecialFolder f)
    {
        try { return Environment.GetFolderPath(f); }
        catch (Exception) { return null; }
    }

    /// <summary>Every entry, sorted by display name. Read-only; never throws (unreadable sources are skipped and logged).</summary>
    public IReadOnlyList<StartupEntry> List()
    {
        var list = new List<StartupEntry>();
        AddRun(list, StartupLocation.UserRun, machine: false, StartupApproved.UserRunKey);
        AddRun(list, StartupLocation.MachineRun, machine: true, StartupApproved.MachineRunKey);
        AddRun(list, StartupLocation.MachineRun32, machine: true, StartupApproved.MachineRun32Key);
        AddFolder(list, StartupLocation.UserStartupFolder, _userFolder);
        AddFolder(list, StartupLocation.CommonStartupFolder, _commonFolder);
        return list
            .OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Location)
            .ToList();
    }

    public static StartupSummary Summarize(IReadOnlyList<StartupEntry> entries)
    {
        var enabled = entries.Where(e => e.Enabled).ToList();
        return new StartupSummary(entries.Count, enabled.Count, enabled);
    }

    private void AddRun(List<StartupEntry> list, StartupLocation location, bool machine, string runKey)
    {
        IReadOnlyList<string> names;
        try { names = _reader.GetValueNames(machine, runKey); }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Startup list " + location);
            return;
        }
        foreach (var name in names)
        {
            try
            {
                var v = _reader.GetValue(machine, runKey, name);
                if (v == null || v.Kind is not (RegValueKind.String or RegValueKind.Other)) continue;
                string command = v.Data;
                if (string.IsNullOrWhiteSpace(command)) continue;
                string? exe = StartupCommand.ExtractExecutable(command);
                list.Add(Build(location, name, name, command, exe));
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "Startup entry " + name);
            }
        }
    }

    private void AddFolder(List<StartupEntry> list, StartupLocation location, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        IEnumerable<FileInfo> files;
        try
        {
            var di = new DirectoryInfo(folder);
            if (!di.Exists) return;
            files = di.EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false })
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Startup folder " + location);
            return;
        }
        foreach (var f in files)
        {
            if (string.Equals(f.Name, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            if ((f.Attributes & FileAttributes.Hidden) != 0 && (f.Attributes & FileAttributes.System) != 0) continue;
            string command = f.FullName;
            string? exe = f.FullName;
            if (string.Equals(f.Extension, ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var target = _probe.ResolveShortcut(f.FullName);
                if (target is { } t)
                {
                    exe = t.Target;
                    command = string.IsNullOrWhiteSpace(t.Arguments) ? "\"" + t.Target + "\"" : "\"" + t.Target + "\" " + t.Arguments;
                }
                else exe = null;
            }
            list.Add(Build(location, f.Name, Path.GetFileNameWithoutExtension(f.Name), command, exe));
        }
    }

    private StartupEntry Build(StartupLocation location, string name, string display, string command, string? exe)
    {
        RegValue? approved = null;
        try { approved = _reader.GetValue(StartupApproved.IsMachine(location), StartupApproved.ApprovedKey(location), name); }
        catch (Exception ex) { AppLog.Warn(ex, "StartupApproved " + name); }

        long? size = null;
        string? company = null;
        if (!string.IsNullOrWhiteSpace(exe))
        {
            try { (size, company) = _probe.Describe(exe); }
            catch (Exception) { /* probe never matters for listing */ }
        }

        return new StartupEntry
        {
            Location = location,
            Name = name,
            DisplayName = display,
            Command = command,
            ExecutablePath = exe,
            Publisher = company,
            FileSizeBytes = size,
            Enabled = StartupApproved.IsEnabled(approved),
            ApprovedHex = approved?.Kind == RegValueKind.Binary ? approved.Data : null,
            ToggleBlock = ToggleBlockFor(location)
        };
    }

    private StartupToggleBlock ToggleBlockFor(StartupLocation location) =>
        StartupApproved.IsMachine(location) && _broker == null ? StartupToggleBlock.NoBroker : StartupToggleBlock.None;

    /// <summary>
    /// Target for a <c>startup.enable|…</c> / <c>startup.disable|…</c> id; null for unknown ids and for
    /// HKLM locations when no elevation broker was supplied.
    /// </summary>
    public ISafeActionTarget? CreateTarget(string actionId)
    {
        if (!StartupToggle.TryParseActionId(actionId, out bool enable, out var location, out var name)) return null;
        if (ToggleBlockFor(location) != StartupToggleBlock.None) return null;
        return StartupApproved.IsMachine(location)
            ? new ElevatedStartupToggleTarget(enable, location, name, _broker!, _hklm, _utcNow)
            : new StartupToggleTarget(enable, location, name, _hkcu, _utcNow);
    }

    /// <summary>Registers both toggles of every listed entry that can be toggled.</summary>
    public void Register(SafeActionService service, IEnumerable<StartupEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(service);
        foreach (var e in entries ?? Enumerable.Empty<StartupEntry>())
        {
            if (!e.CanToggle) continue;
            foreach (var id in new[] { e.DisableActionId, e.EnableActionId })
            {
                var t = CreateTarget(id);
                if (t != null) service.Register(t);
            }
        }
    }

    /// <summary>
    /// Re-creates targets for journaled startup toggles (so an undo still works after a restart, even
    /// when the entry has since vanished from the Run key). Returns how many were registered.
    /// </summary>
    public int RegisterJournaled(SafeActionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        int n = 0;
        foreach (var entry in service.Journal.Entries)
        {
            if (!entry.Key.StartsWith(StartupToggle.JournalPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var t = CreateTarget(entry.ActionId);
            if (t == null) continue;
            service.Register(t);
            n++;
        }
        return n;
    }
}
