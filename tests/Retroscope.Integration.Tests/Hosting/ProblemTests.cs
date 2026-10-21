using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

[Trait("Category", "Hosting")]
public sealed class ProblemTests {
    [Fact]
    public async Task Unknown_route_is_a_problem_json_404() {
        await using var app = await StartAsync();

        using var response = await app.Client.GetAsync(new Uri("/api/nowhere", UriKind.Relative), TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, 404, "not_found");
    }

    [Fact]
    public async Task Wrong_method_is_a_problem_json_405() {
        await using var app = await StartAsync();
        using var request = CsrfTests.Unsafe(HttpMethod.Delete, "/healthz");

        using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, 405, "method_not_allowed");
    }

    [Fact]
    public async Task Unhandled_exception_is_a_problem_json_500_without_details() {
        await using var app = await StartAsync();

        using var response = await app.Client.GetAsync(new Uri("/test/throws", UriKind.Relative), TestContext.Current.CancellationToken);

        var body = await AssertProblemAsync(response, 500, "internal");
        Assert.DoesNotContain("secret detail", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_json_body_is_a_problem_json_400() {
        await using var app = await StartAsync();
        using var request = CsrfTests.Unsafe(HttpMethod.Post, "/test/json");
        request.Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json");

        using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, 400, "invalid");
    }

    [Fact]
    public async Task Handler_problem_carries_code_and_fields() {
        await using var app = await StartAsync();

        using var response = await app.Client.GetAsync(new Uri("/test/fields", UriKind.Relative), TestContext.Current.CancellationToken);

        var body = await AssertProblemAsync(response, 422, "validation");
        using var json = JsonDocument.Parse(body);
        Assert.Equal("too_long", json.RootElement.GetProperty("fields").GetProperty("title").GetString());
        Assert.Equal("Unprocessable Content", json.RootElement.GetProperty("title").GetString());
    }

    internal static async Task<string> AssertProblemAsync(HttpResponseMessage response, int status, string code) {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(status == (int)response.StatusCode, $"{(int)response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(status, json.RootElement.GetProperty("status").GetInt32());
        return body;
    }

    private static Task<TestApp> StartAsync() => TestApp.StartAsync(map: app => {
        app.MapGet("/test/throws", (Func<string>)(() => throw new InvalidOperationException("secret detail")))
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(RateLimit.Unlimited);
        app.MapPost("/test/json", (Payload payload) => Results.Ok(payload.Name))
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(RateLimit.Unlimited);
        app.MapGet("/test/fields", () => Problem.Result(422, "validation", new Dictionary<string, string> { ["title"] = "too_long" }))
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(RateLimit.Unlimited);
    });

    private sealed record Payload(string Name);
}
