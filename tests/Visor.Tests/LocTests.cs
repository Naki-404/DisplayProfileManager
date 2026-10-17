using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class LocTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

    private static IReadOnlyDictionary<string, (string En, string Ru)> Table()
    {
        var field = typeof(Loc).GetField("Map", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var map = field!.GetValue(null) as Dictionary<string, (string En, string Ru)>;
        Assert.NotNull(map);
        return map!;
    }

    [Fact]
    public void Table_is_not_empty_and_has_no_empty_strings()
    {
        var map = Table();
        Assert.True(map.Count > 100);
        foreach (var (key, (en, ru)) in map)
        {
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key}: empty EN");
            Assert.False(string.IsNullOrWhiteSpace(ru), $"{key}: empty RU");
        }
    }

    [Fact]
    public void Placeholders_match_between_EN_and_RU()
    {
        var mismatches = new List<string>();
        foreach (var (key, (en, ru)) in Table())
        {
            var a = Placeholder.Matches(en).Select(m => m.Value).OrderBy(x => x).ToArray();
            var b = Placeholder.Matches(ru).Select(m => m.Value).OrderBy(x => x).ToArray();
            if (!a.SequenceEqual(b))
                mismatches.Add($"{key}: EN [{string.Join(",", a)}] vs RU [{string.Join(",", b)}]");
        }
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    [Fact]
    public void Placeholders_are_contiguous_from_zero()
    {
        var bad = new List<string>();
        foreach (var (key, (en, _)) in Table())
        {
            var idx = Placeholder.Matches(en).Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(x => x).ToArray();
            if (idx.Length > 0 && !idx.SequenceEqual(Enumerable.Range(0, idx.Length)))
                bad.Add($"{key}: {string.Join(",", idx)}");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void Source_table_has_no_duplicate_keys()
    {
        var source = FindRepoFile(Path.Combine("Services", "Ui", "Loc.cs"));
        Assert.NotNull(source);
        var keys = Regex.Matches(File.ReadAllText(source!), @"^\s*\[""([^""]+)""\]\s*=", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.True(keys.Count > 100);
        var dupes = keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, "duplicate Loc keys: " + string.Join(", ", dupes));
        Assert.Equal(keys.Count, Table().Count);
    }

    [Fact]
    public void T_returns_locale_specific_text_and_falls_back_to_key()
    {
        var before = Loc.Locale;
        try
        {
            Loc.SetLocale("en");
            Assert.Equal("en", Loc.Locale);
            Assert.Equal("Cancel", Loc.T("btn.cancel"));
            Loc.SetLocale("ru");
            Assert.Equal("ru", Loc.Locale);
            Assert.Equal("Отмена", Loc.T("btn.cancel"));
            Assert.Equal("Отмена", Loc.T("BTN.CANCEL"));
            Assert.Equal("no.such.key.xyz", Loc.T("no.such.key.xyz"));
        }
        finally
        {
            Loc.SetLocale(before);
        }
    }

    [Fact]
    public void Tf_formats_every_placeholder_key_in_both_locales()
    {
        var before = Loc.Locale;
        try
        {
            var args = Enumerable.Range(0, 10).Select(i => (object)("A" + i)).ToArray();
            foreach (var locale in new[] { "en", "ru" })
            {
                Loc.SetLocale(locale);
                foreach (var (key, (en, _)) in Table())
                {
                    if (!Placeholder.IsMatch(en)) continue;
                    var text = Loc.Tf(key, args);
                    Assert.DoesNotContain("{0}", text);
                    Assert.Contains("A0", text);
                }
            }
        }
        finally
        {
            Loc.SetLocale(before);
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(11, 2)]
    [InlineData(12, 2)]
    [InlineData(14, 2)]
    [InlineData(21, 0)]
    [InlineData(22, 1)]
    [InlineData(25, 2)]
    [InlineData(100, 2)]
    [InlineData(101, 0)]
    [InlineData(111, 2)]
    [InlineData(0, 2)]
    public void PluralIndex_follows_russian_rules(int n, int expected)
    {
        Assert.Equal(expected, Loc.PluralIndex(n, "ru"));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 2)]
    [InlineData(2, 2)]
    [InlineData(21, 2)]
    public void PluralIndex_english_only_has_one_and_many(int n, int expected)
    {
        Assert.Equal(expected, Loc.PluralIndex(n, "en"));
    }

    [Fact]
    public void Plural_picks_the_unit_form_for_the_locale()
    {
        var before = Loc.Locale;
        try
        {
            Loc.SetLocale("ru");
            Assert.Equal("1 игра", Loc.Plural("unit.games", 1));
            Assert.Equal("3 игры", Loc.Plural("unit.games", 3));
            Assert.Equal("12 игр", Loc.Plural("unit.games", 12));
            Assert.Equal("5 пресетов", Loc.Plural("unit.presets", 5));
            Loc.SetLocale("en");
            Assert.Equal("1 game", Loc.Plural("unit.games", 1));
            Assert.Equal("2 games", Loc.Plural("unit.games", 2));
            Assert.Equal("7 no.such.unit", Loc.Plural("no.such.unit", 7));
        }
        finally
        {
            Loc.SetLocale(before);
        }
    }

    [Fact]
    public void Unit_keys_have_three_forms_in_both_locales()
    {
        foreach (var (key, (en, ru)) in Table())
        {
            if (!key.StartsWith("unit.", StringComparison.OrdinalIgnoreCase)) continue;
            Assert.Equal(3, en.Split('|').Length);
            Assert.Equal(3, ru.Split('|').Length);
        }
    }

    [Fact]
    public void Unknown_locale_falls_back_to_english()
    {
        var before = Loc.Locale;
        try
        {
            Loc.SetLocale("de");
            Assert.Equal("Cancel", Loc.T("btn.cancel"));
            Loc.SetLocale(null);
            Assert.Equal("Cancel", Loc.T("btn.cancel"));
        }
        finally
        {
            Loc.SetLocale(before);
        }
    }

    [Fact]
    public void Nav_rail_sections_have_the_mock_names_in_both_locales()
    {
        var before = Loc.Locale;
        try
        {
            Loc.SetLocale("en");
            Assert.Equal(new[] { "Home", "My PC", "Games", "Optimize", "Network", "Settings" },
                NavKeys.Select(Loc.T).ToArray());
            Loc.SetLocale("ru");
            Assert.Equal(new[] { "Главная", "Мой ПК", "Игры", "Оптимизация", "Сеть", "Настройки" },
                NavKeys.Select(Loc.T).ToArray());
        }
        finally
        {
            Loc.SetLocale(before);
        }
    }

    [Fact]
    public void Removed_top_level_sections_have_no_nav_keys()
    {
        // Presets became a Games section, Tools became Settings, System became My PC + Optimize.
        var map = Table();
        Assert.False(map.ContainsKey("nav.presets"));
        Assert.False(map.ContainsKey("nav.tools"));
        Assert.False(map.ContainsKey("nav.system"));
    }

    [Fact]
    public void Obsolete_external_tool_and_shortcut_keys_are_gone()
    {
        // FPS capture is built in (no external binary to locate, verify or download), and the in-app pass
        // replaced the "open Windows dialog" shortcuts — none of these keys may come back.
        var map = Table();
        foreach (var key in new[]
                 {
                     "monitor.uac.stop", "monitor.err.binaryMissing", "monitor.err.binaryMissing.body", "monitor.err.unverified",
                     "monitor.err.unverified.body", "monitor.locate.found", "monitor.locate.unverified", "monitor.locate.missing",
                     "monitor.choosePath", "monitor.settings.hint", "monitor.settings.path", "monitor.settings.locate",
                     "monitor.openDownload", "monitor.verify.None", "monitor.verify.AuthenticodeIntel", "monitor.verify.PinnedHash",
                     "monitor.verify.DevOverride", "monitor.state.locating", "hw.stability.openEventViewer",
                     "hw.stability.openEventViewer.fail", "optimize.ui.openPerfOptions", "optimize.ui.openSettings",
                     "optimize.ui.goSystem", "optimize.cleanup.infoOnly", "optimize.cleanup.openStorage",
                     "optimize.cleanup.cat.deliveryOptimization", "net.sug.dnsManual"
                 })
            Assert.False(map.ContainsKey(key), key);
        // Old external tool name, split so a repo-wide grep for it only finds historical docs.
        const string oldTool = "Present" + "Mon";
        foreach (var (key, (en, ru)) in map)
        {
            Assert.DoesNotContain(oldTool, en, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(oldTool, ru, StringComparison.OrdinalIgnoreCase);
            if (key is "autostart.failed" or "optimize.startup.readOnly")
            {
                Assert.DoesNotContain("Task Manager", en);
                Assert.DoesNotContain("Диспетчер", ru);
            }
        }
        Assert.True(map.ContainsKey("monitor.err.elevationUnavailable"));
        Assert.True(map.ContainsKey("net.sug.dnsInApp"));
    }

    [Fact]
    public void Shell_strings_exist_and_say_nothing_about_round_the_clock_support()
    {
        var map = Table();
        foreach (var key in new[] { "shell.beta", "shell.themes", "shell.help.title", "shell.help.body", "shell.help.link",
                     "shell.tooltipMode", "shell.tooltipMode.tip", "status.dot.ok", "status.dot.warn", "status.dot.bad", "status.dot.unknown" })
            Assert.True(map.ContainsKey(key), key);
        foreach (var (key, (en, ru)) in map)
        {
            if (!key.StartsWith("shell.", StringComparison.OrdinalIgnoreCase)) continue;
            Assert.DoesNotContain("24/7", en);
            Assert.DoesNotContain("24/7", ru);
        }
    }

    [Fact]
    public void Every_HealthReasons_constant_has_EN_and_RU()
    {
        var map = Table();
        var keys = typeof(HealthReasons)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Assert.True(keys.Count >= 20, "HealthReasons constants: " + keys.Count);
        var missing = keys.Where(k => !map.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "missing Loc keys: " + string.Join(", ", missing));
        foreach (var k in keys)
        {
            var (en, ru) = map[k];
            Assert.False(string.IsNullOrWhiteSpace(en), k + " EN");
            Assert.False(string.IsNullOrWhiteSpace(ru), k + " RU");
        }
    }

    [Fact]
    public void Every_MonitorErrorCode_and_ProbeErrorCode_has_a_loc_key_in_both_locales()
    {
        var map = Table();
        var missing = new List<string>();
        foreach (var code in Enum.GetValues<MonitorErrorCode>())
        {
            if (code == MonitorErrorCode.None) continue;
            string key = "monitor.err." + char.ToLowerInvariant(code.ToString()[0]) + code.ToString()[1..];
            if (!map.TryGetValue(key, out var pair) || string.IsNullOrWhiteSpace(pair.En) || string.IsNullOrWhiteSpace(pair.Ru))
                missing.Add(key);
        }
        foreach (var code in Enum.GetValues<ProbeErrorCode>())
        {
            if (code == ProbeErrorCode.None) continue;
            string key = NetworkProbeService.ErrorLocKey(code);
            if (!map.TryGetValue(key, out var pair) || string.IsNullOrWhiteSpace(pair.En) || string.IsNullOrWhiteSpace(pair.Ru))
                missing.Add(key);
        }
        foreach (var state in Enum.GetValues<MonitorState>())
        {
            string key = "monitor.state." + char.ToLowerInvariant(state.ToString()[0]) + state.ToString()[1..];
            if (!map.ContainsKey(key)) missing.Add(key);
        }
        Assert.True(missing.Count == 0, "missing Loc keys: " + string.Join(", ", missing));
    }

    [Fact]
    public void Hardware_card_keys_exist_for_every_UnavailableReason_tooltip()
    {
        var map = Table();
        foreach (var reason in Enum.GetValues<UnavailableReason>())
        {
            if (reason == UnavailableReason.None) continue;
            string key = HardwareText.ReasonKey(reason);
            Assert.True(map.ContainsKey(key), key);
        }
        foreach (var flag in Enum.GetValues<GpuThrottleFlags>())
        {
            if (flag == GpuThrottleFlags.None) continue;
            string key = HardwareText.ThrottleFlagKey(flag);
            Assert.True(map.ContainsKey(key), key);
        }
    }

    /// <summary>Every literal key passed to Loc.T / Loc.Tf / Loc.Plural in the app sources exists in the table.</summary>
    [Fact]
    public void Every_literal_key_used_in_the_sources_exists()
    {
        var csproj = FindRepoFile("Visor.csproj");
        Assert.NotNull(csproj);
        var root = Path.GetDirectoryName(csproj)!;
        var call = new Regex(@"Loc\.(?:T|Tf|Plural)\(\s*""([^""]+)""\s*[,)]", RegexOptions.Compiled);
        var map = Table();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        int used = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            var first = rel.Split(Path.DirectorySeparatorChar)[0];
            if (first is "tests" or "obj" or "bin" or "Installer" or "tools" or "_IconGen") continue;
            if (string.Equals(Path.GetFileName(file), "Loc.cs", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (Match m in call.Matches(File.ReadAllText(file)))
            {
                used++;
                if (!map.ContainsKey(m.Groups[1].Value))
                    missing.Add(m.Groups[1].Value + " (" + rel + ")");
            }
        }
        Assert.True(used > 500, "literal Loc calls found: " + used);
        Assert.True(missing.Count == 0, "missing Loc keys:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Has_reports_table_membership()
    {
        Assert.True(Loc.Has("btn.cancel"));
        Assert.True(Loc.Has("BTN.CANCEL"));
        Assert.False(Loc.Has("no.such.key.xyz"));
        Assert.False(Loc.Has(""));
    }

    private static readonly string[] NavKeys ={ "nav.home", "nav.mypc", "nav.games", "nav.optimize", "nav.network", "nav.settings" };

    private static string? FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "Visor.csproj")))
                return candidate;
        }
        return null;
    }
}
