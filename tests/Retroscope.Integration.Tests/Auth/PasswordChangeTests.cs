using System.Net;
using Retroscope.Core.Security;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;

namespace Retroscope.Integration.Tests.Auth;

[Trait("Category", "AuthCore")]
[Collection(SharedPostgres.Name)]
public sealed class PasswordChangeTests(PostgresFixture postgres) {
    private const string NewPassword = "a much longer passphrase";

    [Fact]
    public async Task Changing_the_password_revokes_every_session_and_rotates_this_browsers_token() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var phone = await host.CreateSessionAsync(user, AuthMethod.Password);
        var laptop = host.Browser();
        (await laptop.LoginAsync("ada@example.com", AuthHost.ImportedPassword)).Dispose();
        var oldToken = laptop.Cookie(Sessions.CookieName)!;

        using var response = await laptop.PutAsync("/api/me/password", new { current = AuthHost.ImportedPassword, @new = NewPassword });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotEqual(oldToken, laptop.Cookie(Sessions.CookieName));
        Assert.Equal(
            new[] { phone, oldToken }.Select(x => Sessions.PublicId(TokenHash.Of(x))).Order(),
            host.Revoker.AuthSessions.Order());
        Assert.Null(await host.Service<Sessions>().ResolveAsync(phone, AuthHost.Token));
        Assert.Null(await host.Service<Sessions>().ResolveAsync(oldToken, AuthHost.Token));
        using (var me = await laptop.GetAsync("/api/me")) {
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        }
        Assert.True(Bcrypt.Verify(NewPassword, await host.ScalarAsync<string>("select password_hash from users where id = $1", user.Value)));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'auth.password.change'"));
        using var login = await host.Browser("198.51.100.7").LoginAsync("ada@example.com", NewPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task A_wrong_or_missing_current_password_changes_nothing() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var browser = host.Browser();
        (await browser.LoginAsync("ada@example.com", AuthHost.ImportedPassword)).Dispose();

        using var wrong = await browser.PutAsync("/api/me/password", new { current = "nope", @new = NewPassword });
        using var missing = await browser.PutAsync("/api/me/password", new { @new = NewPassword });

        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal("wrong_password", await wrong.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(AuthHost.ImportedHash, await host.ScalarAsync<string>("select password_hash from users where id = $1", user.Value));
        Assert.Empty(host.Revoker.AuthSessions);
    }

    [Theory]
    [InlineData("short", "too_short")]
    [InlineData("Basketball", "too_common")]
    [InlineData("1234567890", "too_common")]
    public async Task The_new_password_must_pass_the_signup_rule(string password, string code) {
        await using var host = await AuthHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com");
        var browser = host.Browser();
        (await browser.LoginAsync("ada@example.com", AuthHost.ImportedPassword)).Dispose();

        using var response = await browser.PutAsync("/api/me/password", new { current = AuthHost.ImportedPassword, @new = password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await response.JsonAsync()).GetProperty("fields").GetProperty("new").GetString());
    }

    [Theory]
    [InlineData(AuthMethod.MagicLink)]
    [InlineData(AuthMethod.Google)]
    public async Task A_first_password_needs_a_fresh_oidc_or_magic_link_session(AuthMethod method) {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com", passwordHash: null, verified: true);
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(user, method));
        host.Time.Advance(TimeSpan.FromMinutes(9));

        using var response = await browser.PutAsync("/api/me/password", new { @new = NewPassword });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'auth.password.set'"));
    }

    [Fact]
    public async Task A_first_password_from_a_stale_session_requires_re_authentication() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com", passwordHash: null, verified: true);
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(user, AuthMethod.MagicLink));
        host.Time.Advance(TimeSpan.FromMinutes(11));

        using var response = await browser.PutAsync("/api/me/password", new { @new = NewPassword });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("reauth_required", await response.ProblemCodeAsync());
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from users where password_hash is not null"));
    }

    [Fact]
    public async Task A_first_password_never_comes_from_a_password_session() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com", passwordHash: null, verified: true);
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(user, AuthMethod.Password));

        using var response = await browser.PutAsync("/api/me/password", new { @new = NewPassword });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("reauth_required", await response.ProblemCodeAsync());
    }
}
