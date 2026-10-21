using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

[Trait("Category", "Hosting")]
public sealed class RoutePolicyTests {
    public static TheoryData<RoutePolicy, string?, int> Matrix() {
        var data = new TheoryData<RoutePolicy, string?, int>();
        var callers = new string?[] { null, "guest", "unverified", "user", "admin" };
        var expected = new Dictionary<RoutePolicy, int[]> {
            // anonymous, guest, unverified user, verified user, app admin
            [RoutePolicy.Public] = [204, 204, 204, 204, 204],
            [RoutePolicy.Guest] = [401, 204, 204, 204, 204],
            [RoutePolicy.User] = [401, 401, 204, 204, 204],
            [RoutePolicy.VerifiedUser] = [401, 401, 403, 204, 204],
            [RoutePolicy.TeamMember] = [401, 401, 204, 204, 204],
            [RoutePolicy.TeamAdmin] = [401, 401, 204, 204, 204],
            [RoutePolicy.TeamOwner] = [401, 401, 204, 204, 204],
            [RoutePolicy.AppAdmin] = [401, 401, 403, 403, 204],
            [RoutePolicy.BoardAccess] = [401, 204, 204, 204, 204],
            [RoutePolicy.BoardManage] = [401, 204, 204, 204, 204],
            [RoutePolicy.SessionManage] = [401, 204, 204, 204, 204],
        };
        Assert.Equal(Enum.GetValues<RoutePolicy>().Length, expected.Count);
        foreach (var (policy, statuses) in expected) {
            for (var i = 0; i < callers.Length; i++) {
                data.Add(policy, callers[i], statuses[i]);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task The_middleware_enforces_the_caller_half_of_each_policy(RoutePolicy policy, string? caller, int status) {
        await using var app = await TestApp.StartAsync(map: app => {
            foreach (var each in Enum.GetValues<RoutePolicy>()) {
                app.MapGet($"/test/{each}", () => Results.NoContent())
                    .RequireRoutePolicy(each)
                    .RequireRateLimit(RateLimit.Unlimited);
            }
        });
        using var request = TestApp.Get($"/test/{policy}");
        if (caller is not null) {
            request.Headers.Add(TestApp.CallerHeader, caller);
        }

        using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        if (status == 204) {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        } else {
            var code = status == 401 ? "unauthenticated" : policy == RoutePolicy.VerifiedUser ? "email_unverified" : "forbidden";
            await ProblemTests.AssertProblemAsync(response, status, code);
        }
    }

    [Theory]
    [InlineData("/test/no-policy")]
    [InlineData("/test/no-limit")]
    public async Task An_endpoint_missing_either_declaration_fails_closed(string path) {
        var called = false;
        await using var app = await TestApp.StartAsync(map: app => {
            app.MapGet("/test/no-policy", () => called = true).RequireRateLimit(RateLimit.Unlimited);
            app.MapGet("/test/no-limit", () => called = true).RequireRoutePolicy(RoutePolicy.Public);
        });

        using var response = await app.Client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        await ProblemTests.AssertProblemAsync(response, 403, "forbidden");
        Assert.False(called);
    }

    [Fact]
    public async Task The_caller_is_resolved_once_per_request() {
        var resolver = new CountingResolver();
        await using var app = await TestApp.StartAsync(
            services: services => services.AddSingleton<ICallerResolver>(resolver),
            map: app => app.MapGet("/test/once", async (HttpContext context) => (await context.GetCallerAsync())?.Principal.DisplayName)
                .RequireRoutePolicy(RoutePolicy.Guest)
                .RequireRateLimit(RateLimit.Define("test.once", RateRule.PerIp(100, TimeSpan.FromMinutes(1)), RateRule.PerPrincipal(100, TimeSpan.FromMinutes(1)))));
        using var request = TestApp.Get("/test/once");
        request.Headers.Add(TestApp.CallerHeader, "guest");

        using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("Guest", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, resolver.Calls);
    }

    private sealed class CountingResolver : ICallerResolver {
        private readonly HeaderCallerResolver _inner = new();

        public int Calls { get; private set; }

        public ValueTask<Caller?> ResolveAsync(HttpContext context) {
            Calls++;
            return _inner.ResolveAsync(context);
        }
    }
}
