using System.Net;
using Retroscope.Core.Security;
using Retroscope.Integration.Tests.Auth;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;

namespace Retroscope.Integration.Tests.Email;

// Who a magic link claims an account from (§e takeover rule), and the preview route the magic-link page calls.
[Trait("Category", "Email")]
[Collection(SharedPostgres.Name)]
public sealed class MagicLinkEdgeTests(PostgresFixture postgres) {
    [Fact]
    public async Task The_session_holder_consuming_their_own_link_verifies_without_claiming() {
        await using var host = await EmailHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var phone = await host.CreateSessionAsync(user);
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(user));
        await browser.PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
        var (_, token) = await host.DeliverLinkAsync("magic");

        using var consume = await browser.PostAsync("/api/auth/magic/consume", new { token });

        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);
        Assert.True((await consume.JsonAsync()).GetProperty("user").GetProperty("emailVerified").GetBoolean());
        Assert.Equal(AuthHost.ImportedHash, await host.ScalarAsync<string>("select password_hash from users where id = $1", user.Value));
        Assert.True(await host.ScalarAsync<bool>("select email_verified_at is not null from users where id = $1", user.Value));
        Assert.NotNull(await host.Service<Sessions>().ResolveAsync(phone, EmailHost.Token));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'auth.claim'"));
        Assert.Equal(1L, await host.ScalarAsync<long>(
            "select count(*) from audit_log where action = 'auth.verify' and meta->>'proof' = 'magic_link'"));
        Assert.Equal("magic_link", await host.ScalarAsync<string>(
            "select auth_method from sessions where id_hash = $1", TokenHash.Of(browser.Cookie(Sessions.CookieName)!)));
    }

    // The cached session still says verified, but the row no longer is (changed behind the cache's back).
    [Fact]
    public async Task The_session_holders_link_verifies_by_the_row_not_the_cached_session() {
        await using var host = await EmailHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com", verified: true);
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(user));
        await browser.GetAsync("/api/me");
        await host.ExecuteAsync("update users set email_verified_at = null where id = $1", user.Value);
        await browser.PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
        var (_, token) = await host.DeliverLinkAsync("magic");

        using var consume = await browser.PostAsync("/api/auth/magic/consume", new { token });

        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);
        Assert.True(await host.ScalarAsync<bool>("select email_verified_at is not null from users where id = $1", user.Value));
        Assert.True((await consume.JsonAsync()).GetProperty("user").GetProperty("emailVerified").GetBoolean());
        Assert.Equal(1L, await host.ScalarAsync<long>(
            "select count(*) from audit_log where action = 'auth.verify' and meta->>'proof' = 'magic_link' and target = $1", user.ToString()));
    }

    [Fact]
    public async Task An_account_verified_since_the_session_was_cached_is_not_verified_again() {
        await using var host = await EmailHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(user));
        await browser.GetAsync("/api/me");
        var verifiedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        await host.ExecuteAsync("update users set email_verified_at = $2 where id = $1", user.Value, verifiedAt);
        await browser.PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
        var (_, token) = await host.DeliverLinkAsync("magic");

        using var consume = await browser.PostAsync("/api/auth/magic/consume", new { token });

        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);
        Assert.True(await host.ScalarAsync<bool>("select email_verified_at = $2 from users where id = $1", user.Value, verifiedAt));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'auth.verify'"));
    }

    [Fact]
    public async Task A_browser_signed_in_as_someone_else_still_claims() {
        await using var host = await EmailHost.StartAsync(postgres);
        var squatted = await host.CreateUserAsync("ada@example.com");
        var squatter = await host.CreateSessionAsync(squatted);
        var grace = await host.CreateUserAsync("grace@example.com", verified: true);
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(grace));
        await browser.PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
        var (_, token) = await host.DeliverLinkAsync("magic");

        using var consume = await browser.PostAsync("/api/auth/magic/consume", new { token });

        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);
        Assert.True(await host.ScalarAsync<bool>("select password_hash is null from users where id = $1", squatted.Value));
        Assert.Null(await host.Service<Sessions>().ResolveAsync(squatter, EmailHost.Token));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'auth.claim'"));
    }

    [Fact]
    public async Task A_magic_link_claim_unlinks_the_squatters_identities() {
        await using var host = await EmailHost.StartAsync(postgres);
        var squatted = await host.CreateUserAsync("ada@example.com");
        await host.ExecuteAsync(
            "insert into external_identities (provider, subject, user_id) values ('google', 'g-squatter', $1), ('google', 'g-squatter-2', $1)",
            squatted.Value);
        var owner = host.Browser("198.51.100.7");
        await owner.PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
        var (_, token) = await host.DeliverLinkAsync("magic");

        using var consume = await owner.PostAsync("/api/auth/magic/consume", new { token });

        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from external_identities"));
        Assert.Equal(2L, await host.ScalarAsync<long>(
            "select count(*) from audit_log where action = 'auth.unlink' and meta->>'reason' = 'claim' and target = $1",
            squatted.ToString()));
    }

    [Fact]
    public async Task A_verified_account_keeps_its_identities() {
        await using var host = await EmailHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com", verified: true);
        await host.ExecuteAsync("insert into external_identities (provider, subject, user_id) values ('google', 'g-ada', $1)", user.Value);
        var browser = host.Browser();
        await browser.PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
        var (_, token) = await host.DeliverLinkAsync("magic");

        using var consume = await browser.PostAsync("/api/auth/magic/consume", new { token });

        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from external_identities where user_id = $1", user.Value));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'auth.unlink'"));
    }

    [Fact]
    public async Task Preview_answers_only_the_masked_address() {
        await using var host = await EmailHost.StartAsync(postgres);
        var browser = host.Browser();
        await browser.PostAsync("/api/auth/magic/request", new { email = "new.person@example.com" });
        var (_, token) = await host.DeliverLinkAsync("magic");

        using var preview = await browser.PostAsync("/api/auth/magic/preview", new { token });

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var body = await preview.JsonAsync();
        Assert.Equal("email", Assert.Single(body.EnumerateObject()).Name);
        Assert.Equal("n•••@example.com", body.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Preview_refuses_dead_tokens_and_unknown_members() {
        await using var host = await EmailHost.StartAsync(postgres);
        var browser = host.Browser();
        await browser.PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
        var (_, token) = await host.DeliverLinkAsync("magic");

        using var unknown = await browser.PostAsync("/api/auth/magic/preview", new { token = OpaqueToken.New() });
        using var extra = await browser.PostAsync("/api/auth/magic/preview", new { token, email = "x@example.com" });
        host.Time.Advance(Tokens.Lifetime(TokenPurpose.MagicLink));
        using var expired = await browser.PostAsync("/api/auth/magic/preview", new { token });

        Assert.Equal("invalid_token", await unknown.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal("invalid_token", await expired.ProblemCodeAsync());
    }
}
