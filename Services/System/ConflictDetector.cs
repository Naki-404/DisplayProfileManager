using System.Diagnostics;
using Microsoft.Win32;

namespace DisplayProfileManager.Services;

/// <summary>
/// Best-effort detection of other tools that also touch gamma/color/vibrance, so users
/// understand why DPM's settings might get fought or overridden. Never throws.
/// </summary>
public static class ConflictDetector
{
    /// <summary>Process names (no .exe) commonly used by other gamma/color utilities.</summary>
    private static readonly string[] ConflictingProcessNames =
    {
        "flux", "f.lux", "LightBulb", "Gammy", "NVIDIA Share"
    };

    public sealed record Result(List<string> ConflictingApps, bool NightLightOn)
    {
        public bool HasAny => ConflictingApps.Count > 0 || NightLightOn;
    }

    public static Result Scan()
    {
        var found = new List<string>();
        try
        {
            var running = Process.GetProcesses();
            try
            {
                foreach (var name in ConflictingProcessNames)
                {
                    if (running.Any(p => MatchesName(p, name)))
                        found.Add(name);
                }
            }
            finally
            {
                foreach (var p in running)
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("ConflictDetector process scan: " + ex.Message);
        }

        bool nightLight = false;
        try { nightLight = IsNightLightOn(); }
        catch (Exception ex) { AppLog.Error("ConflictDetector night light check: " + ex.Message); }

        return new Result(found, nightLight);
    }

    /// <summary>Human-readable one-line summary for a toast, or null if nothing found.</summary>
    public static string? Describe(Result r)
    {
        if (!r.HasAny) return null;
        var parts = new List<string>();
        if (r.ConflictingApps.Count > 0)
            parts.Add("Other color tool running: " + string.Join(", ", r.ConflictingApps));
        if (r.NightLightOn)
            parts.Add("Windows Night Light is on");
        return string.Join(" · ", parts);
    }

    private static bool MatchesName(Process p, string want)
    {
        try { return p.ProcessName.Equals(want, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>
    /// Undocumented binary blob — heuristic only, may be wrong on some Windows builds.
    /// See: HKCU...bluelightreductionstate\Data, bytes[23]==0x10 && bytes[24]==0x00 when active.
    /// </summary>
    private static bool IsNightLightOn()
    {
        const string keyPath =
            @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current" +
            @"\default$windows.data.bluelightreduction.bluelightreductionstate" +
            @"\windows.data.bluelightreduction.bluelightreductionstate";

        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        if (key?.GetValue("Data") is byte[] data && data.Length > 24)
            return data[23] == 0x10 && data[24] == 0x00;
        return false;
    }
}
