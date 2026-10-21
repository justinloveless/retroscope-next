using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

[Trait("Category", "Hosting")]
public sealed class ForwardedTests {
    private const string Proxies = "10.0.1.0/24,fd00:1::/64";

    [Theory]
    [InlineData("10.0.1.7", "198.51.100.20", "198.51.100.20")]
    [InlineData("fd00:1::5", "198.51.100.20", "198.51.100.20")]
    [InlineData("10.0.1.7", "6.6.6.6, 198.51.100.20", "198.51.100.20")]
    [InlineData("10.0.1.7", "2001:db8:1:2:3:4:5:6", "2001:db8:1:2:3:4:5:6")]
    [InlineData("10.0.2.7", "198.51.100.20", "10.0.2.7")]
    [InlineData("127.0.0.1", "198.51.100.20", "127.0.0.1")]
    [InlineData("203.0.113.9", "198.51.100.20", "203.0.113.9")]
    public async Task Forwarded_for_is_read_only_from_trusted_proxies_and_only_the_rightmost_hop(
        string peer,
        string forwardedFor,
        string expected) {
        await using var app = await StartAsync(new() { ["TRUSTED_PROXY_CIDRS"] = Proxies });

        var ip = await ClientIpAsync(app, peer, ("X-Forwarded-For", forwardedFor));

        Assert.Equal(expected, ip);
    }

    [Fact]
    public async Task Nothing_is_trusted_without_configured_cidrs_not_even_loopback() {
        await using var app = await StartAsync();

        Assert.Equal("127.0.0.1", await ClientIpAsync(app, "127.0.0.1", ("X-Forwarded-For", "198.51.100.20")));
        Assert.Equal("::1", await ClientIpAsync(app, "::1", ("X-Forwarded-For", "198.51.100.20")));
    }

    [Theory]
    [InlineData("10.0.1.7", "198.51.100.20")]
    [InlineData("203.0.113.9", "203.0.113.9")]
    public async Task Cloudflare_mode_reads_the_configured_header_from_trusted_proxies_only(string peer, string expected) {
        await using var app = await StartAsync(new() {
            ["TRUSTED_PROXY_CIDRS"] = Proxies,
            ["CLIENT_IP_HEADER"] = "CF-Connecting-IP",
        });

        var ip = await ClientIpAsync(app, peer, ("CF-Connecting-IP", "198.51.100.20"), ("X-Forwarded-For", "6.6.6.6"));

        Assert.Equal(expected, ip);
    }

    [Fact]
    public async Task Cloudflare_mode_ignores_x_forwarded_for() {
        await using var app = await StartAsync(new() {
            ["TRUSTED_PROXY_CIDRS"] = Proxies,
            ["CLIENT_IP_HEADER"] = "CF-Connecting-IP",
        });

        Assert.Equal("10.0.1.7", await ClientIpAsync(app, "10.0.1.7", ("X-Forwarded-For", "6.6.6.6")));
    }

    [Fact]
    public async Task A_malformed_cidr_fails_startup() {
        await Assert.ThrowsAsync<InvalidOperationException>(async () => {
            await using var app = await StartAsync(new() { ["TRUSTED_PROXY_CIDRS"] = "10.0.1.0/24,not-a-cidr" });
        });
    }

    [Theory]
    [InlineData("198.51.100.20", "198.51.100.20/32", "198.51.100.0/24")]
    [InlineData("::ffff:198.51.100.20", "198.51.100.20/32", "198.51.100.0/24")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64", "2001:db8:1::/48")]
    public void Limit_keys_use_the_ipv6_64_and_audit_prefixes_the_24_and_48(string address, string limitKey, string auditPrefix) {
        var ip = IPAddress.Parse(address);

        Assert.Equal(limitKey, ClientIp.LimitKey(ip));
        Assert.Equal(auditPrefix, ClientIp.AuditPrefix(ip));
    }

    private static Task<TestApp> StartAsync(Dictionary<string, string?>? settings = null) =>
        TestApp.StartAsync(settings, map: app =>
            app.MapGet("/test/ip", (HttpContext context) => ClientIp.Of(context)?.ToString() ?? "none")
                .RequireRoutePolicy(RoutePolicy.Public)
                .RequireRateLimit(RateLimit.Unlimited));

    private static async Task<string> ClientIpAsync(TestApp app, string peer, params (string Name, string Value)[] headers) {
        using var request = TestApp.Get("/test/ip", peer);
        foreach (var (name, value) in headers) {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
