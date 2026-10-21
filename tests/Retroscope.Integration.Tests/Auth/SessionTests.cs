using System.Net;
using System.Text.Json;
using Retroscope.Core.Security;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;

namespace Retroscope.Integration.Tests.Auth;

[Trait("Category", "AuthCore")]
[Collection(SharedPostgres.Name)]
public sealed class SessionTests(PostgresFixture postgres) {
    [Fact]
    public async Task A_cached_session_resolves_with_zero_queries_until_the_ttl_passes() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var token = await host.CreateSessionAsync(user, AuthMethod.Password);
        var sessions = host.Service<Sessions>();

        var first = await host.CountQueriesAsync(async () => Assert.NotNull(await sessions.ResolveAsync(token, AuthHost.Token)));
        var cached = await host.CountQueriesAsync(async () => Assert.NotNull(await sessions.ResolveAsync(token, AuthHost.Token)));
        host.Time.Advance(Sessions.CacheTtl);
        var expired = await host.CountQueriesAsync(async () => Assert.NotNull(await sessions.ResolveAsync(token, AuthHost.Token)));

        Assert.True(first >= 1, "The listener saw no query at all, so it cannot prove a cache hit.");
        Assert.Equal(0, cached);
        Assert.True(expired >= 1);
    }

    [Fact]
    public async Task Last_seen_is_written_at_most_every_five_minutes_and_slides_the_idle_expiry() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var token = await host.CreateSessionAsync(user, AuthMethod.Password);
        var sessions = host.Service<Sessions>();
        var created = host.Time.GetUtcNow();
        var hash = TokenHash.Of(token);

        host.Time.Advance(TimeSpan.FromMinutes(4));
        await sessions.ResolveAsync(token, AuthHost.Token);
        var afterFour = await host.ScalarAsync<DateTime>("select last_seen_at from sessions where id_hash = $1", hash);
        host.Time.Advance(TimeSpan.FromMinutes(2));
        await sessions.ResolveAsync(token, AuthHost.Token);
        var afterSix = await host.ScalarAsync<DateTime>("select last_seen_at from sessions where id_hash = $1", hash);
        var idle = await host.ScalarAsync<DateTime>("select idle_expires_at from sessions where id_hash = $1", hash);

        AssertClose(created, afterFour);
        AssertClose(created.AddMinutes(6), afterSix);
        AssertClose(created.AddMinutes(6) + Sessions.IdleLifetime, idle);
    }

    [Fact]
    public async Task Sessions_expire_after_thirty_idle_days_and_ninety_days_in_all() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var idleToken = await host.CreateSessionAsync(user, AuthMethod.Password);
        var activeToken = await host.CreateSessionAsync(user, AuthMethod.Password);
        var sessions = host.Service<Sessions>();

        for (var i = 0; i < 4; i++) {
            host.Time.Advance(TimeSpan.FromDays(20));
            Assert.NotNull(await sessions.ResolveAsync(activeToken, AuthHost.Token));
        }
        Assert.Null(await sessions.ResolveAsync(idleToken, AuthHost.Token));
        host.Time.Advance(TimeSpan.FromDays(11));

        Assert.Null(await sessions.ResolveAsync(activeToken, AuthHost.Token));
    }

    [Fact]
    public async Task Logout_deletes_the_session_clears_the_cookie_and_closes_its_sockets() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var browser = host.Browser();
        (await browser.LoginAsync("ada@example.com", AuthHost.ImportedPassword)).Dispose();
        var token = browser.Cookie(Sessions.CookieName)!;
        var id = Sessions.PublicId(TokenHash.Of(token));

        using var response = await browser.PostAsync("/api/auth/logout");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(TimeSpan.Zero, SetCookieHeader.Find(response, Sessions.CookieName).MaxAge);
        Assert.Equal([id], host.Revoker.AuthSessions);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from sessions where user_id = $1", user.Value));
        var replay = host.Browser();
        replay.SetCookie(Sessions.CookieName, token);
        using var me = await replay.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Logout_all_revokes_every_session_of_the_user() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var other = await host.CreateUserAsync("bob@example.com");
        var phone = await host.CreateSessionAsync(user, AuthMethod.Google);
        var bobs = await host.CreateSessionAsync(other, AuthMethod.Password);
        var laptop = host.Browser();
        (await laptop.LoginAsync("ada@example.com", AuthHost.ImportedPassword)).Dispose();
        Assert.NotNull(await host.Service<Sessions>().ResolveAsync(phone, AuthHost.Token));

        using var response = await laptop.PostAsync("/api/auth/logout-all");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(2, host.Revoker.AuthSessions.Count);
        Assert.Contains(Sessions.PublicId(TokenHash.Of(phone)), host.Revoker.AuthSessions);
        Assert.Null(await host.Service<Sessions>().ResolveAsync(phone, AuthHost.Token));
        Assert.NotNull(await host.Service<Sessions>().ResolveAsync(bobs, AuthHost.Token));
    }

    [Fact]
    public async Task The_account_page_lists_sessions_and_revokes_one_of_them() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var phone = await host.CreateSessionAsync(user, AuthMethod.MagicLink);
        var laptop = host.Browser();
        (await laptop.LoginAsync("ada@example.com", AuthHost.ImportedPassword)).Dispose();
        var phoneId = Sessions.PublicId(TokenHash.Of(phone));

        using var list = await laptop.GetAsync("/api/me/sessions");
        var sessions = (await list.JsonAsync()).EnumerateArray().ToList();
        using var delete = await laptop.DeleteAsync($"/api/me/sessions/{phoneId}");
        using var again = await laptop.DeleteAsync($"/api/me/sessions/{phoneId}");

        Assert.Equal(2, sessions.Count);
        Assert.Single(sessions, x => x.GetProperty("current").GetBoolean() && x.GetProperty("method").GetString() == "password");
        Assert.Single(sessions, x => x.GetProperty("id").GetGuid() == phoneId && x.GetProperty("method").GetString() == "magic_link");
        Assert.DoesNotContain(sessions, x => x.ToString().Contains(phone, StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal([phoneId], host.Revoker.AuthSessions);
        Assert.Null(await host.Service<Sessions>().ResolveAsync(phone, AuthHost.Token));
    }

    [Fact]
    public async Task Another_users_session_id_cannot_be_revoked() {
        await using var host = await AuthHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com");
        var bob = await host.CreateUserAsync("bob@example.com");
        var bobs = await host.CreateSessionAsync(bob, AuthMethod.Password);
        var ada = host.Browser();
        (await ada.LoginAsync("ada@example.com", AuthHost.ImportedPassword)).Dispose();

        using var response = await ada.DeleteAsync($"/api/me/sessions/{Sessions.PublicId(TokenHash.Of(bobs))}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(await host.Service<Sessions>().ResolveAsync(bobs, AuthHost.Token));
    }

    [Fact]
    public async Task Boot_json_carries_me_and_the_users_theme() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        await host.ExecuteAsync("update users set theme = 'dark' where id = $1", user.Value);
        var browser = host.Browser();

        using var anonymous = await browser.GetAsync("/api/boot?path=/");
        (await browser.LoginAsync("ada@example.com", AuthHost.ImportedPassword)).Dispose();
        using var signedIn = await browser.GetAsync("/api/boot?path=/");

        var before = await anonymous.JsonAsync();
        Assert.Equal(JsonValueKind.Null, before.GetProperty("me").ValueKind);
        Assert.Equal("system", before.GetProperty("theme").GetString());
        var after = await signedIn.JsonAsync();
        Assert.Equal(user.Value, after.GetProperty("me").GetProperty("principal").GetProperty("id").GetGuid());
        Assert.Equal("dark", after.GetProperty("theme").GetString());
    }

    private static void AssertClose(DateTimeOffset expected, DateTime actual) =>
        Assert.InRange((new DateTimeOffset(DateTime.SpecifyKind(actual, DateTimeKind.Utc)) - expected).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
}
