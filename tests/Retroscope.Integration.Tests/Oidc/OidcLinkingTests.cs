using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Retroscope.Integration.Tests.Auth;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;
using Retroscope.Server.Endpoints;

namespace Retroscope.Integration.Tests.Oidc;

// §e account linking (pre-hijack defence, S1, S5): which emails are trusted, what a trusted email may link or claim,
// and the explicit link flow.
[Trait("Category", "Oidc")]
[Collection(SharedPostgres.Name)]
public sealed class OidcLinkingTests(PostgresFixture postgres) {
    [Fact]
    public async Task Google_sign_in_creates_a_verified_account_with_the_picture_as_avatar() {
        await using var host = await OidcHost.StartAsync(postgres);
        var claims = OidcHost.Google("g-1", "Grace@Example.com ");
        claims["picture"] = OidcHost.PictureUrl;
        var browser = host.Browser();

        var response = await browser.SignInAsync("google", claims, "/retro/ABC");

        OidcAssert.RedirectsTo(response, "/retro/ABC");
        var me = await (await browser.GetAsync("/api/me")).JsonAsync();
        Assert.Equal("grace@example.com", me.GetProperty("user").GetProperty("email").GetString());
        Assert.True(me.GetProperty("user").GetProperty("emailVerified").GetBoolean());
        Assert.False(me.GetProperty("user").GetProperty("hasPassword").GetBoolean());
        Assert.Equal("Grace Hopper", me.GetProperty("principal").GetProperty("name").GetString());
        var avatar = me.GetProperty("principal").GetProperty("avatar").GetGuid();
        Assert.Equal("avatar", await host.ScalarAsync<string>("select purpose from blobs where id = $1", avatar));
        Assert.Equal(1, host.Provider.Requests.Count(x => x.Path.StartsWith("/picture/", StringComparison.Ordinal)));
        Assert.Equal(new Guid(me.GetProperty("principal").GetProperty("id").GetString()!), await host.LinkedUserAsync("google", "g-1"));
        Assert.Equal("google", await host.ScalarAsync<string>("select auth_method from sessions"));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'auth.login' and meta->>'method' = 'google'"));
    }

    [Fact]
    public async Task The_picture_is_fetched_once_and_never_from_another_host() {
        await using var host = await OidcHost.StartAsync(postgres);
        var claims = OidcHost.Google("g-1", "grace@example.com");
        claims["picture"] = OidcHost.PictureUrl;
        await host.Browser().SignInAsync("google", claims);
        await host.Browser().SignInAsync("google", claims);

        claims = OidcHost.Google("g-2", "other@example.com");
        claims["picture"] = "https://evil.example/a.png";
        await host.Browser().SignInAsync("google", claims);

        Assert.Equal(1, host.Provider.Requests.Count(x => x.Path.StartsWith("/picture/", StringComparison.Ordinal)));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from blobs"));
    }

    [Fact]
    public async Task A_picture_that_is_not_an_image_is_ignored() {
        await using var host = await OidcHost.StartAsync(postgres);
        host.Provider.Picture = "<html>not an image</html>"u8.ToArray();
        var claims = OidcHost.Google("g-1", "grace@example.com");
        claims["picture"] = OidcHost.PictureUrl;

        var response = await host.Browser().SignInAsync("google", claims);

        OidcAssert.RedirectsTo(response, "/dashboard");
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from blobs"));
    }

    [Fact]
    public async Task A_linked_subject_signs_in_whatever_its_email_says_now() {
        await using var host = await OidcHost.StartAsync(postgres);
        var owner = await host.CreateUserAsync("grace@example.com", verified: true);
        await host.LinkAsync(owner, "google", "g-1");
        var browser = host.Browser();

        var response = await browser.SignInAsync("google", OidcHost.Google("g-1", "renamed@example.com", emailVerified: false));

        OidcAssert.RedirectsTo(response, "/dashboard");
        var me = await (await browser.GetAsync("/api/me")).JsonAsync();
        Assert.Equal(owner.Value, new Guid(me.GetProperty("principal").GetProperty("id").GetString()!));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from users"));
    }

    [Fact]
    public async Task A_trusted_email_links_to_the_verified_account_and_keeps_its_password() {
        await using var host = await OidcHost.StartAsync(postgres);
        var owner = await host.CreateUserAsync("grace@example.com", verified: true);
        await host.CreateSessionAsync(owner);

        var response = await host.Browser().SignInAsync("google", OidcHost.Google("g-1", "grace@example.com"));

        OidcAssert.RedirectsTo(response, "/dashboard");
        Assert.Equal(owner.Value, await host.LinkedUserAsync("google", "g-1"));
        Assert.Equal(AuthHost.ImportedHash, await host.ScalarAsync<string>("select password_hash from users where id = $1", owner.Value));
        Assert.Equal(2L, await host.ScalarAsync<long>("select count(*) from sessions where user_id = $1", owner.Value));
    }

