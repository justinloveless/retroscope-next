using System.Net;
using Retroscope.Core.Security;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;

namespace Retroscope.Integration.Tests.Auth;

[Trait("Category", "AuthCore")]
[Collection(SharedPostgres.Name)]
public sealed class AccountClaimTests(PostgresFixture postgres) {
    [Fact]
    public async Task Claiming_an_unverified_account_verifies_it_clears_the_password_and_revokes_every_session() {
        await using var host = await AuthHost.StartAsync(postgres);
        var squatted = await host.CreateUserAsync("ada@example.com");
        var bystander = await host.CreateUserAsync("bob@example.com");
        var first = await host.CreateSessionAsync(squatted, AuthMethod.Password);
        var second = await host.CreateSessionAsync(squatted, AuthMethod.Password);
        var bobs = await host.CreateSessionAsync(bystander, AuthMethod.Password);
        var sessions = host.Service<Sessions>();
        Assert.NotNull(await sessions.ResolveAsync(first, AuthHost.Token));

        var claimed = await host.Service<AccountClaims>().ClaimAsync(squatted, OwnershipProof.MagicLink, AuthHost.Token);

        Assert.True(claimed);
        Assert.True(await host.ScalarAsync<bool>(
            "select email_verified_at is not null and password_hash is null from users where id = $1", squatted.Value));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from sessions where user_id = $1", squatted.Value));
        Assert.Null(await sessions.ResolveAsync(first, AuthHost.Token));
        Assert.NotNull(await sessions.ResolveAsync(bobs, AuthHost.Token));
        Assert.Equal(
            new[] { first, second }.Select(x => Sessions.PublicId(TokenHash.Of(x))).Order(),
            host.Revoker.AuthSessions.Order());
        Assert.Equal(
            """{"proof": "magic_link"}""",
            await host.ScalarAsync<string>("select meta::text from audit_log where action = 'auth.claim' and actor_id = $1", squatted.Value));
        using var login = await host.Browser().LoginAsync("ada@example.com", AuthHost.ImportedPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task A_verified_account_is_never_claimed() {
        await using var host = await AuthHost.StartAsync(postgres);
        var owner = await host.CreateUserAsync("ada@example.com", verified: true);
        var token = await host.CreateSessionAsync(owner, AuthMethod.Password);

        var claimed = await host.Service<AccountClaims>().ClaimAsync(owner, OwnershipProof.Google, AuthHost.Token);

        Assert.False(claimed);
        Assert.Equal(AuthHost.ImportedHash, await host.ScalarAsync<string>("select password_hash from users where id = $1", owner.Value));
        Assert.NotNull(await host.Service<Sessions>().ResolveAsync(token, AuthHost.Token));
        Assert.Empty(host.Revoker.AuthSessions);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from audit_log"));
    }

    [Fact]
    public async Task A_second_claim_does_nothing() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var claims = host.Service<AccountClaims>();

        Assert.True(await claims.ClaimAsync(user, OwnershipProof.Entra, AuthHost.Token));
        Assert.False(await claims.ClaimAsync(user, OwnershipProof.Entra, AuthHost.Token));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'auth.claim'"));
    }
}

[Trait("Category", "AuthCore")]
[Collection(SharedPostgres.Name)]
public sealed class SignupTests(PostgresFixture postgres) {
    [Fact]
    public async Task Signup_creates_an_unverified_account_that_can_log_in_at_once() {
        await using var host = await AuthHost.StartAsync(postgres);
        var browser = host.Browser();

        using var response = await browser.PostAsync(
            "/api/auth/signup", new { email = " Ada@Example.com", password = "correct horse battery", fullName = "Ada" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var id = await host.ScalarAsync<Guid>("select id from users where email = 'ada@example.com' and email_verified_at is null");
        Assert.Equal([("signed_up", new Core.Ids.PrincipalId(id), "ada@example.com")], host.Mail.Sent);
        using var login = await browser.LoginAsync("ada@example.com", "correct horse battery");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False((await login.JsonAsync()).GetProperty("user").GetProperty("emailVerified").GetBoolean());
    }

    [Fact]
    public async Task Signing_up_with_a_taken_address_answers_the_same_and_tells_the_owner() {
        await using var host = await AuthHost.StartAsync(postgres);
        var owner = await host.CreateUserAsync("ada@example.com");

        using var response = await host.Browser().PostAsync(
            "/api/auth/signup", new { email = "ada@example.com", password = "correct horse battery", fullName = "Mallory" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal([("already_registered", owner, "ada@example.com")], host.Mail.Sent);
        Assert.Equal(AuthHost.ImportedHash, await host.ScalarAsync<string>("select password_hash from users where id = $1", owner.Value));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from users"));
    }

    [Theory]
    [InlineData("not-an-email", "correct horse battery", "Ada", "email", "invalid")]
    [InlineData("ada@example.com", "short", "Ada", "password", "too_short")]
    [InlineData("ada@example.com", "qwertyuiop", "Ada", "password", "too_common")]
    [InlineData("ada@example.com", "correct horse battery", " ", "fullName", "required")]
    public async Task Signup_validates_its_fields(string email, string password, string fullName, string field, string code) {
        await using var host = await AuthHost.StartAsync(postgres);

        using var response = await host.Browser().PostAsync("/api/auth/signup", new { email, password, fullName });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await response.JsonAsync()).GetProperty("fields").GetProperty(field).GetString());
    }

    [Fact]
    public async Task Unknown_members_in_the_body_are_refused() {
        await using var host = await AuthHost.StartAsync(postgres);

        using var response = await host.Browser().PostAsync(
            "/api/auth/signup", new { email = "ada@example.com", password = "correct horse battery", fullName = "Ada", isAdmin = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from users"));
    }
}

[Trait("Category", "AuthCore")]
[Collection(SharedPostgres.Name)]
public sealed class MeTests(PostgresFixture postgres) {
    [Fact]
    public async Task Me_lists_live_teams_and_unread_mentions_on_readable_boards() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var team = Guid.CreateVersion7();
        var gone = Guid.CreateVersion7();
        await host.ExecuteAsync("insert into teams (id, name) values ($1, 'Platform'), ($2, 'Gone')", team, gone);
        await host.ExecuteAsync("update teams set deleted_at = now() where id = $1", gone);
        await host.ExecuteAsync("insert into team_members (team_id, user_id, role) values ($1, $3, 'admin'), ($2, $3, 'owner')", team, gone, user.Value);
        var readable = Guid.CreateVersion7();
        var locked = Guid.CreateVersion7();
        await host.ExecuteAsync("""
            insert into retro_boards (id, code, title, visibility, password_hash, password_set_at)
            values ($1, 'AAAAAAAAAA', 'Open', 'link', null, null), ($2, 'BBBBBBBBBB', 'Locked', 'password', $3, now())
            """, readable, locked, AuthHost.ImportedHash);
        await host.ExecuteAsync("update users set mentions_seen_at = now() - interval '1 day' where id = $1", user.Value);
        await host.ExecuteAsync("""
            insert into mentions (source_kind, source_id, board_id, user_id)
            values ('card', $1, $3, $5), ('card', $2, $4, $5)
            """, Guid.CreateVersion7(), Guid.CreateVersion7(), readable, locked, user.Value);
        var browser = host.Browser();
        (await browser.LoginAsync("ada@example.com", AuthHost.ImportedPassword)).Dispose();

        using var response = await browser.GetAsync("/api/me");

        var me = await response.JsonAsync();
        var teams = me.GetProperty("teams").EnumerateArray().ToList();
        Assert.Single(teams);
        Assert.Equal(team, teams[0].GetProperty("id").GetGuid());
        Assert.Equal("admin", teams[0].GetProperty("role").GetString());
        Assert.Equal(1, me.GetProperty("unreadMentions").GetInt32());
    }

    [Fact]
    public async Task Profile_changes_apply_to_the_next_request_and_never_expose_the_email_as_a_name() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com", fullName: "");
        var browser = host.Browser();
        using var login = await browser.LoginAsync("ada@example.com", AuthHost.ImportedPassword);
        Assert.Equal("Player", (await login.JsonAsync()).GetProperty("principal").GetProperty("name").GetString());

        using var patched = await browser.PatchAsync("/api/me", new { fullName = "Ada Lovelace", nickname = "Countess", theme = "dark" });
        var caller = await host.Service<Sessions>().ResolveAsync(browser.Cookie(Sessions.CookieName), AuthHost.Token);
        using var cleared = await browser.PatchAsync("/api/me", new { nickname = "" });

        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var body = await patched.JsonAsync();
        Assert.Equal("Countess", body.GetProperty("principal").GetProperty("name").GetString());
        Assert.Equal("dark", body.GetProperty("user").GetProperty("theme").GetString());
        Assert.Equal("Countess", caller!.User.DisplayName);
        Assert.Equal("Ada Lovelace", (await cleared.JsonAsync()).GetProperty("principal").GetProperty("name").GetString());
        Assert.Equal(user.Value, body.GetProperty("principal").GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("""{"theme":"neon"}""", "theme")]
    [InlineData("""{"nickname":"12345678901234567890123456789012345678901"}""", "nickname")]
    [InlineData("""{"fullName":"  "}""", "fullName")]
    public async Task Invalid_profile_fields_are_refused(string json, string field) {
        await using var host = await AuthHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com");
        var browser = host.Browser();
        (await browser.LoginAsync("ada@example.com", AuthHost.ImportedPassword)).Dispose();

        using var response = await browser.PatchAsync("/api/me", System.Text.Json.JsonDocument.Parse(json).RootElement);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.JsonAsync()).GetProperty("fields").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Anonymous_callers_get_401_from_me() {
        await using var host = await AuthHost.StartAsync(postgres);

        using var response = await host.Browser().GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
