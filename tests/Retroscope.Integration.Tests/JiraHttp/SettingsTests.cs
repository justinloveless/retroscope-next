using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Fakes.FakeJira;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.JiraHttp;

/// <summary>The team's Jira settings: write-only credentials, host changes, discovery, the rotate-required banner.</summary>
[Trait("Category", "JiraHttp")]
[Collection(SharedPostgres.Name)]
public sealed class SettingsTests(PostgresFixture postgres) {
    private const string BoardPointsField = "customfield_10028";

    [Fact]
    public async Task Credentials_are_write_only_and_admins_see_only_their_masked_forms() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (admin, member, team) = await TeamAsync(host);

        using (var response = await host.SendAsync(HttpMethod.Put, admin, JiraHttpHost.Url(team), Save(host, boardId: 7, membersCanEdit: false))) {
            var text = await response.Content.ReadAsStringAsync(JiraHttpHost.Token);
            Assert.Equal(200, (int)response.StatusCode);
            Assert.DoesNotContain(host.Jira.Token, text, StringComparison.Ordinal);
            Assert.DoesNotContain(host.Jira.Email, text, StringComparison.Ordinal);
        }
        var view = await host.JsonAsync(HttpMethod.Get, admin, JiraHttpHost.Url(team));
        string[] names = ["configured", "siteHost", "projectKey", "boardId", "pointsFieldId", "sprintFieldId", "membersCanEdit", "status", "tokenLast4", "emailMasked"];
        Assert.Equal(
            ["true", "acme.atlassian.net", "RS", "7", "customfield_10016", "customfield_10020", "false", "ok", "1234", "b•••@acme.test"],
            names.Select(name => view[name]!.ToString()));
        Assert.NotNull(view["lastCheckedAt"]);

        var seen = await host.JsonAsync(HttpMethod.Get, member, JiraHttpHost.Url(team));
        Assert.Null(seen["tokenLast4"]);
        Assert.Null(seen["emailMasked"]);
        var (status, _) = await host.ProblemAsync(HttpMethod.Put, member, JiraHttpHost.Url(team), Save(host));
        Assert.Equal(403, status);

