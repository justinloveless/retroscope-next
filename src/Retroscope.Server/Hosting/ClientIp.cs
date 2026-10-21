using System.Net;
using System.Net.Sockets;

namespace Retroscope.Server.Hosting;

/// <summary>
/// The caller's address after <see cref="Forwarded"/> has run. IPv6 callers control a whole /64, so limits key on
/// the prefix (S32); IPv4-mapped addresses from dual-mode sockets count as the IPv4 address they carry.
/// </summary>
public static class ClientIp {
    public static IPAddress? Of(HttpContext context) {
        var address = context.Connection.RemoteIpAddress;
        return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
    }

    /// <summary>The key every per-IP limit uses: the IPv4 address, or the IPv6 /64.</summary>
    public static string LimitKey(HttpContext context) => LimitKey(Of(context));

    public static string LimitKey(IPAddress? address) => Prefix(address, 32, 64);

    /// <summary>The prefix audit rows and login backoff store: IPv4 /24, IPv6 /48 (S22).</summary>
    public static string AuditPrefix(IPAddress? address) => Prefix(address, 24, 48);

    public static string Prefix(IPAddress? address, int v4Bits, int v6Bits) {
        if (address is null) {
            return "unknown";
        }
        if (address.IsIPv4MappedToIPv6) {
            address = address.MapToIPv4();
        }
        var bits = address.AddressFamily == AddressFamily.InterNetwork ? v4Bits : v6Bits;
        var network = new IPNetwork(Mask(address, bits), bits);
        return network.ToString();
    }

    private static IPAddress Mask(IPAddress address, int bits) {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++) {
            var keep = Math.Clamp(bits - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }
        return new IPAddress(bytes);
    }
}
