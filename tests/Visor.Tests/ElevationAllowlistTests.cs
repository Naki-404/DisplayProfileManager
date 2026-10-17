using Visor.Services.Elevation;
using Xunit;

namespace Visor.Tests;

public class ElevationAllowlistTests
{
    private const string Iface = "{3F2504E0-4F89-11D3-9A0C-0305E82C3301}";
    private const string IfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string IfaceKey = IfacesKey + "\\" + Iface;
    private const string Mmcss = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string Gfx = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string Run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private static string? V(ElevatedOp op, bool adapterExists = true) =>
        ElevationAllowlist.Validate(op, _ => adapterExists);

    private static ElevatedOp Dw(string key, string name, string value = "1") => new(ElevatedOpKind.RegSetDword, key, name, value);

    // ---------------- accepted ----------------

    public static IEnumerable<object[]> Allowed() => new[]
    {
        new object[] { Dw(IfaceKey, "TcpAckFrequency") },
        new object[] { Dw(IfaceKey, "TCPNoDelay") },
        new object[] { Dw(IfaceKey, "tcpnodelay") },                       // value names are case-insensitive in the registry
        new object[] { Dw(IfaceKey.ToLowerInvariant(), "TcpDelAckTicks", "0") },
        new object[] { new ElevatedOp(ElevatedOpKind.RegDeleteValue, IfaceKey, "TcpAckFrequency") },
        new object[] { Dw(Mmcss, "NetworkThrottlingIndex", "4294967295") },
        new object[] { Dw(Mmcss, "NetworkThrottlingIndex", "-1") },        // journal (signed) form of 0xFFFFFFFF
        new object[] { Dw(Mmcss, "SystemResponsiveness", "10") },
        new object[] { Dw(Gfx, "HwSchMode", "2") },
        new object[] { new ElevatedOp(ElevatedOpKind.RegSetString, Run, "Some Tool", "\"C:\\Program Files\\Tool\\tool.exe\" --tray") },
        new object[] { new ElevatedOp(ElevatedOpKind.RegDeleteValue, Run, "Some Tool") },
        new object[] { new ElevatedOp(ElevatedOpKind.RegSetBinary, Approved, "Some Tool", "030000000000000000000000") },
        new object[] { new ElevatedOp(ElevatedOpKind.RegDeleteValue, Approved, "Some Tool") },
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, null, "1.1.1.1,1.0.0.1") },
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, null, "127.0.0.1") },          // local proxy must stay restorable
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, null, "1.1.1.1, 8.8.8.8,9.9.9.9,192.168.1.1") },
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetDhcp, Iface) },
        new object[] { new ElevatedOp(ElevatedOpKind.TcpGlobalSet, "autotuninglevel", null, "normal") },
        new object[] { new ElevatedOp(ElevatedOpKind.TcpGlobalSet, "autotuninglevel", null, "highlyrestricted") },
        new object[] { new ElevatedOp(ElevatedOpKind.TcpGlobalSet, "ecncapability", null, "default") },
        new object[] { new ElevatedOp(ElevatedOpKind.TcpGlobalSet, "rss", null, "enabled") },
        new object[] { new ElevatedOp(ElevatedOpKind.PowerDuplicateScheme, "", null, ElevationAllowlist.UltimatePerformanceScheme) },
        new object[] { new ElevatedOp(ElevatedOpKind.PowerDuplicateScheme, ElevationAllowlist.UltimatePerformanceScheme) },
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public void Allowlisted_ops_pass(ElevatedOp op) => Assert.Null(V(op));

    // ---------------- rejected ----------------

    public static IEnumerable<object[]> Rejected() => new[]
    {
        // other hives / hive prefixes / other keys
        new object[] { Dw(@"HKLM\" + Mmcss, "SystemResponsiveness"), ElevationAllowlist.ReasonPath },
        new object[] { Dw(@"HKEY_LOCAL_MACHINE\" + Mmcss, "SystemResponsiveness"), ElevationAllowlist.ReasonPath },
        new object[] { Dw(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "X"), ElevationAllowlist.ReasonPath },
        new object[] { Dw(@"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", "0"), ElevationAllowlist.ReasonPath },
        new object[] { Dw(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "DefaultTTL", "64"), ElevationAllowlist.ReasonPath },
        new object[] { Dw(IfacesKey, "TcpAckFrequency"), ElevationAllowlist.ReasonPath },
        new object[] { Dw(@"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware"), ElevationAllowlist.ReasonPath },
        // traversal / malformed paths
        new object[] { Dw(Mmcss + @"\..\..\..\..\Policies", "SystemResponsiveness"), ElevationAllowlist.ReasonPath },
        new object[] { Dw(IfacesKey + @"\" + Iface + @"\..\..\..\..\..\Control\Power", "TcpAckFrequency"), ElevationAllowlist.ReasonPath },
        new object[] { Dw(IfacesKey + @"\..", "TcpAckFrequency"), ElevationAllowlist.ReasonPath },
        new object[] { Dw(@"\" + Mmcss, "SystemResponsiveness"), ElevationAllowlist.ReasonPath },
        new object[] { Dw(Mmcss + @"\", "SystemResponsiveness"), ElevationAllowlist.ReasonPath },
        new object[] { Dw(Mmcss.Replace(@"\", @"\\"), "SystemResponsiveness"), ElevationAllowlist.ReasonPath },
        new object[] { Dw(Mmcss.Replace('\\', '/'), "SystemResponsiveness"), ElevationAllowlist.ReasonPath },
        new object[] { Dw(Mmcss + @"\Tasks\Games", "Priority", "6"), ElevationAllowlist.ReasonPath },
        new object[] { Dw(IfacesKey + @"\{not-a-guid}", "TcpAckFrequency"), ElevationAllowlist.ReasonPath },
        new object[] { Dw(IfacesKey + @"\3F2504E0-4F89-11D3-9A0C-0305E82C3301", "TcpAckFrequency"), ElevationAllowlist.ReasonPath },
        new object[] { Dw(IfaceKey + @"\Sub", "TcpAckFrequency"), ElevationAllowlist.ReasonPath },
        new object[] { Dw("", "TcpAckFrequency"), ElevationAllowlist.ReasonPath },
        new object[] { Dw(Mmcss + "\0", "SystemResponsiveness"), ElevationAllowlist.ReasonPath },
        // wrong value names
        new object[] { Dw(IfaceKey, "DhcpNameServer"), ElevationAllowlist.ReasonValueName },
        new object[] { Dw(Mmcss, "NoLazyMode"), ElevationAllowlist.ReasonValueName },
        new object[] { Dw(Gfx, "TdrDelay", "60"), ElevationAllowlist.ReasonValueName },
        new object[] { Dw(Mmcss, ""), ElevationAllowlist.ReasonValueName },
        new object[] { new ElevatedOp(ElevatedOpKind.RegSetString, Run, null, "x"), ElevationAllowlist.ReasonValueName },
        new object[] { new ElevatedOp(ElevatedOpKind.RegSetString, Run, "a\0b", "x"), ElevationAllowlist.ReasonValueName },
        // wrong kind for the key
        new object[] { new ElevatedOp(ElevatedOpKind.RegSetString, Mmcss, "SystemResponsiveness", "10"), ElevationAllowlist.ReasonKind },
        new object[] { Dw(Run, "Tool", "1"), ElevationAllowlist.ReasonKind },
        new object[] { new ElevatedOp(ElevatedOpKind.RegSetString, Approved, "Tool", "x"), ElevationAllowlist.ReasonKind },
        new object[] { new ElevatedOp(ElevatedOpKind.RegSetBinary, Run, "Tool", "00"), ElevationAllowlist.ReasonKind },
        new object[] { new ElevatedOp((ElevatedOpKind)999, Mmcss, "SystemResponsiveness", "10"), ElevationAllowlist.ReasonKind },
        // bad data
        new object[] { Dw(Mmcss, "SystemResponsiveness", "ten"), ElevationAllowlist.ReasonValueData },
        new object[] { Dw(Mmcss, "SystemResponsiveness", "4294967296"), ElevationAllowlist.ReasonValueData },
        new object[] { Dw(Mmcss, "SystemResponsiveness", null!), ElevationAllowlist.ReasonValueData },
        new object[] { Dw(Mmcss, "SystemResponsiveness", "0x10"), ElevationAllowlist.ReasonValueData },
        new object[] { new ElevatedOp(ElevatedOpKind.RegSetBinary, Approved, "Tool", "0G"), ElevationAllowlist.ReasonValueData },
        new object[] { new ElevatedOp(ElevatedOpKind.RegSetBinary, Approved, "Tool", "030"), ElevationAllowlist.ReasonValueData },
        new object[] { new ElevatedOp(ElevatedOpKind.RegSetBinary, Approved, "Tool", new string('0', 200)), ElevationAllowlist.ReasonValueData },
        new object[] { new ElevatedOp(ElevatedOpKind.RegSetString, Run, "Tool", "a\0b"), ElevationAllowlist.ReasonValueData },
        new object[] { new ElevatedOp(ElevatedOpKind.RegDeleteValue, Run, "Tool", "x"), ElevationAllowlist.ReasonValueData },
        // DNS
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, null, "1.1.1"), ElevationAllowlist.ReasonDnsServers },
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, null, "1"), ElevationAllowlist.ReasonDnsServers },
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, null, "001.1.1.1"), ElevationAllowlist.ReasonDnsServers },
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, null, "dns.google"), ElevationAllowlist.ReasonDnsServers },
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, null, "2606:4700:4700::1111"), ElevationAllowlist.ReasonDnsServers },
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, null, "0.0.0.0"), ElevationAllowlist.ReasonDnsServers },
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, null, "224.0.0.1"), ElevationAllowlist.ReasonDnsServers },
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, null, "255.255.255.255"), ElevationAllowlist.ReasonDnsServers },
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, null, "1.1.1.1,1.1.1.1"), ElevationAllowlist.ReasonDnsServers },
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, null, "1.1.1.1,1.0.0.1,8.8.8.8,8.8.4.4,9.9.9.9"), ElevationAllowlist.ReasonDnsServers },
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, null, "1.1.1.1;rm -rf"), ElevationAllowlist.ReasonDnsServers },
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, null, ""), ElevationAllowlist.ReasonDnsServers },
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetDhcp, Iface, null, "1.1.1.1"), ElevationAllowlist.ReasonDnsServers },
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetStatic, "Ethernet", null, "1.1.1.1"), ElevationAllowlist.ReasonAdapter },
        new object[] { new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, "x", "1.1.1.1"), ElevationAllowlist.ReasonValueName },
        // TCP
        new object[] { new ElevatedOp(ElevatedOpKind.TcpGlobalSet, "autotuninglevel", null, "experimental"), ElevationAllowlist.ReasonTcpValue },
        new object[] { new ElevatedOp(ElevatedOpKind.TcpGlobalSet, "autotuninglevel", null, "normal & calc"), ElevationAllowlist.ReasonTcpValue },
        new object[] { new ElevatedOp(ElevatedOpKind.TcpGlobalSet, "rss", null, "default"), ElevationAllowlist.ReasonTcpValue },
        new object[] { new ElevatedOp(ElevatedOpKind.TcpGlobalSet, "ecncapability", null, null), ElevationAllowlist.ReasonTcpValue },
        new object[] { new ElevatedOp(ElevatedOpKind.TcpGlobalSet, "timestamps", null, "enabled"), ElevationAllowlist.ReasonTcpTarget },
        new object[] { new ElevatedOp(ElevatedOpKind.TcpGlobalSet, "chimney", null, "enabled"), ElevationAllowlist.ReasonTcpTarget },
        // power
        new object[] { new ElevatedOp(ElevatedOpKind.PowerDuplicateScheme, "", null, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), ElevationAllowlist.ReasonScheme },
        new object[] { new ElevatedOp(ElevatedOpKind.PowerDuplicateScheme, "x", null, ElevationAllowlist.UltimatePerformanceScheme), ElevationAllowlist.ReasonScheme },
        new object[] { new ElevatedOp(ElevatedOpKind.PowerDuplicateScheme, ""), ElevationAllowlist.ReasonScheme },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void Ops_outside_the_allowlist_are_rejected_with_a_reason(ElevatedOp op, string reason) =>
        Assert.Equal(reason, V(op));

    [Fact]
    public void Dns_op_for_an_adapter_that_does_not_exist_is_rejected()
    {
        Assert.Equal(ElevationAllowlist.ReasonAdapter,
            V(new ElevatedOp(ElevatedOpKind.DnsSetStatic, Iface, null, "1.1.1.1"), adapterExists: false));
        Assert.Equal(ElevationAllowlist.ReasonAdapter,
            ElevationAllowlist.Validate(new ElevatedOp(ElevatedOpKind.DnsSetDhcp, Iface), _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void One_bad_op_rejects_the_whole_batch_with_per_op_reasons()
    {
        var ops = new[]
        {
            Dw(IfaceKey, "TcpAckFrequency"),
            Dw(@"SYSTEM\Setup", "CmdLine", "1"),
            Dw(IfaceKey, "TCPNoDelay"),
        };
        var reasons = ElevationAllowlist.ValidateBatch(ops, _ => true);
        Assert.NotNull(reasons);
        Assert.Equal(new string?[] { null, ElevationAllowlist.ReasonPath, null }, reasons);

        Assert.Null(ElevationAllowlist.ValidateBatch(new[] { ops[0], ops[2] }, _ => true));
        Assert.NotNull(ElevationAllowlist.ValidateBatch(Array.Empty<ElevatedOp>(), _ => true));
        Assert.NotNull(ElevationAllowlist.ValidateBatch(Enumerable.Repeat(ops[0], ElevationAllowlist.MaxOpsPerBatch + 1).ToList(), _ => true));
    }

    [Theory]
    [InlineData("0", 0u)]
    [InlineData("10", 10u)]
    [InlineData("4294967295", uint.MaxValue)]
    [InlineData("-1", uint.MaxValue)]
    [InlineData("-2147483648", 0x80000000u)]
    public void Dword_parsing_accepts_unsigned_and_signed_journal_forms(string text, uint expected)
    {
        Assert.True(ElevationAllowlist.TryParseDword(text, out uint v));
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("1.1.1.01", false)]
    [InlineData(" 1.1.1.1", false)]
    [InlineData("::1", false)]
    [InlineData("16843009", false)]
    public void Canonical_ipv4_check(string s, bool ok) => Assert.Equal(ok, ElevationAllowlist.IsCanonicalIPv4(s, out _));
}
