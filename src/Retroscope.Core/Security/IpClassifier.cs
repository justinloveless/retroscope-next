using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Retroscope.Core.Security;

public enum IpRange {
    Public,
    Unspecified,
    Loopback,
    Private,
    CarrierGradeNat,
    LinkLocal,
    Multicast,
    UniqueLocal,
    Reserved,
}

/// <summary>
/// Decides whether an outbound connection may go to an address (DESIGN §f: the Jira handler's ConnectCallback vets
/// every resolved IP). Only globally reachable unicast is <see cref="IpRange.Public"/>; IPv6 outside 2000::/3 and the
/// IANA special-purpose ranges are refused. IPv4-mapped addresses are classified by the IPv4 address they carry;
/// other IPv4-embedding forms (IPv4-compatible, NAT64, 6to4, Teredo) are refused outright.
/// </summary>
public static class IpClassifier {
    private static readonly (uint Prefix, int Length, IpRange Range)[] V4Ranges = [
        (V4(0, 0, 0, 0), 8, IpRange.Reserved),
        (V4(10, 0, 0, 0), 8, IpRange.Private),
        (V4(100, 64, 0, 0), 10, IpRange.CarrierGradeNat),
        (V4(127, 0, 0, 0), 8, IpRange.Loopback),
        (V4(169, 254, 0, 0), 16, IpRange.LinkLocal),
        (V4(172, 16, 0, 0), 12, IpRange.Private),
        (V4(192, 0, 0, 0), 24, IpRange.Reserved),
        (V4(192, 0, 2, 0), 24, IpRange.Reserved),
        (V4(192, 88, 99, 0), 24, IpRange.Reserved),
        (V4(192, 168, 0, 0), 16, IpRange.Private),
        (V4(198, 18, 0, 0), 15, IpRange.Reserved),
        (V4(198, 51, 100, 0), 24, IpRange.Reserved),
        (V4(203, 0, 113, 0), 24, IpRange.Reserved),
        (V4(224, 0, 0, 0), 4, IpRange.Multicast),
        (V4(240, 0, 0, 0), 4, IpRange.Reserved),
    ];

    // Inside 2000::/3, plus the ranges outside it that deserve a more specific name than Reserved.
    private static readonly (UInt128 Prefix, int Length, IpRange Range)[] V6Ranges = [
        (V6(0x2001_0000, 0, 0, 0), 23, IpRange.Reserved),
        (V6(0x2001_0db8, 0, 0, 0), 32, IpRange.Reserved),
        (V6(0x2002_0000, 0, 0, 0), 16, IpRange.Reserved),
        (V6(0x3fff_0000, 0, 0, 0), 20, IpRange.Reserved),
        (V6(0xfc00_0000, 0, 0, 0), 7, IpRange.UniqueLocal),
        (V6(0xfe80_0000, 0, 0, 0), 10, IpRange.LinkLocal),
        (V6(0xff00_0000, 0, 0, 0), 8, IpRange.Multicast),
    ];

    private static readonly UInt128 GlobalUnicast = V6(0x2000_0000, 0, 0, 0);

    public static bool IsPublic(IPAddress address) => Classify(address) == IpRange.Public;

    public static IpRange Classify(IPAddress address) {
        ArgumentNullException.ThrowIfNull(address);
        return address.AddressFamily switch {
            AddressFamily.InterNetwork => ClassifyV4(ToUInt32(address)),
            AddressFamily.InterNetworkV6 when address.IsIPv4MappedToIPv6 => ClassifyV4(ToUInt32(address.MapToIPv4())),
            AddressFamily.InterNetworkV6 => ClassifyV6(address),
            _ => IpRange.Reserved,
        };
    }

    private static IpRange ClassifyV4(uint value) {
        if (value == 0) {
            return IpRange.Unspecified;
        }

        foreach (var (prefix, length, range) in V4Ranges) {
            if (value >> (32 - length) == prefix >> (32 - length)) {
                return range;
            }
        }

        return IpRange.Public;
    }

    private static IpRange ClassifyV6(IPAddress address) {
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        var value = BinaryPrimitives.ReadUInt128BigEndian(bytes);
        if (value == UInt128.Zero) {
            return IpRange.Unspecified;
        }
        if (value == UInt128.One) {
            return IpRange.Loopback;
        }

        foreach (var (prefix, length, range) in V6Ranges) {
            if (value >> (128 - length) == prefix >> (128 - length)) {
                return range;
            }
        }

        return value >> 125 == GlobalUnicast >> 125 ? IpRange.Public : IpRange.Reserved;
    }

    private static uint ToUInt32(IPAddress address) {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static uint V4(byte a, byte b, byte c, byte d) => (uint)(a << 24 | b << 16 | c << 8 | d);

    private static UInt128 V6(uint a, uint b, uint c, uint d) =>
        new((ulong)a << 32 | b, (ulong)c << 32 | d);
}
