using Visor.Services;
using Xunit;

namespace Visor.Tests;

/// <summary>Pure parts of the process watcher: watch-list normalization / diffing and WMI name matching. No WMI, no polling.</summary>
public class ProcessWatcherTests
{
    private static HashSet<string> Set(params string[] keys) => new(keys, StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ watch list

    [Fact]
    public void NormalizeWatchList_dedups_case_extension_and_paths_and_skips_blanks()
    {
        var keys = ProcessWatcher.NormalizeWatchList(new[]
        {
            "Game.exe", "game", " GAME.EXE ", "", "   ", "C:\\Games\\Other.exe", "other.EXE", "VALORANT-Win64-Shipping.exe"
        });

        Assert.Equal(new[] { "game", "other", "valorant-win64-shipping" }, keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void SetWatchList_without_Start_normalizes_and_does_not_throw()
    {
        using var w = new ProcessWatcher();
        w.SetWatchList(new[] { "A.exe", "b", "a" });
        Assert.Equal(new[] { "a", "b" }, w.WatchedKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        w.SetWatchList(new[] { "b.exe" });            // changed → poll requested (no timer yet: harmless)
        Assert.Equal(new[] { "b" }, w.WatchedKeys);

        w.SetWatchList(Array.Empty<string>());
        Assert.Empty(w.WatchedKeys);
    }

    // ------------------------------------------------------------------ diffing

    [Fact]
    public void Diff_reports_started_and_stopped_keys()
    {
        var started = new List<string> { "stale" };
        var stopped = new List<string> { "stale" };

        ProcessWatcher.Diff(Set("a", "b"), Set("b", "c"), started, stopped);

        Assert.Equal(new[] { "c" }, started);
        Assert.Equal(new[] { "a" }, stopped);
    }

    [Fact]
    public void Diff_with_equal_sets_reports_nothing()
    {
        var started = new List<string>();
        var stopped = new List<string>();

        ProcessWatcher.Diff(Set("a", "B"), Set("A", "b"), started, stopped);

        Assert.Empty(started);
        Assert.Empty(stopped);
    }

    [Fact]
    public void Diff_from_empty_seen_reports_everything_as_started()
    {
        var started = new List<string>();
        var stopped = new List<string>();

        ProcessWatcher.Diff(Set(), Set("x", "y"), started, stopped);

        Assert.Equal(2, started.Count);
        Assert.Empty(stopped);
    }

    // ------------------------------------------------------------------ WMI name matching

    [Theory]
    [InlineData("VALORANT-Win64-Shipping.exe")]
    [InlineData("valorant-win64-shipping")]
    [InlineData("  VALORANT-WIN64-SHIPPING.EXE ")]
    public void Match_exact_for_full_names_in_any_form(string name)
    {
        Assert.Equal(ProcessWatcher.WatchMatch.Exact,
            ProcessWatcher.Match(name, Set("valorant-win64-shipping", "game")));
    }

    [Fact]
    public void Match_prefix_for_15_char_truncated_long_name()
    {
        // Win32_ProcessStartTrace may deliver only the first 15 characters of the image name.
        const string truncated = "VALORANT-Win64-";
        Assert.Equal(15, truncated.Length);

        Assert.Equal(ProcessWatcher.WatchMatch.Prefix,
            ProcessWatcher.Match(truncated, Set("valorant-win64-shipping")));
    }

    [Fact]
    public void Match_prefix_when_truncation_cuts_inside_the_extension()
    {
        // "abcdefghijkl.exe" (16 chars) truncated to 15 → "abcdefghijkl.ex"; the watched key is "abcdefghijkl".
        Assert.Equal(ProcessWatcher.WatchMatch.Prefix,
            ProcessWatcher.Match("abcdefghijkl.ex", Set("abcdefghijkl")));
        // "abcdefghijklmn.exe" → "abcdefghijklmn."
        Assert.Equal(ProcessWatcher.WatchMatch.Prefix,
            ProcessWatcher.Match("abcdefghijklmn.", Set("abcdefghijklmn")));
    }

    [Fact]
    public void Match_prefix_only_applies_to_exactly_15_characters()
    {
        // A shorter name that happens to be a prefix is a different process, not a truncation.
        Assert.Equal(ProcessWatcher.WatchMatch.None,
            ProcessWatcher.Match("VALORANT-Win64", Set("valorant-win64-shipping")));
        // A 16+ character name is complete and must match exactly.
        Assert.Equal(ProcessWatcher.WatchMatch.None,
            ProcessWatcher.Match("VALORANT-Win64-Sh", Set("valorant-win64-shipping")));
    }

    [Fact]
    public void Match_none_for_unrelated_names_and_blanks()
    {
        var watched = Set("valorant-win64-shipping", "game");
        Assert.Equal(ProcessWatcher.WatchMatch.None, ProcessWatcher.Match("notepad.exe", watched));
        Assert.Equal(ProcessWatcher.WatchMatch.None, ProcessWatcher.Match("abcdefghijklmno", watched)); // 15 chars, no prefix
        Assert.Equal(ProcessWatcher.WatchMatch.None, ProcessWatcher.Match(null, watched));
        Assert.Equal(ProcessWatcher.WatchMatch.None, ProcessWatcher.Match("", watched));
        Assert.Equal(ProcessWatcher.WatchMatch.None, ProcessWatcher.Match("   ", watched));
        Assert.Equal(ProcessWatcher.WatchMatch.None, ProcessWatcher.Match("game.exe", Set()));
    }

    [Fact]
    public void Match_prefers_exact_over_prefix()
    {
        // A 15-char name that is itself watched (and a prefix of a longer key) is Exact.
        Assert.Equal(ProcessWatcher.WatchMatch.Exact,
            ProcessWatcher.Match("abcdefghijklmno", Set("abcdefghijklmno", "abcdefghijklmnopq")));
    }
}
