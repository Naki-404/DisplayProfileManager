using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Visor.Services.NetworkTweaks;
using Xunit;

namespace Visor.Tests;

public class NetTweakDnsTests
{
    // ---------------- packet build ----------------

    [Fact]
    public void BuildAQuery_matches_rfc1035_layout()
    {
        var q = DnsWire.BuildAQuery(0xBEEF, "www.example.com");
        Assert.Equal(new byte[] { 0xBE, 0xEF, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, q[..12]);
        var name = new byte[] { 3, (byte)'w', (byte)'w', (byte)'w', 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
            3, (byte)'c', (byte)'o', (byte)'m', 0 };
        Assert.Equal(name, q[12..(12 + name.Length)]);
        Assert.Equal(new byte[] { 0, 1, 0, 1 }, q[^4..]);
        Assert.Equal(DnsWire.BuildAQuery(1, "example.com."), DnsWire.BuildAQuery(1, "example.com"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a..b")]
    [InlineData("пример.рф")]
    [InlineData("bad name.com")]
    public void BuildAQuery_rejects_invalid_names(string name) =>
        Assert.Throws<ArgumentException>(() => DnsWire.BuildAQuery(1, name));

    [Fact]
    public void BuildAQuery_rejects_overlong_labels_and_names()
    {
        Assert.Throws<ArgumentException>(() => DnsWire.BuildAQuery(1, new string('a', 64) + ".com"));
        Assert.Throws<ArgumentException>(() => DnsWire.BuildAQuery(1, string.Join(".", Enumerable.Repeat("abcdefghi", 30))));
    }

    // ---------------- response parse ----------------

    /// <summary>Builds a response to <paramref name="query"/>: header flags, the echoed question and A answers (compressed name pointer).</summary>
    private static byte[] Response(byte[] query, ushort flags, params byte[][] aRecords)
    {
        var list = new List<byte>(query);
        BinaryPrimitives.WriteUInt16BigEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)[2..], flags);
        BinaryPrimitives.WriteUInt16BigEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)[6..], (ushort)aRecords.Length);
        foreach (var ip in aRecords)
        {
            list.AddRange(new byte[] { 0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0x0E, 0x10, 0, 4 });
            list.AddRange(ip);
        }
        return list.ToArray();
    }

    [Fact]
    public void Parse_reads_A_records_with_compression()
    {
        var q = DnsWire.BuildAQuery(7, "store.steampowered.com");
        var r = DnsWire.Parse(Response(q, 0x8180, new byte[] { 23, 1, 2, 3 }, new byte[] { 23, 1, 2, 4 }), 7);
        Assert.Equal(DnsResponseStatus.Ok, r.Status);
        Assert.True(r.Answered);
        Assert.Equal(new[] { IPAddress.Parse("23.1.2.3"), IPAddress.Parse("23.1.2.4") }, r.Addresses);
    }

    [Fact]
    public void Parse_skips_cname_records_and_keeps_following_A()
    {
        var q = DnsWire.BuildAQuery(9, "www.twitch.tv");
        var bytes = new List<byte>(Response(q, 0x8180));
        bytes[7] = 2; // ANCOUNT = 2
        // CNAME → "a.b" (uncompressed rdata)
        bytes.AddRange(new byte[] { 0xC0, 0x0C, 0, 5, 0, 1, 0, 0, 0, 60, 0, 5, 1, (byte)'a', 1, (byte)'b', 0 });
        bytes.AddRange(new byte[] { 0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 151, 101, 1, 1 });
        var r = DnsWire.Parse(bytes.ToArray(), 9);
        Assert.Equal(DnsResponseStatus.Ok, r.Status);
        Assert.Equal(new[] { IPAddress.Parse("151.101.1.1") }, r.Addresses);
    }

    [Fact]
    public void Parse_maps_rcodes_truncation_and_id()
    {
        var q = DnsWire.BuildAQuery(42, "example.com");
        Assert.Equal(DnsResponseStatus.NxDomain, DnsWire.Parse(Response(q, 0x8183), 42).Status);
        Assert.True(DnsWire.Parse(Response(q, 0x8183), 42).Answered);
        Assert.Equal(DnsResponseStatus.ServerFailure, DnsWire.Parse(Response(q, 0x8182), 42).Status);
        Assert.False(DnsWire.Parse(Response(q, 0x8182), 42).Answered);
        Assert.Equal(DnsResponseStatus.Refused, DnsWire.Parse(Response(q, 0x8185), 42).Status);
        Assert.Equal(DnsResponseStatus.OtherError, DnsWire.Parse(Response(q, 0x8184), 42).Status);

        var tc = DnsWire.Parse(Response(q, 0x8380), 42);   // QR + TC + RD + RA
        Assert.Equal(DnsResponseStatus.Truncated, tc.Status);
        Assert.True(tc.Answered);

        Assert.Equal(DnsResponseStatus.IdMismatch, DnsWire.Parse(Response(q, 0x8180), 43).Status);
        Assert.Equal(DnsResponseStatus.Malformed, DnsWire.Parse(q, 42).Status);   // our own query echoed (QR=0)
    }

