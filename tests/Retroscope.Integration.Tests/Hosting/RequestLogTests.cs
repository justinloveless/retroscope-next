using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Retroscope.Integration.Tests.Blobs;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

// §i.3 request logs and S23: one line per request without query or body; a legacy invite token never reaches a log.
[Trait("Category", "Hosting")]
public sealed class RequestLogTests {
    private const string Token = "s3cr3t-Invite-Token_0123456789abcdef";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_legacy_invite_link_redirects_to_the_fragment_and_its_token_is_never_logged() {
        var logs = new CapturingLoggerProvider();
        await using var app = await StartAsync(logs);

        using var response = await app.Client.GetAsync(new Uri($"/invite/{Token}?utm=x", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/invite#t={Token}", response.Headers.Location!.OriginalString);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains(logs.Entries, x => x.Message.StartsWith("GET /invite/[redacted] 302 in ", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, x => x.Message.Contains(Token, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/invite/abc", "/invite/[redacted]")]
    [InlineData("/INVITE/abc/def", "/invite/[redacted]")]
    [InlineData("/invite", "/invite")]
    [InlineData("/invite/", "/invite/")]
    [InlineData("/invites/abc", "/invites/abc")]
    [InlineData("/retro/ABC123", "/retro/ABC123")]
    public void Only_paths_under_the_invite_segment_are_redacted(string path, string logged) =>
        Assert.Equal(logged, RequestLog.Redact(new PathString(path)));

    [Fact]
    public async Task Each_request_logs_method_path_and_status_but_not_the_query_nor_the_health_probe() {
        var logs = new CapturingLoggerProvider();
        await using var app = await StartAsync(logs, map: web => web.MapGet("/test/echo", () => Results.StatusCode(418))
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(RateLimit.Unlimited));

        using var teapot = await app.Client.GetAsync(new Uri("/test/echo?secret=q1", UriKind.Relative), Ct);
        using var health = await app.Client.GetAsync(new Uri("/healthz", UriKind.Relative), Ct);

        var line = Assert.Single(logs.Entries, x => x.Message.StartsWith("GET /test/echo", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.StartsWith("GET /test/echo 418 in ", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Entries, x => x.Message.Contains("q1", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, x => x.Message.Contains("/healthz", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_request_is_logged_with_the_problem_status() {
        var logs = new CapturingLoggerProvider();
        await using var app = await StartAsync(logs, map: web => web.MapGet("/test/throws", (Func<string>)(() => throw new InvalidOperationException("boom")))
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(RateLimit.Unlimited));

        using var response = await app.Client.GetAsync(new Uri("/test/throws", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(logs.Entries, x => x.Message.StartsWith("GET /test/throws 500 in ", StringComparison.Ordinal));
    }

    private static Task<TestApp> StartAsync(CapturingLoggerProvider logs, Action<WebApplication>? map = null) =>
        TestApp.StartAsync(services: services => services.AddLogging(logging => logging.AddProvider(logs)), map: map);
}
