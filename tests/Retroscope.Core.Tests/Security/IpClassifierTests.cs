using System.Net;
using Retroscope.Core.Security;

namespace Retroscope.Core.Tests.Security;

[Trait("Category", "Security")]
public sealed class IpClassifierTests {
    [Theory]
    // Loopback in every notation IPAddress (and inet_aton) accepts: decimal, octal, hex, shortened.
    [InlineData("127.0.0.1", IpRange.Loopback)]
    [InlineData("2130706433", IpRange.Loopback)]
    [InlineData("0177.0.0.1", IpRange.Loopback)]
    [InlineData("017700000001", IpRange.Loopback)]
    [InlineData("0x7f.0.0.1", IpRange.Loopback)]
    [InlineData("0x7f000001", IpRange.Loopback)]
    [InlineData("127.1", IpRange.Loopback)]
    [InlineData("127.255.255.254", IpRange.Loopback)]
    // RFC 1918, also written in decimal and hex.
    [InlineData("10.0.0.1", IpRange.Private)]
    [InlineData("167772161", IpRange.Private)]
    [InlineData("0xa.0.0.1", IpRange.Private)]
    [InlineData("172.16.0.1", IpRange.Private)]
    [InlineData("172.31.255.255", IpRange.Private)]
    [InlineData("192.168.1.1", IpRange.Private)]
    [InlineData("0300.0250.1.1", IpRange.Private)]
    // CGNAT 100.64.0.0/10 and its edges.
    [InlineData("100.64.0.1", IpRange.CarrierGradeNat)]
    [InlineData("100.127.255.255", IpRange.CarrierGradeNat)]
    [InlineData("100.63.255.255", IpRange.Public)]
    [InlineData("100.128.0.0", IpRange.Public)]
    [InlineData("169.254.169.254", IpRange.LinkLocal)]
    [InlineData("0.0.0.0", IpRange.Unspecified)]
    [InlineData("0", IpRange.Unspecified)]
    [InlineData("0.1.2.3", IpRange.Reserved)]
    [InlineData("224.0.0.1", IpRange.Multicast)]
    [InlineData("239.255.255.250", IpRange.Multicast)]
    [InlineData("240.0.0.1", IpRange.Reserved)]
    [InlineData("255.255.255.255", IpRange.Reserved)]
    [InlineData("192.0.2.10", IpRange.Reserved)]
    [InlineData("198.18.0.1", IpRange.Reserved)]
    [InlineData("172.15.255.255", IpRange.Public)]
    [InlineData("172.32.0.0", IpRange.Public)]
    [InlineData("8.8.8.8", IpRange.Public)]
    [InlineData("104.192.136.1", IpRange.Public)]
    // IPv6.
    [InlineData("::", IpRange.Unspecified)]
    [InlineData("::1", IpRange.Loopback)]
    [InlineData("fe80::1", IpRange.LinkLocal)]
    [InlineData("fc00::1", IpRange.UniqueLocal)]
    [InlineData("fd12:3456:789a::1", IpRange.UniqueLocal)]
    [InlineData("ff02::1", IpRange.Multicast)]
    [InlineData("fec0::1", IpRange.Reserved)]
    [InlineData("2001:db8::1", IpRange.Reserved)]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2", IpRange.Reserved)]
    [InlineData("2002:7f00:1::1", IpRange.Reserved)]
    [InlineData("64:ff9b::7f00:1", IpRange.Reserved)]
    [InlineData("::127.0.0.1", IpRange.Reserved)]
    [InlineData("100::1", IpRange.Reserved)]
    [InlineData("2606:4700:4700::1111", IpRange.Public)]
    [InlineData("2a00:1450:4001:80b::200e", IpRange.Public)]
    // IPv4-mapped IPv6 is classified by the IPv4 address it carries.
    [InlineData("::ffff:127.0.0.1", IpRange.Loopback)]
    [InlineData("::ffff:7f00:1", IpRange.Loopback)]
    [InlineData("::ffff:10.1.2.3", IpRange.Private)]
    [InlineData("::ffff:192.168.0.1", IpRange.Private)]
    [InlineData("::ffff:100.64.0.1", IpRange.CarrierGradeNat)]
    [InlineData("::ffff:169.254.169.254", IpRange.LinkLocal)]
    [InlineData("::ffff:0.0.0.0", IpRange.Unspecified)]
    [InlineData("::ffff:8.8.8.8", IpRange.Public)]
    public void Classifies(string literal, IpRange expected) {
        var address = IPAddress.Parse(literal);

        Assert.Equal(expected, IpClassifier.Classify(address));
        Assert.Equal(expected == IpRange.Public, IpClassifier.IsPublic(address));
    }

    [Fact]
    public void Scoped_link_local_is_still_link_local() {
        Assert.Equal(IpRange.LinkLocal, IpClassifier.Classify(IPAddress.Parse("fe80::1%3")));
    }
}
