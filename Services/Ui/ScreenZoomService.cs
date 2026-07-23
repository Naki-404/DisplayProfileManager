using System.Runtime.InteropServices;
using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

/// <summary>
/// Center-screen digital zoom via Windows Magnification API (accessibility).
/// No game process injection — desktop composition only.
/// </summary>
public sealed class ScreenZoomService : IDisposable
{
    private bool _ready;
    private bool _active;
    private float _factor = 4f;
    private bool _disposed;

    [DllImport("Magnification.dll")]
    private static extern bool MagInitialize();

    [DllImport("Magnification.dll")]
    private static extern bool MagUninitialize();

    [DllImport("Magnification.dll")]
    private static extern bool MagSetFullscreenTransform(float magLevel, int xOffset, int yOffset);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    public bool IsAvailable => _ready;
    public bool IsActive => _active;
    public float Factor => _factor;

    public ScreenZoomService()
    {
        try
        {
            _ready = MagInitialize();
            if (!_ready)
                AppLog.Info("Magnification API unavailable — zoom disabled.");
        }
        catch (Exception ex)
        {
            _ready = false;
            AppLog.Error("MagInitialize failed: " + ex.Message);
        }
    }

    public void ApplyFromConfig(UiPreferences? ui)
    {
        int f = ui?.ZoomFactor ?? 4;
        if (f < 2) f = 2;
        if (f > 12) f = 12;
        _factor = f;
        if (_active)
            ApplyTransform(_factor);
    }

    public bool Toggle()
    {
        if (!_ready) return false;
        if (_active) { Off(); return false; }
        On();
        return true;
    }

    public void On()
    {
        if (!_ready || _disposed) return;
        ApplyTransform(_factor);
        _active = true;
    }

    public void Off()
    {
        if (!_ready) return;
        try { MagSetFullscreenTransform(1f, 0, 0); } catch { }
        _active = false;
    }

    private void ApplyTransform(float factor)
    {
        // Prefer virtual desktop bounds so multi-monitor setups stay centered correctly.
        int vx = GetSystemMetrics(SmXVirtualScreen);
        int vy = GetSystemMetrics(SmYVirtualScreen);
        int vw = GetSystemMetrics(SmCxVirtualScreen);
        int vh = GetSystemMetrics(SmCyVirtualScreen);
        if (vw <= 0 || vh <= 0)
        {
            vx = 0;
            vy = 0;
            vw = GetSystemMetrics(SmCxScreen);
            vh = GetSystemMetrics(SmCyScreen);
        }

        if (vw <= 0 || vh <= 0 || factor < 1.01f)
        {
            MagSetFullscreenTransform(1f, 0, 0);
            return;
        }

        // Mag offsets are relative to the virtual desktop origin.
        int xOffset = vx + (int)Math.Round((vw - vw / factor) / 2.0);
        int yOffset = vy + (int)Math.Round((vh - vh / factor) / 2.0);
        if (xOffset < vx) xOffset = vx;
        if (yOffset < vy) yOffset = vy;

        MagSetFullscreenTransform(factor, xOffset, yOffset);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Off(); } catch { }
        try { if (_ready) MagUninitialize(); } catch { }
        _ready = false;
    }
}
