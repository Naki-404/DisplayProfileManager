using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Visor.Setup;

internal static class Native
{
    // ---------- processes ----------
    private const int ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder buffer, ref int size);

    /// <summary>Full image path of a process (works across bitness); null if not accessible.</summary>
    public static string? GetProcessPath(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr h, out int exitCode);

    private const int StillActive = 259;
    private const int ErrorInvalidParameter = 87;

    /// <summary>
    /// True unless the process provably exited. A PID that no longer exists (ERROR_INVALID_PARAMETER)
    /// counts as exited; any other failure (e.g. access denied) counts as still running.
    /// </summary>
    public static bool IsProcessRunning(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() != ErrorInvalidParameter;
        try
        {
            return !GetExitCodeProcess(h, out var code) || code == StillActive;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>True if this process runs with an elevated (admin) token.</summary>
    public static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ---------- gamma ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    private const int DisplayDeviceAttachedToDesktop = 0x1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, int index, ref DisplayDevice dd, int flags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDCW(string? driver, string device, string? output, IntPtr devMode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

    /// <summary>Sets a linear (identity) gamma ramp on every monitor attached to the desktop.</summary>
    public static void ResetGammaAllMonitors()
    {
        var ramp = new ushort[768];
        for (int i = 0; i < 256; i++)
        {
            ushort v = (ushort)(i * 257);
            ramp[i] = v; ramp[256 + i] = v; ramp[512 + i] = v;
        }

        for (int i = 0; ; i++)
        {
            var dd = new DisplayDevice { cb = Marshal.SizeOf(typeof(DisplayDevice)) };
            if (!EnumDisplayDevicesW(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & DisplayDeviceAttachedToDesktop) == 0) continue;
            var hdc = CreateDCW(null, dd.DeviceName, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero) continue;
            try { SetDeviceGammaRamp(hdc, ramp); }
            finally { DeleteDC(hdc); }
        }
    }

    // ---------- known folders ----------
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, int flags, IntPtr token, out IntPtr path);

    public static string? GetDownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    // ---------- shortcuts (IShellLink, no WScript) ----------
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    public static void CreateShortcut(string lnkPath, string target, string workDir, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(workDir);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    /// <summary>Target path of a .lnk, or null if it cannot be read.</summary>
    public static string? GetShortcutTarget(string lnkPath)
    {
        if (!File.Exists(lnkPath)) return null;
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var s = sb.ToString();
            return s.Length == 0 ? null : s;
        }
        catch { return null; }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    // ---------- folder picker (IFileOpenDialog, Vista-style) ----------
    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogCoClass { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, IntPtr filterSpec);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint fos);
        void GetOptions(out uint fos);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int place);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string ext);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
        void GetResults(out IntPtr items);
        void GetSelectedItems(out IntPtr items);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdn, out IntPtr name);
        void GetAttributes(uint mask, out uint attribs);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid riid, out IShellItem item);

    private const uint FosPickFolders = 0x20, FosForceFileSystem = 0x40, FosPathMustExist = 0x800;
    private const uint SigdnFileSysPath = 0x80058000;

    /// <summary>Shows the Explorer-style folder picker. Returns null on cancel.</summary>
    public static string? PickFolder(IntPtr owner, string title, string? initialDir)
    {
        var dlg = (IFileOpenDialog)new FileOpenDialogCoClass();
        try
        {
            dlg.GetOptions(out var opts);
            dlg.SetOptions(opts | FosPickFolders | FosForceFileSystem | FosPathMustExist);
            dlg.SetTitle(title);
            if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
            {
                var iid = typeof(IShellItem).GUID;
                if (SHCreateItemFromParsingName(initialDir!, IntPtr.Zero, ref iid, out var item) == 0)
                    dlg.SetFolder(item);
            }
            if (dlg.Show(owner) != 0) return null; // cancelled or failed
            dlg.GetResult(out var result);
            result.GetDisplayName(SigdnFileSysPath, out var p);
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        finally { Marshal.FinalReleaseComObject(dlg); }
    }
}
