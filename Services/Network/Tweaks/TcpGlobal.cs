using Visor.Services.Elevation;

namespace Visor.Services.NetworkTweaks;

/// <summary>
/// Parsed <c>netsh int tcp show global</c>. Every field is a normalized English token
/// (enabled / disabled / default / normal / restricted / highlyrestricted / experimental / …) or
/// <see cref="Unknown"/> when it could not be identified.
/// </summary>
public sealed record TcpGlobalInfo(string AutoTuningLevel, string Ecn, string Rss, string CongestionProvider)
{
    public const string Unknown = "unknown";

    public static readonly TcpGlobalInfo Empty = new(Unknown, Unknown, Unknown, Unknown);

    public bool IsParsed => AutoTuningLevel != Unknown || Ecn != Unknown || Rss != Unknown;

    /// <summary>Auto-tuning is below Windows' default — the one TCP global Visor offers to fix.</summary>
    public bool AutoTuningDegraded => TcpAutotuningSafeActionTarget.DegradedLevels.Contains(AutoTuningLevel);
}

/// <summary>
/// Locale-tolerant parser. netsh labels are localized (and decoded via the OEM code page), so fields are
/// found by (1) known EN / RU label keywords, then (2) value tokens and line order: the auto-tuning line is
/// the only one with normal / restricted / highlyrestricted / experimental; RSS is the line right before
/// it and ECN two lines after it (stable since Windows 7; Chimney-era layouts keep the same relative order).
/// Anything not identified stays <see cref="TcpGlobalInfo.Unknown"/>.
/// </summary>
public static class NetshTcpGlobalParser
{
    private static readonly HashSet<string> AutoTuningOnlyTokens = new(StringComparer.Ordinal)
        { "normal", "restricted", "highlyrestricted", "experimental" };

    private static readonly HashSet<string> CongestionTokens = new(StringComparer.Ordinal)
        { "default", "none", "ctcp", "dctcp", "cubic", "newreno", "bbr", "bbr2" };

    /// <summary>Localized → normalized value tokens (lower-case keys).</summary>
    private static readonly Dictionary<string, string> ValueMap = new(StringComparer.Ordinal)
    {
        ["enabled"] = "enabled", ["disabled"] = "disabled", ["default"] = "default",
        ["normal"] = "normal", ["restricted"] = "restricted", ["highlyrestricted"] = "highlyrestricted",
        ["highly restricted"] = "highlyrestricted", ["experimental"] = "experimental",
        // ru-RU
        ["включено"] = "enabled", ["включен"] = "enabled", ["включена"] = "enabled", ["вкл"] = "enabled",
        ["отключено"] = "disabled", ["отключен"] = "disabled", ["отключена"] = "disabled",
        ["выключено"] = "disabled", ["выключен"] = "disabled", ["откл"] = "disabled",
        ["по умолчанию"] = "default", ["обычный"] = "normal", ["нормальный"] = "normal", ["обычная"] = "normal",
        ["ограниченный"] = "restricted", ["ограничено"] = "restricted", ["ограниченная"] = "restricted",
        ["строго ограниченный"] = "highlyrestricted", ["сильно ограниченный"] = "highlyrestricted",
        ["высоко ограниченный"] = "highlyrestricted", ["экспериментальный"] = "experimental",
    };

    public static TcpGlobalInfo Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return TcpGlobalInfo.Empty;

        var lines = new List<(string Label, string Value)>();
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("**", StringComparison.Ordinal) || line.StartsWith("--", StringComparison.Ordinal))
                continue;
            int colon = line.LastIndexOf(':');
            if (colon <= 0 || colon == line.Length - 1) continue;
            string label = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (label.Length == 0 || value.Length == 0) continue;
            lines.Add((label, Normalize(value)));
        }
        if (lines.Count == 0) return TcpGlobalInfo.Empty;

        int at = -1, ecn = -1, rss = -1, cong = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            string l = lines[i].Label.ToLowerInvariant();
            if (at < 0 && (l.Contains("auto-tuning") || l.Contains("autotuning") || l.Contains("автонастр"))) at = i;
            else if (ecn < 0 && l.Contains("ecn")) ecn = i;
            else if (rss < 0 && (l.Contains("receive-side scaling") || l.Contains("масштабир"))) rss = i;
            else if (cong < 0 && (l.Contains("congestion") || l.Contains("перегруз"))) cong = i;
        }

        // Value-token / line-order fallback for other locales or mis-decoded labels.
        if (at < 0)
            at = lines.FindIndex(x => AutoTuningOnlyTokens.Contains(x.Value));
        if (at >= 0)
        {
            if (rss < 0 && at - 1 >= 0 && lines[at - 1].Value is "enabled" or "disabled") rss = at - 1;
            if (cong < 0 && at + 1 < lines.Count && CongestionTokens.Contains(lines[at + 1].Value)) cong = at + 1;
            if (ecn < 0 && cong == at + 1 && at + 2 < lines.Count && lines[at + 2].Value is "enabled" or "disabled" or "default")
                ecn = at + 2;
        }

        return new TcpGlobalInfo(
            Pick(lines, at, v => AutoTuningOnlyTokens.Contains(v) || v == "disabled"),
            Pick(lines, ecn, v => v is "enabled" or "disabled" or "default"),
            Pick(lines, rss, v => v is "enabled" or "disabled"),
            Pick(lines, cong, v => v.Length is > 0 and <= 24 && v.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))));
    }

    private static string Pick(List<(string Label, string Value)> lines, int index, Func<string, bool> valid) =>
        index >= 0 && index < lines.Count && valid(lines[index].Value) ? lines[index].Value : TcpGlobalInfo.Unknown;

    internal static string Normalize(string value)
    {
        string v = value.Trim().TrimEnd('.').ToLowerInvariant();
        return ValueMap.TryGetValue(v, out var mapped) ? mapped : v;
    }
}

