using NvAPIWrapper.DRS;
using NvAPIWrapper.Native.DRS;

namespace Visor.Services;

/// <summary>
/// Best-effort per-application FPS cap via the NVIDIA driver settings (DRS) API.
/// Uses the public "Frame Rate Limiter" setting (FRL_FPS_ID = 0x10835002, documented in
/// NVIDIA's NvApiDriverSettings.h) - the same setting NVIDIA Control Panel / Profile
/// Inspector expose as "Max Frame Rate". AMD/Intel have no equivalent public driver API,
/// so this is NVIDIA-only.
/// Visor only writes the setting when a profile asks for a cap, remembers what was there
/// before (<see cref="FpsOriginal"/>) and puts exactly that back.
/// Calls are slow (DRS load/save) — run them off the UI thread.
/// </summary>
public static class FpsLimitService
{
    private const uint FrlFpsSettingId = 0x10835002;
    private const uint FrlFpsMax = 0x000003FF; // driver-enforced cap (~1023 fps)
    private const string ProfileSuffix = "_Visor";
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);

    private static bool? _available;
    private static DateTime _checkedUtc = DateTime.MinValue;
    /// <summary>Serializes DRS sessions; NvApiHost takes it before unloading NvAPI.</summary>
    internal static readonly object Gate = new();

    /// <summary>FRL state of an application profile before Visor changed it.</summary>
    public sealed record FpsOriginal(bool HadSetting, bool WasPredefined, uint Value, bool CreatedProfile);

    /// <summary>
    /// True once NvAPI + a DRS session could be created. A failure is re-checked after a minute
    /// instead of being cached for the whole run.
    /// </summary>
    public static bool IsNvidiaAvailable
    {
        get
        {
            lock (Gate)
            {
                if (_available == true) return true;
                if (_available == false && DateTime.UtcNow - _checkedUtc < RetryAfter) return false;
                _available = TryCreateSession();
                _checkedUtc = DateTime.UtcNow;
                return _available.Value;
            }
        }
    }

    private static bool TryCreateSession()
    {
        if (!NvApiHost.TryInitialize()) return false;
        try
        {
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
    /// Sets a per-application FPS cap (1..1023).
    /// </summary>
    /// <param name="exePathOrName">Full path or exe file name of the target process.</param>
    /// <returns>The setting's state before this call, or null when nothing was changed.</returns>
    public static FpsOriginal? TryApply(string exePathOrName, int fpsLimit)
    {
        if (fpsLimit <= 0 || string.IsNullOrWhiteSpace(exePathOrName)) return null;
        if (!IsNvidiaAvailable)
        {
            AppLog.Info("FPS limit not available (no NVIDIA DRS on this system).");
            return null;
        }

        try
        {
            lock (Gate)
            {
                using var session = DriverSettingsSession.CreateAndLoad();
                var profile = FindProfile(session, exePathOrName);
                bool created = false;
                if (profile == null)
                {
                    profile = CreateProfile(session, exePathOrName);
                    if (profile == null) return null;
                    created = true;
                }

                var original = ReadOriginal(profile, created);
                uint value = (uint)Math.Clamp(fpsLimit, 1, (int)FrlFpsMax);
                profile.SetSetting(FrlFpsSettingId, value);
                session.Save();
                AppLog.Info($"FPS limit -> {value} for {System.IO.Path.GetFileName(exePathOrName)} (NVIDIA DRS).");
                return original;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("FPS limit apply failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Puts the Frame Rate Limiter back exactly as it was before <see cref="TryApply"/>.</summary>
    public static bool Restore(string exePathOrName, FpsOriginal original)
    {
        if (string.IsNullOrWhiteSpace(exePathOrName)) return false;
        if (!IsNvidiaAvailable) return false;
        try
        {
            lock (Gate)
            {
                using var session = DriverSettingsSession.CreateAndLoad();
                var profile = FindProfile(session, exePathOrName);
                if (profile == null) return true; // nothing left to undo

                if (original.CreatedProfile)
                    profile.Delete();
                else if (original.HadSetting && original.WasPredefined)
                    profile.RestoreSettingToDefault(FrlFpsSettingId);
                else if (original.HadSetting)
                    profile.SetSetting(FrlFpsSettingId, original.Value);
                else
                    profile.DeleteSetting(FrlFpsSettingId);

                session.Save();
                AppLog.Info($"FPS limit restored for {System.IO.Path.GetFileName(exePathOrName)}.");
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("FPS limit restore failed: " + ex.Message);
            return false;
        }
    }

    private static FpsOriginal ReadOriginal(DriverSettingsProfile profile, bool created)
    {
        if (created) return new FpsOriginal(false, false, 0, true);
        try
        {
            var setting = profile.GetSetting(FrlFpsSettingId);
            if (setting == null || setting.SettingLocation != DRSSettingLocation.CurrentProfile)
                return new FpsOriginal(false, false, 0, false);
            uint value = setting.CurrentValue is uint u ? u : Convert.ToUInt32(setting.CurrentValue);
            return new FpsOriginal(true, setting.IsCurrentValuePredefined, value, false);
        }
        catch
        {
            // GetSetting throws when the profile has no such setting.
            return new FpsOriginal(false, false, 0, false);
        }
    }

    private static DriverSettingsProfile? FindProfile(DriverSettingsSession session, string exePathOrName)
    {
        // FindApplicationProfile throws when nothing matches — each lookup gets its own try.
        foreach (var name in new[] { exePathOrName, System.IO.Path.GetFileName(exePathOrName) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var existing = session.FindApplicationProfile(name);
                if (existing != null && existing.IsValid)
                    return existing;
            }
            catch
            {
                // not found under this name
            }
        }
        return null;
    }

    private static DriverSettingsProfile? CreateProfile(DriverSettingsSession session, string exePathOrName)
    {
        try
        {
            string exeName = System.IO.Path.GetFileName(exePathOrName);
            string profileName = System.IO.Path.GetFileNameWithoutExtension(exePathOrName) + ProfileSuffix;
            DriverSettingsProfile? profile = null;
            try { profile = session.FindProfileByName(profileName); }
            catch { /* not found */ }
            profile ??= DriverSettingsProfile.CreateProfile(session, profileName, null);
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
