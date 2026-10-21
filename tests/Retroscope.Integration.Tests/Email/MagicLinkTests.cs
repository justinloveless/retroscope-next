using System.Net;
using Retroscope.Core.Security;
using Retroscope.Integration.Tests.Auth;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;

namespace Retroscope.Integration.Tests.Email;

[Trait("Category", "Email")]
[Collection(SharedPostgres.Name)]
public sealed class MagicLinkTests(PostgresFixture postgres) {
    [Fact]
    public async Task A_magic_link_arrives_as_plain_text_with_the_token_in_the_fragment() {
        await using var host = await EmailHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com", verified: true);

        using var response = await host.Browser().PostAsync("/api/auth/magic/request", new { email = " Ada@Example.com " });
        var (mail, token) = await host.DeliverLinkAsync("magic");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(["<ada@example.com>"], mail.Recipients);
        Assert.Equal("<no-reply@retro.test>", mail.MailFrom);
        Assert.Equal("text/plain; charset=utf-8", mail.Header("Content-Type"));
        Assert.Equal("Your Retroscope sign-in link", mail.Header("Subject"));
        Assert.Equal("Retroscope <no-reply@retro.test>", mail.Header("From"));
        Assert.DoesNotContain("<html", mail.Data, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"{EmailHost.PublicUrl}/auth/magic#t={token}", mail.Body, StringComparison.Ordinal);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from email_outbox"));
        Assert.Equal(
            1L,
            await host.ScalarAsync<long>(
                "select count(*) from auth_tokens where token_hash = $1 and purpose = 'magic_link' and expires_at - created_at = interval '15 minutes'",
                TokenHash.Of(token)));
    }

    [Fact]
    public async Task Opening_the_link_with_GET_or_previewing_it_consumes_nothing() {
        await using var host = await EmailHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com", verified: true);
        var browser = host.Browser();
        await browser.PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
        var (_, token) = await host.DeliverLinkAsync("magic");

        using var page = await browser.GetAsync($"/auth/magic?t={token}");
        using var getConsume = await browser.GetAsync($"/api/auth/magic/consume?token={token}");
        using var preview = await browser.PostAsync("/api/auth/magic/preview", new { token });
        using var again = await browser.PostAsync("/api/auth/magic/preview", new { token });

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getConsume.StatusCode);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("a•••@example.com", (await preview.JsonAsync()).GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Null(browser.Cookie(Sessions.CookieName));
        Assert.True(await host.ScalarAsync<bool>("select consumed_at is null from auth_tokens where token_hash = $1", TokenHash.Of(token)));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from sessions"));
    }

    [Fact]
    public async Task Clicking_sign_in_consumes_the_link_once_and_starts_a_magic_link_session() {
        await using var host = await EmailHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com", verified: true);
        var browser = host.Browser();
        await browser.PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
        var (_, token) = await host.DeliverLinkAsync("magic");

        using var consume = await browser.PostAsync("/api/auth/magic/consume", new { token });
        using var reuse = await host.Browser().PostAsync("/api/auth/magic/consume", new { token });
        using var preview = await host.Browser().PostAsync("/api/auth/magic/preview", new { token });

        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);
        Assert.Equal(user.Value, (await consume.JsonAsync()).GetProperty("principal").GetProperty("id").GetGuid());
        Assert.NotNull(browser.Cookie(Sessions.CookieName));
        Assert.Equal("magic_link", await host.ScalarAsync<string>("select auth_method from sessions where user_id = $1", user.Value));
        Assert.Equal(AuthHost.ImportedHash, await host.ScalarAsync<string>("select password_hash from users where id = $1", user.Value));
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
        Assert.Equal("invalid_token", await reuse.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from sessions"));
        Assert.Equal(
            """{"ip": "203.0.113.0/24", "method": "magic_link"}""",
            await host.ScalarAsync<string>("select meta::text from audit_log where action = 'auth.login'"));
    }

