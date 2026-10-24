using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Retroscope.Core.Ids;
using Retroscope.Fakes.FakeJira;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Jira;

namespace Retroscope.Integration.Tests.JiraHttp;

/// <summary>The retro board's sprint report (UX17) through the real proxy and FakeJira.</summary>
[Trait("Category", "JiraHttp")]
[Collection(SharedPostgres.Name)]
public sealed partial class SprintReportTests(PostgresFixture postgres) {
    private const string Report = "/sprint-report";

    [Fact]
    public async Task A_closed_sprint_reports_what_was_done_done_late_and_left_and_where_it_went() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        SeedSprints(host);

        var report = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, $"{Report}?sprint=231"));

        Assert.Equal(JsonNode.Parse(ClosedSprintReport)!.ToJsonString(), report.ToJsonString());
        Assert.DoesNotContain(host.Jira.Requests, r => DeprecatedAgile().IsMatch(r.Path));
        var search = JsonNode.Parse(Assert.Single(host.Jira.Requests, r => r.Path == "/rest/api/3/search/jql").Body)!;
        Assert.Equal("sprint = 231 AND issuetype not in (Epic, subtaskIssueTypes()) ORDER BY Rank ASC", (string)search["jql"]!);
        Assert.Contains("resolutiondate", search["fields"]!.AsArray().Select(f => (string)f!));
        Assert.Contains("statuscategorychangedate", search["fields"]!.AsArray().Select(f => (string)f!));
    }

    [Fact]
    public async Task Without_a_sprint_the_most_recently_closed_one_is_reported() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        SeedSprints(host);

        var report = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, Report));

        Assert.Equal(231, (long)report["report"]!["sprintId"]!);
        Assert.Equal(["232 active", "231 closed", "230 closed"], report["sprints"]!.AsArray().Select(s => $"{s!["id"]} {s["state"]}"));
    }

    [Fact]
    public async Task An_active_sprint_has_no_late_work_and_no_destinations() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        SeedSprints(host);

        var report = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, $"{Report}?sprint=232"));

        var groups = report["report"]!["groups"]!.AsArray();
        Assert.Equal(["done RS-3", "notDone RS-4"], Rows(groups).Select(r => $"{r.Outcome} {r.Row["key"]}"));
        Assert.Equal(0, (int)groups[1]!["count"]!);
        Assert.True((bool)Rows(groups).Single(r => (string)r.Row["key"]! == "RS-3").Row["carriedIn"]!);
        Assert.All(Rows(groups), r => Assert.Null(r.Row["next"]));
    }

    [Fact]
    public async Task Only_the_pickers_sprints_can_be_reported_and_nothing_else_is_searched() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        SeedSprints(host);
        host.Jira.AddSprint(240, "Their sprint", "closed", "2026-10-05T09:00:00.000Z", completeDate: "2026-10-19T09:00:00.000Z", originBoardId: 8);
        host.Jira.Script("GET", "/rest/agile/1.0/board/7/sprint", new FakeJiraResponse(200, """
            {"startAt":0,"maxResults":50,"isLast":true,"values":[{"id":250,"name":"Shared","state":"active","originBoardId":8}]}
            """));

        foreach (var sprint in new[] { 233, 240, 250, 999 }) {
            var (status, problem) = await host.ProblemAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, $"{Report}?sprint={sprint}"));
            Assert.Equal((404, "jira_not_found"), (status, (string)problem["code"]!));
        }
        Assert.DoesNotContain(host.Jira.Requests, r => r.Path == "/rest/api/3/search/jql");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("")]
    [InlineData("99999999999999999999")]
    public async Task A_sprint_that_is_not_a_positive_id_is_refused(string sprint) {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);

        var (status, problem) = await host.ProblemAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, $"{Report}?sprint={sprint}"));

        Assert.Equal((400, "invalid", "invalid"), (status, (string)problem["code"]!, (string)problem["fields"]!["sprint"]!));
        Assert.Empty(host.Jira.Requests);
    }

    [Fact]
    public async Task Unconfigured_teams_and_outsiders_are_refused() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var ada = await host.UserAsync("Ada");
        var team = await host.TeamAsync((ada, "member"));
        var outsider = await host.UserAsync("Mallory");

        var (status, problem) = await host.ProblemAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, Report));
        Assert.Equal((409, "jira_not_configured"), (status, (string)problem["code"]!));

        await host.ConfigureAsync(team);
        (status, problem) = await host.ProblemAsync(HttpMethod.Get, outsider, JiraHttpHost.Url(team, Report));
        Assert.Equal((404, "not_found"), (status, (string)problem["code"]!));
        (status, _) = await host.ProblemAsync(HttpMethod.Get, outsider, JiraHttpHost.Url(new TeamId(Guid.CreateVersion7()), Report));
        Assert.Equal(404, status);
        Assert.Empty(host.Jira.Requests);
    }

    [Fact]
    public async Task A_team_without_a_board_or_sprints_gets_an_empty_report() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);

        var noSprints = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, Report));
        Assert.Equal("""{"sprints":[],"empty":"no_sprints","report":null}""", noSprints.ToJsonString());

        await host.ExecuteAsync("update team_jira_config set board_id = null where team_id = $1", team.Value);
        host.App.Services.GetRequiredService<Credentials>().Invalidate(team);
        var before = host.Jira.Requests.Count;
        var noBoard = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, Report));

        Assert.Equal("""{"sprints":[],"empty":"no_board","report":null}""", noBoard.ToJsonString());
        Assert.Equal(before, host.Jira.Requests.Count);
    }

    [Fact]
    public async Task Without_a_points_field_no_points_are_shown() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var ada = await host.UserAsync("Ada");
        var team = await host.TeamAsync((ada, "member"));
        await host.ConfigureAsync(team, pointsField: null);
        host.AddBoard();
        SeedSprints(host);

        var report = (await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, $"{Report}?sprint=231")))["report"]!;

        Assert.False((bool)report["pointsKnown"]!);
        Assert.All(report["groups"]!.AsArray(), g => Assert.Null(g!["points"]));
        Assert.All(Rows(report["groups"]!.AsArray()), r => Assert.Null(r.Row["points"]));
    }

    [Fact]
    public async Task A_closed_sprint_is_cached_for_15_minutes_and_an_active_one_for_60_seconds() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        var grace = await host.UserAsync("Grace");
        await host.ExecuteAsync("insert into team_members (team_id, user_id, role) values ($1, $2, 'member')", team.Value, grace.Id.Value);
        SeedSprints(host);
        var closed = JiraHttpHost.Url(team, $"{Report}?sprint=231");
        var active = JiraHttpHost.Url(team, $"{Report}?sprint=232");

        var first = await host.JsonAsync(HttpMethod.Get, ada, closed);
        await host.JsonAsync(HttpMethod.Get, ada, active);
        var before = host.Jira.Requests.Count;
        var shared = await host.JsonAsync(HttpMethod.Get, grace, closed);
        await host.JsonAsync(HttpMethod.Get, grace, active);
        Assert.Equal(before, host.Jira.Requests.Count);
        Assert.Equal(first.ToJsonString(), shared.ToJsonString());

        host.Time.Advance(TimeSpan.FromSeconds(61));
        await host.JsonAsync(HttpMethod.Get, ada, closed);
        Assert.Equal(0, Searches(host, before, 231));
        await host.JsonAsync(HttpMethod.Get, ada, active);
        Assert.Equal(1, Searches(host, before, 232));

        host.Time.Advance(TimeSpan.FromMinutes(13));
        await host.JsonAsync(HttpMethod.Get, ada, closed);
        Assert.Equal(0, Searches(host, before, 231));
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await host.JsonAsync(HttpMethod.Get, ada, closed);
        Assert.Equal(1, Searches(host, before, 231));
    }

    [Fact]
    public async Task A_write_to_a_listed_issue_or_a_settings_change_drops_the_cached_report() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host, role: "admin");
        SeedSprints(host);
        var url = JiraHttpHost.Url(team, $"{Report}?sprint=231");
        await host.JsonAsync(HttpMethod.Get, ada, url);

        await host.JsonAsync(HttpMethod.Put, ada, JiraHttpHost.Url(team, "/issues/RS-1/points"), new { points = 13 });
        var before = host.Jira.Requests.Count;
        var after = await host.JsonAsync(HttpMethod.Get, ada, url);
        Assert.Equal(1, Searches(host, before, 231));
        Assert.Equal(18m, (decimal)after["report"]!["groups"]![0]!["points"]!);

        await host.JsonAsync(HttpMethod.Put, ada, JiraHttpHost.Url(team), new { siteHost = JiraHttpHost.SiteHost, projectKey = "RS", membersCanEdit = true });
        before = host.Jira.Requests.Count;
        await host.JsonAsync(HttpMethod.Get, ada, url);
        Assert.Equal(1, Searches(host, before, 231));
    }

    [Fact]
    public async Task Jira_rate_limiting_is_passed_on_with_its_wait() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        SeedSprints(host);
        host.Jira.Script("POST", "/rest/api/3/search/jql", new FakeJiraResponse(429, "{}", new Dictionary<string, string> { ["Retry-After"] = "60" }));

        using var response = await host.GetAsync(ada, JiraHttpHost.Url(team, $"{Report}?sprint=231"));
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(JiraHttpHost.Token))!;

        Assert.Equal((503, "jira_rate_limited", 60), ((int)response.StatusCode, (string)problem["code"]!, (int)problem["retryAfter"]!));
        Assert.Equal("60", response.Headers.RetryAfter?.ToString());
    }

    [Fact]
    public async Task A_sprint_beyond_the_search_cap_is_reported_truncated() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        host.Jira.AddSprint(231, "Sprint 42", "closed", "2026-10-05T09:00:00.000Z", "2026-10-19T09:00:00.000Z", "2026-10-19T16:00:00.000Z");
        for (var i = 1; i <= 1001; i++) {
            host.Jira.AddIssue($"RS-{i}", $"Issue {i}", sprintId: 231);
        }

        var report = (await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, Report)))["report"]!;

        Assert.True((bool)report["truncated"]!);
        Assert.Equal(1000, report["groups"]!.AsArray().Sum(g => (int)g!["count"]!));
        Assert.Equal(10, host.Jira.Requests.Count(r => r.Path == "/rest/api/3/search/jql"));
    }

    private const string ClosedSprintReport = """
        {"sprints":[
          {"id":232,"name":"Sprint 43","state":"active","startDate":"2026-10-19T09:00:00.000Z","endDate":"2026-11-02T09:00:00.000Z","completeDate":null},
          {"id":231,"name":"Sprint 42","state":"closed","startDate":"2026-10-05T09:00:00.000Z","endDate":"2026-10-19T09:00:00.000Z","completeDate":"2026-10-19T16:00:00.000Z"},
          {"id":230,"name":"Sprint 41","state":"closed","startDate":"2026-09-21T09:00:00.000Z","endDate":"2026-10-05T09:00:00.000Z","completeDate":"2026-10-05T15:00:00.000Z"}],
         "empty":null,
         "report":{"sprintId":231,"goal":"Ship checkout polish","pointsKnown":true,"truncated":false,"groups":[
           {"outcome":"done","count":2,"points":8,"issues":[
             {"key":"RS-1","summary":"Card hover state","issueType":"Story","status":"Done","statusCategory":"done","assignee":"Ada Lovelace","points":3,"carriedIn":false,"next":null},
             {"key":"RS-2","summary":"Saved cards","issueType":"Story","status":"Done","statusCategory":"done","assignee":null,"points":5,"carriedIn":true,"next":null}]},
           {"outcome":"doneAfter","count":1,"points":2,"issues":[
             {"key":"RS-3","summary":"Refund flow","issueType":"Bug","status":"Done","statusCategory":"done","assignee":null,"points":2,"carriedIn":false,"next":"Sprint 43"}]},
           {"outcome":"notDone","count":2,"points":8,"issues":[
             {"key":"RS-4","summary":"Receipt email","issueType":"Story","status":"In Progress","statusCategory":"indeterminate","assignee":null,"points":8,"carriedIn":false,"next":"Sprint 43"},
             {"key":"RS-5","summary":"Gift wrap option","issueType":"Story","status":"To Do","statusCategory":"new","assignee":null,"points":null,"carriedIn":false,"next":null}]}]}}
        """;

    // Sprint 42 (231) closed on 19 October: RS-1 was done in it, RS-2 was carried in from Sprint 41 and done, RS-3 was
    // done after the close in Sprint 43, RS-4 moved on to Sprint 43 unfinished and RS-5 went back to the backlog.
    private static void SeedSprints(JiraHttpHost host) {
        var jira = host.Jira;
        jira.AddSprint(230, "Sprint 41", "closed", "2026-09-21T09:00:00.000Z", "2026-10-05T09:00:00.000Z", "2026-10-05T15:00:00.000Z");
        jira.AddSprint(231, "Sprint 42", "closed", "2026-10-05T09:00:00.000Z", "2026-10-19T09:00:00.000Z", "2026-10-19T16:00:00.000Z", "Ship checkout polish");
        jira.AddSprint(232, "Sprint 43", "active", "2026-10-19T09:00:00.000Z", "2026-11-02T09:00:00.000Z");
        jira.AddSprint(233, "Sprint 44", "future", null);

        jira.AddIssue("RS-1", "Card hover state", sprintId: 231, points: 3, status: "Done");
        jira.Issues["RS-1"]["fields"]!["assignee"] = new JsonObject { ["accountId"] = "acc-1", ["displayName"] = "Ada Lovelace" };
        jira.Resolve("RS-1", "2026-10-15T11:00:00.000+0000");
        jira.AddIssue("RS-2", "Saved cards", points: 5, status: "Done");
        jira.SetSprints("RS-2", 230, 231);
        jira.Resolve("RS-2", "2026-10-18T11:00:00.000+0000");
        jira.AddIssue("RS-3", "Refund flow", points: 2, status: "Done", issueType: "Bug");
        jira.SetSprints("RS-3", 231, 232);
        jira.Resolve("RS-3", "2026-10-20T10:00:00.000+0000");
        jira.AddIssue("RS-4", "Receipt email", points: 8, status: "In Progress");
        jira.SetSprints("RS-4", 231, 232);
        jira.AddIssue("RS-5", "Gift wrap option", sprintId: 231);
        jira.AddIssue("RS-6", "Wrap paper colours", sprintId: 231, issueType: "Sub-task");
        jira.AddIssue("RS-7", "Checkout", sprintId: 231, issueType: "Epic");
        jira.AddIssue("RS-8", "Older work", sprintId: 230, status: "Done");
    }

    private static IEnumerable<(string Outcome, JsonNode Row)> Rows(JsonArray groups) =>
        groups.SelectMany(g => g!["issues"]!.AsArray().Select(r => ((string)g["outcome"]!, r!)));

    private static int Searches(JiraHttpHost host, int since, long sprint) =>
        host.JiraRequestsSince(since).Count(r => r.Path == "/rest/api/3/search/jql" && r.Body.Contains($"sprint = {sprint} ", StringComparison.Ordinal));

    [GeneratedRegex(@"/rest/agile/1\.0/(sprint/[0-9]+/issue|board/[0-9]+/backlog)$")]
    private static partial Regex DeprecatedAgile();
}