    [Fact]
    public async Task A_trusted_email_claims_the_unverified_account() {
        await using var host = await OidcHost.StartAsync(postgres);
        var squatter = await host.CreateUserAsync("grace@example.com", verified: false);
        var squatterSession = await host.CreateSessionAsync(squatter);
        var browser = host.Browser();

        var response = await browser.SignInAsync("google", OidcHost.Google("g-1", "grace@example.com"));

        OidcAssert.RedirectsTo(response, "/dashboard");
        Assert.Equal(squatter.Value, await host.LinkedUserAsync("google", "g-1"));
        Assert.Null(await host.ScalarAsync<string>("select password_hash from users where id = $1", squatter.Value));
        Assert.True(await host.ScalarAsync<bool>("select email_verified_at is not null from users where id = $1", squatter.Value));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from sessions where user_id = $1", squatter.Value));
        Assert.Equal("google", await host.ScalarAsync<string>("select auth_method from sessions where user_id = $1", squatter.Value));
        Assert.Single(host.Revoker.AuthSessions);
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'auth.claim' and meta->>'proof' = 'google'"));

        var stale = host.Browser();
        stale.SetCookie(Sessions.CookieName, squatterSession);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/api/me")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task An_unverified_google_email_never_links_to_an_existing_account(bool? emailVerified) {
        await using var host = await OidcHost.StartAsync(postgres);
        var squatter = await host.CreateUserAsync("grace@example.com", verified: false);
        var browser = host.Browser();

        var response = await browser.SignInAsync("google", OidcHost.Google("g-1", "grace@example.com", emailVerified));

        OidcAssert.RedirectsTo(response, "/login?error=account_exists");
        Assert.Null(browser.Cookie(Sessions.CookieName));
        Assert.Null(await host.LinkedUserAsync("google", "g-1"));
        Assert.Equal(AuthHost.ImportedHash, await host.ScalarAsync<string>("select password_hash from users where id = $1", squatter.Value));
        Assert.False(await host.ScalarAsync<bool>("select email_verified_at is not null from users where id = $1", squatter.Value));
    }

    [Fact]
    public async Task An_unverified_google_email_creates_an_unverified_account_when_none_exists() {
        await using var host = await OidcHost.StartAsync(postgres);
        var browser = host.Browser();

        var response = await browser.SignInAsync("google", OidcHost.Google("g-1", "grace@example.com", emailVerified: false));

        OidcAssert.RedirectsTo(response, "/dashboard");
        var me = await (await browser.GetAsync("/api/me")).JsonAsync();
        Assert.False(me.GetProperty("user").GetProperty("emailVerified").GetBoolean());
        Assert.NotNull(await host.LinkedUserAsync("google", "g-1"));
    }

    [Fact]
    public async Task A_token_without_an_email_cannot_create_an_account() {
        await using var host = await OidcHost.StartAsync(postgres);

        var response = await host.Browser().SignInAsync("google", OidcHost.Google("g-1", email: null));

        OidcAssert.RedirectsTo(response, "/login?error=email_required");
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from users"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task An_untrusted_entra_email_never_links(bool? edov) {
        await using var host = await OidcHost.StartAsync(postgres);
        var owner = await host.CreateUserAsync("alan@contoso.com", verified: true);
        var squatter = await host.CreateUserAsync("bob@contoso.com", verified: false);
        var browser = host.Browser();

        var verified = await browser.SignInAsync("entra", OidcHost.Entra(OidcHost.TenantA, "oid-1", "alan@contoso.com", edov));
        var unverified = await browser.SignInAsync("entra", OidcHost.Entra(OidcHost.TenantA, "oid-2", "bob@contoso.com", edov));

        OidcAssert.RedirectsTo(verified, "/login?error=account_exists");
        OidcAssert.RedirectsTo(unverified, "/login?error=account_exists");
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from external_identities"));
        Assert.Equal(AuthHost.ImportedHash, await host.ScalarAsync<string>("select password_hash from users where id = $1", squatter.Value));
        Assert.Equal(AuthHost.ImportedHash, await host.ScalarAsync<string>("select password_hash from users where id = $1", owner.Value));
        Assert.Null(browser.Cookie(Sessions.CookieName));
    }

