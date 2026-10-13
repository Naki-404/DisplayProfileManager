using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class NetworkWlanTests
{
    [Theory]
    [InlineData(100, -50)]
    [InlineData(50, -75)]
    [InlineData(0, -100)]
    [InlineData(80, -60)]
    [InlineData(120, -50)]   // clamped
    [InlineData(-5, -100)]   // clamped
    public void EstimateDbm_inverts_windows_quality_formula(int quality, int dbm)
    {
        Assert.Equal(dbm, WlanInfoService.EstimateDbm(quality));
    }

    [Fact]
    public void MaskBssid_keeps_vendor_oui_only()
    {
        Assert.Equal("A4:2B:B0", WlanInfoService.MaskBssid(new byte[] { 0xA4, 0x2B, 0xB0, 0x12, 0x34, 0x56 }));
        Assert.Equal("00:11:22", WlanInfoService.MaskBssid(new byte[] { 0x00, 0x11, 0x22, 0xFF, 0xFF, 0xFF }));
        Assert.Null(WlanInfoService.MaskBssid(new byte[6]));
        Assert.Null(WlanInfoService.MaskBssid(new byte[] { 1, 2, 3 }));
        Assert.Null(WlanInfoService.MaskBssid(null));
        string masked = WlanInfoService.MaskBssid(new byte[] { 1, 2, 3, 4, 5, 6 })!;
        Assert.Equal(8, masked.Length);           // "XX:XX:XX" — never the full 17-char MAC
        Assert.DoesNotContain("04", masked);
    }

    [Theory]
    [InlineData(7, "802.11n")]
    [InlineData(8, "802.11ac")]
    [InlineData(10, "802.11ax")]
    [InlineData(11, "802.11be")]
    [InlineData(6, "802.11g")]
    [InlineData(0, "unknown")]
    [InlineData(99, "unknown")]
    public void PhyCodeOf_maps_dot11_phy_types(int phy, string code)
    {
        Assert.Equal(code, WlanInfoService.PhyCodeOf(phy));
    }

    [Theory]
    [InlineData(1, "2.4")]
    [InlineData(11, "2.4")]
    [InlineData(14, "2.4")]
    [InlineData(36, "5")]
    [InlineData(149, "5")]
    [InlineData(0, "unknown")]
    [InlineData(200, "unknown")]
    [InlineData(null, "unknown")]
    public void BandOf_maps_channel_numbers(int? channel, string band)
    {
        Assert.Equal(band, WlanInfoService.BandOf(channel));
    }

    [Fact]
    public void DecodeSsid_handles_utf8_and_trailing_nul()
    {
        Assert.Equal("Café", WlanInfoService.DecodeSsid(System.Text.Encoding.UTF8.GetBytes("Café\0")));
        Assert.Equal("", WlanInfoService.DecodeSsid(Array.Empty<byte>()));
    }

    [Fact]
    public void Connection_info_dbm_prefers_driver_rssi()
    {
        var est = new WlanConnectionInfo { InterfaceDescription = "x", PhyCode = "802.11ax", SignalQuality = 60, EstimatedDbm = -70 };
        Assert.Equal(-70, est.Dbm);
        var drv = new WlanConnectionInfo { InterfaceDescription = "x", PhyCode = "802.11ax", SignalQuality = 60, EstimatedDbm = -70, RssiDbm = -63 };
        Assert.Equal(-63, drv.Dbm);
    }

    [SkippableFact]
    public void Query_live_smoke_never_throws_and_masks_bssid()
    {
        // Read-only WLAN API call; skipped when the host has no WLAN adapter / service.
        var r = WlanInfoService.Query();
        Assert.NotNull(r);
        Skip.If(r.Connection == null, "no connected WLAN interface: " + r.Reason + " " + r.Detail);
        var c = r.Connection!;
        Assert.InRange(c.SignalQuality, 0, 100);
        Assert.InRange(c.EstimatedDbm, -100, -50);
        Assert.False(string.IsNullOrWhiteSpace(c.InterfaceDescription));
        Assert.False(string.IsNullOrWhiteSpace(c.PhyCode));
        if (c.BssidOui != null) Assert.Equal(8, c.BssidOui.Length);
        Assert.True(c.RxRateMbps >= 0 && c.TxRateMbps >= 0);
        Assert.Contains(c.BandCode, new[] { "2.4", "5", "unknown" });
        Assert.Equal(WlanUnavailableReason.None, r.Reason);
    }
}
