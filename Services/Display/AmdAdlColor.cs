using System.Runtime.InteropServices;

namespace Visor.Services;

/// <summary>
/// AMD Display Library (atiadlxx) — saturation at driver level on the target display.
/// </summary>
internal sealed class AmdAdlColor : IDisposable
{
    private const int AdlOk = 0;
    private const int AdlDisplayColorBrightness = 1;
    private const int AdlDisplayColorContrast = 2;
    private const int AdlDisplayColorSaturation = 4;
    private const int AdlDisplayInfoConnected = 0x1;
    private const int AdlDisplayInfoMapped = 0x2;

    private delegate IntPtr AdlMainMemoryAlloc(int size);

    /// <summary>
    /// ADL keeps this callback for the lifetime of the context and calls it from native code —
    /// it must never be collected, so it lives in a static field (not a temporary delegate).
    /// </summary>
    private static readonly AdlMainMemoryAlloc AllocCallback = Marshal.AllocHGlobal;

    private IntPtr _context;
    private bool _ready;
    private int _adapterIndex = -1;
    private int _displayIndex = -1;
    private string? _targetDevice;
    /// <summary>Saturation per adapter/display the first time Visor saw it — the "50%" point.</summary>
    private readonly Dictionary<(int Adapter, int Display), int> _baselineSaturation = new();

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
        /// <summary>GDI device name of this logical adapter (\\.\DISPLAYn).</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DisplayName;
        public int Present;
        public int Exist;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPathExt;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string PNPString;
        public int OSDisplayIndex;
    }

    /// <summary>ADLDisplayInfo (ADLDisplayID first: logical, physical, logical adapter, physical adapter).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AdlDisplayInfo
    {
        public int DisplayLogicalIndex;
        public int DisplayPhysicalIndex;
        public int DisplayLogicalAdapterIndex;
        public int DisplayPhysicalAdapterIndex;
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

    /// <summary>Requested target (\.DISPLAYn) or null for the first connected display.</summary>
    public string? CurrentDisplayName => _targetDevice;

    /// <summary>Target display (\\.\DISPLAYn); null = first connected AMD display.</summary>
    public void SetTargetDevice(string? deviceName)
    {
        if (string.Equals(deviceName, _targetDevice, StringComparison.OrdinalIgnoreCase)) return;
        _targetDevice = deviceName;
        if (_ready) SelectDisplay();
    }

    public bool TryInit()
    {
        if (_ready) return true;
        try
        {
            if (_context == IntPtr.Zero &&
                ADL2_Main_Control_Create(AllocCallback, 1, out _context) != AdlOk)
            {
                _context = IntPtr.Zero;
                return false;
            }

            if (!SelectDisplay())
            {
                FailCleanup();
                return false;
            }
            return true;
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

    /// <summary>Picks the connected + mapped display of the adapter whose GDI name is the target (else the first such display).</summary>
    private bool SelectDisplay()
    {
        if (ADL2_Adapter_NumberOfAdapters_Get(_context, out int n) != AdlOk || n <= 0)
            return false;

        int size = Marshal.SizeOf<AdapterInfo>();
        IntPtr buf = Marshal.AllocHGlobal(size * n);
        (int Adapter, int Display)? first = null, preferred = null;
        try
        {
            for (int i = 0; i < n; i++)
                Marshal.StructureToPtr(new AdapterInfo { Size = size }, buf + i * size, false);
            if (ADL2_Adapter_AdapterInfo_Get(_context, buf, size * n) != AdlOk)
                return false;

            for (int i = 0; i < n && preferred == null; i++)
            {
                var info = Marshal.PtrToStructure<AdapterInfo>(buf + i * size);
                if (ADL2_Adapter_Active_Get(_context, info.AdapterIndex, out int active) != AdlOk || active == 0)
                    continue;
                int? display = FindConnectedDisplay(info.AdapterIndex);
                if (display == null) continue;

                first ??= (info.AdapterIndex, display.Value);
                if (_targetDevice != null && string.Equals(info.DisplayName, _targetDevice, StringComparison.OrdinalIgnoreCase))
                    preferred = (info.AdapterIndex, display.Value);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        var pick = preferred ?? first;
        if (pick == null) return false;
        _adapterIndex = pick.Value.Adapter;
        _displayIndex = pick.Value.Display;
        _ready = true;
        if (!_baselineSaturation.ContainsKey(pick.Value) && Read(AdlDisplayColorSaturation) is int sat)
            _baselineSaturation[pick.Value] = sat;
        AppLog.Info($"AMD ADL ready: adapter={_adapterIndex} display={_displayIndex}");
        return true;
    }

    private int? FindConnectedDisplay(int adapterIndex)
    {
        if (ADL2_Display_DisplayInfo_Get(_context, adapterIndex, out int got, out IntPtr dinfo, 0) != AdlOk)
            return null;
        try
        {
            int size = Marshal.SizeOf<AdlDisplayInfo>();
            for (int j = 0; j < got; j++)
            {
                var di = Marshal.PtrToStructure<AdlDisplayInfo>(dinfo + j * size);
                const int want = AdlDisplayInfoConnected | AdlDisplayInfoMapped;
                if ((di.DisplayInfoValue & want) == want && di.DisplayLogicalAdapterIndex == adapterIndex)
                    return di.DisplayLogicalIndex;
            }
            return null;
        }
        finally
        {
            if (dinfo != IntPtr.Zero) Marshal.FreeHGlobal(dinfo);
        }
    }

    private void FailCleanup()
    {
        if (_context != IntPtr.Zero)
        {
            try { ADL2_Main_Control_Destroy(_context); } catch { /* best effort */ }
            _context = IntPtr.Zero;
        }
        _ready = false;
        _adapterIndex = -1;
        _displayIndex = -1;
    }

    public DriverColorSnapshot? Capture()
    {
        if (!TryInit()) return null;
        try
        {
            return new DriverColorSnapshot
            {
                Vendor = "amd",
                DisplayName = _targetDevice,
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

    /// <summary>
    /// Saturation from the UI vibrance slider: 50 = the display's baseline, 0 = min, 100 = max.
    /// Brightness/contrast stay untouched (those belong to the gamma ramp).
    /// </summary>
    public void Apply(int vibrancePercent)
    {
        if (vibrancePercent < 0) return;
        if (!TryInit()) return;
        try
        {
            if (ADL2_Display_Color_Get(_context, _adapterIndex, _displayIndex, AdlDisplayColorSaturation,
                    out _, out int def, out int min, out int max, out _) != AdlOk || max <= min)
                return;

            int baseline = _baselineSaturation.TryGetValue((_adapterIndex, _displayIndex), out var b) ? b : def;
            double t = (Math.Clamp(vibrancePercent, 0, 100) - 50) / 50.0;
            double value = t >= 0 ? baseline + (max - baseline) * t : baseline + (baseline - min) * t;
            Write(AdlDisplayColorSaturation, Math.Clamp((int)Math.Round(value), min, max));
            AppLog.Info($"AMD saturation → vib UI {vibrancePercent}% (baseline {baseline}, B/C left untouched).");
        }
        catch (Exception ex)
        {
            AppLog.Error("AMD apply failed: " + ex.Message);
        }
    }

    /// <returns>False when ADL is unavailable or a write was rejected.</returns>
    public bool Restore(DriverColorSnapshot snap)
    {
        if (snap.Vendor != "amd") return false;
        if (snap.DisplayName != null) SetTargetDevice(snap.DisplayName);
        if (!TryInit())
        {
            AppLog.Warn("AMD restore skipped: ADL not available.");
            return false;
        }
        try
        {
            bool ok = true;
            if (snap.Brightness.HasValue) ok &= Write(AdlDisplayColorBrightness, snap.Brightness.Value);
            if (snap.Contrast.HasValue) ok &= Write(AdlDisplayColorContrast, snap.Contrast.Value);
            if (snap.Saturation.HasValue) ok &= Write(AdlDisplayColorSaturation, snap.Saturation.Value);
            return ok;
        }
        catch (Exception ex)
        {
            AppLog.Error("AMD restore failed: " + ex.Message);
            return false;
        }
    }

    private int? Read(int type)
    {
        if (ADL2_Display_Color_Get(_context, _adapterIndex, _displayIndex, type,
                out int cur, out _, out _, out _, out _) != AdlOk)
            return null;
        return cur;
    }

    private bool Write(int type, int value)
    {
        int rc = ADL2_Display_Color_Set(_context, _adapterIndex, _displayIndex, type, value);
        if (rc == AdlOk) return true;
        AppLog.Warn($"ADL color set (type {type}) returned {rc}.");
        return false;
    }

    public void Dispose()
    {
        if (_context != IntPtr.Zero)
        {
            try { ADL2_Main_Control_Destroy(_context); } catch { /* best effort */ }
            _context = IntPtr.Zero;
        }
        _ready = false;
    }
}

public sealed class DriverColorSnapshot
{
    public string Vendor { get; set; } = "";
    /// <summary>\\.\DISPLAYn the values were read from (null = first connected display).</summary>
    public string? DisplayName { get; set; }
    public int? Brightness { get; set; }
    public int? Contrast { get; set; }
    public int? Saturation { get; set; }
    public int? VibranceLevel { get; set; }
    public float? NormalizedVibrance { get; set; }
    /// <summary>NVIDIA HUE offset angle (0..359). Null on vendors/drivers without HUE support.</summary>
    public int? HueAngle { get; set; }
}