    [Fact]
    public async Task An_untrusted_entra_email_creates_an_unverified_account_when_none_exists() {
        await using var host = await OidcHost.StartAsync(postgres);
        var browser = host.Browser();

        var response = await browser.SignInAsync("entra", OidcHost.Entra(OidcHost.TenantB, "oid-1", "alan@acme.com"));

        OidcAssert.RedirectsTo(response, "/dashboard");
        var me = await (await browser.GetAsync("/api/me")).JsonAsync();
        Assert.False(me.GetProperty("user").GetProperty("emailVerified").GetBoolean());
        Assert.NotNull(await host.LinkedUserAsync("entra", $"{OidcHost.TenantB}:oid-1"));
        Assert.Equal("entra", await host.ScalarAsync<string>("select auth_method from sessions"));
    }

    [Fact]
    public async Task Xms_edov_makes_the_entra_email_trusted() {
        await using var host = await OidcHost.StartAsync(postgres);
        var squatter = await host.CreateUserAsync("alan@contoso.com", verified: false);

        var response = await host.Browser().SignInAsync("entra", OidcHost.Entra(OidcHost.TenantB, "OID-1", "alan@contoso.com", edov: true));

        OidcAssert.RedirectsTo(response, "/dashboard");
        Assert.Equal(squatter.Value, await host.LinkedUserAsync("entra", $"{OidcHost.TenantB}:oid-1"));
        Assert.Null(await host.ScalarAsync<string>("select password_hash from users where id = $1", squatter.Value));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'auth.claim' and meta->>'proof' = 'entra'"));
    }

    [Theory]
    [InlineData("alan@acme.com")]
    [InlineData("alan@ACME.io")]
    public async Task A_listed_tenant_domain_makes_the_entra_email_trusted(string email) {
        await using var host = await OidcHost.StartAsync(postgres);
        var owner = await host.CreateUserAsync(email.ToLowerInvariant(), verified: true);

        var response = await host.Browser().SignInAsync("entra", OidcHost.Entra(OidcHost.TenantA, "oid-1", email));

        OidcAssert.RedirectsTo(response, "/dashboard");
        Assert.Equal(owner.Value, await host.LinkedUserAsync("entra", $"{OidcHost.TenantA}:oid-1"));
    }

    [Fact]
    public async Task A_domain_listed_for_another_tenant_is_not_trusted() {
        await using var host = await OidcHost.StartAsync(postgres);
        await host.CreateUserAsync("alan@acme.com", verified: true);

        var response = await host.Browser().SignInAsync("entra", OidcHost.Entra(OidcHost.TenantB, "oid-1", "alan@acme.com"));

        OidcAssert.RedirectsTo(response, "/login?error=account_exists");
    }

    [Fact]
    public async Task A_disabled_account_cannot_sign_in() {
        await using var host = await OidcHost.StartAsync(postgres);
        var owner = await host.CreateUserAsync("grace@example.com", verified: true);
        await host.ExecuteAsync("update users set disabled_at = now() where id = $1", owner.Value);
        var browser = host.Browser();

        var byEmail = await browser.SignInAsync("google", OidcHost.Google("g-1", "grace@example.com"));
        await host.LinkAsync(owner, "google", "g-2");
        var bySubject = await browser.SignInAsync("google", OidcHost.Google("g-2", "grace@example.com"));

        OidcAssert.RedirectsTo(byEmail, "/login?error=account_disabled");
        OidcAssert.RedirectsTo(bySubject, "/login?error=account_disabled");
        Assert.Null(browser.Cookie(Sessions.CookieName));
    }

    [Fact]
    public async Task Sign_in_rotates_a_session_the_browser_already_had() {
        await using var host = await OidcHost.StartAsync(postgres);
        var other = await host.CreateUserAsync("other@example.com", verified: true);
        var browser = host.Browser();
        var planted = await host.CreateSessionAsync(other);
        browser.SetCookie(Sessions.CookieName, planted);

        await browser.SignInAsync("google", OidcHost.Google("g-1", "grace@example.com"));

        Assert.NotEqual(planted, browser.Cookie(Sessions.CookieName));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from sessions where user_id = $1", other.Value));
    }

    [Fact]
    public async Task Link_flow_links_the_identity_to_the_signed_in_user() {
        await using var host = await OidcHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com", verified: true);
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(user));
        host.Provider.Claims = OidcHost.Entra(OidcHost.TenantB, "oid-1", "someone-else@contoso.com");
        host.Provider.Claims["picture"] = OidcHost.PictureUrl;

