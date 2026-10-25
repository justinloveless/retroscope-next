using System.Net;
using Retroscope.Integration.Tests.Auth;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;

namespace Retroscope.Integration.Tests.Email;

// UX18 decision 14: an unverified user can neither create a team nor accept an email invite, so they can ask for the
// verification mail again. The answer is the same whether or not a mail went out.
[Trait("Category", "Email")]
[Collection(SharedPostgres.Name)]
public sealed class VerifyRequestTests(PostgresFixture postgres) {
    [Fact]
    public async Task An_unverified_user_gets_a_new_link_that_verifies_their_address() {
        await using var host = await EmailHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(user));

        using var request = await browser.PostAsync("/api/auth/verify/request");
        var (mail, token) = await host.DeliverLinkAsync("verify");
        using var verify = await browser.PostAsync("/api/auth/verify/consume", new { token });
        using var me = await browser.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        Assert.Equal("ada@example.com", mail.Header("To"));
        Assert.Equal(HttpStatusCode.NoContent, verify.StatusCode);
        Assert.True((await me.JsonAsync()).GetProperty("user").GetProperty("emailVerified").GetBoolean());
    }

    [Fact]
    public async Task A_verified_user_gets_the_same_answer_and_no_mail() {
        await using var host = await EmailHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com", verified: true);
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(user));

        using var request = await browser.PostAsync("/api/auth/verify/request");
        await host.DeliverAsync();

        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        Assert.Empty(host.Smtp.Received);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from auth_tokens"));
    }

    [Fact]
    public async Task Each_user_gets_three_mails_an_hour_and_the_same_answer_after_that() {
        await using var host = await EmailHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        var other = await host.CreateUserAsync("bob@example.com");
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(user));
        var neighbour = host.Browser();
        neighbour.SetCookie(Sessions.CookieName, await host.CreateSessionAsync(other));

        var answers = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) {
            using var request = await browser.PostAsync("/api/auth/verify/request");
            answers.Add(request.StatusCode);
        }
        using var theirs = await neighbour.PostAsync("/api/auth/verify/request");

        Assert.All(answers, x => Assert.Equal(HttpStatusCode.Accepted, x));
        Assert.Equal(HttpStatusCode.Accepted, theirs.StatusCode);
        Assert.Equal(3L, await host.ScalarAsync<long>("select count(*) from auth_tokens where purpose = 'verify_email' and user_id = $1", user.Value));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from auth_tokens where purpose = 'verify_email' and user_id = $1", other.Value));
        host.Time.Advance(TimeSpan.FromHours(1));
        using var later = await browser.PostAsync("/api/auth/verify/request");
        Assert.Equal(4L, await host.ScalarAsync<long>("select count(*) from auth_tokens where purpose = 'verify_email' and user_id = $1", user.Value));
    }

    [Fact]
    public async Task Asking_needs_a_signed_in_user() {
        await using var host = await EmailHost.StartAsync(postgres);

        using var request = await host.Browser().PostAsync("/api/auth/verify/request");

        Assert.Equal(HttpStatusCode.Unauthorized, request.StatusCode);
        Assert.Equal("unauthenticated", await request.ProblemCodeAsync());
    }
}
