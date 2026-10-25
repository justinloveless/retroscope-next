using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Fakes.FakeJira;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Realtime.Rooms;

namespace Retroscope.Integration.Tests.JiraHttp;

/// <summary>The real <c>PokerJiraPort</c> behind live poker rooms: shared metadata, refreshes after writes, submits.</summary>
[Trait("Category", "JiraHttp")]
[Collection(SharedPostgres.Name)]
public sealed class PokerJiraTests(PostgresFixture postgres) {
    private const string BoardPointsField = "customfield_10028";

    [Fact]
    public async Task Ten_clients_cost_one_metadata_fetch_and_a_PATCH_refreshes_the_title_in_the_room() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        var members = new List<Principal> { ada };
        for (var i = 1; i < 10; i++) {
            var member = await host.UserAsync($"Member {i}");
            await host.ExecuteAsync("insert into team_members (team_id, user_id, role) values ($1, $2, 'member')", team.Value, member.Id.Value);
            members.Add(member);
        }
        host.Jira.AddIssue("RS-1", "Pay with a card");
        host.Jira.AddIssue("RS-2", "Refunds");
        var room = await host.SessionAsync(ada, team, ["RS-1", "RS-2"]);
        var tabs = new List<JiraSocket>();
        try {
            foreach (var member in members) {
                tabs.Add(await host.JoinAsync(member, room));
            }
            var from = host.Jira.Requests.Count;

            await host.AdvanceAsync(room, PokerJiraLoop.Coalesce);
            foreach (var tab in tabs) {
                await tab.WaitForAsync<XFrame>(x => x.D is JiraMeta { Key: "RS-2", Summary: "Refunds" });
                await tab.WaitForAsync<XFrame>(x => x.D is JiraMeta { Key: "RS-1", Summary: "Pay with a card" });
            }
            Assert.Single(host.JiraRequestsSince(from), r => r.Path == "/rest/api/3/search/jql");

            // The panel's own metadata request is served from the same cache.
            var before = host.Jira.Requests.Count;
            await host.JsonAsync(HttpMethod.Post, ada, JiraHttpHost.Url(team, "/meta"), """{"keys":["RS-1","RS-2"]}""");
            Assert.Equal(before, host.Jira.Requests.Count);

            var marks = tabs.Select(tab => tab.Count).ToList();
            await host.JsonAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-1"), new { summary = "Pay with a saved card" });
            await host.AdvanceAsync(room, PokerJiraLoop.Coalesce);
            for (var i = 0; i < tabs.Count; i++) {
                await tabs[i].WaitForAsync<XFrame>(x => x.D is JiraMeta { Key: "RS-1", Summary: "Pay with a saved card" }, marks[i]);
            }
        } finally {
            foreach (var tab in tabs) {
                await tab.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_submit_is_pending_for_everyone_until_Jira_answers_and_a_second_submit_meanwhile_is_refused() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        var grace = await host.UserAsync("Grace");
        await host.ExecuteAsync("insert into team_members (team_id, user_id, role) values ($1, $2, 'member')", team.Value, grace.Id.Value);
        host.Jira.AddIssue("RS-1", "Pay with a card");
        var room = await host.SessionAsync(ada, team, ["RS-1"]);
        var round = (await host.RoundsAsync(room))[0];
        await host.ExecuteAsync("update poker_rounds set state = 'revealed', revealed_at = now() where id = $1", round.Value);
        await using var first = await host.JoinAsync(ada, room);
        await using var second = await host.JoinAsync(grace, room);
        var gate = new TaskCompletionSource();
        host.Jira.Gate = gate.Task;

        Assert.IsType<AckFrame>(await first.CommandAsync(room, new PokerJiraSubmitPoints(round, 2.5m)));
        foreach (var tab in new[] { first, second }) {
            await tab.WaitForEventAsync<PokerJiraPoints>(e => e is { Status: JiraSubmitStatus.Pending, Key: "RS-1", Points: 2.5m });
        }
        var refused = Assert.IsType<NackFrame>(await second.CommandAsync(room, new PokerJiraSubmitPoints(round, 3)));
        Assert.Equal(NackCode.JiraPending, refused.Code);

        gate.SetResult();
        foreach (var tab in new[] { first, second }) {
            await tab.WaitForEventAsync<PokerJiraPoints>(e => e is { Status: JiraSubmitStatus.Ok, Points: 2.5m });
        }
        Assert.Equal(2.5m, (decimal)host.Jira.Issues["RS-1"]["fields"]![JiraHttpHost.PointsField]!);
        var stored = await host.RowsAsync("select jira_points from poker_rounds where id = $1", round.Value);
        Assert.Equal(2.5m, (decimal)stored[0][0]!);
        Assert.Equal(["jira.points RS-1"], await host.AuditAsync(team));
    }

    [Fact]
    public async Task A_submit_writes_the_boards_estimation_field_when_the_site_has_two_story_points_fields() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        host.Jira.BoardEstimation = FakeJiraServer.FieldEstimation(BoardPointsField, "Story Points");
        host.Jira.EditableFields.Add(BoardPointsField);
        host.Jira.AddIssue("RS-1", "Pay with a card");
        var room = await host.SessionAsync(ada, team, ["RS-1"]);
        var round = (await host.RoundsAsync(room))[0];
        await host.ExecuteAsync("update poker_rounds set state = 'revealed', revealed_at = now() where id = $1", round.Value);
        await using var tab = await host.JoinAsync(ada, room);

        Assert.IsType<AckFrame>(await tab.CommandAsync(room, new PokerJiraSubmitPoints(round, 8)));
        await tab.WaitForEventAsync<PokerJiraPoints>(e => e is { Status: JiraSubmitStatus.Ok, Points: 8m });

        Assert.Equal(8m, (decimal?)host.Jira.Issues["RS-1"]["fields"]![BoardPointsField]);
        Assert.Null(host.Jira.Issues["RS-1"]["fields"]![JiraHttpHost.PointsField]);
    }

    [Fact]
    public async Task A_submit_Jira_refuses_is_failed_for_everyone_with_the_code() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        var grace = await host.UserAsync("Grace");
        await host.ExecuteAsync("insert into team_members (team_id, user_id, role) values ($1, $2, 'member')", team.Value, grace.Id.Value);
        host.Jira.AddIssue("RS-1", "Pay with a card");
        host.Jira.Script("PUT", "/rest/api/3/issue/RS-1", new FakeJiraResponse(400, """{"errorMessages":[],"errors":{"customfield_10016":"Field cannot be set."}}"""));
        var room = await host.SessionAsync(ada, team, ["RS-1"]);
        var round = (await host.RoundsAsync(room))[0];
        await host.ExecuteAsync("update poker_rounds set state = 'revealed', revealed_at = now() where id = $1", round.Value);
        await using var first = await host.JoinAsync(ada, room);
        await using var second = await host.JoinAsync(grace, room);

        Assert.IsType<AckFrame>(await first.CommandAsync(room, new PokerJiraSubmitPoints(round, 5)));

        foreach (var tab in new[] { first, second }) {
            await tab.WaitForEventAsync<PokerJiraPoints>(e => e is { Status: JiraSubmitStatus.Failed, Code: "jira_validation" });
        }
        Assert.Empty(await host.AuditAsync(team));
    }
}
