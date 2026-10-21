using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

[Trait("Category", "Hosting")]
public sealed class CsrfTests {
    public static TheoryData<string, string?, string?, string?, bool> Matrix() {
        var data = new TheoryData<string, string?, string?, string?, bool>();
        foreach (var method in new[] { "POST", "PUT", "PATCH", "DELETE" }) {
            // method, X-RS, Origin, Sec-Fetch-Site, allowed
            data.Add(method, "1", TestApp.PublicUrl, null, true);
            data.Add(method, "1", TestApp.PublicUrl, "same-origin", true);
            data.Add(method, "1", TestApp.PublicUrl, "cross-site", true);
            data.Add(method, "1", null, "same-origin", true);
            data.Add(method, null, TestApp.PublicUrl, "same-origin", false);
            data.Add(method, "0", TestApp.PublicUrl, null, false);
            data.Add(method, "true", TestApp.PublicUrl, null, false);
            data.Add(method, "1", "https://evil.test", null, false);
            data.Add(method, "1", "https://evil.test", "same-origin", false);
            data.Add(method, "1", "https://retro.test.evil.test", null, false);
            data.Add(method, "1", "http://retro.test", null, false);
            data.Add(method, "1", "https://retro.test:8443", null, false);
            data.Add(method, "1", "https://RETRO.test", null, false);
            data.Add(method, "1", "null", null, false);
            data.Add(method, "1", null, "same-site", false);
            data.Add(method, "1", null, "cross-site", false);
            data.Add(method, "1", null, "none", false);
            data.Add(method, "1", null, null, false);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Unsafe_methods_need_the_custom_header_and_a_same_origin_signal(
        string method,
        string? xrs,
        string? origin,
        string? fetchSite,
        bool allowed) {
        await using var app = await StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/test/write");
        AddHeaders(request, xrs, origin, fetchSite);

        using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        if (allowed) {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        } else {
            await ProblemTests.AssertProblemAsync(response, 403, "csrf");
        }
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task Safe_methods_need_neither(string method) {
        await using var app = await StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/test/write");
        request.Headers.Add("Origin", "https://evil.test");

        using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Two_origin_headers_are_refused() {
        await using var app = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/test/write");
        request.Headers.Add(CsrfFilter.HeaderName, "1");
        request.Headers.TryAddWithoutValidation("Origin", [TestApp.PublicUrl, TestApp.PublicUrl]);

        using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        await ProblemTests.AssertProblemAsync(response, 403, "csrf");
    }

    [Fact]
    public async Task Csrf_runs_before_the_handler_and_before_rate_limits() {
        var calls = 0;
        await using var app = await TestApp.StartAsync(map: app =>
            app.MapPost("/test/counted", () => {
                calls++;
                return Results.NoContent();
            }).RequireRoutePolicy(RoutePolicy.Public)
              .RequireRateLimit(RateLimit.Define("test.counted", RateRule.PerIp(1, TimeSpan.FromHours(1)))));

        for (var i = 0; i < 3; i++) {
            using var forged = new HttpRequestMessage(HttpMethod.Post, "/test/counted");
            using var refused = await app.Client.SendAsync(forged, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        using var request = Unsafe(HttpMethod.Post, "/test/counted");
        using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, calls);
    }

    internal static HttpRequestMessage Unsafe(HttpMethod method, string path) {
        var request = new HttpRequestMessage(method, path);
        AddHeaders(request, "1", TestApp.PublicUrl, "same-origin");
        return request;
    }

    private static void AddHeaders(HttpRequestMessage request, string? xrs, string? origin, string? fetchSite) {
        if (xrs is not null) {
            request.Headers.Add(CsrfFilter.HeaderName, xrs);
        }
        if (origin is not null) {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }
        if (fetchSite is not null) {
            request.Headers.Add("Sec-Fetch-Site", fetchSite);
        }
    }

    private static Task<TestApp> StartAsync() => TestApp.StartAsync(map: app =>
        app.MapMethods("/test/write", ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"], () => Results.NoContent())
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(RateLimit.Unlimited));
}
