using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

[Trait("Category", "Hosting")]
public sealed partial class SecurityHeaderTests : IDisposable {
    private readonly FakeWebBuild _web = new();

    public void Dispose() => _web.Dispose();

    [Fact]
    public async Task Shell_carries_the_exact_csp_with_the_hash_from_csp_json() {
        await using var app = await TestApp.StartAsync(_web.Settings());

        using var response = await app.Client.GetAsync(new Uri("/dashboard", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(
            "default-src 'none'; " +
            $"script-src 'self' '{_web.ThemeHash}'; " +
            "style-src 'self'; img-src 'self' data: blob:; font-src 'self'; " +
            "connect-src 'self' wss://retro.test; media-src 'self'; manifest-src 'self'; form-action 'self'; " +
            "frame-ancestors 'none'; base-uri 'none'; object-src 'none'; " +
            "require-trusted-types-for 'script'; trusted-types 'none'",
            Header(response, "Content-Security-Policy"));
    }

    [Fact]
    public async Task The_inlined_theme_script_hashes_to_the_csp_source() {
        await using var app = await TestApp.StartAsync(_web.Settings());

        using var response = await app.Client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var inline = InlineScript().Match(html);
        Assert.True(inline.Success, html);
        var hash = $"sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(inline.Groups[1].Value)))}";
        Assert.Contains($"'{hash}'", Header(response, "Content-Security-Policy"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Csp_names_the_plain_websocket_scheme_for_an_http_origin() {
        await using var app = await TestApp.StartAsync(new() { ["PUBLIC_URL"] = "http://localhost:5000" });

        using var response = await app.Client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("connect-src 'self' ws://localhost:5000;", Header(response, "Content-Security-Policy"), StringComparison.Ordinal);
        Assert.Contains("script-src 'self';", Header(response, "Content-Security-Policy"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/healthz")]
    [InlineData("/assets/entry-AAAA.js")]
    [InlineData("/no/such/route")]
    [InlineData("/test/throws")]
    public async Task Every_response_carries_the_security_headers(string path) {
        await using var app = await TestApp.StartAsync(_web.Settings(), map: app =>
            app.MapGet("/test/throws", (Func<string>)(() => throw new InvalidOperationException("boom")))
                .RequireRoutePolicy(RoutePolicy.Public)
                .RequireRateLimit(RateLimit.Unlimited));

        using var response = await app.Client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.StartsWith("default-src 'none';", Header(response, "Content-Security-Policy"), StringComparison.Ordinal);
        Assert.Equal("max-age=63072000; includeSubDomains", Header(response, "Strict-Transport-Security"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("same-origin", Header(response, "Referrer-Policy"));
        Assert.Equal("camera=(), microphone=(), geolocation=()", Header(response, "Permissions-Policy"));
        Assert.Equal("same-origin", Header(response, "Cross-Origin-Opener-Policy"));
        Assert.False(response.Headers.Contains("Server"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/dashboard")]
    [InlineData("/healthz")]
    public async Task An_https_public_url_sends_hsts(string path) {
        await using var app = await TestApp.StartAsync(_web.Settings());

        using var response = await app.Client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal("max-age=63072000; includeSubDomains", Header(response, "Strict-Transport-Security"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/dashboard")]
    [InlineData("/healthz")]
    public async Task An_http_public_url_sends_no_hsts(string path) {
        var settings = _web.Settings();
        settings["PUBLIC_URL"] = "http://localhost:5000";
        await using var app = await TestApp.StartAsync(settings);

        using var response = await app.Client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [Fact]
    public async Task An_endpoint_may_replace_the_csp_with_a_stricter_one() {
        const string Sandbox = "default-src 'none'; sandbox";
        await using var app = await TestApp.StartAsync(map: app =>
            app.MapGet("/test/blob", (HttpContext context) => {
                context.Response.Headers.ContentSecurityPolicy = Sandbox;
                return Results.Bytes([1, 2, 3], "image/png");
            }).RequireRoutePolicy(RoutePolicy.Public).RequireRateLimit(RateLimit.Unlimited));

        using var response = await app.Client.GetAsync(new Uri("/test/blob", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Sandbox, Header(response, "Content-Security-Policy"));
    }

    internal static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : "";

    [GeneratedRegex("""</script>\n<script>(.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex InlineScript();
}
