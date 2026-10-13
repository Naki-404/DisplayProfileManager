using System.IO;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

[Collection("PresentMonLocator")]   // AllowUnverified is process-global
public class PresentMonLocatorTests
{
    private static readonly byte[] FakeExe = System.Text.Encoding.ASCII.GetBytes("MZ not really a PE file - never executed");

    private static PresentMonFileCheck Never(string _) => new(PresentMonVerification.None, "test: unverified");
    private static PresentMonFileCheck Always(string _) => new(PresentMonVerification.AuthenticodeIntel, "test: Intel");

    [Fact]
    public void Missing_when_no_folder_has_the_exe()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        var loc = new PresentMonLocator(new[] { a.Path, b.Path, Path.Combine(a.Path, "nope") }, Always);
        var r = loc.Locate();
        Assert.Equal(PresentMonStatus.Missing, r.Status);
        Assert.Null(r.Path);
        Assert.Null(r.Version);
        Assert.Equal(PresentMonVerification.None, r.Verification);
        Assert.Contains(a.Path, r.Detail);
        Assert.Empty(loc.Candidates());
    }

    [Fact]
    public void Found_in_first_search_dir_with_injected_verifier()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        var exeA = a.File(PresentMonLocator.ExeName);
        var exeB = b.File(PresentMonLocator.ExeName);
        File.WriteAllBytes(exeA, FakeExe);
        File.WriteAllBytes(exeB, FakeExe);
        var loc = new PresentMonLocator(new[] { a.Path, b.Path }, Always);
        var r = loc.Locate();
        Assert.Equal(PresentMonStatus.Found, r.Status);
        Assert.Equal(exeA, r.Path, ignoreCase: true);
        Assert.Equal(PresentMonVerification.AuthenticodeIntel, r.Verification);
        Assert.Equal(2, loc.Candidates().Count);
    }

    [Fact]
    public void Unverified_when_present_but_not_signed_and_not_pinned()
    {
        using var a = new TempDir();
        var exe = a.File(PresentMonLocator.ExeName);
        File.WriteAllBytes(exe, FakeExe);
        var loc = new PresentMonLocator(new[] { a.Path });   // real verifier: unsigned garbage → None
        var r = loc.Locate();
        Assert.Equal(PresentMonStatus.Unverified, r.Status);
        Assert.Equal(exe, r.Path, ignoreCase: true);
        Assert.Equal(PresentMonVerification.None, r.Verification);
        Assert.Contains("not pinned", r.Detail);
    }

    [Fact]
    public void Pinned_hash_verifies_without_signature()
    {
        using var a = new TempDir();
        var exe = a.File(PresentMonLocator.ExeName);
        File.WriteAllBytes(exe, FakeExe);
        var hash = PresentMonLocator.ComputeSha256(exe);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
        var pinned = new Dictionary<string, string> { ["9.9.9"] = hash.ToUpperInvariant() };
        var loc = new PresentMonLocator(new[] { a.Path }, verify: null, pinnedHashes: pinned);
        var r = loc.Locate();
        Assert.Equal(PresentMonStatus.Found, r.Status);
        Assert.Equal(PresentMonVerification.PinnedHash, r.Verification);
        Assert.Contains("9.9.9", r.Detail);
    }

    [Fact]
    public void Placeholder_pinned_hash_never_matches()
    {
        Assert.All(PresentMonLocator.PinnedHashes.Values, h => Assert.Equal(64, h.Length));
        using var a = new TempDir();
        var exe = a.File(PresentMonLocator.ExeName);
        File.WriteAllBytes(exe, new byte[0]);   // SHA-256 of empty ≠ all zeros
        var loc = new PresentMonLocator(new[] { a.Path });
        Assert.Equal(PresentMonStatus.Unverified, loc.Locate().Status);
    }

    [Fact]
    public void Verified_candidate_later_in_the_list_beats_unverified_earlier()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        File.WriteAllBytes(a.File(PresentMonLocator.ExeName), FakeExe);
        var good = b.File(PresentMonLocator.ExeName);
        File.WriteAllBytes(good, new byte[] { 1, 2, 3 });
        var loc = new PresentMonLocator(new[] { a.Path, b.Path },
            p => string.Equals(p, good, StringComparison.OrdinalIgnoreCase)
                ? new PresentMonFileCheck(PresentMonVerification.PinnedHash, "ok")
                : new PresentMonFileCheck(PresentMonVerification.None, "bad"));
        var r = loc.Locate();
        Assert.Equal(PresentMonStatus.Found, r.Status);
        Assert.Equal(good, r.Path, ignoreCase: true);
    }

    [Fact]
    public void User_configured_path_accepts_file_or_folder()
    {
        using var a = new TempDir();
        using var user = new TempDir();
        var exe = user.File(PresentMonLocator.ExeName);
        File.WriteAllBytes(exe, FakeExe);
        var loc = new PresentMonLocator(new[] { a.Path }, Always);
        Assert.Equal(PresentMonStatus.Missing, loc.Locate().Status);
        Assert.Equal(exe, loc.Locate(user.Path).Path, ignoreCase: true);
        Assert.Equal(exe, loc.Locate(exe).Path, ignoreCase: true);
        Assert.Equal(PresentMonStatus.Missing, loc.Locate(Path.Combine(user.Path, "missing.exe")).Status);
    }

    [Fact]
    public void AllowUnverified_dev_flag_overrides_and_is_reported()
    {
        using var a = new TempDir();
        File.WriteAllBytes(a.File(PresentMonLocator.ExeName), FakeExe);
        var loc = new PresentMonLocator(new[] { a.Path }, Never);
        Assert.False(PresentMonLocator.AllowUnverified);
        try
        {
            PresentMonLocator.AllowUnverified = true;
            var r = loc.Locate();
            Assert.Equal(PresentMonStatus.Found, r.Status);
            Assert.Equal(PresentMonVerification.DevOverride, r.Verification);
            Assert.Contains("AllowUnverified", r.Detail);
        }
        finally { PresentMonLocator.AllowUnverified = false; }
        Assert.Equal(PresentMonStatus.Unverified, loc.Locate().Status);
    }

    [Fact]
    public void Verifier_exception_is_treated_as_unverified()
    {
        using var a = new TempDir();
        File.WriteAllBytes(a.File(PresentMonLocator.ExeName), FakeExe);
        var loc = new PresentMonLocator(new[] { a.Path }, _ => throw new InvalidOperationException("boom"));
        var r = loc.Locate();
        Assert.Equal(PresentMonStatus.Unverified, r.Status);
        Assert.Contains("boom", r.Detail);
    }

    [Fact]
    public void File_version_is_null_for_a_non_pe_file_and_read_for_a_real_one()
    {
        using var a = new TempDir();
        var fake = a.File(PresentMonLocator.ExeName);
        File.WriteAllBytes(fake, FakeExe);
        Assert.Null(PresentMonLocator.ReadFileVersion(fake));
        // Our own test assembly carries a version resource.
        var self = typeof(PresentMonLocatorTests).Assembly.Location;
        var v = PresentMonLocator.ReadFileVersion(self);
        Assert.False(string.IsNullOrWhiteSpace(v));
    }

    [Fact]
    public void Authenticode_check_rejects_unsigned_file_without_throwing()
    {
        using var a = new TempDir();
        var fake = a.File(PresentMonLocator.ExeName);
        File.WriteAllBytes(fake, FakeExe);
        var r = AuthenticodeVerifier.Check(fake);
        Assert.False(r.Trusted);
        Assert.Null(r.Signer);
        Assert.False(AuthenticodeVerifier.Check(Path.Combine(a.Path, "none.exe")).Trusted);
        Assert.Null(AuthenticodeVerifier.ReadSigner(fake));
    }

    [Fact]
    public void Default_search_dirs_are_app_and_localappdata()
    {
        var dirs = PresentMonLocator.DefaultSearchDirs().ToList();
        Assert.Equal(2, dirs.Count);
        Assert.EndsWith(@"tools\presentmon", dirs[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"\Visor\tools\presentmon", dirs[1], StringComparison.OrdinalIgnoreCase);
    }
}
