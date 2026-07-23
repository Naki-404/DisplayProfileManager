using NvAPIWrapper;
using NvAPIWrapper.DRS;

namespace DisplayProfileManager.Services;

/// <summary>
/// Best-effort per-application FPS cap via the NVIDIA driver settings (DRS) API.
/// Uses the public "Frame Rate Limiter" setting (FRL_FPS_ID = 0x10835002, documented in
/// NVIDIA's NvApiDriverSettings.h) - the same setting NVIDIA Control Panel / Profile
/// Inspector expose as "Max Frame Rate". AMD/Intel have no equivalent public driver API,
/// so this is NVIDIA-only; other vendors always get false from TryApply.
/// </summary>
public static class FpsLimitService
{
    private const uint FrlFpsSettingId = 0x10835002;
    private const uint FrlFpsDisabled = 0x00000000;
    private const uint FrlFpsMax = 0x000003FF; // driver-enforced cap (~1023 fps)

    private static bool? _available;
    private static bool _nvapiInitialized;
    private static readonly object Gate = new();

    /// <summary>True once NVIDIA NvAPI + a DRS session could be created on this machine.</summary>
    public static bool IsNvidiaAvailable
    {
        get
        {
            lock (Gate)
            {
                if (_available.HasValue) return _available.Value;
                _available = TryEnsureInitialized();
                return _available.Value;
            }
        }
    }

    private static bool TryEnsureInitialized()
    {
        try
        {
            if (!_nvapiInitialized)
            {
                NVIDIA.Initialize();
                _nvapiInitialized = true;
            }

            using var session = DriverSettingsSession.CreateAndLoad();
            return session.BaseProfile != null;
        }
        catch (Exception ex)
        {
            AppLog.Info("FPS limit not available (NVIDIA DRS init failed): " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Applies (or clears, when fpsLimit &lt;= 0) a per-application FPS cap.
    /// </summary>
    /// <param name="exePath">Full path or exe file name of the target process; null/empty targets the global (Base) profile.</param>
    /// <param name="fpsLimit">0 = restore/clear any override; otherwise clamped to 1..1023.</param>
    /// <returns>True if the driver setting was applied (or cleared) successfully.</returns>
    public static bool TryApply(string? exePath, int fpsLimit)
    {
        if (fpsLimit <= 0)
        {
            Clear(exePath);
            return true;
        }

        if (!IsNvidiaAvailable)
        {
            AppLog.Info("FPS limit not available (no NVIDIA DRS on this system).");
            return false;
        }

        try
        {
            lock (Gate)
            {
                using var session = DriverSettingsSession.CreateAndLoad();
                var profile = ResolveProfile(session, exePath, createIfMissing: true);
                if (profile == null)
                {
                    AppLog.Info($"FPS limit: could not resolve/create a driver profile for {exePath ?? "(global)"}.");
                    return false;
                }

                uint value = (uint)Math.Clamp(fpsLimit, 1, (int)FrlFpsMax);
                profile.SetSetting(FrlFpsSettingId, value);
                session.Save();
                AppLog.Info($"FPS limit -> {value} for {exePath ?? "(global)"} (NVIDIA DRS).");
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("FPS limit apply failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Removes any FPS cap override previously set by TryApply for this target.</summary>
    public static void Clear(string? exePath)
    {
        if (!IsNvidiaAvailable) return;
        try
        {
            lock (Gate)
            {
                using var session = DriverSettingsSession.CreateAndLoad();
                var profile = ResolveProfile(session, exePath, createIfMissing: false);
                if (profile == null) return;

                profile.SetSetting(FrlFpsSettingId, FrlFpsDisabled);
                session.Save();
                AppLog.Info($"FPS limit cleared for {exePath ?? "(global)"}.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("FPS limit clear failed: " + ex.Message);
        }
    }

    private static DriverSettingsProfile? ResolveProfile(DriverSettingsSession session, string? exePath, bool createIfMissing)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return session.BaseProfile;

        try
        {
            var existing = session.FindApplicationProfile(exePath);
            if (existing != null && existing.IsValid)
                return existing;
        }
        catch
        {
            // FindApplicationProfile throws when nothing matches — fall through to create.
        }

        if (!createIfMissing) return null;

        try
        {
            string exeName = System.IO.Path.GetFileName(exePath);
            string profileName = System.IO.Path.GetFileNameWithoutExtension(exePath) + "_DPM";
            var profile = DriverSettingsProfile.CreateProfile(session, profileName, null);
            ProfileApplication.CreateApplication(profile, exeName, exeName, null, null, false, null);
            return profile;
        }
        catch (Exception ex)
        {
            AppLog.Error("FPS limit: could not create a driver profile: " + ex.Message);
            return null;
        }
    }
}
