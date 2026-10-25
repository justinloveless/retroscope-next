using System.Net;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.TeamHttp;

// UX18 decision 8 on the whole server: a signed-in '/' goes to the team visited last, else the first team by name,
// else the create-or-join screen; a signed-out '/' is the welcome page.
[Trait("Category", "TeamHttp")]
[Collection(SharedPostgres.Name)]
public sealed class HomeRedirectTests(PostgresFixture postgres) {
    [Fact]
    public async Task A_user_in_no_team_is_sent_to_create_or_join_one() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var loner = await host.UserAsync("Lou", verified: false);

        using var response = await host.GetAsync(loner, "/");

        AssertRedirect(response, "/teams");
    }

    [Fact]
    public async Task Without_a_visit_the_first_team_by_name_wins_and_then_the_team_visited_last() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var beta = await host.TeamAsync(owner, "beta");
        var alpha = await host.TeamAsync(owner, "Alpha");

        using (var first = await host.GetAsync(owner, "/")) {
            AssertRedirect(first, $"/teams/{alpha}");
        }
        using (var visit = await host.GetAsync(owner, $"/api/teams/{beta}")) {
            Assert.Equal(HttpStatusCode.OK, visit.StatusCode);
        }
        using var after = await host.GetAsync(owner, "/");

        AssertRedirect(after, $"/teams/{beta}");
    }

    [Fact]
    public async Task A_visited_team_the_user_left_is_skipped() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var member = await host.UserAsync("Mia");
        var stays = await host.TeamAsync(owner, "Zulu", (member, "member"));
        var left = await host.TeamAsync(owner, "Alpha", (member, "member"));
        using (var visit = await host.GetAsync(member, $"/api/teams/{left}")) {
            Assert.Equal(HttpStatusCode.OK, visit.StatusCode);
        }
        await host.ExecuteAsync("delete from team_members where team_id = $1 and user_id = $2", left, member.Guid);

        using var response = await host.GetAsync(member, "/");

        AssertRedirect(response, $"/teams/{stays}");
    }

    [Fact]
    public async Task A_signed_out_visitor_gets_the_welcome_page_and_member_pages_ask_for_sign_in() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var team = await host.TeamAsync(owner);

        using var welcome = await host.GetAsync(TeamHttpHost.Anonymous, "/");
        using var teamPage = await host.GetAsync(TeamHttpHost.Anonymous, $"/teams/{team}?tab=poker");
        using var dashboard = await host.GetAsync(owner, "/dashboard");

        Assert.Equal(HttpStatusCode.OK, welcome.StatusCode);
        Assert.Equal("text/html", welcome.Content.Headers.ContentType?.MediaType);
        AssertRedirect(teamPage, "/login?return=" + Uri.EscapeDataString($"/teams/{team}?tab=poker"));
        AssertRedirect(dashboard, "/");
    }

    private static void AssertRedirect(HttpResponseMessage response, string location) {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }
}
