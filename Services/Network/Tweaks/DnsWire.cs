using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Visor.Services.NetworkTweaks;

/// <summary>What happened to one DNS query.</summary>
public enum DnsResponseStatus
{
    /// <summary>RCODE 0 (NOERROR) — may still carry zero A records (CNAME-only / NODATA).</summary>
    Ok,
    /// <summary>RCODE 3 — the server answered "no such name" (still a timely answer).</summary>
    NxDomain,
    /// <summary>TC bit set — the server answered but the UDP response was cut; it still counts as an answer for timing.</summary>
    Truncated,
    /// <summary>RCODE 2.</summary>
    ServerFailure,
    /// <summary>RCODE 5.</summary>
    Refused,
    /// <summary>Any other non-zero RCODE.</summary>
    OtherError,
    /// <summary>Not a parsable DNS response.</summary>
    Malformed,
    /// <summary>Response for another query id (ignored by the client while waiting).</summary>
    IdMismatch,
    Timeout,
    /// <summary>Socket error (e.g. ICMP port unreachable, no route).</summary>
    NetworkError,
}

public sealed record DnsParseResult(DnsResponseStatus Status, int Rcode, IReadOnlyList<IPAddress> Addresses)
{
    /// <summary>True when the server answered in time with a usable response (timing counts).</summary>
    public bool Answered => Status is DnsResponseStatus.Ok or DnsResponseStatus.NxDomain or DnsResponseStatus.Truncated;
}

/// <summary>
/// Minimal RFC 1035 wire format: one A question with recursion desired, and a bounds-checked parser
/// for the response (compression pointers supported, every length checked, no recursion).
/// </summary>
public static class DnsWire
{
    public const int HeaderLength = 12;
    public const ushort TypeA = 1;
    public const ushort ClassIn = 1;
    public const int MaxNameLength = 253;
    public const int MaxLabelLength = 63;

    private const ushort FlagQr = 0x8000;
    private const ushort FlagTc = 0x0200;
    private const ushort FlagRd = 0x0100;

    /// <summary>Query packet for an A record of <paramref name="name"/>. Throws <see cref="ArgumentException"/> for an invalid name.</summary>
    public static byte[] BuildAQuery(ushort id, string name)
    {
        var qname = EncodeName(name);
        var packet = new byte[HeaderLength + qname.Length + 4];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), id);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), FlagRd);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 1);   // QDCOUNT
        // ANCOUNT / NSCOUNT / ARCOUNT stay 0.
        qname.CopyTo(packet, HeaderLength);
        int o = HeaderLength + qname.Length;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(o), TypeA);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(o + 2), ClassIn);
        return packet;
    }

    /// <summary>"www.example.com" → 3www7example3com0. ASCII host names only (IDN must be punycoded first).</summary>
    public static byte[] EncodeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("empty name", nameof(name));
        name = name.TrimEnd('.');
        if (name.Length == 0 || name.Length > MaxNameLength) throw new ArgumentException("bad name length", nameof(name));
        var bytes = new List<byte>(name.Length + 2);
        foreach (var label in name.Split('.'))
        {
            if (label.Length == 0 || label.Length > MaxLabelLength) throw new ArgumentException("bad label", nameof(name));
            foreach (char c in label)
            {
                bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_';
                if (!ok) throw new ArgumentException("non-ASCII host name", nameof(name));
            }
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        return bytes.ToArray();
    }

    public static ushort ReadId(ReadOnlySpan<byte> packet) =>
        packet.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(packet) : (ushort)0;

    /// <summary>Parses a response to the query with <paramref name="expectedId"/>.</summary>
    public static DnsParseResult Parse(ReadOnlySpan<byte> packet, ushort expectedId)
    {
        var none = Array.Empty<IPAddress>();
        if (packet.Length < HeaderLength) return new DnsParseResult(DnsResponseStatus.Malformed, -1, none);
        ushort id = BinaryPrimitives.ReadUInt16BigEndian(packet);
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        int qd = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        int an = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        int rcode = flags & 0x000F;

        if (id != expectedId) return new DnsParseResult(DnsResponseStatus.IdMismatch, rcode, none);
        if ((flags & FlagQr) == 0) return new DnsParseResult(DnsResponseStatus.Malformed, rcode, none);
        if ((flags & FlagTc) != 0) return new DnsParseResult(DnsResponseStatus.Truncated, rcode, none);
        switch (rcode)
        {
            case 0: break;
            case 2: return new DnsParseResult(DnsResponseStatus.ServerFailure, rcode, none);
            case 3: return new DnsParseResult(DnsResponseStatus.NxDomain, rcode, none);
            case 5: return new DnsParseResult(DnsResponseStatus.Refused, rcode, none);
            default: return new DnsParseResult(DnsResponseStatus.OtherError, rcode, none);
        }

        int o = HeaderLength;
        for (int i = 0; i < qd; i++)
        {
            if (!SkipName(packet, ref o) || o + 4 > packet.Length)
                return new DnsParseResult(DnsResponseStatus.Malformed, rcode, none);
            o += 4;
        }

        var addresses = new List<IPAddress>();
        for (int i = 0; i < an; i++)
        {
            if (!SkipName(packet, ref o) || o + 10 > packet.Length)
                return new DnsParseResult(DnsResponseStatus.Malformed, rcode, none);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet[o..]);
            ushort cls = BinaryPrimitives.ReadUInt16BigEndian(packet[(o + 2)..]);
            int rdLen = BinaryPrimitives.ReadUInt16BigEndian(packet[(o + 8)..]);
            o += 10;
            if (o + rdLen > packet.Length)
                return new DnsParseResult(DnsResponseStatus.Malformed, rcode, none);
            if (type == TypeA && cls == ClassIn && rdLen == 4)
                addresses.Add(new IPAddress(packet.Slice(o, 4)));
            o += rdLen;
        }
        return new DnsParseResult(DnsResponseStatus.Ok, rcode, addresses);
    }

    /// <summary>Advances past a (possibly compressed) name. False when it runs off the packet or is malformed.</summary>
    private static bool SkipName(ReadOnlySpan<byte> packet, ref int o)
    {
        int labels = 0;
        while (true)
        {
            if (o >= packet.Length) return false;
            byte len = packet[o];
            if (len == 0) { o += 1; return true; }
            if ((len & 0xC0) == 0xC0)
            {
                // Compression pointer ends the name in this position (target not followed — we only skip).
                if (o + 2 > packet.Length) return false;
                o += 2;
                return true;
            }
            if ((len & 0xC0) != 0 || len > MaxLabelLength) return false;
            o += 1 + len;
            if (++labels > 127) return false;
        }
    }
}
