using System.Runtime.InteropServices;
using Visor.Models;

namespace Visor.Services;

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

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    public bool IsAvailable => _ready;
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
        // Center on the monitor the game is on (foreground window / cursor) — never the seam
        // between screens of the virtual desktop. Offsets are in desktop (physical) coordinates.
        int mx, my, mw, mh;
        if (OverlayWin32.TryGetTargetMonitorBounds(out var b) && b.Width > 0 && b.Height > 0)
        {
            mx = b.Left;
            my = b.Top;
            mw = b.Width;
            mh = b.Height;
        }
        else
        {
            mx = 0;
            my = 0;
            mw = GetSystemMetrics(SmCxScreen);
            mh = GetSystemMetrics(SmCyScreen);
        }

        if (mw <= 0 || mh <= 0 || factor < 1.01f)
        {
            MagSetFullscreenTransform(1f, 0, 0);
            return;
        }

        int xOffset = mx + (int)Math.Round((mw - mw / factor) / 2.0);
        int yOffset = my + (int)Math.Round((mh - mh / factor) / 2.0);
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