    [Fact]
    public void Parse_survives_truncated_and_hostile_packets()
    {
        var q = DnsWire.BuildAQuery(5, "example.com");
        var ok = Response(q, 0x8180, new byte[] { 1, 2, 3, 4 });
        Assert.Equal(DnsResponseStatus.Malformed, DnsWire.Parse(ok[..^2], 5).Status);   // rdata cut
        Assert.Equal(DnsResponseStatus.Malformed, DnsWire.Parse(ok[..5], 5).Status);    // header cut
        Assert.Equal(DnsResponseStatus.Malformed, DnsWire.Parse(Array.Empty<byte>(), 5).Status);

        var lyingCount = (byte[])ok.Clone();
        lyingCount[7] = 50; // ANCOUNT 50, only one record present
        Assert.Equal(DnsResponseStatus.Malformed, DnsWire.Parse(lyingCount, 5).Status);

        var badLabel = (byte[])ok.Clone();
        badLabel[12] = 0x80; // reserved label type
        Assert.Equal(DnsResponseStatus.Malformed, DnsWire.Parse(badLabel, 5).Status);

        var rnd = new Random(1234);
        for (int i = 0; i < 2000; i++)
        {
            var junk = new byte[rnd.Next(0, 80)];
            rnd.NextBytes(junk);
            if (junk.Length >= 2) { junk[0] = 0; junk[1] = 5; }
            DnsWire.Parse(junk, 5);  // must never throw
        }
    }

    // ---------------- UDP client (loopback only) ----------------

    [Fact]
    public async Task Udp_client_times_out_when_server_stays_silent()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        var client = new UdpDnsQueryClient(port);
        var r = await client.QueryAsync(IPAddress.Loopback, "example.com", TimeSpan.FromMilliseconds(200), default);
        Assert.Equal(DnsResponseStatus.Timeout, r.Status);
        Assert.Null(r.RttMs);
    }

    [Fact]
    public async Task Udp_client_ignores_stray_ids_and_times_the_real_answer()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var serverTask = Task.Run(async () =>
        {
            var got = await server.ReceiveAsync();
            var q = got.Buffer;
            var stray = Response(q, 0x8180, new byte[] { 6, 6, 6, 6 });
            stray[0] ^= 0xFF;                                           // wrong id first
            await server.SendAsync(stray, stray.Length, got.RemoteEndPoint);
            var real = Response(q, 0x8180, new byte[] { 1, 1, 1, 1 });
            await server.SendAsync(real, real.Length, got.RemoteEndPoint);
        });
        var r = await new UdpDnsQueryClient(port).QueryAsync(IPAddress.Loopback, "example.com", TimeSpan.FromSeconds(3), default);
        await serverTask;
        Assert.Equal(DnsResponseStatus.Ok, r.Status);
        Assert.NotNull(r.RttMs);
        Assert.Equal(new[] { IPAddress.Parse("1.1.1.1") }, r.Addresses);
    }

    // ---------------- DNS state ----------------

    [Theory]
    [InlineData(null, "dhcp")]
    [InlineData("", "dhcp")]
    [InlineData("   ", "dhcp")]
    [InlineData("1.1.1.1,1.0.0.1", "static:1.1.1.1,1.0.0.1")]
    [InlineData("8.8.8.8 8.8.4.4", "static:8.8.8.8,8.8.4.4")]
    [InlineData("192.168.1.1", "static:192.168.1.1")]
    public void NameServer_value_maps_to_state(string? value, string expected) =>
        Assert.Equal(expected, DnsServerState.FromNameServer(value)!.Serialize());

    [Theory]
    [InlineData("fe80::1")]
    [InlineData("1.1.1.1,garbage")]
    [InlineData("1.1.1.1,2.2.2.2,3.3.3.3,4.4.4.4,5.5.5.5")]
    public void NameServer_values_that_cannot_be_restored_exactly_are_unreadable(string value) =>
        Assert.Null(DnsServerState.FromNameServer(value));

    [Fact]
    public void State_serialize_parse_and_equality()
    {
        Assert.True(DnsServerState.TryParse("dhcp", out var d));
        Assert.True(d.IsDhcp);
        Assert.True(DnsServerState.TryParse("static:9.9.9.9,149.112.112.112", out var s));
        Assert.Equal(new[] { "9.9.9.9", "149.112.112.112" }, s.Servers);
        Assert.Equal("static:9.9.9.9,149.112.112.112", s.Serialize());
        Assert.True(s.SameAs(DnsServerState.Static(new[] { "9.9.9.9", "149.112.112.112" })));
        Assert.False(s.SameAs(DnsServerState.Static(new[] { "149.112.112.112", "9.9.9.9" })));   // order matters
        Assert.False(s.SameAs(DnsServerState.Dhcp));
        Assert.False(DnsServerState.TryParse("static:", out _));
        Assert.False(DnsServerState.TryParse("DHCP", out _));
        Assert.False(DnsServerState.TryParse("static:1.1.1.1;calc", out _));
    }

    [Fact]
    public void Action_id_round_trip()
    {
        const string g = "{3f2504e0-4f89-11d3-9a0c-0305e82c3301}";
        string id = DnsSafeActionTarget.ActionIdFor(g, new[] { "1.1.1.1", "1.0.0.1" });
        Assert.Equal("dns.set|{3F2504E0-4F89-11D3-9A0C-0305E82C3301}|1.1.1.1,1.0.0.1", id);
        Assert.True(DnsSafeActionTarget.TryParseActionId(id, out var guid, out var servers));
        Assert.Equal("{3F2504E0-4F89-11D3-9A0C-0305E82C3301}", guid);
        Assert.Equal(new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("1.0.0.1") }, servers);
        Assert.False(DnsSafeActionTarget.TryParseActionId("dns.set|Ethernet|1.1.1.1", out _, out _));
        Assert.False(DnsSafeActionTarget.TryParseActionId("dns.set|" + g + "|1.1.1.1|x", out _, out _));
        Assert.False(DnsSafeActionTarget.TryParseActionId("dns.set|" + g + "|", out _, out _));
    }
}
