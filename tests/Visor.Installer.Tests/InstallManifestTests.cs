using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Visor.Setup;
using Xunit;

namespace Visor.Installer.Tests;

/// <summary>
/// .visor-install.json is the only thing Uninstall trusts when deciding what to delete, so Load must
/// refuse anything that is not clearly ours and strip every entry that could point outside the folder.
/// </summary>
public class InstallManifestTests
{
    private static string ManifestJson(string app = "Visor", params string[] files)
    {
        var quoted = new List<string>();
        foreach (var f in files) quoted.Add("\"" + f.Replace("\\", "\\\\") + "\"");
        return "{\"app\":\"" + app + "\",\"version\":\"2.0.0\",\"installedUtc\":\"2026-10-13T00:00:00Z\",\"files\":["
            + string.Join(",", quoted) + "]}";
    }

    [Fact]
    public void PathIn_is_the_manifest_file_inside_the_folder()
    {
        Assert.Equal(@"C:\Apps\Visor\.visor-install.json", InstallManifest.PathIn(@"C:\Apps\Visor"));
    }

    [Fact]
    public void Load_returns_null_when_the_folder_does_not_exist()
    {
        using var tmp = new TempDir();
        Assert.Null(InstallManifest.Load(Path.Combine(tmp.Path, "missing")));
    }

    [Fact]
    public void Load_returns_null_when_there_is_no_manifest()
    {
        using var tmp = new TempDir();
        tmp.Write("Visor.exe", "not really");
        Assert.Null(InstallManifest.Load(tmp.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{\"files\":[\"Visor.exe\"]}")]            // no app name
    [InlineData("{\"app\":\"\",\"files\":[\"Visor.exe\"]}")]
    [InlineData("{\"app\":null,\"files\":[\"Visor.exe\"]}")]
    public void Load_returns_null_for_corrupt_or_foreign_content(string content)
    {
        using var tmp = new TempDir();
        tmp.Write(Product.ManifestName, content);
        Assert.Null(InstallManifest.Load(tmp.Path));
    }

    [Theory]
    [InlineData("Other")]
    [InlineData("DisplayProfileManager")]
    [InlineData("Visor2")]
    [InlineData(" Visor")]
    public void Load_rejects_a_manifest_of_another_app(string app)
    {
        using var tmp = new TempDir();
        tmp.Write(Product.ManifestName, ManifestJson(app, "Visor.exe"));
        Assert.Null(InstallManifest.Load(tmp.Path));
    }

    [Fact]
    public void Load_accepts_the_app_name_case_insensitively()
    {
        using var tmp = new TempDir();
        tmp.Write(Product.ManifestName, ManifestJson("visor", "Visor.exe"));
        var m = InstallManifest.Load(tmp.Path);
        Assert.NotNull(m);
        Assert.Equal(new[] { "Visor.exe" }, m!.Files);
    }

    [Fact]
    public void Load_reads_a_valid_manifest_and_normalizes_slashes()
    {
        using var tmp = new TempDir();
        tmp.Write(Product.ManifestName, ManifestJson("Visor", "Visor.exe", "Assets/Sounds/click.wav", @"Assets\Packs\tarkov.json", "Uninstall.exe"));

        var m = InstallManifest.Load(tmp.Path);

        Assert.NotNull(m);
        Assert.Equal("Visor", m!.App);
        Assert.Equal("2.0.0", m.Version);
        Assert.Equal("2026-10-13T00:00:00Z", m.InstalledUtc);
        Assert.Equal(new[] { "Visor.exe", @"Assets\Sounds\click.wav", @"Assets\Packs\tarkov.json", "Uninstall.exe" }, m.Files);
    }

    [Fact]
    public void Load_drops_entries_that_could_escape_the_install_folder()
    {
        using var tmp = new TempDir();
        tmp.Write(Product.ManifestName, ManifestJson("Visor",
            "Visor.exe",
            @"..\..\Windows\explorer.exe",
            @"../Visor/Visor.exe",
            @"Assets\..\Visor.exe",
            @"C:\Windows\System32\drivers\etc\hosts",
            @"\Users\x\file",
            @"\\server\share\x",
            @"Visor.exe:Zone.Identifier",
            @"Assets\\x",
            @"Assets\x.",
            @".\Uninstall.exe",
            "",
            "   "));

        var m = InstallManifest.Load(tmp.Path);

        Assert.NotNull(m);
        Assert.Equal(new[] { "Visor.exe" }, m!.Files);
    }

    [Fact]
    public void Load_excludes_the_manifest_itself_and_dedupes_case_insensitively()
    {
        using var tmp = new TempDir();
        tmp.Write(Product.ManifestName, ManifestJson("Visor",
            "Visor.exe", "VISOR.EXE", "visor.exe", ".visor-install.json", ".VISOR-INSTALL.JSON", "Uninstall.exe", "Uninstall.exe"));

        var m = InstallManifest.Load(tmp.Path);

        Assert.NotNull(m);
        Assert.Equal(new[] { "Visor.exe", "Uninstall.exe" }, m!.Files);
    }

    [Fact]
    public void Load_tolerates_missing_files_and_unknown_properties()
    {
        using var tmp = new TempDir();
        tmp.Write(Product.ManifestName, "{\"app\":\"Visor\",\"version\":\"1.9.0\",\"future\":{\"x\":[1,2]},\"files\":null}");

        var m = InstallManifest.Load(tmp.Path);

        Assert.NotNull(m);
        Assert.Equal("1.9.0", m!.Version);
        Assert.NotNull(m.Files);
        Assert.Empty(m.Files);
    }

    [Fact]
    public void Load_refuses_an_oversized_manifest()
    {
        using var tmp = new TempDir();
        // Valid content followed by whitespace past the 4 MB limit: size is checked before parsing.
        var padding = new string(' ', 4 * 1024 * 1024 + 1);
        tmp.Write(Product.ManifestName, ManifestJson("Visor", "Visor.exe") + padding);

        Assert.Null(InstallManifest.Load(tmp.Path));
    }

    [Fact]
    public void Save_then_Load_round_trips_and_leaves_no_temp_file()
    {
        using var tmp = new TempDir();
        var m = new InstallManifest { Files = { "Visor.exe", @"Assets\Sounds\click.wav", "Uninstall.exe" } };

        m.Save(tmp.Path);

        var path = InstallManifest.PathIn(tmp.Path);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$"), m.InstalledUtc);

        var text = File.ReadAllText(path);
        foreach (var key in new[] { "\"app\"", "\"version\"", "\"installedUtc\"", "\"files\"" })
            Assert.Contains(key, text);

        var back = InstallManifest.Load(tmp.Path);
        Assert.NotNull(back);
        Assert.Equal(Product.Name, back!.App);
        Assert.Equal(Product.Version, back.Version);
        Assert.Equal(m.InstalledUtc, back.InstalledUtc);
        Assert.Equal(m.Files, back.Files);
    }

    [Fact]
    public void Save_replaces_an_existing_manifest()
    {
        using var tmp = new TempDir();
        new InstallManifest { Files = { "Old.exe" } }.Save(tmp.Path);
        new InstallManifest { Files = { "Visor.exe" } }.Save(tmp.Path);

        var back = InstallManifest.Load(tmp.Path);
        Assert.NotNull(back);
        Assert.Equal(new[] { "Visor.exe" }, back!.Files);
        Assert.Single(Directory.GetFiles(tmp.Path));
    }
}