        // Saving without the token keeps the stored box.
        var box = (byte[])(await host.RowsAsync("select credentials_box from team_jira_config where team_id = $1", team.Value))[0][0]!;
        await host.JsonAsync(HttpMethod.Put, admin, JiraHttpHost.Url(team), new { siteHost = JiraHttpHost.SiteHost, projectKey = "RS", membersCanEdit = true });
        Assert.Equal(box, (byte[])(await host.RowsAsync("select credentials_box from team_jira_config where team_id = $1", team.Value))[0][0]!);
        Assert.Equal(["jira.config.set acme.atlassian.net", "jira.config.set acme.atlassian.net"], await host.AuditAsync(team));
    }

    [Fact]
    public async Task Changing_the_site_needs_the_token_again() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (admin, _, team) = await TeamAsync(host);
        await host.ConfigureAsync(team);

        var (status, problem) = await host.ProblemAsync(HttpMethod.Put, admin, JiraHttpHost.Url(team), new { siteHost = "other.atlassian.net" });
        Assert.Equal((422, "required"), (status, (string)problem["fields"]!["token"]!));
        (status, problem) = await host.ProblemAsync(HttpMethod.Put, admin, JiraHttpHost.Url(team), new { siteHost = "atlassian.net.evil.com", token = "x-token" });
        Assert.Equal((422, "not_allowed"), (status, (string)problem["fields"]!["siteHost"]!));
        Assert.Equal(JiraHttpHost.SiteHost, (string)(await host.RowsAsync("select site_host from team_jira_config where team_id = $1", team.Value))[0][0]!);

        var moved = await host.JsonAsync(HttpMethod.Put, admin, JiraHttpHost.Url(team), new { siteHost = "https://Other.atlassian.net/", token = host.Jira.Token });
        Assert.Equal("other.atlassian.net", (string)moved["siteHost"]!);
        Assert.Contains(host.Jira.Requests, r => r.Host == "other.atlassian.net" && r.Path == "/rest/api/3/myself");
    }

    [Fact]
    public async Task An_imported_token_keeps_the_rotate_banner_until_a_new_token_is_saved() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (admin, member, team) = await TeamAsync(host);
        await host.ConfigureAsync(team, status: "rotate_required");

        Assert.Equal("rotate_required", (string)(await host.JsonAsync(HttpMethod.Get, member, JiraHttpHost.Url(team)))["status"]!);
        var kept = await host.JsonAsync(HttpMethod.Put, admin, JiraHttpHost.Url(team), new { siteHost = JiraHttpHost.SiteHost, projectKey = "RS" });
        Assert.Equal("rotate_required", (string)kept["status"]!);
        var test = await host.JsonAsync(HttpMethod.Post, admin, JiraHttpHost.Url(team, "/test"), "{}");
        Assert.True((bool)test["ok"]!);
        Assert.Equal("rotate_required", (string)(await host.JsonAsync(HttpMethod.Get, admin, JiraHttpHost.Url(team)))["status"]!);

        var rotated = await host.JsonAsync(HttpMethod.Put, admin, JiraHttpHost.Url(team), new { siteHost = JiraHttpHost.SiteHost, projectKey = "RS", token = host.Jira.Token });
        Assert.Equal("ok", (string)rotated["status"]!);
    }

    [Fact]
    public async Task A_refused_token_is_not_saved_and_names_the_classic_token() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (admin, _, team) = await TeamAsync(host);

        var (status, problem) = await host.ProblemAsync(HttpMethod.Put, admin, JiraHttpHost.Url(team),
            new { siteHost = JiraHttpHost.SiteHost, email = host.Jira.Email, token = "scoped-token-9999" });

        Assert.Equal((502, "jira_auth_failed"), (status, (string)problem["code"]!));
        Assert.Contains("classic (unscoped)", (string)problem["fields"]!["token"]!, StringComparison.Ordinal);
        Assert.Empty(await host.RowsAsync("select 1 from team_jira_config where team_id = $1", team.Value));
    }

    [Fact]
    public async Task The_connection_test_reports_the_account_and_the_discovered_fields() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (admin, member, team) = await TeamAsync(host);
        await host.ConfigureAsync(team, status: "auth_failed");

        var test = await host.JsonAsync(HttpMethod.Post, admin, JiraHttpHost.Url(team, "/test"), "{}");

        Assert.Equal("Retroscope Bot", (string)test["accountName"]!);
        Assert.Equal("customfield_10016", (string)test["discovered"]!["pointsFieldId"]!);
        Assert.Equal(7, (long)test["discovered"]!["boardId"]!);
        Assert.Equal("ok", (string)(await host.JsonAsync(HttpMethod.Get, admin, JiraHttpHost.Url(team)))["status"]!);
        var (status, _) = await host.ProblemAsync(HttpMethod.Post, member, JiraHttpHost.Url(team, "/test"), "{}");
        Assert.Equal(403, status);
    }

    [Fact]
    public async Task A_save_without_a_chosen_field_stores_the_boards_estimation_field_and_the_test_reports_it() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (admin, _, team) = await TeamAsync(host);
        host.Jira.BoardEstimation = FakeJiraServer.FieldEstimation(BoardPointsField, "Story Points");

        var saved = await host.JsonAsync(HttpMethod.Put, admin, JiraHttpHost.Url(team), Save(host, boardId: 7));
        var test = await host.JsonAsync(HttpMethod.Post, admin, JiraHttpHost.Url(team, "/test"), "{}");

        Assert.Equal(BoardPointsField, (string)saved["pointsFieldId"]!);
        var stored = Assert.Single(await host.RowsAsync("select points_field_id, points_field_overridden from team_jira_config where team_id = $1", team.Value));
        Assert.Equal((BoardPointsField, false), ((string?)stored[0], (bool)stored[1]!));
        Assert.Equal(BoardPointsField, (string)test["discovered"]!["pointsFieldId"]!);
    }

    [Theory]
    [InlineData(200, """{"estimation":{"type":"issueCount"}}""")]
    [InlineData(404, "{}")]
    public async Task A_save_whose_board_has_no_estimation_field_stores_the_discovered_one(int status, string body) {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (admin, _, team) = await TeamAsync(host);
        host.Jira.Script("GET", "/rest/agile/1.0/board/7/configuration", new FakeJiraResponse(status, body));

        var saved = await host.JsonAsync(HttpMethod.Put, admin, JiraHttpHost.Url(team), Save(host, boardId: 7));

        Assert.Equal("customfield_10016", (string)saved["pointsFieldId"]!);
    }

    [Fact]
    public async Task A_chosen_points_field_is_stored_over_the_boards() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (admin, _, team) = await TeamAsync(host);
        host.Jira.BoardEstimation = FakeJiraServer.FieldEstimation(BoardPointsField, "Story Points");

        await host.JsonAsync(HttpMethod.Put, admin, JiraHttpHost.Url(team), new {
            siteHost = JiraHttpHost.SiteHost, email = host.Jira.Email, token = host.Jira.Token, projectKey = "RS", boardId = 7, pointsFieldId = "customfield_10016",
        });

        var stored = Assert.Single(await host.RowsAsync("select points_field_id, points_field_overridden from team_jira_config where team_id = $1", team.Value));
        Assert.Equal(("customfield_10016", true), ((string?)stored[0], (bool)stored[1]!));
    }

    [Fact]
    public async Task Settings_changes_reach_the_team_live_poker_rooms() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (admin, _, team) = await TeamAsync(host);
        await host.ConfigureAsync(team);
        var room = await host.SessionAsync(admin, team, ["RS-1"]);
        await using var tab = await host.JoinAsync(admin, room);
        Assert.True(await host.QueryAsync(room, state => state.Jira.MembersCanEdit));

        await host.JsonAsync(HttpMethod.Put, admin, JiraHttpHost.Url(team), new { siteHost = JiraHttpHost.SiteHost, projectKey = "RS", ticketPrefix = "PAY", membersCanEdit = false });
        Assert.Equal((true, "PAY", false), await host.QueryAsync(room, state => (state.Jira.Configured, state.Jira.TicketPrefix, state.Jira.MembersCanEdit)));

        await host.JsonAsync(HttpMethod.Delete, admin, JiraHttpHost.Url(team), expected: 204);
        Assert.False(await host.QueryAsync(room, state => state.Jira.Configured));
        Assert.False((bool)(await host.JsonAsync(HttpMethod.Get, admin, JiraHttpHost.Url(team)))["configured"]!);
        Assert.Contains("jira.config.delete", await host.AuditAsync(team));
    }

    private static async Task<(Principal Admin, Principal Member, TeamId Team)> TeamAsync(JiraHttpHost host) {
        var admin = await host.UserAsync("Ada");
        var member = await host.UserAsync("Grace");
        var team = await host.TeamAsync((admin, "admin"), (member, "member"));
        host.AddBoard();
        return (admin, member, team);
    }

    private static object Save(JiraHttpHost host, long? boardId = null, bool membersCanEdit = true) => new {
        siteHost = JiraHttpHost.SiteHost,
        email = host.Jira.Email,
        token = host.Jira.Token,
        projectKey = "RS",
        boardId,
        membersCanEdit,
    };
}
