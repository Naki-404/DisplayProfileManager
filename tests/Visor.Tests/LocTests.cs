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
