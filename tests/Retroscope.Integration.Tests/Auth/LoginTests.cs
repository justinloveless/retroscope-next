using System.Diagnostics;
using System.Net;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;

namespace Retroscope.Integration.Tests.Auth;

[Trait("Category", "AuthCore")]
[Collection(SharedPostgres.Name)]
public sealed class LoginTests(PostgresFixture postgres) {
    [Fact]
    public async Task Imported_gotrue_hash_of_a_six_character_password_logs_in_and_is_never_rehashed() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var browser = host.Browser();

        using var response = await browser.LoginAsync("Ada@Example.com ", AuthHost.ImportedPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.JsonAsync();
        Assert.Equal(user.Value, me.GetProperty("principal").GetProperty("id").GetGuid());
        Assert.Equal("user", me.GetProperty("principal").GetProperty("kind").GetString());
        Assert.Equal("ada@example.com", me.GetProperty("user").GetProperty("email").GetString());
        var cookie = SetCookieHeader.Find(response, Sessions.CookieName);
        Assert.Contains("httponly", cookie.Attributes);
        Assert.Contains("secure", cookie.Attributes);
        Assert.Contains("samesite=lax", cookie.Attributes);
        Assert.Contains("path=/", cookie.Attributes);
        Assert.DoesNotContain(cookie.Attributes, x => x.StartsWith("domain", StringComparison.Ordinal));
        Assert.Equal(AuthHost.ImportedHash, await host.ScalarAsync<string>("select password_hash from users where id = $1", user.Value));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from sessions where user_id = $1 and auth_method = 'password'", user.Value));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'auth.login' and actor_id = $1", user.Value));

        using var meResponse = await browser.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_unknown_email_and_password_less_account_get_the_same_answer() {
        await using var host = await AuthHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com");
        await host.CreateUserAsync("oidc@example.com", passwordHash: null, verified: true);

        var bodies = new List<string>();
        foreach (var (email, peer) in new[] { ("ada@example.com", "198.51.100.1"), ("nobody@example.com", "198.51.100.2"), ("oidc@example.com", "198.51.100.3") }) {
            using var response = await host.Browser(peer).LoginAsync(email, "not-the-password");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            bodies.Add(await response.Content.ReadAsStringAsync(AuthHost.Token));
        }

        Assert.Single(bodies.Distinct());
        Assert.Contains("invalid_credentials", bodies[0], StringComparison.Ordinal);
        Assert.Equal(3L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'auth.login.fail'"));
        var meta = await host.ScalarAsync<string>("select meta::text from audit_log where action = 'auth.login.fail' limit 1");
        Assert.Contains("email_hmac", meta, StringComparison.Ordinal);
        Assert.DoesNotContain("@", meta, StringComparison.Ordinal);
    }

    // Compared within one run, not against a budget: the two kinds of login alternate, and the fastest of each counts,
    // so load slows both alike. Without the dummy verify an unknown email answers in a few milliseconds, against the
    // tens of milliseconds of a bcrypt verify.
    [Fact]
    public async Task Unknown_email_costs_as_much_as_a_wrong_password() {
        await using var host = await AuthHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com");

        var known = new List<double>();
        var unknown = new List<double>();
        for (var i = 0; i < 9; i++) {
            // A network of its own per attempt, so the per-account backoff never answers 429 instead of verifying.
            known.Add(await TimeLoginAsync(host, "ada@example.com", $"198.51.{100 + i}.7"));
            unknown.Add(await TimeLoginAsync(host, $"ghost{i}@example.com", $"203.0.{113 + i}.7"));
        }

        Assert.True(
            unknown.Min() > known.Min() * 0.6,
            $"Unknown email took {unknown.Min():F1} ms at best, wrong password {known.Min():F1} ms: timing reveals the account.");
    }

    [Fact]
    public async Task Disabled_account_cannot_log_in_with_the_right_password() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        await host.ExecuteAsync("update users set disabled_at = now() where id = $1", user.Value);

        using var response = await host.Browser().LoginAsync("ada@example.com", AuthHost.ImportedPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_rotates_a_session_the_browser_already_holds() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var browser = host.Browser();
        using (var first = await browser.LoginAsync("ada@example.com", AuthHost.ImportedPassword)) {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }
        var firstToken = browser.Cookie(Sessions.CookieName);

        using var second = await browser.LoginAsync("ada@example.com", AuthHost.ImportedPassword);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotEqual(firstToken, browser.Cookie(Sessions.CookieName));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from sessions where user_id = $1", user.Value));
        Assert.Single(host.Revoker.AuthSessions);
    }

    private static async Task<double> TimeLoginAsync(AuthHost host, string email, string peer) {
        var stopwatch = Stopwatch.StartNew();
        using var response = await host.Browser(peer).LoginAsync(email, "not-the-password");
        stopwatch.Stop();
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        return stopwatch.Elapsed.TotalMilliseconds;
    }
}
