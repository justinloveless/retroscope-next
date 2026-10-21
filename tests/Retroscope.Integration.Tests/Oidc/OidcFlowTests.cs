using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Retroscope.Fakes.FakeOidc;
using Retroscope.Integration.Tests.Auth;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;
using OidcService = Retroscope.Server.Auth.Oidc;

namespace Retroscope.Integration.Tests.Oidc;

// §e / S14 / S20: the authorization code flow itself: PKCE, state and nonce, the single-use Lax cookie, ID token
// validation and the `return` corpus.
[Trait("Category", "Oidc")]
[Collection(SharedPostgres.Name)]
public sealed class OidcFlowTests(PostgresFixture postgres) {
    [Fact]
    public async Task Start_redirects_with_pkce_state_nonce_and_query_response_mode() {
        await using var host = await OidcHost.StartAsync(postgres);
        var browser = host.Browser();

        var response = await browser.GetAsync("/auth/oidc/google/start?return=/retro/ABC");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal(host.Provider.GoogleAuthorizeUrl, location.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(OidcHost.GoogleClient, query["client_id"]);
        Assert.Equal($"{OidcHost.PublicUrl}/auth/oidc/google/callback", query["redirect_uri"]);
        Assert.Equal("openid email profile", query["scope"]);
        Assert.Equal("query", query["response_mode"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(43, query["code_challenge"].ToString().Length);
        Assert.Equal(43, query["state"].ToString().Length);
        Assert.Equal(43, query["nonce"].ToString().Length);
        Assert.NotEqual(query["state"], query["nonce"]);
    }

    [Fact]
    public async Task Flow_cookie_is_sealed_host_only_lax_and_lives_ten_minutes() {
        await using var host = await OidcHost.StartAsync(postgres);

        var response = await host.Browser().GetAsync("/auth/oidc/entra/start");

        var cookie = SetCookieHeader.Find(response, OidcService.CookieName);
        Assert.Equal(TimeSpan.FromMinutes(10), cookie.MaxAge);
        Assert.Contains("httponly", cookie.Attributes);
        Assert.Contains("secure", cookie.Attributes);
        Assert.Contains("samesite=lax", cookie.Attributes);
        Assert.Contains("path=/", cookie.Attributes);
        var state = QueryHelpers.ParseQuery(response.Headers.Location!.Query)["state"].ToString();
        Assert.DoesNotContain(state, cookie.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(state, Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cookie.Value)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Code_is_exchanged_with_the_secret_redirect_uri_and_pkce_verifier() {
        await using var host = await OidcHost.StartAsync(postgres);
        var browser = host.Browser();

        var response = await browser.SignInAsync("google", OidcHost.Google("g-1", "grace@example.com"));

        OidcAssert.RedirectsTo(response, "/dashboard");
        var authorize = host.Provider.Requests.Single(x => x.Path == "/google/authorize");
        var token = host.Provider.Requests.Single(x => x.Path == "/google/token");
        Assert.Equal("authorization_code", token.Values["grant_type"]);
        Assert.Equal("google-secret", token.Values["client_secret"]);
        Assert.Equal(authorize.Values["redirect_uri"], token.Values["redirect_uri"]);
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(token.Values["code_verifier"])));
        Assert.Equal(authorize.Values["code_challenge"], challenge);
    }

    [Fact]
    public async Task A_code_from_another_flow_fails_the_pkce_check() {
        await using var host = await OidcHost.StartAsync(postgres);
        host.Provider.Claims = OidcHost.Google("g-1", "grace@example.com");
        var victim = host.Browser();
        var attacker = host.Browser();
        var attackerCallback = await attacker.AuthorizeAsync("google");
        var victimCallback = await victim.AuthorizeAsync("google");

        // The attacker's code with the victim's own state and cookie: only PKCE ties the code to the flow.
        var code = QueryHelpers.ParseQuery(new Uri(new Uri(OidcHost.PublicUrl), attackerCallback).Query)["code"];
        var state = QueryHelpers.ParseQuery(new Uri(new Uri(OidcHost.PublicUrl), victimCallback).Query)["state"];
        var response = await victim.GetAsync($"/auth/oidc/google/callback?code={Uri.EscapeDataString(code!)}&state={Uri.EscapeDataString(state!)}");

        OidcAssert.RedirectsTo(response, "/login?error=oidc_failed");
        Assert.Null(victim.Cookie(Sessions.CookieName));
    }

    [Fact]
    public async Task Callback_deletes_the_flow_cookie_and_a_replayed_cookie_is_refused() {
        await using var host = await OidcHost.StartAsync(postgres);
        host.Provider.Claims = OidcHost.Google("g-1", "grace@example.com");
        var browser = host.Browser();
        var callback = await browser.AuthorizeAsync("google");
        var flowCookie = browser.Cookie(OidcService.CookieName)!;

        var first = await browser.GetAsync(callback);
        Assert.Equal(TimeSpan.Zero, SetCookieHeader.Find(first, OidcService.CookieName).MaxAge);
        OidcAssert.RedirectsTo(first, "/dashboard");

        var replay = host.Browser();
        replay.SetCookie(OidcService.CookieName, flowCookie);
        var second = await replay.GetAsync(callback);
        OidcAssert.RedirectsTo(second, "/login?error=oidc_state");
        Assert.Null(replay.Cookie(Sessions.CookieName));
    }

    [Fact]
    public async Task Callback_without_the_cookie_or_with_another_state_is_refused() {
        await using var host = await OidcHost.StartAsync(postgres);
        host.Provider.Claims = OidcHost.Google("g-1", "grace@example.com");
        var browser = host.Browser();
        var callback = await browser.AuthorizeAsync("google");

        var stranger = await host.Browser().GetAsync(callback);
        OidcAssert.RedirectsTo(stranger, "/login?error=oidc_state");

        var wrongState = callback.Replace("state=", "state=x", StringComparison.Ordinal);
        var tampered = await browser.GetAsync(wrongState);
        OidcAssert.RedirectsTo(tampered, "/login?error=oidc_state");
        Assert.Null(browser.Cookie(OidcService.CookieName));
        Assert.Null(await host.LinkedUserAsync("google", "g-1"));
    }

    [Fact]
    public async Task Flow_cookie_for_another_provider_is_refused() {
        await using var host = await OidcHost.StartAsync(postgres);
        host.Provider.Claims = OidcHost.Google("g-1", "grace@example.com");
        var browser = host.Browser();
        var callback = await browser.AuthorizeAsync("google");

        var response = await browser.GetAsync(callback.Replace("/google/", "/entra/", StringComparison.Ordinal));

        OidcAssert.RedirectsTo(response, "/login?error=oidc_state");
    }

    [Fact]
    public async Task Provider_error_is_reported_as_denied() {
        await using var host = await OidcHost.StartAsync(postgres);
        var browser = host.Browser();
        var start = await browser.GetAsync("/auth/oidc/google/start");
        var state = QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"];

        var response = await browser.GetAsync($"/auth/oidc/google/callback?error=access_denied&state={state}");

        OidcAssert.RedirectsTo(response, "/login?error=oidc_denied");
    }

    [Fact]
    public async Task Token_endpoint_failure_is_reported() {
        await using var host = await OidcHost.StartAsync(postgres);
        host.Provider.TokenStatus = 400;

        var response = await host.Browser().SignInAsync("google", OidcHost.Google("g-1", "grace@example.com"));

        OidcAssert.RedirectsTo(response, "/login?error=oidc_failed");
    }

    [Theory]
    [InlineData("https://accounts.google.com")]
    [InlineData("accounts.google.com")]
    public async Task Both_google_issuer_forms_are_accepted(string issuer) {
        await using var host = await OidcHost.StartAsync(postgres);
        var claims = OidcHost.Google("g-1", "grace@example.com");
        claims["iss"] = issuer;

        var response = await host.Browser().SignInAsync("google", claims);

        OidcAssert.RedirectsTo(response, "/dashboard");
    }

    public static TheoryData<string, string, JsonNode?, string> BadTokens() {
        var now = DateTimeOffset.UtcNow;
        return new() {
            { "google", "iss", "https://accounts.google.com.evil.test", "oidc_issuer" },
            { "google", "iss", "https://login.microsoftonline.com/" + OidcHost.TenantA + "/v2.0", "oidc_issuer" },
            { "google", "aud", "someone-else", "oidc_audience" },
            { "google", "aud", new JsonArray("someone-else", OidcHost.GoogleClient), "oidc_audience" },
            { "google", "azp", "someone-else", "oidc_audience" },
            { "google", "exp", now.AddMinutes(-6).ToUnixTimeSeconds(), "oidc_expired" },
            { "google", "iat", now.AddMinutes(10).ToUnixTimeSeconds(), "oidc_expired" },
            { "google", "iat", now.AddHours(-2).ToUnixTimeSeconds(), "oidc_expired" },
            { "google", "nonce", "not-the-nonce", "oidc_nonce" },
            { "google", "sub", "", "oidc_subject" },
            { "entra", "iss", "https://login.microsoftonline.com/" + OidcHost.TenantB + "/v2.0", "oidc_issuer" },
            { "entra", "iss", "https://sts.windows.net/" + OidcHost.TenantA + "/", "oidc_issuer" },
            { "entra", "aud", OidcHost.GoogleClient, "oidc_audience" },
            { "entra", "tid", "11111111-2222-3333-4444-555555555555", "oidc_tenant" },
        };
    }

    [Theory]
    [MemberData(nameof(BadTokens))]
    public async Task Invalid_id_tokens_are_refused(string provider, string claim, JsonNode? value, string code) {
        await using var host = await OidcHost.StartAsync(postgres);
        var claims = provider == "google"
            ? OidcHost.Google("g-1", "grace@example.com")
            : OidcHost.Entra(OidcHost.TenantA, "oid-1", "alan@acme.com");
        claims[claim] = value?.DeepClone();
        var browser = host.Browser();

        var response = await browser.SignInAsync(provider, claims);

        OidcAssert.RedirectsTo(response, $"/login?error={code}");
        Assert.Null(browser.Cookie(Sessions.CookieName));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from users"));
    }

    [Fact]
    public async Task An_audience_list_is_accepted_when_azp_names_us() {
        await using var host = await OidcHost.StartAsync(postgres);
        var claims = OidcHost.Google("g-1", "grace@example.com");
        claims["aud"] = new JsonArray(OidcHost.GoogleClient, "other");
        claims["azp"] = OidcHost.GoogleClient;

        var response = await host.Browser().SignInAsync("google", claims);

        OidcAssert.RedirectsTo(response, "/dashboard");
    }

    [Fact]
    public async Task A_token_within_the_clock_skew_is_accepted() {
        await using var host = await OidcHost.StartAsync(postgres);
        var claims = OidcHost.Google("g-1", "grace@example.com");
        claims["exp"] = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds();
        claims["iat"] = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds();

        var response = await host.Browser().SignInAsync("google", claims);

        OidcAssert.RedirectsTo(response, "/dashboard");
    }

    // U18 decision 4: the five-minute leeway applies to `exp` as well as `iat`, and ends exactly at five minutes.
    [Theory]
    [InlineData(-299, 0, null)]
    [InlineData(-300, 0, "oidc_expired")]
    [InlineData(3600, 300, null)]
    [InlineData(3600, 301, "oidc_expired")]
    [InlineData(3600, -300, null)]
    [InlineData(3600, -301, "oidc_expired")]
    public void Exp_and_iat_allow_five_minutes_of_clock_skew(int expSeconds, int iatSeconds, string? expected) {
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var client = new OidcClient(OidcHost.GoogleClient, "secret", new Uri("https://a.test/authorize"), new Uri("https://a.test/token"));
        var claims = new IdTokenClaims(
            "https://accounts.google.com",
            JsonSerializer.SerializeToElement(OidcHost.GoogleClient),
            null,
            now.AddSeconds(expSeconds).ToUnixTimeSeconds(),
            now.AddSeconds(iatSeconds).ToUnixTimeSeconds(),
            "nonce",
            "g-1",
            "grace@example.com",
            JsonSerializer.SerializeToElement(true),
            null,
            null,
            null,
            "Grace",
            null);

        var valid = IdTokenValidator.TryValidate(OidcProvider.Google, new OidcOptions { Google = client }, claims, "nonce", now, out _, out var error);

        Assert.Equal(expected is null, valid);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Id_token_payload_is_decoded_without_checking_the_signature() {
        Assert.Null(IdTokenValidator.DecodePayload("only.two"));
        Assert.Null(IdTokenValidator.DecodePayload("a.!!!.c"));
        Assert.Null(IdTokenValidator.DecodePayload("a." + Base64Url.EncodeToString("[1]"u8) + ".c"));
        var decoded = IdTokenValidator.DecodePayload(FakeOidcServer.Mint(new JsonObject { ["sub"] = "x", ["extra"] = 1 }));
        Assert.Equal("x", decoded!.Sub);
    }

    public static TheoryData<string> HostileReturns() => [
        "//evil.example",
        "/\\evil.example",
        "\\\\evil.example",
        "/%09/evil.example",
        "/%2F%2Fevil.example",
        "/%252F%252Fevil.example",
        "/%5Cevil.example",
        "https://evil.example/",
        "http://retro.test/",
        "https://retro.test.evil.example/",
        "https://retro.test/account",
        "javascript:alert(1)",
        "\t/evil",
        "/ /evil",
        "/\r\nLocation: https://evil.example",
        "evil.example",
        "",
    ];

    [Theory]
    [MemberData(nameof(HostileReturns))]
    public async Task Hostile_return_values_land_on_the_dashboard(string value) {
        await using var host = await OidcHost.StartAsync(postgres);

        var response = await host.Browser().SignInAsync("google", OidcHost.Google("g-1", "grace@example.com"), value);

        OidcAssert.RedirectsTo(response, "/dashboard");
    }

    [Theory]
    [InlineData("/retro/ABCDEF1234", "/retro/ABCDEF1234")]
    [InlineData("/teams/1/poker/ABC?tab=jira", "/teams/1/poker/ABC?tab=jira")]
    public async Task Same_origin_return_paths_are_kept(string value, string expected) {
        await using var host = await OidcHost.StartAsync(postgres);

        var response = await host.Browser().SignInAsync("google", OidcHost.Google("g-1", "grace@example.com"), value);

        OidcAssert.RedirectsTo(response, expected);
    }

    [Fact]
    public async Task Unknown_providers_are_not_found() {
        await using var host = await OidcHost.StartAsync(postgres);
        var browser = host.Browser();

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/auth/oidc/github/start")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/auth/oidc/github/callback?code=x&state=y")).StatusCode);
    }

    [Fact]
    public async Task An_empty_tenant_allow_list_disables_entra() {
        await using var host = await OidcHost.StartAsync(postgres, new Dictionary<string, string?> { ["ENTRA_ALLOWED_TENANTS"] = "" });
        var browser = host.Browser();

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/auth/oidc/entra/start")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/auth/oidc/entra/callback?code=x&state=y")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/auth/oidc/google/start")).StatusCode);
        var boot = await (await browser.GetAsync("/api/boot?path=/login")).JsonAsync();
        Assert.Equal(["google"], boot.GetProperty("oidc").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Boot_lists_the_enabled_providers() {
        await using var host = await OidcHost.StartAsync(postgres);

        var boot = await (await host.Browser().GetAsync("/api/boot?path=/login")).JsonAsync();

        Assert.Equal(["google", "entra"], boot.GetProperty("oidc").EnumerateArray().Select(x => x.GetString()));
    }
}
