using System.Runtime.InteropServices;
using System.Text;

namespace Visor.Services;

public enum WlanUnavailableReason
{
    None,
    /// <summary>wlanapi.dll missing or the WLAN service (WlanSvc) is not running.</summary>
    ApiUnavailable,
    NoAdapter,
    NotConnected,
    Error
}

/// <summary>Snapshot of the current Wi-Fi association. SSID is only kept in memory; it is never logged.</summary>
public sealed class WlanConnectionInfo
{
    public required string InterfaceDescription { get; init; }
    public string? Ssid { get; init; }
    /// <summary>Vendor OUI (first 3 octets) of the access point, e.g. "A4:2B:B0" — never the full BSSID.</summary>
    public string? BssidOui { get; init; }
    /// <summary>Stable code: 802.11a/b/g/n/ac/ax/be or "unknown".</summary>
    public required string PhyCode { get; init; }
    /// <summary>Windows signal quality 0..100.</summary>
    public required int SignalQuality { get; init; }
    /// <summary>Estimated RSSI from quality (see <see cref="WlanInfoService.EstimateDbm"/>).</summary>
    public required int EstimatedDbm { get; init; }
    /// <summary>Driver-reported RSSI when the opcode is supported; else null.</summary>
    public int? RssiDbm { get; init; }
    public double RxRateMbps { get; init; }
    public double TxRateMbps { get; init; }
    public int? Channel { get; init; }
    /// <summary>"2.4" | "5" | "unknown" (6 GHz channel numbers overlap 5 GHz; see <see cref="WlanInfoService.BandOf"/>).</summary>
    public string BandCode { get; init; } = "unknown";
    public bool SecurityEnabled { get; init; }
    /// <summary>Best available dBm: driver RSSI when present, else the estimate.</summary>
    public int Dbm => RssiDbm ?? EstimatedDbm;
}

public sealed class WlanQueryResult
{
    public WlanConnectionInfo? Connection { get; init; }
    public WlanUnavailableReason Reason { get; init; }
    /// <summary>Win32 error code or short diagnostic (never contains SSID).</summary>
    public string? Detail { get; init; }
    public static WlanQueryResult Unavailable(WlanUnavailableReason reason, string? detail = null) =>
        new() { Reason = reason, Detail = detail };
}

/// <summary>
/// Current Wi-Fi association via the Native Wifi API (works for standard users; read-only).
/// Returns a reason instead of throwing when there is no WLAN adapter or the service is off.
/// </summary>
public static class WlanInfoService
{
    // --- Native Wifi API --------------------------------------------------------------------------
    private const uint ClientVersion = 2;
    private const uint ErrorSuccess = 0;
    private const uint ErrorServiceNotActive = 1062;
    private const uint ErrorNotSupported = 50;

    private const int OpcodeCurrentConnection = 7;   // wlan_intf_opcode_current_connection
    private const int OpcodeChannelNumber = 8;       // wlan_intf_opcode_channel_number
    private const int OpcodeRssi = 0x10000102;       // wlan_intf_opcode_rssi

    private const int InterfaceStateConnected = 1;   // wlan_interface_state_connected

    // WLAN_INTERFACE_INFO: GUID(16) + WCHAR[256](512) + state(4)
    private const int InterfaceInfoSize = 532;
    // WLAN_CONNECTION_ATTRIBUTES field offsets (x86/x64 identical: all 4-byte-aligned members).
    private const int OffState = 0;
    private const int OffAssoc = 4 + 4 + 512;        // isState, wlanConnectionMode, strProfileName[256]
    private const int OffSsidLength = OffAssoc;      // DOT11_SSID.uSSIDLength
    private const int OffSsidBytes = OffAssoc + 4;   // DOT11_SSID.ucSSID[32]
    private const int OffBssType = OffAssoc + 36;
    private const int OffBssid = OffAssoc + 40;      // DOT11_MAC_ADDRESS[6]
    private const int OffPhyType = OffAssoc + 48;    // padded to 4
    private const int OffPhyIndex = OffAssoc + 52;
    private const int OffSignalQuality = OffAssoc + 56;
    private const int OffRxRate = OffAssoc + 60;     // kbps
    private const int OffTxRate = OffAssoc + 64;     // kbps
    private const int OffSecurity = OffAssoc + 68;   // WLAN_SECURITY_ATTRIBUTES.bSecurityEnabled
    private const int ConnectionAttributesMinSize = OffSecurity + 4;