    [Fact]
    public async Task Consuming_one_link_consumes_every_other_open_magic_link_of_the_account() {
        await using var host = await EmailHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com", verified: true);
        var browser = host.Browser();
        await browser.PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
        var (_, first) = await host.DeliverLinkAsync("magic");
        await browser.PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
        var (_, second) = await host.DeliverLinkAsync("magic");

        using var consume = await browser.PostAsync("/api/auth/magic/consume", new { token = second });
        using var older = await host.Browser().PostAsync("/api/auth/magic/consume", new { token = first });

        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, older.StatusCode);
    }

    [Fact]
    public async Task A_link_expires_after_15_minutes() {
        await using var host = await EmailHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com", verified: true);
        await host.Browser().PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
        var (_, token) = await host.DeliverLinkAsync("magic");

        host.Time.Advance(TimeSpan.FromMinutes(15));
        using var consume = await host.Browser().PostAsync("/api/auth/magic/consume", new { token });

        Assert.Equal(HttpStatusCode.BadRequest, consume.StatusCode);
        Assert.Equal("invalid_token", await consume.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_link_for_an_unknown_email_creates_a_verified_account_on_consume() {
        await using var host = await EmailHost.StartAsync(postgres);
        var browser = host.Browser();
        await browser.PostAsync("/api/auth/magic/request", new { email = "new@example.com" });
        var (_, token) = await host.DeliverLinkAsync("magic");
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from users"));

        using var consume = await browser.PostAsync("/api/auth/magic/consume", new { token });

        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);
        var me = await consume.JsonAsync();
        Assert.True(me.GetProperty("user").GetProperty("emailVerified").GetBoolean());
        Assert.False(me.GetProperty("user").GetProperty("hasPassword").GetBoolean());
        Assert.True(await host.ScalarAsync<bool>(
            "select email_verified_at is not null and password_hash is null from users where email = 'new@example.com'"));
    }

    [Fact]
    public async Task Consuming_a_link_to_an_unverified_account_claims_it_from_whoever_registered_the_address() {
        await using var host = await EmailHost.StartAsync(postgres);
        var squatted = await host.CreateUserAsync("ada@example.com");
        var squatter = await host.CreateSessionAsync(squatted);
        var owner = host.Browser("198.51.100.7");
        await owner.PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
        var (_, token) = await host.DeliverLinkAsync("magic");

        using var consume = await owner.PostAsync("/api/auth/magic/consume", new { token });

        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);
        Assert.True(await host.ScalarAsync<bool>(
            "select email_verified_at is not null and password_hash is null from users where id = $1", squatted.Value));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from sessions where id_hash = $1", TokenHash.Of(squatter)));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from sessions where user_id = $1", squatted.Value));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'auth.claim'"));
        var squatterBrowser = host.Browser();
        squatterBrowser.SetCookie(Sessions.CookieName, squatter);
        using var me = await squatterBrowser.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task A_disabled_account_gets_no_link_and_cannot_use_an_old_one() {
        await using var host = await EmailHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com", verified: true);
        await host.Browser().PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
        var (_, token) = await host.DeliverLinkAsync("magic");
        await host.ExecuteAsync("update users set disabled_at = now() where id = $1", user.Value);

        using var request = await host.Browser().PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
        using var consume = await host.Browser().PostAsync("/api/auth/magic/consume", new { token });

        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from email_outbox"));
        Assert.Equal(HttpStatusCode.BadRequest, consume.StatusCode);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from sessions"));
    }

    [Theory]
    [InlineData("""{"token":"abc"}""")]
    [InlineData("""{"token":null}""")]
    [InlineData("""{}""")]
    public async Task Malformed_tokens_are_invalid(string json) {
        await using var host = await EmailHost.StartAsync(postgres);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/magic/consume") {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-RS", "1");
        request.Headers.Add("Origin", EmailHost.PublicUrl);

        using var response = await host.Client.SendAsync(request, EmailHost.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_token", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Unknown_request_members_are_refused() {
        await using var host = await EmailHost.StartAsync(postgres);

        using var response = await host.Browser().PostAsync("/api/auth/magic/request", new { email = "ada@example.com", userId = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from email_outbox"));
    }
}
