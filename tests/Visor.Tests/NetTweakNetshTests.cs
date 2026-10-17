using Visor.Services.Elevation;
using Visor.Services.NetworkTweaks;
using Xunit;

namespace Visor.Tests;

public class NetTweakNetshTests
{
    private const string En11 = """
        Querying active state...

        TCP Global Parameters
        ----------------------------------------------
        Receive-Side Scaling State          : enabled
        Receive Window Auto-Tuning Level    : normal
        Add-On Congestion Control Provider  : default
        ECN Capability                      : disabled
        RFC 1323 Timestamps                 : disabled
        Initial RTO                         : 1000
        Receive Segment Coalescing State    : enabled
        Non Sack Rtt Resiliency             : disabled
        Max SYN Retransmissions             : 4
        Fast Open                           : enabled
        Fast Open Fallback                  : enabled
        HyStart                             : enabled
        Proportional Rate Reduction         : enabled
        Pacing Profile                      : off
        """;

    private const string EnTweakedWithPolicyNote = """
        Querying active state...

        TCP Global Parameters
        ----------------------------------------------
        Receive-Side Scaling State          : disabled
        Receive Window Auto-Tuning Level    : highlyrestricted
        Add-On Congestion Control Provider  : ctcp
        ECN Capability                      : enabled
        RFC 1323 Timestamps                 : disabled

        ** The above autotuninglevel setting is the result of Windows Scaling heuristics
        overriding any local/policy configuration on at least one profile.
        """;

    private const string Ru = """
        Запрос активного состояния...

        Глобальные параметры TCP
        ----------------------------------------------
        Состояние масштабирования на стороне получения : enabled
        Уровень автонастройки окна получения    : disabled
        Поставщик надстройки контроля перегрузки : default
        Возможность ECN                     : disabled
        Временные метки RFC 1323            : disabled
        Начальный RTO                       : 1000
        """;

    private const string RuLocalizedValues = """
        Глобальные параметры TCP
        ----------------------------------------------
        Состояние масштабирования на стороне получения : включено
        Уровень автонастройки окна получения    : ограниченный
        Поставщик надстройки контроля перегрузки : по умолчанию
        Возможность ECN                     : отключено
        """;

    /// <summary>Labels in a locale we do not know (or mis-decoded) — values / line order still identify the fields.</summary>
    private const string UnknownLocale = """
        Globale TCP-Parameter
        ----------------------------------------------
        Status der empfangsseitigen Skalierung : enabled
        Optimierungsebene des Empfangsfensters : restricted
        Zusätzlicher Überlastungssteuerungsanbieter : default
        Fähigkeit : disabled
        RFC 1323-Zeitstempel                : disabled
        """;

    /// <summary>Old Windows 10 layout with Chimney first.</summary>
    private const string ChimneyEra = """
        TCP Global Parameters
        ----------------------------------------------
        Chimney Offload State               : disabled
        Receive-Side Scaling State          : enabled
        Receive Window Auto-Tuning Level    : experimental
        Add-On Congestion Control Provider  : none
        ECN Capability                      : default
        """;

    [Fact]
    public void Parses_windows11_english()
    {
        var i = NetshTcpGlobalParser.Parse(En11);
        Assert.Equal(new TcpGlobalInfo("normal", "disabled", "enabled", "default"), i);
        Assert.True(i.IsParsed);
        Assert.False(i.AutoTuningDegraded);
    }

    [Fact]
    public void Parses_tweaked_values_and_ignores_policy_note()
    {
        var i = NetshTcpGlobalParser.Parse(EnTweakedWithPolicyNote);
        Assert.Equal("highlyrestricted", i.AutoTuningLevel);
        Assert.Equal("enabled", i.Ecn);
        Assert.Equal("disabled", i.Rss);
        Assert.Equal("ctcp", i.CongestionProvider);
        Assert.True(i.AutoTuningDegraded);
    }

    [Fact]
    public void Parses_russian_labels()
    {
        var i = NetshTcpGlobalParser.Parse(Ru);
        Assert.Equal(new TcpGlobalInfo("disabled", "disabled", "enabled", "default"), i);
        Assert.True(i.AutoTuningDegraded);
    }

