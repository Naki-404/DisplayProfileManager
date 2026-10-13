using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Visor.Services;

/// <summary>Win32 helpers for topmost, non-activating tool windows (overlay, OSD) and monitor-aware placement.</summary>
internal static class OverlayWin32
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpShowwindow = 0x0040;

    private const uint MonitorDefaultToNull = 0;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointApi
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int CbSize;
        public Rect RcMonitor;
        public Rect RcWork;
        public int DwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointApi pt);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointApi pt, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    public static void StayTopmost(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate | SwpShowwindow);
    }

    /// <summary>Do not steal keyboard focus from the game.</summary>
    public static void SetNoActivate(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int style = GetWindowLong(hwnd, GwlExstyle);
        style |= WsExToolwindow | WsExNoActivate;
        SetWindowLong(hwnd, GwlExstyle, style);
    }

    /// <summary>Mouse clicks pass through to the window below (OSD banner).</summary>
    public static void SetClickThrough(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int style = GetWindowLong(hwnd, GwlExstyle);
        style |= WsExToolwindow | WsExNoActivate | WsExTransparent | WsExLayered;
        SetWindowLong(hwnd, GwlExstyle, style);
    }

    /// <summary>
    /// Monitor the user is looking at: the foreground window's monitor when it belongs to another process
    /// (i.e. the game), otherwise the monitor under the cursor.
    /// </summary>
    public static IntPtr TargetMonitor()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                GetWindowThreadProcessId(fg, out uint pid);
                if (pid != 0 && pid != (uint)Environment.ProcessId)
                {
                    var m = MonitorFromWindow(fg, MonitorDefaultToNull);
                    if (m != IntPtr.Zero) return m;
                }
            }
            if (GetCursorPos(out var pt))
                return MonitorFromPoint(pt, MonitorDefaultToNearest);
        }
        catch { /* fall through */ }
        return MonitorFromPoint(new PointApi(), MonitorDefaultToNearest);
    }

    public static bool TryGetMonitorRects(IntPtr monitor, out Rect bounds, out Rect work)
    {
        bounds = default;
        work = default;
        if (monitor == IntPtr.Zero) return false;
        var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        bounds = info.RcMonitor;
        work = info.RcWork;
        return true;
    }

    /// <summary>Full bounds (physical px) of the target monitor — used to center zoom on the game screen.</summary>
    public static bool TryGetTargetMonitorBounds(out Rect bounds) =>
        TryGetMonitorRects(TargetMonitor(), out bounds, out _);

    /// <summary>Moves a shown window to the top-right corner of the target monitor's work area.</summary>
    public static bool PlaceTopRight(Window window, int marginPx)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return false;
        if (!TryGetMonitorRects(TargetMonitor(), out _, out var work)) return false;
        int x = Math.Max(work.Left + marginPx, work.Right - r.Width - marginPx);
        int y = work.Top + marginPx;
        return SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNosize | SwpNozorder | SwpNoactivate);
    }

    /// <summary>Moves a shown window to the top-center of the target monitor (OSD banner).</summary>
    public static bool PlaceTopCenter(Window window, int topMarginPx)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return false;
        if (!TryGetMonitorRects(TargetMonitor(), out var bounds, out _)) return false;
        int x = bounds.Left + (bounds.Width - r.Width) / 2;
        int y = bounds.Top + topMarginPx;
        return SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNosize | SwpNozorder | SwpNoactivate);
    }

    /// <summary>True when a usable part of the window (≥ 48×24 px) lies inside some monitor's work area.</summary>
    public static bool IsReasonablyOnScreen(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return true;
        var mon = MonitorFromRect(ref r, MonitorDefaultToNull);
        if (mon == IntPtr.Zero || !TryGetMonitorRects(mon, out _, out var work)) return false;
        int w = Math.Min(r.Right, work.Right) - Math.Max(r.Left, work.Left);
        int h = Math.Min(r.Bottom, work.Bottom) - Math.Max(r.Top, work.Top);
        return w >= 48 && h >= 24;
    }

    /// <summary>True when the window sits on the same monitor the user is currently playing on.</summary>
    public static bool IsOnTargetMonitor(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return true;
        return MonitorFromWindow(hwnd, MonitorDefaultToNearest) == TargetMonitor();
    }

    private static int GetWindowLong(IntPtr hwnd, int index) =>
        IntPtr.Size == 8
            ? (int)GetWindowLongPtr64(hwnd, index)
            : GetWindowLong32(hwnd, index);

    private static void SetWindowLong(IntPtr hwnd, int index, int value)
    {
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(hwnd, index, new IntPtr(value));
        else
            SetWindowLong32(hwnd, index, value);
    }
}
