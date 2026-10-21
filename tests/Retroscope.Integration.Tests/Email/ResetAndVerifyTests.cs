using System.Net;
using Retroscope.Core.Security;
using Retroscope.Integration.Tests.Auth;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;

namespace Retroscope.Integration.Tests.Email;

[Trait("Category", "Email")]
[Collection(SharedPostgres.Name)]
public sealed class PasswordResetTests(PostgresFixture postgres) {
    [Fact]
    public async Task Reset_sets_the_password_verifies_the_email_and_revokes_every_session() {
        await using var host = await EmailHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var other = await host.CreateSessionAsync(user);
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(user));
        await browser.PostAsync("/api/auth/reset/request", new { email = "ada@example.com" });
        var (mail, token) = await host.DeliverLinkAsync("reset");

        using var consume = await browser.PostAsync("/api/auth/reset/consume", new { token, password = "a much better passphrase" });

        Assert.Equal("Reset your Retroscope password", mail.Header("Subject"));
        Assert.Equal(HttpStatusCode.NoContent, consume.StatusCode);
        Assert.Null(browser.Cookie(Sessions.CookieName));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from sessions"));
        Assert.Null(await host.Service<Sessions>().ResolveAsync(other, EmailHost.Token));
        Assert.True(await host.ScalarAsync<bool>("select email_verified_at is not null from users where id = $1", user.Value));
        Assert.True(Bcrypt.Verify("a much better passphrase", await host.ScalarAsync<string>("select password_hash from users where id = $1", user.Value)));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'auth.reset'"));
        using var reuse = await browser.PostAsync("/api/auth/reset/consume", new { token, password = "another fine passphrase" });
        Assert.Equal("invalid_token", await reuse.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_weak_password_is_refused_without_consuming_the_token() {
        await using var host = await EmailHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com", verified: true);
        await host.Browser().PostAsync("/api/auth/reset/request", new { email = "ada@example.com" });
        var (_, token) = await host.DeliverLinkAsync("reset");

        using var weak = await host.Browser().PostAsync("/api/auth/reset/consume", new { token, password = "short" });
        using var good = await host.Browser().PostAsync("/api/auth/reset/consume", new { token, password = "long enough passphrase" });

        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal("too_short", (await weak.JsonAsync()).GetProperty("fields").GetProperty("password").GetString());
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
    }

    [Fact]
    public async Task A_reset_link_expires_after_30_minutes() {
        await using var host = await EmailHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com", verified: true);
        await host.Browser().PostAsync("/api/auth/reset/request", new { email = "ada@example.com" });
        var (_, token) = await host.DeliverLinkAsync("reset");

        host.Time.Advance(TimeSpan.FromMinutes(30));
        using var consume = await host.Browser().PostAsync("/api/auth/reset/consume", new { token, password = "long enough passphrase" });

        Assert.Equal("invalid_token", await consume.ProblemCodeAsync());
        Assert.Equal(AuthHost.ImportedHash, await host.ScalarAsync<string>("select password_hash from users"));
    }
}

[Trait("Category", "Email")]
[Collection(SharedPostgres.Name)]
public sealed class VerifyEmailTests(PostgresFixture postgres) {
    [Fact]
    public async Task Signup_mails_a_verify_link_that_verifies_only_for_a_session_of_the_same_user() {
        await using var host = await EmailHost.StartAsync(postgres);
        var owner = host.Browser();
        using var signup = await owner.PostAsync(
            "/api/auth/signup", new { email = "ada@example.com", password = "correct horse battery", fullName = "Ada" });
        var (mail, token) = await host.DeliverLinkAsync("verify");
        var user = await host.ScalarAsync<Guid>("select id from users where email = 'ada@example.com'");
        var stranger = host.Browser();
        stranger.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(await host.CreateUserAsync("bob@example.com", verified: true)));

        using var anonymous = await host.Browser().PostAsync("/api/auth/verify/consume", new { token });
        using var wrongUser = await stranger.PostAsync("/api/auth/verify/consume", new { token });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongUser.StatusCode);
        Assert.Equal("other_account", await wrongUser.ProblemCodeAsync());
        Assert.True(await host.ScalarAsync<bool>("select email_verified_at is null from users where id = $1", user));
        Assert.True(await host.ScalarAsync<bool>("select consumed_at is null from auth_tokens where token_hash = $1", TokenHash.Of(token)));

        using var login = await owner.PostAsync("/api/auth/login", new { email = "ada@example.com", password = "correct horse battery" });
        Assert.False((await login.JsonAsync()).GetProperty("user").GetProperty("emailVerified").GetBoolean());
        using var verify = await owner.PostAsync("/api/auth/verify/consume", new { token });
        using var me = await owner.GetAsync("/api/me");
        using var again = await owner.PostAsync("/api/auth/verify/consume", new { token });

        Assert.Equal(HttpStatusCode.Accepted, signup.StatusCode);
        Assert.Contains("If you didn't create this account, ignore this email.", mail.Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, verify.StatusCode);
        Assert.True((await me.JsonAsync()).GetProperty("user").GetProperty("emailVerified").GetBoolean());
        Assert.Equal("invalid_token", await again.ProblemCodeAsync());
        Assert.Equal(
            1L,
            await host.ScalarAsync<long>(
                "select count(*) from auth_tokens where purpose = 'verify_email' and expires_at - created_at = interval '24 hours'"));
    }

    [Fact]
    public async Task Signing_up_with_a_taken_address_mails_its_owner_without_a_token() {
        await using var host = await EmailHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com", verified: true);

        using var signup = await host.Browser().PostAsync(
            "/api/auth/signup", new { email = "ada@example.com", password = "correct horse battery", fullName = "Mallory" });
        await host.DeliverAsync();

        Assert.Equal(HttpStatusCode.Accepted, signup.StatusCode);
        var mail = Assert.Single(host.Smtp.Received);
        Assert.Equal("You already have a Retroscope account", mail.Header("Subject"));
        Assert.Contains($"{EmailHost.PublicUrl}/login", mail.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Mallory", mail.Body, StringComparison.Ordinal);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from auth_tokens"));
    }

    [Fact]
    public async Task Hitting_the_hourly_login_cap_mails_a_sign_in_link() {
        await using var host = await EmailHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com", verified: true);
        var throttle = host.Service<LoginThrottle>();
        for (var i = 0; i < LoginThrottle.AccountCap - 1; i++) {
            throttle.RecordFailure("ada@example.com", $"10.0.{i}.0/24");
        }

        using var login = await host.Browser("192.0.2.1").PostAsync("/api/auth/login", new { email = "ada@example.com", password = "wrong" });
        var (mail, token) = await host.DeliverLinkAsync("magic");
        using var consume = await host.Browser().PostAsync("/api/auth/magic/consume", new { token });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal("Too many failed sign-ins on your Retroscope account", mail.Header("Subject"));
        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);
    }
}
