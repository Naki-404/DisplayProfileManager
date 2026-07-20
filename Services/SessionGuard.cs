using System.IO;

namespace DisplayProfileManager.Services;

/// <summary>
/// Dirty-session flag under AppData. If the process dies without a clean exit,
/// the next launch restores identity gamma + driver vibrance.
/// </summary>
public static class SessionGuard
{
    private static string LockPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DisplayProfileManager",
        "session.lock");

    public static bool WasDirtyShutdown()
    {
        try { return File.Exists(LockPath); }
        catch { return false; }
    }

    public static void MarkRunning()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
            File.WriteAllText(LockPath, DateTime.UtcNow.ToString("o"));
        }
        catch (Exception ex)
        {
            AppLog.Error("SessionGuard.MarkRunning: " + ex.Message);
        }
    }

    public static void MarkCleanExit()
    {
        try
        {
            if (File.Exists(LockPath))
                File.Delete(LockPath);
        }
        catch (Exception ex)
        {
            AppLog.Error("SessionGuard.MarkCleanExit: " + ex.Message);
        }
    }
}
