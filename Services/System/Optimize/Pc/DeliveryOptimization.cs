using System.Globalization;
using System.IO;
using System.Management;
using Visor.Services.Elevation;

namespace Visor.Services.Optimize.Pc;

/// <summary>Why the Delivery Optimization cache size is not shown.</summary>
public enum DeliveryOptimizationSizeReason
{
    None,
    /// <summary>The service's WMI status did not answer and the cache folder is readable only by administrators.</summary>
    NeedsAdmin,
    /// <summary>Delivery Optimization is not present / not readable at all.</summary>
    Unavailable
}

/// <summary>Measured cache size (null + reason when it could not be read).</summary>
public sealed record DeliveryOptimizationStatus(long? CacheBytes, DeliveryOptimizationSizeReason Reason);

public enum DeliveryOptimizationCleanStatus
{
    Cleaned,
    /// <summary>The user clicked "No" on the UAC prompt — nothing changed.</summary>
    Declined,
    /// <summary>The helper refused the op (allowlist) — nothing changed.</summary>
    Rejected,
    Failed
}

/// <summary>Outcome of one clean. <see cref="FreedBytes"/> is null when it could not be measured.</summary>
public sealed record DeliveryOptimizationCleanResult(DeliveryOptimizationCleanStatus Status, long? FreedBytes = null, string? Code = null);

/// <summary>
/// Read-only size probes for the Delivery Optimization cache. Used by the unelevated UI and by the elevated helper
/// (before / after the delete), so it never logs: the helper must not touch AppLog.
/// </summary>
public static class DeliveryOptimizationCache
{
    /// <summary>The service's WMI namespace (the same one the documented DeliveryOptimization cmdlets use).</summary>
    public const string WmiNamespace = @"root\Microsoft\Windows\DeliveryOptimization";
    public const string WmiQuery = "SELECT CacheSizeBytes FROM MSFT_DOCurrentStatus";