    [Fact]
    public void Parses_russian_localized_values()
    {
        var i = NetshTcpGlobalParser.Parse(RuLocalizedValues);
        Assert.Equal(new TcpGlobalInfo("restricted", "disabled", "enabled", "default"), i);
    }

    [Fact]
    public void Unknown_locale_falls_back_to_value_tokens_and_line_order()
    {
        var i = NetshTcpGlobalParser.Parse(UnknownLocale);
        Assert.Equal("restricted", i.AutoTuningLevel);
        Assert.Equal("enabled", i.Rss);
        Assert.Equal("default", i.CongestionProvider);
        Assert.Equal("disabled", i.Ecn);
    }

    [Fact]
    public void Chimney_era_layout_keeps_relative_order()
    {
        var i = NetshTcpGlobalParser.Parse(ChimneyEra);
        Assert.Equal(new TcpGlobalInfo("experimental", "default", "enabled", "none"), i);
        Assert.False(i.AutoTuningDegraded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("The requested operation requires elevation.")]
    [InlineData("Garbage\r\n:::\r\nx : \r\n")]
    public void Unparsable_output_is_unknown_not_a_guess(string? output)
    {
        var i = NetshTcpGlobalParser.Parse(output);
        Assert.Equal(TcpGlobalInfo.Unknown, i.AutoTuningLevel);
        Assert.Equal(TcpGlobalInfo.Unknown, i.Ecn);
        Assert.Equal(TcpGlobalInfo.Unknown, i.Rss);
        Assert.False(i.IsParsed);
    }

    [Fact]
    public void Disabled_autotuning_is_not_guessed_from_an_unlabelled_disabled_line()
    {
        // Labels unknown and no auto-tuning-only token: "disabled" lines must not be assigned to auto-tuning.
        var i = NetshTcpGlobalParser.Parse("Foo : disabled\nBar : enabled\n");
        Assert.Equal(TcpGlobalInfo.Unknown, i.AutoTuningLevel);
    }

    [Fact]
    public void Crlf_and_odd_spacing_are_tolerated()
    {
        var i = NetshTcpGlobalParser.Parse(En11.Replace("\n", "\r\n").Replace(": normal", ":normal  "));
        Assert.Equal("normal", i.AutoTuningLevel);
    }

    private sealed class CannedRunner : ICommandRunner
    {
        public CommandResult Result = new(0, "");
        public readonly List<(string Tool, IReadOnlyList<string> Args)> Calls = new();
        public CommandResult Run(string systemTool, IReadOnlyList<string> args, TimeSpan timeout)
        {
            Calls.Add((systemTool, args));
            return Result;
        }
    }

    [Fact]
    public void Reader_runs_netsh_show_global_with_argument_list()
    {
        var runner = new CannedRunner { Result = new CommandResult(0, En11) };
        var info = new NetshTcpGlobalReader(runner).Read();
        Assert.Equal("normal", info!.AutoTuningLevel);
        Assert.Equal("netsh.exe", runner.Calls.Single().Tool);
        Assert.Equal(new[] { "int", "tcp", "show", "global" }, runner.Calls.Single().Args);

        runner.Result = new CommandResult(null, "");
        Assert.Null(new NetshTcpGlobalReader(runner).Read());   // timeout → unknown, not a guess
        runner.Result = new CommandResult(1, "error");
        Assert.Null(new NetshTcpGlobalReader(runner).Read());
    }

    [Fact]
    public void Powercfg_duplicate_output_parsing_is_locale_independent()
    {
        const string src = ElevationAllowlist.UltimatePerformanceScheme;
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            SystemElevatedExecutor.ParseDuplicatedScheme("Power Scheme GUID: AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE  (Ultimate Performance)", src));
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            SystemElevatedExecutor.ParseDuplicatedScheme("GUID схемы питания: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee  (Максимальная производительность)", src));
        Assert.Null(SystemElevatedExecutor.ParseDuplicatedScheme("Power Scheme GUID: " + src, src));
        Assert.Null(SystemElevatedExecutor.ParseDuplicatedScheme("", src));
    }
}