        var start = await browser.PostAsync("/api/me/identities/entra/link", JsonContent.Create(new { @return = "/account?tab=identities" }));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var url = new Uri((await start.JsonAsync()).GetProperty("url").GetString()!);
        Assert.StartsWith(host.Provider.EntraAuthority + "/organizations/oauth2/v2.0/authorize?", url.AbsoluteUri, StringComparison.Ordinal);
        var response = await browser.GetAsync(await OidcBrowser.FollowProviderAsync(url));

        OidcAssert.RedirectsTo(response, "/account?tab=identities");
        Assert.Equal(user.Value, await host.LinkedUserAsync("entra", $"{OidcHost.TenantB}:oid-1"));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from sessions where user_id = $1", user.Value));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'auth.link'"));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from blobs"));
    }

    [Fact]
    public async Task Link_flow_without_a_body_returns_to_the_account_page() {
        await using var host = await OidcHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com", verified: true);
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(user));
        host.Provider.Claims = OidcHost.Google("g-1", "ada@gmail.com");
        host.Provider.Claims["picture"] = OidcHost.PictureUrl;

        var start = await browser.PostAsync("/api/me/identities/google/link");
        var url = new Uri((await start.JsonAsync()).GetProperty("url").GetString()!);
        var response = await browser.GetAsync(await OidcBrowser.FollowProviderAsync(url));

        OidcAssert.RedirectsTo(response, OidcEndpoints.DefaultLinkReturn);
        Assert.Equal(user.Value, await host.LinkedUserAsync("google", "g-1"));
        var me = await (await browser.GetAsync("/api/me")).JsonAsync();
        Assert.Equal(JsonValueKind.String, me.GetProperty("principal").GetProperty("avatar").ValueKind);
    }

    [Fact]
    public async Task Link_flow_refuses_a_subject_linked_to_another_user() {
        await using var host = await OidcHost.StartAsync(postgres);
        var other = await host.CreateUserAsync("other@example.com", verified: true);
        await host.LinkAsync(other, "google", "g-1");
        var user = await host.CreateUserAsync("ada@example.com", verified: true);
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(user));
        host.Provider.Claims = OidcHost.Google("g-1", "other@example.com");

        var start = await browser.PostAsync("/api/me/identities/google/link");
        var url = new Uri((await start.JsonAsync()).GetProperty("url").GetString()!);
        var response = await browser.GetAsync(await OidcBrowser.FollowProviderAsync(url));

        OidcAssert.RedirectsTo(response, "/account?error=identity_linked_elsewhere");
        Assert.Equal(other.Value, await host.LinkedUserAsync("google", "g-1"));
        var me = await (await browser.GetAsync("/api/me")).JsonAsync();
        Assert.Equal(user.Value, new Guid(me.GetProperty("principal").GetProperty("id").GetString()!));
    }

    [Fact]
    public async Task Link_flow_already_linked_to_the_same_user_is_fine() {
        await using var host = await OidcHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com", verified: true);
        await host.LinkAsync(user, "google", "g-1");
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(user));
        host.Provider.Claims = OidcHost.Google("g-1", "ada@example.com");

        var start = await browser.PostAsync("/api/me/identities/google/link");
        var url = new Uri((await start.JsonAsync()).GetProperty("url").GetString()!);
        var response = await browser.GetAsync(await OidcBrowser.FollowProviderAsync(url));

        OidcAssert.RedirectsTo(response, "/account");
    }

    [Fact]
    public async Task Link_flow_needs_the_same_session_at_the_callback() {
        await using var host = await OidcHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com", verified: true);
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(user));
        host.Provider.Claims = OidcHost.Google("g-1", "ada@example.com");
        var start = await browser.PostAsync("/api/me/identities/google/link");
        var url = new Uri((await start.JsonAsync()).GetProperty("url").GetString()!);

        browser.RemoveCookie(Sessions.CookieName);
        var response = await browser.GetAsync(await OidcBrowser.FollowProviderAsync(url));

        OidcAssert.RedirectsTo(response, "/account?error=link_session");
        Assert.Null(await host.LinkedUserAsync("google", "g-1"));
    }

    [Fact]
    public async Task Link_needs_a_signed_in_user_and_refuses_unknown_members() {
        await using var host = await OidcHost.StartAsync(postgres);
        var anonymous = host.Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/me/identities/google/link")).StatusCode);

        var user = await host.CreateUserAsync("ada@example.com", verified: true);
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(user));
        var extra = await browser.PostAsync("/api/me/identities/google/link", JsonContent.Create(new { @return = "/account", userId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync("/api/me/identities/github/link")).StatusCode);
    }
}
