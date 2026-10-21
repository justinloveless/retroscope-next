using System.Net;
using Microsoft.Extensions.Time.Testing;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;

namespace Retroscope.Integration.Tests.Auth;

[Trait("Category", "AuthCore")]
public sealed class LoginThrottleTests {
    private const string Ada = "ada@example.com";
    private const string HomeIp = "203.0.113.0/24";

    [Fact]
    public void Five_failures_from_one_address_start_a_backoff_that_doubles_up_to_fifteen_minutes() {
        var time = new FakeTimeProvider();
        var throttle = new LoginThrottle(time);

        for (var i = 0; i < 4; i++) {
            throttle.RecordFailure(Ada, HomeIp);
            Assert.Null(throttle.RetryAfter(Ada, HomeIp));
        }
        var waits = new List<TimeSpan>();
        for (var i = 0; i < 6; i++) {
            throttle.RecordFailure(Ada, HomeIp);
            var wait = throttle.RetryAfter(Ada, HomeIp)!.Value;
            waits.Add(wait);
            time.Advance(wait);
            Assert.Null(throttle.RetryAfter(Ada, HomeIp));
        }

        Assert.Equal([1, 2, 4, 8, 15, 15], waits.Select(x => x.TotalMinutes));
    }

    [Fact]
    public void Backoff_is_per_account_and_address_and_a_success_clears_it() {
        var time = new FakeTimeProvider();
        var throttle = new LoginThrottle(time);
        for (var i = 0; i < 5; i++) {
            throttle.RecordFailure(Ada, HomeIp);
        }

        Assert.NotNull(throttle.RetryAfter(Ada, HomeIp));
        Assert.Null(throttle.RetryAfter(Ada, "198.51.100.0/24"));
        Assert.Null(throttle.RetryAfter("bob@example.com", HomeIp));

        time.Advance(LoginThrottle.FirstBackoff);
        throttle.RecordSuccess(Ada, HomeIp);
        throttle.RecordFailure(Ada, HomeIp);
        Assert.Null(throttle.RetryAfter(Ada, HomeIp));
    }

    [Fact]
    public void Fifty_failures_an_hour_from_any_addresses_cap_the_account_once_and_it_recovers() {
        var time = new FakeTimeProvider();
        var throttle = new LoginThrottle(time);

        var capped = Enumerable.Range(0, LoginThrottle.AccountCap)
            .Select(i => throttle.RecordFailure(Ada, $"10.{i}.0.0/24"))
            .ToList();

        Assert.Equal(LoginThrottle.AccountCap - 1, capped.IndexOf(true));
        Assert.Single(capped, x => x);
        Assert.NotNull(throttle.RetryAfter(Ada, "198.51.100.0/24"));
        Assert.Null(throttle.RetryAfter("bob@example.com", "198.51.100.0/24"));
        Assert.False(throttle.RecordFailure(Ada, "10.99.0.0/24"));

        time.Advance(LoginThrottle.AccountWindow / 10);
        Assert.Null(throttle.RetryAfter(Ada, "198.51.100.0/24"));
    }
}

[Trait("Category", "AuthCore")]
[Collection(SharedPostgres.Name)]
public sealed class BackoffEndpointTests(PostgresFixture postgres) {
    [Fact]
    public async Task Backoff_is_keyed_on_account_and_ip_prefix() {
        await using var host = await AuthHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com");
        await host.CreateUserAsync("bob@example.com");
        var attacker = host.Browser("203.0.113.9");
        for (var i = 0; i < LoginThrottle.FreeFailures; i++) {
            using var failed = await attacker.LoginAsync("ada@example.com", "guess-" + i);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        using var blocked = await attacker.LoginAsync("ada@example.com", AuthHost.ImportedPassword);
        using var sameNetwork = await host.Browser("203.0.113.77").LoginAsync("ada@example.com", AuthHost.ImportedPassword);
        using var elsewhere = await host.Browser("198.51.100.7").LoginAsync("ada@example.com", AuthHost.ImportedPassword);
        using var otherAccount = await attacker.LoginAsync("bob@example.com", AuthHost.ImportedPassword);

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("60", blocked.Headers.GetValues("Retry-After").Single());
        Assert.Equal(HttpStatusCode.TooManyRequests, sameNetwork.StatusCode);
        Assert.Equal(HttpStatusCode.OK, elsewhere.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherAccount.StatusCode);

        host.Time.Advance(LoginThrottle.FirstBackoff);
        using var later = await attacker.LoginAsync("ada@example.com", AuthHost.ImportedPassword);
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }

    [Fact]
    public async Task The_account_cap_mails_an_unlock_link_once_instead_of_locking_the_owner_out() {
        await using var host = await AuthHost.StartAsync(postgres);
        var ada = await host.CreateUserAsync("ada@example.com");
        var attempts = 0;
        for (var network = 0; attempts < LoginThrottle.AccountCap; network++) {
            var browser = host.Browser($"10.{network}.0.1");
            for (var i = 0; i < LoginThrottle.FreeFailures - 1 && attempts < LoginThrottle.AccountCap; i++, attempts++) {
                using var failed = await browser.LoginAsync("ada@example.com", "guess-" + attempts);
                Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
            }
        }

        using var owner = await host.Browser("198.51.100.7").LoginAsync("ada@example.com", AuthHost.ImportedPassword);

        Assert.Equal(HttpStatusCode.TooManyRequests, owner.StatusCode);
        Assert.Equal([("login_capped", ada, "ada@example.com")], host.Mail.Sent);
        host.Time.Advance(LoginThrottle.AccountWindow / 10);
        using var recovered = await host.Browser("198.51.100.7").LoginAsync("ada@example.com", AuthHost.ImportedPassword);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task An_unknown_email_reaches_the_cap_too_but_nothing_is_mailed() {
        await using var host = await AuthHost.StartAsync(postgres);
        var throttle = host.Service<LoginThrottle>();
        for (var i = 0; i < LoginThrottle.AccountCap - 1; i++) {
            throttle.RecordFailure("ghost@example.com", $"10.{i}.0.0/24");
        }

        using var last = await host.Browser("198.51.100.7").LoginAsync("ghost@example.com", "guess");
        using var after = await host.Browser("198.51.100.8").LoginAsync("ghost@example.com", "guess");

        Assert.Equal(HttpStatusCode.Unauthorized, last.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, after.StatusCode);
        Assert.Empty(host.Mail.Sent);
    }
}
