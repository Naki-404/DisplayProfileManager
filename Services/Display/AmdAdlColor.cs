using System.Runtime.InteropServices;

namespace DisplayProfileManager.Services;

/// <summary>
/// AMD Display Library (atiadlxx) — brightness / contrast / saturation at driver level.
/// </summary>
internal sealed class AmdAdlColor : IDisposable
{
    private const int AdlOk = 0;
    private const int AdlDisplayColorBrightness = 1;
    private const int AdlDisplayColorContrast = 2;
    private const int AdlDisplayColorSaturation = 4;

    private IntPtr _context;
    private bool _ready;
    private int _adapterIndex = -1;
    private int _displayIndex = -1;

    private delegate IntPtr AdlMainMemoryAlloc(int size);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Main_Control_Create(AdlMainMemoryAlloc callback, int enumConnectedAdapters, out IntPtr context);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Main_Control_Destroy(IntPtr context);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, out int numAdapters);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr context, IntPtr info, int size);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Adapter_Active_Get(IntPtr context, int adapterIndex, out int status);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Display_NumberOfDisplays_Get(IntPtr context, int adapterIndex, out int numDisplays);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Display_DisplayInfo_Get(IntPtr context, int adapterIndex, out int numDisplays, out IntPtr info, int forceDetect);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Display_Color_Get(IntPtr context, int adapterIndex, int displayIndex, int colorType,
        out int current, out int defaultValue, out int min, out int max, out int step);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Display_Color_Set(IntPtr context, int adapterIndex, int displayIndex, int colorType, int current);

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterInfo
    {
        public int Size;
        public int AdapterIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Udid;
        public int BusNumber;
        public int DeviceNumber;
        public int FunctionNumber;
        public int VendorID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AdapterName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DisplayName;
        public int Present;
        public int Exist;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPathExt;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string PNPString;
        public int OSDisplayIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlDisplayInfo
    {
        public int DisplayID_DisplayLogicalIndex;
        public int DisplayID_DisplayPhysicalAdapterIndex;
        public int DisplayID_DisplayPhysicalIndex;
        public int DisplayID_DisplayLogicalAdapterIndex;
        public int DisplayControllerIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DisplayManufacturerName;
        public int DisplayType;
        public int DisplayOutputType;
        public int DisplayConnector;
        public int DisplayInfoMask;
        public int DisplayInfoValue;
    }

    public bool IsAvailable => _ready;

    public bool TryInit()
    {
        if (_ready) return true;
        try
        {
            if (ADL2_Main_Control_Create(Marshal.AllocHGlobal, 1, out _context) != AdlOk)
            {
                _context = IntPtr.Zero;
                return false;
            }

            if (ADL2_Adapter_NumberOfAdapters_Get(_context, out int n) != AdlOk || n <= 0)
            {
                FailCleanup();
                return false;
            }

            int size = Marshal.SizeOf<AdapterInfo>();
            IntPtr buf = Marshal.AllocHGlobal(size * n);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var tmp = new AdapterInfo { Size = size };
                    Marshal.StructureToPtr(tmp, buf + i * size, false);
                }
                if (ADL2_Adapter_AdapterInfo_Get(_context, buf, size * n) != AdlOk)
                {
                    FailCleanup();
                    return false;
                }

                for (int i = 0; i < n; i++)
                {
                    var info = Marshal.PtrToStructure<AdapterInfo>(buf + i * size);
                    if (ADL2_Adapter_Active_Get(_context, info.AdapterIndex, out int active) != AdlOk || active == 0)
                        continue;
                    if (ADL2_Display_NumberOfDisplays_Get(_context, info.AdapterIndex, out int nd) != AdlOk || nd <= 0)
                        continue;

                    if (ADL2_Display_DisplayInfo_Get(_context, info.AdapterIndex, out int got, out IntPtr dinfo, 0) != AdlOk)
                        continue;
                    try
                    {
                        if (got <= 0) continue;
                        _adapterIndex = info.AdapterIndex;
                        var di = Marshal.PtrToStructure<AdlDisplayInfo>(dinfo);
                        _displayIndex = di.DisplayID_DisplayLogicalIndex;
                        _ready = true;
                        AppLog.Info($"AMD ADL ready: adapter={_adapterIndex} display={_displayIndex}");
                        return true;
                    }
                    finally
                    {
                        if (dinfo != IntPtr.Zero) Marshal.FreeHGlobal(dinfo);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }

            FailCleanup();
            return false;
        }
        catch (DllNotFoundException)
        {
            AppLog.Info("AMD ADL: atiadlxx.dll not found.");
            FailCleanup();
        }
        catch (Exception ex)
        {
            AppLog.Error("AMD ADL init failed: " + ex.Message);
            FailCleanup();
        }
        return false;
    }

    private void FailCleanup()
    {
        if (_context != IntPtr.Zero)
        {
            try { ADL2_Main_Control_Destroy(_context); } catch { }
            _context = IntPtr.Zero;
        }
        _ready = false;
        _adapterIndex = -1;
        _displayIndex = -1;
    }

    public DriverColorSnapshot? Capture()
    {
        if (!_ready && !TryInit()) return null;
        try
        {
            return new DriverColorSnapshot
            {
                Vendor = "amd",
                Brightness = Read(AdlDisplayColorBrightness),
                Contrast = Read(AdlDisplayColorContrast),
                Saturation = Read(AdlDisplayColorSaturation)
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("AMD capture failed: " + ex.Message);
            return null;
        }
    }

    public void Apply(Models.ColorSettings color, int vibrancePercent)
    {
        if (!_ready && !TryInit()) return;
        try
        {
            // Only saturation/vibrance from the UI slider.
            // Do NOT map profile Brightness/Contrast (those are for GDI/RT Low Level) —
            // that was crushing ADL B/C and looking like extreme contrast / gray.
            if (vibrancePercent >= 0)
            {
                // 50 ≈ keep around default center of ADL range
                WriteMapped(AdlDisplayColorSaturation, vibrancePercent / 100.0, 0.0, 1.0);
            }
            AppLog.Info($"AMD saturation → vib UI {vibrancePercent}% (B/C left untouched).");
        }
        catch (Exception ex)
        {
            AppLog.Error("AMD apply failed: " + ex.Message);
        }
    }

    /// <summary>
    /// AMD ADL has no public HUE offset control (only brightness/contrast/saturation via
    /// ADL_DISPLAY_COLOR). Stub kept parallel to NvidiaDriverColor.TrySetHue so callers can
    /// treat both vendors uniformly; always returns false.
    /// </summary>
    public bool TrySetHue(int hue)
    {
        _ = hue;
        AppLog.Info("AMD HUE control not available (ADL has no HUE API) - skipped.");
        return false;
    }

    public void Restore(DriverColorSnapshot snap)
    {
        if (!_ready || snap.Vendor != "amd") return;
        try
        {
            if (snap.Brightness.HasValue) Write(AdlDisplayColorBrightness, snap.Brightness.Value);
            if (snap.Contrast.HasValue) Write(AdlDisplayColorContrast, snap.Contrast.Value);
            if (snap.Saturation.HasValue) Write(AdlDisplayColorSaturation, snap.Saturation.Value);
        }
        catch (Exception ex)
        {
            AppLog.Error("AMD restore failed: " + ex.Message);
        }
    }

    private int? Read(int type)
    {
        if (ADL2_Display_Color_Get(_context, _adapterIndex, _displayIndex, type,
                out int cur, out _, out _, out _, out _) != AdlOk)
            return null;
        return cur;
    }

    private void Write(int type, int value)
    {
        ADL2_Display_Color_Set(_context, _adapterIndex, _displayIndex, type, value);
    }

    private void WriteMapped(int type, double normalized, double srcMin, double srcMax)
    {
        if (ADL2_Display_Color_Get(_context, _adapterIndex, _displayIndex, type,
                out _, out int def, out int min, out int max, out _) != AdlOk)
            return;
        double t = (normalized - srcMin) / Math.Max(0.001, srcMax - srcMin);
        t = Math.Clamp(t, 0.0, 1.0);
        int value = (int)Math.Round(min + t * (max - min));
        value = Math.Clamp(value, min, max);
        // Prefer keeping default when near neutral center of our scale
        _ = def;
        Write(type, value);
    }

    public void Dispose()
    {
        if (_context != IntPtr.Zero)
        {
            try { ADL2_Main_Control_Destroy(_context); } catch { }
            _context = IntPtr.Zero;
        }
        _ready = false;
    }
}

public sealed class DriverColorSnapshot
{
    public string Vendor { get; set; } = "";
    public int? Brightness { get; set; }
    public int? Contrast { get; set; }
    public int? Saturation { get; set; }
    public int? VibranceLevel { get; set; }
    public float? NormalizedVibrance { get; set; }
    /// <summary>NVIDIA HUE offset angle (0..359). Null on vendors/drivers without HUE support.</summary>
    public int? HueAngle { get; set; }
}