    /// <summary>%WINDIR%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache.</summary>
    public static string CacheFolder()
    {
        string windows;
        try { windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows); }
        catch (Exception) { windows = ""; }
        return windows.Length == 0
            ? ""
            : Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows",
                "DeliveryOptimization", "Cache");
    }

    /// <summary>
    /// <c>MSFT_DOCurrentStatus.CacheSizeBytes</c> (what <c>Get-DeliveryOptimizationPerfSnap</c> reports). Readable by a
    /// standard user on Windows 10 / 11. Null when WMI does not answer. Never throws.
    /// </summary>
    public static long? ReadWmiBytes()
    {
        try
        {
            var scope = new ManagementScope(WmiNamespace);
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(WmiQuery));
            searcher.Options.Timeout = TimeSpan.FromSeconds(10);
            using var results = searcher.Get();
            foreach (ManagementBaseObject mo in results)
            {
                using (mo)
                {
                    object? raw = mo["CacheSizeBytes"];
                    if (raw == null) continue;
                    return Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                }
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Sum of file sizes under the cache folder (reparse points never followed). Null + <paramref name="denied"/> when
    /// the folder is not readable (standard users: NetworkService owns it).
    /// </summary>
    public static long? ReadFolderBytes(string? folder, out bool denied)
    {
        denied = false;
        if (string.IsNullOrEmpty(folder)) return null;
        try
        {
            var root = new DirectoryInfo(folder);
            // "Not found" may just mean "not visible to this user": unknown, never 0.
            if (!root.Exists) return null;
            long total = 0;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                foreach (var e in dir.EnumerateFileSystemInfos())
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (e is DirectoryInfo d) stack.Push(d);
                    else if (e is FileInfo f) total += f.Length;
                }
            }
            return total;
        }
        catch (UnauthorizedAccessException)
        {
            denied = true;
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>WMI first, then the folder. Never throws.</summary>
    public static DeliveryOptimizationStatus Read()
    {
        var wmi = ReadWmiBytes();
        if (wmi != null) return new DeliveryOptimizationStatus(wmi, DeliveryOptimizationSizeReason.None);
        var folder = ReadFolderBytes(CacheFolder(), out bool denied);
        if (folder != null) return new DeliveryOptimizationStatus(folder, DeliveryOptimizationSizeReason.None);
        return new DeliveryOptimizationStatus(null, denied ? DeliveryOptimizationSizeReason.NeedsAdmin : DeliveryOptimizationSizeReason.Unavailable);
    }

    /// <summary>Best effort byte count for before / after comparisons (WMI, else the folder).</summary>
    public static long? ReadBytes() => ReadWmiBytes() ?? ReadFolderBytes(CacheFolder(), out _);

    /// <summary>before − after, clamped at 0; null when either side is unknown.</summary>
    public static long? Freed(long? before, long? after) =>
        before is { } b && after is { } a ? Math.Max(0, b - a) : null;
}

/// <summary>
/// Delivery Optimization cache: measure (no admin) and clear (one UAC prompt, elevated helper op
/// <see cref="ElevatedOpKind.DeliveryOptimizationClear"/> → the documented <c>Delete-DeliveryOptimizationCache -Force</c>).
/// Not journaled: a cache cannot be restored, and nothing needs restoring — Windows Update downloads what it needs
/// again from Microsoft. Pinned files are kept (the cmdlet's default).
/// </summary>
public sealed class DeliveryOptimizationService
{
    private readonly IElevationBroker _broker;
    private readonly Func<DeliveryOptimizationStatus> _read;

    /// <param name="broker">Elevated helper.</param>
    /// <param name="read">Size probe; default <see cref="DeliveryOptimizationCache.Read"/>. Tests inject a fake.</param>
    public DeliveryOptimizationService(IElevationBroker broker, Func<DeliveryOptimizationStatus>? read = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _read = read ?? DeliveryOptimizationCache.Read;
    }

    /// <summary>The single op the clean sends (no parameters — nothing user-supplied ever reaches the helper).</summary>
    public static ElevatedOp ClearOp() => new(ElevatedOpKind.DeliveryOptimizationClear, "");

    /// <summary>Current size (blocking WMI call — off the UI thread). Never throws.</summary>
    public DeliveryOptimizationStatus Read()
    {
        try { return _read(); }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Delivery Optimization size");
            return new DeliveryOptimizationStatus(null, DeliveryOptimizationSizeReason.Unavailable);
        }
    }

    /// <summary>One UAC prompt → the helper clears the cache → freed bytes (helper measurement, else before − after).</summary>
    public async Task<DeliveryOptimizationCleanResult> ClearAsync(CancellationToken ct = default)
    {
        long? before = Read().CacheBytes;
        ElevatedBatchResult r;
        try
        {
            r = await _broker.RunAsync(new[] { ClearOp() }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Delivery Optimization clear");
            return new DeliveryOptimizationCleanResult(DeliveryOptimizationCleanStatus.Failed, Code: SafeActionCodes.ElevationFailed);
        }

        switch (r.Outcome)
        {
            case ElevationOutcome.Declined:
                return new DeliveryOptimizationCleanResult(DeliveryOptimizationCleanStatus.Declined, Code: SafeActionCodes.ElevationDeclined);
            case ElevationOutcome.Rejected:
                return new DeliveryOptimizationCleanResult(DeliveryOptimizationCleanStatus.Rejected, Code: SafeActionCodes.ElevationRejected);
            case ElevationOutcome.Completed when r.AllOk:
            {
                long? freed = ParseFreed(r.Results[0].Output) ?? DeliveryOptimizationCache.Freed(before, Read().CacheBytes);
                AppLog.Info("Delivery Optimization cache cleared" + (freed is { } f ? $": freed {f} bytes." : "."));
                return new DeliveryOptimizationCleanResult(DeliveryOptimizationCleanStatus.Cleaned, freed);
            }
            default:
                AppLog.Warn("Delivery Optimization clear failed: " + (r.Results.FirstOrDefault()?.ErrorCode ?? r.ErrorCode ?? "?"));
                return new DeliveryOptimizationCleanResult(DeliveryOptimizationCleanStatus.Failed, Code:
                    r.Outcome == ElevationOutcome.Completed ? SafeActionCodes.ApplyFailed : SafeActionCodes.ElevationFailed);
        }
    }

    /// <summary>The helper's output is a decimal byte count when it could measure before and after.</summary>
    public static long? ParseFreed(string? output) =>
        long.TryParse(output, NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : null;
}
