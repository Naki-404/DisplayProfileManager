using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Visor.Services;

public enum PresentMonStatus
{
    /// <summary>A PresentMon.exe exists and passed verification.</summary>
    Found,
    /// <summary>No PresentMon.exe in any search location.</summary>
    Missing,
    /// <summary>A file exists but is neither Intel-signed nor on the pinned hash list (and <see cref="PresentMonLocator.AllowUnverified"/> is off).</summary>
    Unverified
}

public enum PresentMonVerification
{
    None,
    /// <summary>Authenticode chain valid and the signer's name contains "Intel".</summary>
    AuthenticodeIntel,
    /// <summary>SHA-256 matches a pinned, known-good release hash.</summary>
    PinnedHash,
    /// <summary>Accepted only because <see cref="PresentMonLocator.AllowUnverified"/> was set (dev builds).</summary>
    DevOverride
}

public sealed record PresentMonLocateResult(
    PresentMonStatus Status,
    string? Path,
    string? Version,
    PresentMonVerification Verification,
    string Detail)
{
    public static PresentMonLocateResult Missing(string detail) => new(PresentMonStatus.Missing, null, null, PresentMonVerification.None, detail);
}

/// <summary>Result of looking at one candidate file (no execution involved).</summary>
public readonly record struct PresentMonFileCheck(PresentMonVerification Verification, string Detail);

/// <summary>
/// Finds and verifies <c>PresentMon.exe</c> without ever running it. Search order:
/// <c>&lt;app&gt;\tools\presentmon\</c>, <c>%LocalAppData%\Visor\tools\presentmon\</c>, then a
/// user-configured path (file or folder). A candidate counts as verified when its Authenticode
/// signature chains to a trusted root and the signer's subject contains "Intel", OR when its
/// SHA-256 matches <see cref="PinnedHashes"/>. The pinned table is the fallback for machines
/// without catalog/CRL access and the only path for an unsigned debug build of PresentMon.
/// </summary>
public sealed class PresentMonLocator
{
    public const string ExeName = "PresentMon.exe";
    public const string RelativeToolDir = @"tools\presentmon";

    /// <summary>
    /// Dev-only escape hatch: accept a PresentMon.exe that fails both checks. Defaults to false
    /// and is never read from config — set it from a debugger / dev build switch only.
    /// </summary>
    public static bool AllowUnverified { get; set; }

