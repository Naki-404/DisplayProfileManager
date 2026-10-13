using System;
using System.IO;

namespace Visor.Setup;

/// <summary>
/// Test stand-in for Installer\Native.cs (the real one P/Invokes SHGetKnownFolderPath, IShellLink, ...).
/// Only the member Safety.cs uses is provided. The real helper returns the shell's Downloads folder,
/// which on an unmodified profile is %USERPROFILE%\Downloads - the same value PathGuard already
/// protects explicitly, so the stub does not change which folders count as protected.
/// </summary>
internal static class Native
{
    public static string? GetDownloadsFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Downloads");
    }
}
