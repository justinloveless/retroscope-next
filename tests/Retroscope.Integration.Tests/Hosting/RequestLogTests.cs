using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
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

    // Every way a hand-made request can spell a path under /invite: encoded separators and letters, dot and empty
    // segments, backslashes, mixed case, double encoding, and anything at all after the prefix.
    [Theory]
    [InlineData("/invite/abc")]
    [InlineData("/INVITE/abc/def")]
    [InlineData("/Invite/abc")]
    [InlineData("/invite/")]
    [InlineData("/invites/abc")]
    [InlineData("/invite%2Fabc")]
    [InlineData("/invite%2fabc")]
    [InlineData("/InViTe%2Fabc")]
    [InlineData("/invite%252Fabc")]
    [InlineData("/invite%5Cabc")]
    [InlineData("/invite\\abc")]
    [InlineData("/%69nvite/abc")]
    [InlineData("/%49NVITE%2Fabc")]
    [InlineData("/invite/%2e%2e/abc")]
    [InlineData("/invite/%2E%2E/%2E%2E/abc")]
    [InlineData("/invite/../abc")]
    [InlineData("//invite/abc")]
    [InlineData("/./invite/abc")]
    [InlineData("/retro/../invite/abc")]
    [InlineData("/retro/%2e%2e/INVITE/abc")]
    [InlineData("/retro%2F..%2Finvite%2Fabc")]
    [InlineData("/invite;abc")]
    [InlineData("/invite.abc")]
    public void Any_spelling_of_a_path_under_invite_is_redacted(string path) =>
        Assert.Equal(RequestLog.RedactedInvitePath, RequestLog.Redact(new PathString(path)));

    [Theory]
    [InlineData("/invite/abc?x=1")]
    [InlineData("/invite%2Fabc")]
    [InlineData("/%2e/invite/abc")]
    [InlineData("http://retro.example/invite/abc")]
    [InlineData("https://retro.example/INVITE%2Fabc?x=1")]
    public void A_raw_target_under_invite_is_redacted_whatever_the_decoded_path(string rawTarget) =>
        Assert.Equal(RequestLog.RedactedInvitePath, RequestLog.Redact(new PathString("/"), rawTarget));

    [Theory]
    [InlineData("/invite", null)]
    [InlineData("/invite", "/invite?t=abc")]
    [InlineData("/invite", "http://retro.example/invite")]
    [InlineData("/retro/ABC123", "/retro/ABC123")]
    [InlineData("/api/teams/abc/invites", "/api/teams/abc/invites")]
    [InlineData("/i/nvite", "/i/nvite")]
    [InlineData("/retro/invite/abc", "/retro/invite/abc")]
    public void Paths_outside_the_invite_prefix_are_logged_as_they_are(string path, string? rawTarget) =>
        Assert.Equal(path, RequestLog.Redact(new PathString(path), rawTarget));

    [Theory]
    [InlineData("/invite%2F")]
    [InlineData("/INVITE%2f")]
    public async Task An_encoded_invite_path_never_logs_its_token(string prefix) {
        var logs = new CapturingLoggerProvider();
        await using var app = await StartAsync(logs);

        using var response = await app.Client.GetAsync(new Uri(prefix + Token, UriKind.Relative), Ct);

        Assert.Contains(logs.Entries, x => x.Message.StartsWith("GET /invite/[redacted] ", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, x => x.Message.Contains(Token, StringComparison.Ordinal));
    }

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

    [Theory]
    [InlineData(null, "json")]
    [InlineData("simple", "simple")]
    public async Task Console_logs_are_json_unless_configured_otherwise(string? configured, string expected) {
        await using var app = await TestApp.StartAsync(new() { ["Logging:Console:FormatterName"] = configured });

        Assert.Equal(expected, app.Services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName);
        var json = app.Services.GetRequiredService<IOptionsMonitor<JsonConsoleFormatterOptions>>().CurrentValue;
        Assert.Equal("yyyy-MM-ddTHH:mm:ss.fffZ", json.TimestampFormat);
        Assert.True(json.UseUtcTimestamp);
    }

    private static Task<TestApp> StartAsync(CapturingLoggerProvider logs, Action<WebApplication>? map = null) =>
        TestApp.StartAsync(services: services => services.AddLogging(logging => logging.AddProvider(logs)), map: map);
}
