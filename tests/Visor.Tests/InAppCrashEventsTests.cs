using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class InAppCrashEventsTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);
    private const string Profile = @"C:\Users\Kreis";

    private static CrashEventRawDetail Raw(double hoursAgo, string provider, int id, int level, string? message = "msg") =>
        new(Now.AddHours(-hoursAgo), provider, id, level, message);

    [Fact]
    public void List_is_classified_newest_first_windowed_and_capped()
    {
        var records = new[]
        {
            Raw(30, "Microsoft-Windows-Kernel-Power", 41, 1, "The system has rebooted without cleanly shutting down first."),
            Raw(2, "Display", 4101, 3, "Display driver nvlddmkm stopped responding and has successfully recovered."),
            Raw(5, "Service Control Manager", 7034, 2, "noise"),                                  // not crash-related
            Raw(10, "Microsoft-Windows-WHEA-Logger", 17, 3, "A corrected hardware error has occurred."),
            Raw(1, "nvlddmkm", 153, 2, null),                                                     // no description
            Raw(24 * 31, "Microsoft-Windows-Kernel-Power", 41, 1),                                // outside the window
            Raw(-1, "Microsoft-Windows-Kernel-Power", 41, 1),                                     // future (clock skew)
        };

        var list = CrashEventReader.BuildList(records, Now, Window, max: 3, Profile);

        Assert.Equal(UnavailableReason.None, list.Reason);
        Assert.True(list.Truncated); // 4 matching, 3 shown
        Assert.Equal(new[] { CrashEventKind.DisplayDriverError, CrashEventKind.DisplayTdr, CrashEventKind.WheaCorrected },
            list.Events.Select(e => e.Kind));
        Assert.Equal(Now.AddHours(-1), list.Events[0].TimeUtc);
        Assert.Equal("", list.Events[0].Message);
        Assert.Equal("nvlddmkm", list.Events[0].Provider);
        Assert.Equal(153, list.Events[0].EventId);
        Assert.Equal(Now - Window, list.FromUtc);

        var all = CrashEventReader.BuildList(records, Now, Window, max: 50, Profile);
        Assert.False(all.Truncated);
        Assert.Equal(4, all.Events.Count);
        Assert.Equal(CrashEventKind.UnexpectedShutdown, all.Events[^1].Kind);
    }

    [Fact]
    public void Messages_mask_profile_paths_case_insensitively_and_other_accounts()
    {
        string m = CrashEventReader.SanitizeMessage(
            @"Faulting module c:\users\KREIS\AppData\Local\Temp\x.dll; dump at C:\Users\Other.Person\crash.dmp and D:\Users\bob",
            Profile);
        Assert.DoesNotContain("Kreis", m, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Other.Person", m);
        Assert.DoesNotContain("bob", m);
        Assert.Contains(@"%USERPROFILE%\AppData\Local\Temp\x.dll", m);
        Assert.Contains(@"%USERPROFILE%\crash.dmp", m);
    }

    [Fact]
    public void Masking_keeps_the_rest_of_the_line()
    {
        string m = CrashEventReader.SanitizeMessage(@"Saved to C:\Users\anna today.", null);
        Assert.Equal(@"Saved to %USERPROFILE% today.", m);
    }

    [Fact]
    public void Messages_are_trimmed_collapsed_and_truncated()
    {
        Assert.Equal("", CrashEventReader.SanitizeMessage(null, Profile));
        Assert.Equal("", CrashEventReader.SanitizeMessage("  \r\n ", Profile));
        string collapsed = CrashEventReader.SanitizeMessage("  first\r\n\r\n\r\n\r\n\r\nsecond  ", Profile);
        Assert.Equal("first" + Environment.NewLine + Environment.NewLine + "second", collapsed);

        string longText = new('x', 5000);
        string cut = CrashEventReader.SanitizeMessage(longText, Profile, maxChars: 100);
        Assert.Equal(100, cut.Length);
        Assert.EndsWith("…", cut);
        Assert.Equal(CrashEventReader.MaxMessageChars, CrashEventReader.SanitizeMessage(longText, Profile).Length);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("\r\n  \r\nThe display driver stopped.\r\nDetails: x", "The display driver stopped.")]
    [InlineData("single", "single")]
    public void FirstLine(string? message, string expected) =>
        Assert.Equal(expected, CrashEventReader.FirstLine(message));

    [Fact]
    public void Filter_and_kinds_present_follow_the_data()
    {
        var events = new[]
        {
            new CrashEventDetail(Now, CrashEventKind.DisplayTdr, "Display", 4101, 3, "a"),
            new CrashEventDetail(Now.AddHours(-1), CrashEventKind.UnexpectedShutdown, "Microsoft-Windows-Kernel-Power", 41, 1, "b"),
            new CrashEventDetail(Now.AddHours(-2), CrashEventKind.DisplayTdr, "Display", 4101, 3, "c"),
        };
        Assert.Equal(new[] { CrashEventKind.UnexpectedShutdown, CrashEventKind.DisplayTdr }, CrashEventReader.KindsPresent(events));
        Assert.Equal(new[] { "a", "c" }, CrashEventReader.Filter(events, CrashEventKind.DisplayTdr).Select(e => e.Message));
        Assert.Equal(3, CrashEventReader.Filter(events, null).Count);
        Assert.Empty(CrashEventReader.Filter(events, CrashEventKind.WheaFatal));
    }

    [Fact]
    public void Support_report_is_invariant_plain_text_with_offsets_and_indented_messages()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3");
        var events = new[]
        {
            new CrashEventDetail(Now.AddHours(-30), CrashEventKind.UnexpectedShutdown, "Microsoft-Windows-Kernel-Power", 41, 1,
                "The system has rebooted.\r\nCheck power."),
            new CrashEventDetail(Now.AddHours(-1), CrashEventKind.DisplayDriverError, "nvlddmkm", 153, 2, ""),
        };

        string text = CrashEventReader.BuildSupportReport(events, Now, Window, zone);

        Assert.StartsWith("Visor - stability events from the Windows System log (last 30 days), generated 2026-10-17 15:00:00 UTC+03:00", text);
        Assert.Contains("Events: 2", text);
        Assert.Contains("[2026-10-17 14:00:00 UTC+03:00] DisplayDriverError | nvlddmkm | Event ID 153 | Error", text);
        Assert.Contains("[2026-10-16 09:00:00 UTC+03:00] UnexpectedShutdown | Microsoft-Windows-Kernel-Power | Event ID 41 | Critical", text);
        Assert.Contains("    The system has rebooted.", text);
        Assert.Contains("    Check power.", text);
        Assert.Contains("    (no description)", text);
        // Newest first.
        Assert.True(text.IndexOf("DisplayDriverError", StringComparison.Ordinal) < text.IndexOf("UnexpectedShutdown", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, "Critical")]
    [InlineData(2, "Error")]
    [InlineData(3, "Warning")]
    [InlineData(4, "Information")]
    [InlineData(0, "Information")]
    [InlineData(9, "Level 9")]
    public void Level_names(int level, string expected) => Assert.Equal(expected, CrashEventReader.LevelName(level));

    [Fact]
    public void Every_kind_has_a_short_label_in_both_locales()
    {
        foreach (var kind in Enum.GetValues<CrashEventKind>())
            Assert.True(Loc.Has(CrashEventReader.KindLocKey(kind)), kind.ToString());
        Assert.Equal("hw.events.kind.displayTdr", CrashEventReader.KindLocKey(CrashEventKind.DisplayTdr));
    }

    [Fact]
    public void Detail_first_line_comes_from_the_sanitized_message()
    {
        var d = new CrashEventDetail(Now, CrashEventKind.WheaFatal, "Microsoft-Windows-WHEA-Logger", 18, 1, "\nA fatal hardware error has occurred.\nComponent: Processor Core");
        Assert.Equal("A fatal hardware error has occurred.", d.FirstLine);
    }
}
