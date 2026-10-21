using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

[Trait("Category", "Hosting")]
public sealed class RateLimitTests {
    private static readonly RateLimit TwoPerMinute = RateLimit.Define("test.ip", RateRule.PerIp(2, TimeSpan.FromMinutes(1)));

    private static readonly RateLimit GuestRoute = RateLimit.Define(
        "test.guest",
        RateRule.PerIp(5, TimeSpan.FromMinutes(1)),
        RateRule.PerPrincipal(2, TimeSpan.FromMinutes(1)));

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-20T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task Over_the_limit_is_a_429_problem_with_retry_after() {
        await using var app = await StartAsync();

        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(app, "/test/ip", "198.51.100.1"));
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(app, "/test/ip", "198.51.100.1"));
        using var request = TestApp.Get("/test/ip", "198.51.100.1");
        using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        await ProblemTests.AssertProblemAsync(response, 429, "rate_limited");
        Assert.Equal("30", SecurityHeaderTests.Header(response, "Retry-After"));
    }

    [Fact]
    public async Task Buckets_are_per_ip_and_an_ipv6_64_is_one_ip() {
        await using var app = await StartAsync();

        await StatusAsync(app, "/test/ip", "2001:db8:1:2::1");
        await StatusAsync(app, "/test/ip", "2001:db8:1:2::2");

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(app, "/test/ip", "2001:db8:1:2:ffff::3"));
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(app, "/test/ip", "2001:db8:1:3::1"));
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(app, "/test/ip", "198.51.100.2"));
    }

    [Fact]
    public async Task Tokens_refill_over_the_window() {
        await using var app = await StartAsync();
        await StatusAsync(app, "/test/ip", "198.51.100.1");
        await StatusAsync(app, "/test/ip", "198.51.100.1");

        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(app, "/test/ip", "198.51.100.1"));
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(app, "/test/ip", "198.51.100.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(app, "/test/ip", "198.51.100.1"));
    }

    [Fact]
    public async Task Guest_reachable_routes_limit_per_principal_across_ips() {
        await using var app = await StartAsync();
        var caller = $"guest:{Guid.CreateVersion7()}";

        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(app, "/test/guest", "198.51.100.1", caller));
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(app, "/test/guest", "198.51.100.2", caller));

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(app, "/test/guest", "198.51.100.3", caller));
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(app, "/test/guest", "198.51.100.3", $"guest:{Guid.CreateVersion7()}"));
    }

    [Fact]
    public void A_refused_request_is_not_charged_to_the_other_rules() {
        var limiter = new RateLimiter(_time);
        var keys = new RateKeys("198.51.100.1", "p1");
        Assert.True(limiter.TryAcquire(GuestRoute, keys, out _));
        Assert.True(limiter.TryAcquire(GuestRoute, keys, out _));

        for (var i = 0; i < 10; i++) {
            Assert.False(limiter.TryAcquire(GuestRoute, keys, out _));
        }

        Assert.True(limiter.TryAcquire(GuestRoute, new RateKeys("198.51.100.1", "p2"), out _));
        Assert.True(limiter.TryAcquire(GuestRoute, new RateKeys("198.51.100.1", "p3"), out _));
        Assert.True(limiter.TryAcquire(GuestRoute, new RateKeys("198.51.100.1", "p4"), out _));
        Assert.False(limiter.TryAcquire(GuestRoute, new RateKeys("198.51.100.1", "p5"), out _));
    }

    [Fact]
    public void The_limiter_lru_is_bounded_and_evicts_the_least_recently_used_key() {
        var limiter = new RateLimiter(_time, capacity: 3);
        var once = RateLimit.Define("test.once", RateRule.PerIp(1, TimeSpan.FromDays(1)));
        foreach (var ip in new[] { "a", "b", "c" }) {
            Assert.True(limiter.TryAcquire(once, new RateKeys(ip, null), out _));
        }
        Assert.False(limiter.TryAcquire(once, new RateKeys("a", null), out _));

        Assert.True(limiter.TryAcquire(once, new RateKeys("d", null), out _));

        Assert.Equal(3, limiter.Count);
        Assert.True(limiter.TryAcquire(once, new RateKeys("b", null), out _));
        Assert.Equal(3, limiter.Count);
        Assert.False(limiter.TryAcquire(once, new RateKeys("a", null), out _));
        Assert.False(limiter.TryAcquire(once, new RateKeys("d", null), out _));
    }

    [Fact]
    public void The_production_limiter_holds_at_most_100k_keys() {
        var limiter = new RateLimiter(_time);
        var limit = RateLimit.Define("test.flood", RateRule.PerIp(1, TimeSpan.FromHours(1)));

        for (var i = 0; i < 150_000; i++) {
            limiter.TryAcquire(limit, new RateKeys(i.ToString(System.Globalization.CultureInfo.InvariantCulture), null), out _);
        }

        Assert.Equal(RateLimiter.DefaultCapacity, limiter.Count);
    }

    [Fact]
    public void Unlimited_never_refuses_and_stores_nothing() {
        var limiter = new RateLimiter(_time);

        for (var i = 0; i < 1000; i++) {
            Assert.True(limiter.TryAcquire(RateLimit.Unlimited, new RateKeys("a", "b"), out _));
        }
        Assert.Equal(0, limiter.Count);
    }

    [Fact]
    public void A_limit_must_have_rules_or_be_unlimited() {
        Assert.Throws<ArgumentException>(() => RateLimit.Define("test.empty"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RateLimit.Define("test.zero", RateRule.PerIp(0, TimeSpan.FromMinutes(1))));
    }

    private async Task<TestApp> StartAsync() => await TestApp.StartAsync(
        services: services => services.AddSingleton<TimeProvider>(_time),
        map: app => {
            app.MapGet("/test/ip", () => Results.NoContent())
                .RequireRoutePolicy(RoutePolicy.Public)
                .RequireRateLimit(TwoPerMinute);
            app.MapGet("/test/guest", () => Results.NoContent())
                .RequireRoutePolicy(RoutePolicy.Guest)
                .RequireRateLimit(GuestRoute);
        });

    private static async Task<HttpStatusCode> StatusAsync(TestApp app, string path, string peer, string? caller = null) {
        using var request = TestApp.Get(path, peer);
        if (caller is not null) {
            request.Headers.Add(TestApp.CallerHeader, caller);
        }
        using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }
}