    [DllImport("wlanapi.dll", SetLastError = false)]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll", SetLastError = false)]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll", SetLastError = false)]
    private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll", SetLastError = false)]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("wlanapi.dll", SetLastError = false)]
    private static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode,
        IntPtr reserved, out uint dataSize, out IntPtr data, IntPtr opcodeValueType);

    /// <summary>Queries the first connected WLAN interface. Never throws; never logs the SSID.</summary>
    public static WlanQueryResult Query()
    {
        IntPtr handle = IntPtr.Zero;
        IntPtr list = IntPtr.Zero;
        try
        {
            uint rc;
            try
            {
                rc = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out handle);
            }
            catch (DllNotFoundException)
            {
                return WlanQueryResult.Unavailable(WlanUnavailableReason.ApiUnavailable, "wlanapi.dll");
            }
            catch (EntryPointNotFoundException)
            {
                return WlanQueryResult.Unavailable(WlanUnavailableReason.ApiUnavailable, "wlanapi entry point");
            }
            if (rc != ErrorSuccess)
            {
                var reason = rc is ErrorServiceNotActive or ErrorNotSupported
                    ? WlanUnavailableReason.ApiUnavailable
                    : WlanUnavailableReason.Error;
                return WlanQueryResult.Unavailable(reason, "WlanOpenHandle " + rc);
            }

            rc = WlanEnumInterfaces(handle, IntPtr.Zero, out list);
            if (rc != ErrorSuccess || list == IntPtr.Zero)
                return WlanQueryResult.Unavailable(WlanUnavailableReason.Error, "WlanEnumInterfaces " + rc);

            int count = Marshal.ReadInt32(list, 0);
            if (count <= 0)
                return WlanQueryResult.Unavailable(WlanUnavailableReason.NoAdapter);

            bool anyConnected = false;
            for (int i = 0; i < count; i++)
            {
                IntPtr info = list + 8 + i * InterfaceInfoSize;
                var guid = Marshal.PtrToStructure<Guid>(info);
                string description = Marshal.PtrToStringUni(info + 16, 256)?.TrimEnd('\0') ?? "";
                int state = Marshal.ReadInt32(info, 528);
                if (state != InterfaceStateConnected) continue;
                anyConnected = true;

                var conn = QueryConnection(handle, guid, description);
                if (conn != null)
                    return new WlanQueryResult { Connection = conn };
            }
            return WlanQueryResult.Unavailable(anyConnected ? WlanUnavailableReason.Error : WlanUnavailableReason.NotConnected);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "WLAN query");
            return WlanQueryResult.Unavailable(WlanUnavailableReason.Error, ex.GetType().Name);
        }
        finally
        {
            if (list != IntPtr.Zero) { try { WlanFreeMemory(list); } catch { /* ignore */ } }
            if (handle != IntPtr.Zero) { try { WlanCloseHandle(handle, IntPtr.Zero); } catch { /* ignore */ } }
        }
    }

    private static WlanConnectionInfo? QueryConnection(IntPtr handle, Guid guid, string description)
    {
        IntPtr data = IntPtr.Zero;
        try
        {
            uint rc = WlanQueryInterface(handle, ref guid, OpcodeCurrentConnection, IntPtr.Zero, out uint size, out data, IntPtr.Zero);
            if (rc != ErrorSuccess || data == IntPtr.Zero)
            {
                AppLog.Warn("WLAN current_connection rc=" + rc);
                return null;
            }
            if (size < ConnectionAttributesMinSize)
            {
                AppLog.Warn("WLAN current_connection size=" + size);
                return null;
            }
            if (Marshal.ReadInt32(data, OffState) != InterfaceStateConnected)
                return null;

            int ssidLen = Math.Clamp(Marshal.ReadInt32(data, OffSsidLength), 0, 32);
            var ssidBytes = new byte[ssidLen];
            Marshal.Copy(data + OffSsidBytes, ssidBytes, 0, ssidLen);
            string? ssid = ssidLen == 0 ? null : DecodeSsid(ssidBytes);

            var bssid = new byte[6];
            Marshal.Copy(data + OffBssid, bssid, 0, 6);
            int phy = Marshal.ReadInt32(data, OffPhyType);
            int quality = Math.Clamp(Marshal.ReadInt32(data, OffSignalQuality), 0, 100);
            uint rxKbps = (uint)Marshal.ReadInt32(data, OffRxRate);
            uint txKbps = (uint)Marshal.ReadInt32(data, OffTxRate);
            bool security = Marshal.ReadInt32(data, OffSecurity) != 0;

            int? channel = QueryInt(handle, guid, OpcodeChannelNumber);
            int? rssi = QueryInt(handle, guid, OpcodeRssi);
            if (rssi is > 0 or < -120) rssi = null; // some drivers return quality or garbage here

            return new WlanConnectionInfo
            {
                InterfaceDescription = description,
                Ssid = ssid,
                BssidOui = MaskBssid(bssid),
                PhyCode = PhyCodeOf(phy),
                SignalQuality = quality,
                EstimatedDbm = EstimateDbm(quality),
                RssiDbm = rssi,
                RxRateMbps = rxKbps / 1000.0,
                TxRateMbps = txKbps / 1000.0,
                Channel = channel,
                BandCode = BandOf(channel),
                SecurityEnabled = security
            };
        }
        finally
        {
            if (data != IntPtr.Zero) { try { WlanFreeMemory(data); } catch { /* ignore */ } }
        }
    }

    private static int? QueryInt(IntPtr handle, Guid guid, int opcode)
    {
        IntPtr data = IntPtr.Zero;
        try
        {
            uint rc = WlanQueryInterface(handle, ref guid, opcode, IntPtr.Zero, out uint size, out data, IntPtr.Zero);
            if (rc != ErrorSuccess || data == IntPtr.Zero || size < 4) return null;
            return Marshal.ReadInt32(data);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (data != IntPtr.Zero) { try { WlanFreeMemory(data); } catch { /* ignore */ } }
        }
    }

    // --- Pure helpers (unit-tested) ---------------------------------------------------------------

    /// <summary>
    /// Windows derives quality from RSSI linearly: quality = 2 × (RSSI + 100), clamped to 0..100.
    /// Inverse: RSSI ≈ quality / 2 − 100 (so 100 → −50 dBm, 50 → −75 dBm, 0 → −100 dBm).
    /// </summary>
    public static int EstimateDbm(int quality) => Math.Clamp(quality, 0, 100) / 2 - 100;

    /// <summary>"AA:BB:CC" (vendor OUI only) or null for an all-zero / short address.</summary>
    public static string? MaskBssid(byte[]? bssid)
    {
        if (bssid == null || bssid.Length < 6) return null;
        if (bssid.All(b => b == 0)) return null;
        return $"{bssid[0]:X2}:{bssid[1]:X2}:{bssid[2]:X2}";
    }

    /// <summary>DOT11_PHY_TYPE → stable code (802.11n etc.).</summary>
    public static string PhyCodeOf(int phy) => phy switch
    {
        4 => "802.11a",   // dot11_phy_type_ofdm
        5 => "802.11b",   // dot11_phy_type_hrdsss
        6 => "802.11g",   // dot11_phy_type_erp
        7 => "802.11n",   // dot11_phy_type_ht
        8 => "802.11ac",  // dot11_phy_type_vht
        9 => "802.11ad",  // dot11_phy_type_dmg
        10 => "802.11ax", // dot11_phy_type_he
        11 => "802.11be", // dot11_phy_type_eht
        _ => "unknown"
    };

    /// <summary>
    /// Band from channel number: 1..14 → 2.4 GHz, 32..177 → 5 GHz. 6 GHz channels reuse 1..233 and
    /// cannot be told apart from the number alone, so an ax/be link on channel ≤ 14 may really be 6 GHz.
    /// </summary>
    public static string BandOf(int? channel) => channel switch
    {
        >= 1 and <= 14 => "2.4",
        >= 32 and <= 177 => "5",
        _ => "unknown"
    };

    /// <summary>SSID bytes are UTF-8 by convention; undecodable bytes are replaced, never thrown on.</summary>
    public static string DecodeSsid(byte[] bytes)
    {
        try { return Encoding.UTF8.GetString(bytes).TrimEnd('\0'); }
        catch { return Encoding.Latin1.GetString(bytes); }
    }
}
