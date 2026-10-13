using NvAPIWrapper;

namespace Visor.Services;

/// <summary>
/// One NvAPI lifetime for the whole app (Digital Vibrance + DRS FPS cap share it).
/// Initialize is lazy and retried after a cool-down instead of caching the first failure forever;
/// Unload happens once, at app shutdown.
/// </summary>
internal static class NvApiHost
{
    private static readonly object Gate = new();
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);
    private static bool _initialized;
    private static DateTime _lastFailureUtc = DateTime.MinValue;
    private static bool _failureLogged;

    public static bool TryInitialize()
    {
        lock (Gate)
        {
            if (_initialized) return true;
            if (DateTime.UtcNow - _lastFailureUtc < RetryAfter) return false;
            try
            {
                NVIDIA.Initialize();
                _initialized = true;
                _failureLogged = false;
                return true;
            }
            catch (Exception ex)
            {
                _lastFailureUtc = DateTime.UtcNow;
                if (!_failureLogged)
                {
                    AppLog.Info("NVIDIA NvAPI not available: " + ex.Message);
                    _failureLogged = true;
                }
                return false;
            }
        }
    }

    /// <summary>
    /// Unloads NvAPI — but never while a DRS call (FPS cap) is still running on the background
    /// queue: unloading under it would crash in native code. If it does not finish within
    /// <paramref name="maxWait"/> (default 1 s; the exit path already drained that queue) the
    /// library is simply left loaded (the process is exiting anyway).
    /// </summary>
    public static void Shutdown(TimeSpan? maxWait = null)
    {
        if (!Monitor.TryEnter(FpsLimitService.Gate, maxWait ?? TimeSpan.FromSeconds(1)))
        {
            AppLog.Warn("NvAPI unload skipped: a driver-settings call is still running.");
            return;
        }
        try
        {
            lock (Gate)
            {
                if (!_initialized) return;
                try { NVIDIA.Unload(); }
                catch (Exception ex) { AppLog.Warn(ex, "NvAPI unload"); }
                _initialized = false;
            }
        }
        finally
        {
            Monitor.Exit(FpsLimitService.Gate);
        }
    }
}
