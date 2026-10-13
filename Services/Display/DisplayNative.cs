using System.Runtime.InteropServices;

namespace Visor.Services;

/// <summary>
/// Shared Win32 display interop: DEVMODE + EnumDisplaySettings / ChangeDisplaySettingsEx,
/// display device enumeration and per-monitor gamma ramps. Used by DisplayEngine and SessionExtrasService.
/// </summary>
internal static class DisplayNative
{
    public const int ENUM_CURRENT_SETTINGS = -1;
    /// <summary>Dynamic (non-persisted) mode change — the registry keeps the user's desktop mode.</summary>
    public const int CDS_DYNAMIC = 0;
    public const int DISP_CHANGE_SUCCESSFUL = 0;
    public const int DM_BITSPERPEL = 0x40000;
    public const int DM_PELSWIDTH = 0x80000;
    public const int DM_PELSHEIGHT = 0x100000;
    public const int DM_DISPLAYFREQUENCY = 0x400000;
    public const int DM_DISPLAYFIXEDOUTPUT = 0x20000000;
    public const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    public const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAMP
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Blue;

        public static RAMP Create() => new()
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };

        public RAMP Clone() => new()
        {
            Red = (ushort[])Red.Clone(),
            Green = (ushort[])Green.Clone(),
            Blue = (ushort[])Blue.Clone()
        };
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string? lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP ramp);

    [DllImport("gdi32.dll")]
    private static extern bool GetDeviceGammaRamp(IntPtr hDC, ref RAMP ramp);

    /// <summary>256 ushorts → base64 (little-endian), the format used in active-session.json.</summary>
    public static string EncodeChannel(ushort[] data)
    {
        var bytes = new byte[data.Length * 2];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    public static bool TryDecodeChannel(string? b64, out ushort[] data)
    {
        data = Array.Empty<ushort>();
        if (string.IsNullOrWhiteSpace(b64)) return false;
        try
        {
            var bytes = Convert.FromBase64String(b64);
            if (bytes.Length != 512) return false;
            data = new ushort[256];
            Buffer.BlockCopy(bytes, 0, data, 0, 512);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool TryDecodeRamp(string? r, string? g, string? b, out RAMP ramp)
    {
        ramp = default;
        if (!TryDecodeChannel(r, out var red) || !TryDecodeChannel(g, out var green) || !TryDecodeChannel(b, out var blue))
            return false;
        ramp = new RAMP { Red = red, Green = green, Blue = blue };
        return true;
    }

    public static DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    public static bool TryGetCurrentMode(string? device, out DEVMODE mode)
    {
        mode = NewDevMode();
        return EnumDisplaySettings(NullIfEmpty(device), ENUM_CURRENT_SETTINGS, ref mode);
    }

    public static string? NullIfEmpty(string? device) =>
        string.IsNullOrWhiteSpace(device) ? null : device.Trim();

    /// <summary>Attached-to-desktop display devices (\\.\DISPLAYn).</summary>
    public static List<(string DeviceName, string Friendly, bool Primary)> EnumerateDisplays()
    {
        var list = new List<(string, string, bool)>();
        uint i = 0;
        while (true)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            i++;
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            bool primary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
            string friendly = string.IsNullOrWhiteSpace(dd.DeviceString) ? dd.DeviceName : dd.DeviceString;
            list.Add((dd.DeviceName, friendly, primary));
        }
        return list;
    }

    public static string? PrimaryDeviceName()
    {
        foreach (var d in EnumerateDisplays())
            if (d.Primary) return d.DeviceName;
        return null;
    }

    /// <summary>DC for one monitor (CreateDC(null, \\.\DISPLAYn)); whole-desktop DC when the name is unknown.</summary>
    private static IntPtr OpenDisplayDc(string? device)
    {
        var name = NullIfEmpty(device);
        IntPtr hdc = name != null ? CreateDC(null, name, null, IntPtr.Zero) : IntPtr.Zero;
        if (hdc == IntPtr.Zero)
            hdc = CreateDC("DISPLAY", null, null, IntPtr.Zero);
        return hdc;
    }

    public static bool TryGetGammaRamp(string? device, out RAMP ramp)
    {
        ramp = RAMP.Create();
        IntPtr hdc = OpenDisplayDc(device);
        if (hdc == IntPtr.Zero) return false;
        try { return GetDeviceGammaRamp(hdc, ref ramp); }
        finally { DeleteDC(hdc); }
    }

    /// <summary>Sets the ramp and verifies the driver kept it (Windows can return TRUE yet clamp the curve).</summary>
    public static bool TrySetGammaRamp(string? device, RAMP ramp, out string? error)
    {
        error = null;
        IntPtr hdc = OpenDisplayDc(device);
        if (hdc == IntPtr.Zero)
        {
            error = "No HDC for gamma ramp.";
            return false;
        }

        try
        {
            if (!SetDeviceGammaRamp(hdc, ref ramp))
            {
                error = "SetDeviceGammaRamp returned false.";
                return false;
            }

            var check = RAMP.Create();
            if (GetDeviceGammaRamp(hdc, ref check))
            {
                int expect = LowLevelColorRamp.UnpackRampByte(ramp.Red[128]);
                int actual = LowLevelColorRamp.UnpackRampByte(check.Red[128]);
                if (Math.Abs(expect - actual) > 8)
                {
                    error = $"Gamma ramp mismatch at mid: set≈{expect} got≈{actual} (driver may have clamped).";
                    return false;
                }
            }
            return true;
        }
        finally
        {
            DeleteDC(hdc);
        }
    }
}