    /// <summary>
    /// Known-good SHA-256 (lower-case hex) of official <c>PresentMon.exe</c> builds, keyed by
    /// file version. Fill in from the release checked into <c>tools\presentmon\</c> by
    /// <c>build-release.ps1 -FetchPresentMon</c>; a placeholder never matches a real file.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PinnedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // TODO(presentmon): replace with the SHA-256 of the pinned release's PresentMon.exe
        // (same value as $PresentMonSha256 in build-release.ps1). Placeholder = never matches.
        ["2.3.1"] = "0000000000000000000000000000000000000000000000000000000000000000",
    };

    private readonly IReadOnlyList<string> _searchDirs;
    private readonly Func<string, PresentMonFileCheck> _verify;
    private readonly IReadOnlyDictionary<string, string> _pinned;

    /// <summary>Production locator: default folders, real Authenticode + pinned-hash checks.</summary>
    public PresentMonLocator() : this(DefaultSearchDirs(), null, null) { }

    /// <summary>Test / custom locator: explicit folders and an optional verifier override.</summary>
    public PresentMonLocator(IEnumerable<string> searchDirs, Func<string, PresentMonFileCheck>? verify = null,
        IReadOnlyDictionary<string, string>? pinnedHashes = null)
    {
        _searchDirs = searchDirs?.Where(d => !string.IsNullOrWhiteSpace(d)).ToArray() ?? Array.Empty<string>();
        _pinned = pinnedHashes ?? PinnedHashes;
        _verify = verify ?? VerifyFile;
    }

    public static string AppToolDir => Path.Combine(AppContext.BaseDirectory, RelativeToolDir);

    public static string LocalAppDataToolDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Visor", RelativeToolDir);

    public static IEnumerable<string> DefaultSearchDirs()
    {
        yield return AppToolDir;
        yield return LocalAppDataToolDir;
    }

    /// <summary>Candidate files in search order (existing only); the user path may be a file or a folder.</summary>
    public IReadOnlyList<string> Candidates(string? userConfiguredPath = null)
    {
        var list = new List<string>();
        foreach (var dir in _searchDirs)
            AddIfFile(list, Path.Combine(dir, ExeName));
        if (!string.IsNullOrWhiteSpace(userConfiguredPath))
        {
            var p = userConfiguredPath.Trim();
            try
            {
                if (Directory.Exists(p)) AddIfFile(list, Path.Combine(p, ExeName));
                else AddIfFile(list, p);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                AppLog.Warn(ex, "PresentMon user path");
            }
        }
        return list;
    }

    /// <summary>
    /// First verified candidate wins; otherwise the first existing (unverified) candidate is
    /// reported as <see cref="PresentMonStatus.Unverified"/> so the UI can show where it looked.
    /// </summary>
    public PresentMonLocateResult Locate(string? userConfiguredPath = null)
    {
        var candidates = Candidates(userConfiguredPath);
        if (candidates.Count == 0)
        {
            var looked = string.Join("; ", _searchDirs.Concat(string.IsNullOrWhiteSpace(userConfiguredPath) ? Array.Empty<string>() : new[] { userConfiguredPath! }));
            return PresentMonLocateResult.Missing($"{ExeName} not found in: {looked}");
        }

        PresentMonLocateResult? firstUnverified = null;
        foreach (var path in candidates)
        {
            string? version = ReadFileVersion(path);
            PresentMonFileCheck check;
            try { check = _verify(path); }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "PresentMon verify");
                check = new PresentMonFileCheck(PresentMonVerification.None, "verification threw: " + ex.Message);
            }
            if (check.Verification != PresentMonVerification.None)
                return new PresentMonLocateResult(PresentMonStatus.Found, path, version, check.Verification, check.Detail);
            if (AllowUnverified)
                return new PresentMonLocateResult(PresentMonStatus.Found, path, version, PresentMonVerification.DevOverride,
                    "AllowUnverified is on; " + check.Detail);
            firstUnverified ??= new PresentMonLocateResult(PresentMonStatus.Unverified, path, version, PresentMonVerification.None, check.Detail);
        }
        return firstUnverified!;
    }

    /// <summary>Authenticode (Intel signer) first, pinned SHA-256 second. Never executes the file.</summary>
    public PresentMonFileCheck VerifyFile(string path)
    {
        var auth = AuthenticodeVerifier.Check(path);
        if (auth.Trusted && auth.Signer != null && auth.Signer.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return new PresentMonFileCheck(PresentMonVerification.AuthenticodeIntel, $"Authenticode: {auth.Signer}");

        string hash;
        try { hash = ComputeSha256(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PresentMonFileCheck(PresentMonVerification.None, $"cannot hash: {ex.Message}; {auth.Detail}");
        }
        foreach (var kv in _pinned)
        {
            if (string.Equals(kv.Value, hash, StringComparison.OrdinalIgnoreCase))
                return new PresentMonFileCheck(PresentMonVerification.PinnedHash, $"SHA-256 matches pinned {kv.Key}");
        }
        return new PresentMonFileCheck(PresentMonVerification.None,
            $"not Intel-signed ({auth.Detail}) and SHA-256 {hash[..16]}… is not pinned");
    }

    public static string ComputeSha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>File version from the PE resource ("2.3.1" style), or null.</summary>
    public static string? ReadFileVersion(string path)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(path);
            var v = fvi.ProductVersion;
            if (string.IsNullOrWhiteSpace(v)) v = fvi.FileVersion;
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim();
            int plus = v.IndexOf('+');
            if (plus > 0) v = v[..plus];
            return v.Length == 0 ? null : v;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static void AddIfFile(List<string> list, string path)
    {
        try
        {
            if (File.Exists(path) && !list.Contains(path, StringComparer.OrdinalIgnoreCase))
                list.Add(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLog.Warn(ex, "PresentMon candidate");
        }
    }
}

/// <summary>Thin WinVerifyTrust wrapper: Authenticode chain validity + signer subject.</summary>
public static class AuthenticodeVerifier
{
    public readonly record struct Result(bool Trusted, string? Signer, string Detail);

    private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;
    private const uint WTD_SAFER_FLAG = 0x100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

    /// <summary>Verifies the embedded signature (no network revocation checks; cache-only URL retrieval).</summary>
    public static Result Check(string path)
    {
        if (!OperatingSystem.IsWindows()) return new Result(false, null, "not Windows");
        if (!File.Exists(path)) return new Result(false, null, "file missing");

        int status;
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_SAFER_FLAG | WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            try
            {
                status = WinVerifyTrust(IntPtr.Zero, ActionGenericVerifyV2, ref data);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return new Result(false, null, "wintrust unavailable: " + ex.Message);
            }
            finally
            {
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                try { WinVerifyTrust(IntPtr.Zero, ActionGenericVerifyV2, ref data); } catch { }
            }
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }

        string? signer = ReadSigner(path);
        if (status == 0)
            return new Result(true, signer, "trusted" + (signer != null ? " / " + signer : ""));
        return new Result(false, signer, $"WinVerifyTrust 0x{status:X8}" + (signer != null ? " / " + signer : " / no signature"));
    }

    /// <summary>Simple name of the signing certificate's subject (e.g. "Intel Corporation"), or null.</summary>
    public static string? ReadSigner(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile has no non-obsolete replacement for this use
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var name = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return string.IsNullOrWhiteSpace(name) ? cert.Subject : name;
        }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
