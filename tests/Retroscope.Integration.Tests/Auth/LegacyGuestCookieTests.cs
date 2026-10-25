using System.Net;
using Retroscope.Core.Security;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;

namespace Retroscope.Integration.Tests.Auth;

// UX18 decision 3: guests are gone. A browser that still holds the old guest cookie is nobody, and signing in or out
// expires the cookie so the browser stops sending it.
[Trait("Category", "AuthCore")]
[Collection(SharedPostgres.Name)]
public sealed class LegacyGuestCookieTests(PostgresFixture postgres) {
    [Fact]
    public async Task The_guest_cookie_no_longer_identifies_anyone() {
        await using var host = await AuthHost.StartAsync(postgres);
        var browser = host.Browser();
        browser.SetCookie(Sessions.LegacyGuestCookieName, OpaqueToken.New());

        using var me = await browser.GetAsync("/api/me");
        using var logout = await browser.PostAsync("/api/auth/logout");

        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, logout.StatusCode);
    }

    [Fact]
    public async Task Signing_in_expires_the_guest_cookie() {
        await using var host = await AuthHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com");
        var browser = host.Browser();
        browser.SetCookie(Sessions.LegacyGuestCookieName, OpaqueToken.New());

        using var login = await browser.LoginAsync("ada@example.com", AuthHost.ImportedPassword);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var expired = SetCookieHeader.Find(login, Sessions.LegacyGuestCookieName);
        Assert.Equal(TimeSpan.Zero, expired.MaxAge);
        Assert.Contains("secure", expired.Attributes);
        Assert.Contains("path=/", expired.Attributes);
        Assert.Null(browser.Cookie(Sessions.LegacyGuestCookieName));
        Assert.NotNull(browser.Cookie(Sessions.CookieName));
    }

    [Fact]
    public async Task Signing_out_expires_the_guest_cookie() {
        await using var host = await AuthHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com");
        var browser = host.Browser();
        using (var login = await browser.LoginAsync("ada@example.com", AuthHost.ImportedPassword)) {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
        browser.SetCookie(Sessions.LegacyGuestCookieName, OpaqueToken.New());

        using var logout = await browser.PostAsync("/api/auth/logout");

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(TimeSpan.Zero, SetCookieHeader.Find(logout, Sessions.LegacyGuestCookieName).MaxAge);
        Assert.Null(browser.Cookie(Sessions.LegacyGuestCookieName));
        Assert.Null(browser.Cookie(Sessions.CookieName));
    }

    [Fact]
    public async Task A_browser_without_the_guest_cookie_is_not_sent_one() {
        await using var host = await AuthHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com");
        var browser = host.Browser();

        using var login = await browser.LoginAsync("ada@example.com", AuthHost.ImportedPassword);
        using var logout = await browser.PostAsync("/api/auth/logout");

        Assert.DoesNotContain(login.Headers.GetValues("Set-Cookie"), x => x.StartsWith(Sessions.LegacyGuestCookieName, StringComparison.Ordinal));
        Assert.DoesNotContain(logout.Headers.GetValues("Set-Cookie"), x => x.StartsWith(Sessions.LegacyGuestCookieName, StringComparison.Ordinal));
    }
}
