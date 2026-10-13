using System;
using System.Collections.Generic;
using System.IO;
using Visor.Setup;
using Xunit;

namespace Visor.Installer.Tests;

/// <summary>
/// PathGuard decides where Setup may install and what Uninstall may delete. These tests only read
/// well-known folder locations from the environment; nothing is created or removed.
/// </summary>
public class PathGuardTests
{
    private static string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
    private static readonly string Profile = Folder(Environment.SpecialFolder.UserProfile);
    private static readonly string AppData = Folder(Environment.SpecialFolder.ApplicationData);
    private static readonly string LocalAppData = Folder(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Windows = Folder(Environment.SpecialFolder.Windows);
    private static readonly string Temp = Path.GetTempPath().TrimEnd('\\');

    // ------------------------------------------------------------------ Normalize

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\\server\share\Visor")]   // UNC
    [InlineData(@"\\?\C:\Visor")]           // device path
    [InlineData(@"Visor")]                  // relative
    [InlineData(@"..\Visor")]
    [InlineData(@"C:Visor")]                // drive-relative
    [InlineData(@"\Visor")]                 // rooted without a drive
    [InlineData(@"C")]
    public void Normalize_rejects_anything_but_local_absolute_paths(string? input) =>
        Assert.Null(PathGuard.Normalize(input));

    [Theory]
    [InlineData(@"C:\Apps\Visor\", @"C:\Apps\Visor")]
    [InlineData(@"C:/Apps/Visor", @"C:\Apps\Visor")]
    [InlineData(@"  ""C:\Apps\Visor""  ", @"C:\Apps\Visor")]
    [InlineData(@"C:\Apps\.\Visor\..\Visor", @"C:\Apps\Visor")]
    [InlineData(@"c:\apps\visor", @"c:\apps\visor")]
    public void Normalize_returns_full_path_without_trailing_separator(string input, string expected) =>
        Assert.Equal(expected, PathGuard.Normalize(input));

    [Fact]
    public void Normalize_keeps_the_separator_of_a_drive_root()
    {
        Assert.Equal(@"C:\", PathGuard.Normalize(@"C:\"));
        Assert.Equal(@"C:\", PathGuard.Normalize(@"C:/"));
    }

    [Fact]
    public void Normalize_expands_environment_variables()
    {
        var expected = Path.Combine(LocalAppData, "Programs", "Visor");
        Assert.Equal(expected, PathGuard.Normalize(@"%LocalAppData%\Programs\Visor"));
    }

    // ------------------------------------------------------------------ SamePath / IsInsideOrSame

    [Theory]
    [InlineData(@"C:\Apps\Visor", @"c:\apps\visor\", true)]
    [InlineData(@"C:\Apps\Visor\", @"C:\Apps\Visor", true)]
    [InlineData(@"C:\Apps\Visor", @"C:\Apps\VisorX", false)]
    [InlineData(@"C:\Apps\Visor", @"C:\Apps\Visor\sub", false)]
    public void SamePath_ignores_case_and_trailing_separator(string a, string b, bool expected) =>
        Assert.Equal(expected, PathGuard.SamePath(a, b));

    [Theory]
    [InlineData(@"C:\A\B", @"C:\A", true)]
    [InlineData(@"C:\A\B\C", @"C:\A\", true)]
    [InlineData(@"C:\A", @"C:\A\", true)]
    [InlineData(@"c:\a\b", @"C:\A", true)]
    [InlineData(@"C:\AB", @"C:\A", false)]   // prefix of the name, not a child
    [InlineData(@"C:\A", @"C:\A\B", false)]  // parent is not inside the child
    public void IsInsideOrSame_requires_a_real_child_or_the_same_folder(string path, string dir, bool expected) =>
        Assert.Equal(expected, PathGuard.IsInsideOrSame(path, dir));

    // ------------------------------------------------------------------ IsProtected

    public static TheoryData<string> ProtectedDirs()
    {
        var data = new TheoryData<string>();
        void Add(string? p) { if (!string.IsNullOrEmpty(p)) data.Add(p!); }

        // Drive roots in every spelling
        Add(@"C:\"); Add(@"c:\"); Add(@"D:\"); Add(@"Z:\"); Add(@"C:");

        // User profile tree roots
        Add(Profile);
        Add(Path.GetDirectoryName(Profile));                 // C:\Users
        Add(Path.Combine(Profile, "AppData"));
        Add(Path.Combine(Profile, "Downloads"));
        Add(Folder(Environment.SpecialFolder.DesktopDirectory));
        Add(Folder(Environment.SpecialFolder.MyDocuments));
        Add(Folder(Environment.SpecialFolder.MyPictures));
        Add(Folder(Environment.SpecialFolder.MyMusic));
        Add(Folder(Environment.SpecialFolder.MyVideos));
        Add(AppData);
        Add(LocalAppData);
        Add(Path.Combine(LocalAppData, "Programs"));         // parent of the default install dir
        Add(Folder(Environment.SpecialFolder.StartMenu));
        Add(Folder(Environment.SpecialFolder.Programs));
        Add(Temp);

        // System roots
        Add(Folder(Environment.SpecialFolder.ProgramFiles));
        Add(Folder(Environment.SpecialFolder.ProgramFilesX86));
        Add(Folder(Environment.SpecialFolder.CommonApplicationData));
        Add(Folder(Environment.SpecialFolder.CommonDesktopDirectory));
        Add(Windows);
        Add(Path.Combine(Windows, "System32"));              // whole Windows tree

        // Visor's own data (whole trees), both names
        Add(Path.Combine(AppData, "Visor"));
        Add(Path.Combine(AppData, "Visor", "packs"));
        Add(Path.Combine(AppData, "Visor", "a", "b", "c"));
        Add(Path.Combine(AppData, "DisplayProfileManager"));
        Add(Path.Combine(AppData, "DisplayProfileManager", "sub", "deep"));
        Add(Product.AppDataDir);

        // Case and trailing-slash variants
        Add(Folder(Environment.SpecialFolder.ProgramFiles).ToUpperInvariant());
        Add(Folder(Environment.SpecialFolder.ProgramFiles) + "\\");
        Add(Folder(Environment.SpecialFolder.DesktopDirectory).ToLowerInvariant() + "\\");
        Add(Path.Combine(AppData, "visor").ToLowerInvariant() + "\\");
        Add(Profile.ToUpperInvariant() + "\\");
        return data;
    }

    [Theory]
    [MemberData(nameof(ProtectedDirs))]
    public void IsProtected_is_true_for_system_personal_and_app_data_roots(string dir) =>
        Assert.True(PathGuard.IsProtected(dir), dir);

    public static TheoryData<string> AllowedDirs()
    {
        var data = new TheoryData<string>();
        void Add(string? p) { if (!string.IsNullOrEmpty(p)) data.Add(p!); }

        Add(Product.DefaultInstallDir);                                       // %LocalAppData%\Programs\Visor
        Add(Path.Combine(LocalAppData, "Programs", "Visor") + "\\");
        Add(Path.Combine(LocalAppData, "Programs", "visor").ToLowerInvariant());
        Add(Path.Combine(LocalAppData, "Programs", "Visor", "nested"));
        Add(Path.Combine(LocalAppData, "Visor"));                             // sibling of Programs
        Add(@"C:\Apps\Visor");
        Add(@"D:\Games\Visor\");
        Add(@"c:\visor");
        Add(Path.Combine(Folder(Environment.SpecialFolder.ProgramFiles), "Visor"));
        Add(Path.Combine(Profile, "Downloads", "Visor"));
        Add(Path.Combine(Folder(Environment.SpecialFolder.DesktopDirectory), "Visor"));
        Add(Path.Combine(Folder(Environment.SpecialFolder.MyDocuments), "Tools", "Visor"));
        Add(Path.Combine(Temp, "VisorInstallerTests", "install"));
        Add(Path.Combine(AppData, "VisorX"));                                 // same prefix, different folder
        Add(Path.Combine(AppData, "DisplayProfileManagerBackup"));
        Add(Windows + "Old");                                                 // C:\WindowsOld is not C:\Windows\...
        return data;
    }

    [Theory]
    [MemberData(nameof(AllowedDirs))]
    public void IsProtected_is_false_for_a_dedicated_app_subfolder(string dir) =>
        Assert.False(PathGuard.IsProtected(dir), dir);

    [Fact]
    public void Default_install_dir_is_allowed_but_its_parent_is_not()
    {
        var dir = Product.DefaultInstallDir;
        Assert.EndsWith(@"\Programs\Visor", dir, StringComparison.OrdinalIgnoreCase);
        Assert.False(PathGuard.IsProtected(dir));
        Assert.True(PathGuard.IsProtected(Path.GetDirectoryName(dir)!));
    }

    [Fact]
    public void Normalized_user_input_pointing_at_a_protected_root_is_still_protected()
    {
        // The Browse dialog returns paths with mixed case / trailing separators; Normalize + IsProtected is the real pipeline.
        foreach (var raw in new[] { @"%APPDATA%\", @"%USERPROFILE%", @"%ProgramFiles%\", @"%TEMP%", @"C:\" })
        {
            var n = PathGuard.Normalize(raw);
            Assert.NotNull(n);
            Assert.True(PathGuard.IsProtected(n!), raw + " -> " + n);
        }
    }

    // ------------------------------------------------------------------ SafeRelative

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(@"\Visor.exe")]                  // rooted (no drive)
    [InlineData(@"/Visor.exe")]
    [InlineData(@"C:\Visor.exe")]                // rooted (drive)
    [InlineData(@"C:Visor.exe")]                 // drive-relative
    [InlineData(@"\\server\share\Visor.exe")]    // UNC
    [InlineData(@"\\?\C:\Visor.exe")]
    [InlineData(@"Visor.exe:Zone.Identifier")]   // alternate data stream
    [InlineData(@"a:b")]
    [InlineData(@"..")]
    [InlineData(@"..\Visor.exe")]
    [InlineData(@"Assets\..\Visor.exe")]         // even when it would stay inside
    [InlineData(@"Assets/../../x")]
    [InlineData(@".")]
    [InlineData(@".\Visor.exe")]
    [InlineData(@"Assets\.\x")]
    [InlineData(@"Assets\\x")]                   // empty segment
    [InlineData(@"Assets\")]                     // trailing separator = empty segment
    [InlineData(@"Assets\x.")]                   // trailing dot (Win32 strips it => aliasing)
    [InlineData(@"Assets\x ")]                   // trailing space
    [InlineData(@"Assets\ \x")]                  // whitespace-only segment
    [InlineData("Assets\\x|y")]                  // invalid path char
    [InlineData("Assets\\x\0y")]
    public void SafeRelative_rejects_rooted_traversing_or_malformed_paths(string? rel) =>
        Assert.Null(PathGuard.SafeRelative(rel));

    [Theory]
    [InlineData(@"Visor.exe", @"Visor.exe")]
    [InlineData(@"Assets\Sounds\click.wav", @"Assets\Sounds\click.wav")]
    [InlineData(@"Assets/Sounds/click.wav", @"Assets\Sounds\click.wav")]
    [InlineData(@"Assets/Packs\tarkov.json", @"Assets\Packs\tarkov.json")]
    [InlineData(@".visor-install.json", @".visor-install.json")]
    [InlineData(@"..x\y..z", @"..x\y..z")]       // dots inside names are fine
    [InlineData(@"a b\c d.txt", @"a b\c d.txt")]
    public void SafeRelative_accepts_plain_relative_paths_and_normalizes_slashes(string rel, string expected) =>
        Assert.Equal(expected, PathGuard.SafeRelative(rel));

    // ------------------------------------------------------------------ ResolveInside

    private const string Root = @"C:\Apps\Visor";

    [Theory]
    [InlineData(@"Visor.exe", @"C:\Apps\Visor\Visor.exe")]
    [InlineData(@"Assets/Sounds/x.wav", @"C:\Apps\Visor\Assets\Sounds\x.wav")]
    [InlineData(@"Assets\Packs\p.json", @"C:\Apps\Visor\Assets\Packs\p.json")]
    public void ResolveInside_returns_the_full_path_under_root(string rel, string expected) =>
        Assert.Equal(expected, PathGuard.ResolveInside(Root, rel));

    [Fact]
    public void ResolveInside_tolerates_trailing_separator_and_case_on_root()
    {
        Assert.Equal(@"C:\Apps\Visor\Visor.exe", PathGuard.ResolveInside(Root + "\\", "Visor.exe"));
        Assert.Equal(@"c:\apps\visor\Visor.exe", PathGuard.ResolveInside(@"c:\apps\visor", "Visor.exe"));
    }

    [Theory]
    [InlineData(@"..\x")]
    [InlineData(@"..\Visor\Visor.exe")]          // would land inside root, still rejected
    [InlineData(@"..\VisorX\a")]
    [InlineData(@"Assets\..\..\x")]
    [InlineData(@"C:\Apps\Visor\Visor.exe")]     // absolute, even if inside
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData(@"\Visor.exe")]
    [InlineData(@"\\srv\share\x")]
    [InlineData(@"x:y")]
    [InlineData(@"")]
    [InlineData(@".")]
    [InlineData(null)]
    public void ResolveInside_rejects_anything_that_does_not_stay_under_root(string? rel) =>
        Assert.Null(PathGuard.ResolveInside(Root, rel!));

    // ------------------------------------------------------------------ IsKnownAppFile

    [Theory]
    [InlineData("Visor.exe")]
    [InlineData("VISOR.EXE")]
    [InlineData("Uninstall.exe")]
    [InlineData(".visor-install.json")]
    [InlineData(".visor-install.json.tmp")]
    [InlineData(@"Assets\Sounds\click.wav")]
    [InlineData(@"Assets/Sounds/click.wav")]
    [InlineData(@"assets\sounds\CLICK.WAV")]
    [InlineData(@"Assets\Packs\tarkov.json")]
    public void IsKnownAppFile_accepts_the_shipped_file_set(string rel) =>
        Assert.True(PathGuard.IsKnownAppFile(rel), rel);

    [Theory]
    [InlineData("profiles.json")]
    [InlineData("Visor.exe.bak")]
    [InlineData("Visor-Setup.exe")]
    [InlineData("Setup.exe")]
    [InlineData("notes.txt")]
    [InlineData(".visor-install.json.bak")]
    [InlineData(@"Assets\Sounds")]               // the folder itself
    [InlineData(@"Assets\Sounds\sub\x.wav")]     // nested = not ours
    [InlineData(@"Assets\Packs\a\b.json")]
    [InlineData(@"Assets\Other\x.wav")]
    [InlineData(@"Assets\x.wav")]
    [InlineData(@"Sounds\click.wav")]
    [InlineData(@"..\Visor.exe")]
    [InlineData(@"sub\Visor.exe")]
    [InlineData("")]
    public void IsKnownAppFile_rejects_everything_else(string rel) =>
        Assert.False(PathGuard.IsKnownAppFile(rel), rel);

    // ------------------------------------------------------------------ Product

    [Fact]
    public void Product_version_is_a_clean_semver_from_Directory_Build_props()
    {
        Assert.DoesNotContain("+", Product.Version);
        Assert.True(Version.TryParse(Product.Version, out var v), Product.Version);
        Assert.True(v!.Major >= 2);
    }

    [Fact]
    public void Product_paths_point_where_the_README_says()
    {
        Assert.Equal(Path.Combine(LocalAppData, "Programs", "Visor"), Product.DefaultInstallDir);
        Assert.Equal(Path.Combine(AppData, "Visor"), Product.AppDataDir);
        Assert.Equal(".visor-install.json", Product.ManifestName);
        Assert.Equal("Uninstall.exe", Product.UninstallerName);
        Assert.True(PathGuard.IsProtected(Product.AppDataDir));
        Assert.False(PathGuard.IsProtected(Product.DefaultInstallDir));
    }
}