/// <summary>Seam for reading the TCP globals.</summary>
public interface ITcpGlobalReader
{
    /// <summary>Current settings, or null when netsh could not be run.</summary>
    TcpGlobalInfo? Read();
}

/// <summary><c>netsh int tcp show global</c> — works without admin rights.</summary>
public sealed class NetshTcpGlobalReader : ITcpGlobalReader
{
    private readonly ICommandRunner _runner;

    public NetshTcpGlobalReader(ICommandRunner? runner = null)
    {
        _runner = runner ?? SystemCommandRunner.Instance;
    }

    public TcpGlobalInfo? Read()
    {
        try
        {
            var r = _runner.Run("netsh.exe", new[] { "int", "tcp", "show", "global" }, TimeSpan.FromSeconds(10));
            return r.Succeeded ? NetshTcpGlobalParser.Parse(r.Output) : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "netsh tcp show global");
            return null;
        }
    }
}

/// <summary>
/// Restores Windows' default receive-window auto-tuning (<c>tcp.autotuning.normal</c>) when an old
/// "tweak guide" set it to disabled / restricted / highlyrestricted — those cap the TCP receive window and
/// can cut download speed on fast or high-latency links. State = the level token. Offered only when the
/// level is degraded; "experimental" and unreadable levels report unavailable (undo could not restore them
/// through the helper's allowlist). Undo sets the previous level back.
/// </summary>
public sealed class TcpAutotuningSafeActionTarget : ISafeActionTarget
{
    public const string Id = "tcp.autotuning.normal";
    public const string Setting = "autotuninglevel";
    public const string Normal = "normal";

    public static readonly IReadOnlySet<string> DegradedLevels =
        new HashSet<string>(StringComparer.Ordinal) { "disabled", "restricted", "highlyrestricted" };

    private readonly IElevationBroker _broker;
    private readonly ITcpGlobalReader _reader;

    public TcpAutotuningSafeActionTarget(IElevationBroker broker, ITcpGlobalReader? reader = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _reader = reader ?? new NetshTcpGlobalReader();
    }

    public string ActionId => Id;
    public string JournalKey => Id;
    public bool BlockedDuringGameSession => false;
    public string LocPrefix => "net.tweak.autotuning";
    public bool RequiresElevation => true;

    private static bool Restorable(string? level) => level == Normal || (level != null && DegradedLevels.Contains(level));

    public string? ReadState()
    {
        var level = _reader.Read()?.AutoTuningLevel;
        return Restorable(level) ? level : null;
    }

    public string? ResolveTargetState() => Normal;

    public bool SatisfiesTarget(string state, string targetState) =>
        string.Equals(state, Normal, StringComparison.Ordinal) && string.Equals(targetState, Normal, StringComparison.Ordinal);

    public bool StatesEqual(string a, string b) => Restorable(a) && string.Equals(a, b, StringComparison.Ordinal);

    public bool WriteState(string state) => false;

    public Task<SafeActionWriteResult> WriteStateAsync(string state, CancellationToken ct)
    {
        if (!Restorable(state)) return Task.FromResult(SafeActionWriteResult.Fail(SafeActionCodes.BadBackup));
        return ElevatedWrites.RunAsync(_broker, new[] { new ElevatedOp(ElevatedOpKind.TcpGlobalSet, Setting, null, state) },
            ct, "TCP auto-tuning");
    }
}
