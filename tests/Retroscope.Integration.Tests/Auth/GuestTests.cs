using System.Net;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;

namespace Retroscope.Integration.Tests.Auth;

[Trait("Category", "AuthCore")]
public sealed class GuestNameTests {
    [Theory]
    [InlineData("  Ada  ", "Ada")]
    [InlineData("@ada", "@ada")]
    [InlineData("Ada @ home", "Ada @ home")]
    [InlineData("Zoë 🎲", "Zoë 🎲")]
    public void Plain_names_are_trimmed_and_kept(string input, string expected) {
        Assert.True(GuestNames.TryNormalize(input, out var name, out _));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData("   ", "required")]
    [InlineData("ada@example.com", "email_shaped")]
    [InlineData("Ada (ada@corp)", "email_shaped")]
    [InlineData("a@b", "email_shaped")]
    [InlineData("Ada\u0000", "invalid")]
    [InlineData("Ada\nLovelace", "invalid")]
    [InlineData("\u202Eada", "invalid")]
    [InlineData("12345678901234567890123456789012345678901", "too_long")]
    public void Unsafe_names_are_refused(string? input, string error) {
        Assert.False(GuestNames.TryNormalize(input, out _, out var actual));
        Assert.Equal(error, actual);
    }

    [Fact]
    public void A_lone_surrogate_is_refused() {
        Assert.False(GuestNames.TryNormalize("Ada" + '\uD800', out _, out var error));
        Assert.Equal("invalid", error);
    }
}

[Trait("Category", "AuthCore")]
[Collection(SharedPostgres.Name)]
public sealed class GuestTests(PostgresFixture postgres) {
    [Fact]
    public async Task A_guest_is_issued_a_cookie_and_can_rename_itself() {
        await using var host = await AuthHost.StartAsync(postgres);
        var browser = host.Browser();

        using var issued = await browser.PostAsync("/api/guest", new { displayName = " Grace " });
        var id = (await issued.JsonAsync()).GetProperty("principal").GetProperty("id").GetGuid();
        using var renamed = await browser.PatchAsync("/api/guest", new { displayName = "Grace H" });
        using var me = await browser.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        var cookie = SetCookieHeader.Find(issued, Guests.CookieName);
        Assert.Contains("httponly", cookie.Attributes);
        Assert.Contains("secure", cookie.Attributes);
        Assert.Contains("samesite=lax", cookie.Attributes);
        Assert.Equal(Guests.Lifetime, cookie.MaxAge);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var body = await me.JsonAsync();
        Assert.Equal(id, body.GetProperty("principal").GetProperty("id").GetGuid());
        Assert.Equal("guest", body.GetProperty("principal").GetProperty("kind").GetString());
        Assert.Equal("Grace H", body.GetProperty("principal").GetProperty("name").GetString());
        Assert.False(body.TryGetProperty("user", out _));
        Assert.Equal("Grace H", await host.ScalarAsync<string>("select display_name from principals where id = $1", id));
    }

    [Fact]
    public async Task Posting_again_with_a_live_guest_cookie_renames_instead_of_issuing_a_second_guest() {
        await using var host = await AuthHost.StartAsync(postgres);
        var browser = host.Browser();

        using var first = await browser.PostAsync("/api/guest", new { displayName = "Grace" });
        using var second = await browser.PostAsync("/api/guest", new { displayName = "Grace H" });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            (await first.JsonAsync()).GetProperty("principal").GetProperty("id").GetGuid(),
            (await second.JsonAsync()).GetProperty("principal").GetProperty("id").GetGuid());
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from principals where kind = 'guest'"));
    }

    [Fact]
    public async Task Email_shaped_names_are_refused_on_issue_and_rename() {
        await using var host = await AuthHost.StartAsync(postgres);
        var browser = host.Browser();

        using var issue = await browser.PostAsync("/api/guest", new { displayName = "grace@example.com" });
        (await browser.PostAsync("/api/guest", new { displayName = "Grace" })).Dispose();
        using var rename = await browser.PatchAsync("/api/guest", new { displayName = "grace@example.com" });

        Assert.Equal(HttpStatusCode.BadRequest, issue.StatusCode);
        Assert.Equal("email_shaped", (await issue.JsonAsync()).GetProperty("fields").GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, rename.StatusCode);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from principals where display_name like '%@%'"));
    }

    [Fact]
    public async Task Guest_creation_is_limited_to_twenty_an_hour_per_ip() {
        await using var host = await AuthHost.StartAsync(postgres);

        for (var i = 0; i < 20; i++) {
            using var created = await host.Browser().PostAsync("/api/guest", new { displayName = $"Guest {i}" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        using var limited = await host.Browser().PostAsync("/api/guest", new { displayName = "One too many" });
        using var elsewhere = await host.Browser("198.51.100.7").PostAsync("/api/guest", new { displayName = "Elsewhere" });

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.Created, elsewhere.StatusCode);
        Assert.Equal(21L, await host.ScalarAsync<long>("select count(*) from principals where kind = 'guest'"));
    }

    [Fact]
    public async Task A_signed_in_user_takes_precedence_over_the_guest_cookie_which_is_kept() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var browser = host.Browser();
        (await browser.PostAsync("/api/guest", new { displayName = "Grace" })).Dispose();
        var guestCookie = browser.Cookie(Guests.CookieName);

        (await browser.LoginAsync("ada@example.com", AuthHost.ImportedPassword)).Dispose();
        using var signedIn = await browser.GetAsync("/api/me");
        using var rename = await browser.PatchAsync("/api/guest", new { displayName = "Nope" });
        (await browser.PostAsync("/api/auth/logout")).Dispose();
        using var signedOut = await browser.GetAsync("/api/me");

        Assert.Equal(user.Value, (await signedIn.JsonAsync()).GetProperty("principal").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);
        Assert.Equal(guestCookie, browser.Cookie(Guests.CookieName));
        Assert.Equal("guest", (await signedOut.JsonAsync()).GetProperty("principal").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Rename_needs_a_guest_and_an_anonymous_caller_is_refused() {
        await using var host = await AuthHost.StartAsync(postgres);

        using var response = await host.Browser().PatchAsync("/api/guest", new { displayName = "Grace" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_guest_token_slides_at_most_daily_and_expires_after_180_days_unused() {
        await using var host = await AuthHost.StartAsync(postgres);
        var browser = host.Browser();
        using var issued = await browser.PostAsync("/api/guest", new { displayName = "Grace" });
        var id = (await issued.JsonAsync()).GetProperty("principal").GetProperty("id").GetGuid();
        var issuedExpiry = await host.ScalarAsync<DateTime>("select expires_at from guest_tokens where principal_id = $1", id);

        host.Time.Advance(TimeSpan.FromHours(12));
        (await browser.GetAsync("/api/me")).Dispose();
        var sameDay = await host.ScalarAsync<DateTime>("select expires_at from guest_tokens where principal_id = $1", id);
        host.Time.Advance(TimeSpan.FromDays(2));
        (await browser.GetAsync("/api/me")).Dispose();
        var slid = await host.ScalarAsync<DateTime>("select expires_at from guest_tokens where principal_id = $1", id);
        host.Time.Advance(Guests.Lifetime + TimeSpan.FromDays(1));
        using var expired = await browser.GetAsync("/api/me");

        Assert.Equal(issuedExpiry, sameDay);
        Assert.True(slid > issuedExpiry);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }
}
