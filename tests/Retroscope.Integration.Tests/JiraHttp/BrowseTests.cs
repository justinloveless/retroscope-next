using System.Text.Json.Nodes;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Fakes.FakeJira;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.JiraHttp;

/// <summary>The browse panel and issue metadata through the real proxy and FakeJira.</summary>
[Trait("Category", "JiraHttp")]
[Collection(SharedPostgres.Name)]
public sealed class BrowseTests(PostgresFixture postgres) {
    [Fact]
    public async Task Board_browse_ranks_sprints_by_start_date_then_natural_name_with_the_backlog_last() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await TeamAsync(host);
        host.Jira.AddSprint(201, "Sprint 10", "future", null);
        host.Jira.AddSprint(202, "Sprint 9", "future", null);
        host.Jira.AddSprint(203, "Sprint 11", "active", "2026-10-12T09:00:00.000+0000");
        host.Jira.AddIssue("RS-1", "Ten", sprintId: 201);
        host.Jira.AddIssue("RS-2", "Nine", sprintId: 202);
        host.Jira.AddIssue("RS-3", "Eleven a", sprintId: 203);
        host.Jira.AddIssue("RS-4", "Backlog a");
        host.Jira.AddIssue("RS-5", "Eleven b", sprintId: 203, points: 3);
        host.Jira.AddIssue("RS-6", "Backlog b");
        // Issues in a sprint of another board are found by the search the ranked browse makes after the board's sprints.
        host.Jira.Script("POST", "/rest/api/3/search/jql", new FakeJiraResponse(200, """
            {"issues":[{"id":"9","key":"RS-9","fields":{"summary":"Refined","customfield_10020":[{"id":300,"name":"Refinement","state":"closed"}]}}],"isLast":true}
            """));

        var browse = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/browse"));

        Assert.False((bool)browse["partial"]!);
        var buckets = browse["buckets"]!.AsArray();
        Assert.Equal(["Sprint 11", "Refinement", "Sprint 9", "Sprint 10", "Backlog"], buckets.Select(b => (string)b!["name"]!));
        Assert.Equal(["RS-3", "RS-5"], Keys(buckets[0]!));
        Assert.Equal(["203", "sprint", "active"], [(string)buckets[0]!["id"]!, (string)buckets[0]!["kind"]!, (string)buckets[0]!["state"]!]);
        Assert.Equal(["RS-4", "RS-6"], Keys(buckets[^1]!));
        Assert.Equal("backlog", (string)buckets[^1]!["kind"]!);
        var row = buckets[0]!["issues"]![1]!;
        Assert.Equal(3m, (decimal)row["points"]!);
        Assert.Equal("Story", (string)row["issueType"]!);
        Assert.Null(row["roundState"]);

        var sprintCalls = host.Jira.Requests.Where(r => r.Path.StartsWith("/rest/agile/1.0/sprint/", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, sprintCalls.Count);
        Assert.All(sprintCalls, r => Assert.Contains("jql=", r.Query, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rows_carry_pointing_and_pointed_and_the_hide_filter_is_ignored_while_searching() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await TeamAsync(host);
        host.Jira.AddSprint(203, "Sprint 11", "active", "2026-10-12T09:00:00.000+0000");
        host.Jira.AddIssue("RS-1", "Active round", sprintId: 203);
        host.Jira.AddIssue("RS-2", "Finished round", sprintId: 203);
        host.Jira.AddIssue("RS-3", "Only previewed", sprintId: 203);
        host.Jira.AddIssue("RS-4", "Free", sprintId: 203);
        host.Jira.AddIssue("RS-7", "Only on the session");
        var room = await host.SessionAsync(ada, team, ["RS-1", "RS-2", "RS-3", "RS-7"], inactive: ["RS-2"], pending: ["RS-3"]);
        // The backlog page does not list RS-7, so it shows only because the session carries it.
        host.Jira.Script("GET", "/rest/agile/1.0/board/7/backlog", new FakeJiraResponse(200, """{"startAt":0,"maxResults":100,"total":0,"issues":[]}"""));
        host.Jira.Script("POST", "/rest/api/3/search/jql", new FakeJiraResponse(200, """{"issues":[],"isLast":true}"""));
        var session = $"session={room.Id}";

        var all = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, $"/browse?{session}"));
        var states = Rows(all).ToDictionary(r => (string)r["key"]!, r => (string?)r["roundState"]);
        Assert.Equal("pointing", states["RS-1"]);
        Assert.Equal("pointed", states["RS-2"]);
        Assert.Null(states["RS-3"]);
        Assert.Null(states["RS-4"]);
        Assert.Equal("pointing", states["RS-7"]);

        var hidden = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, $"/browse?{session}&hidePointedOrPointing=true"));
        Assert.Equal(["RS-3", "RS-4"], Rows(hidden).Select(r => (string)r["key"]!).Order(StringComparer.Ordinal));

        host.Jira.Script("POST", "/rest/api/3/search/jql", new FakeJiraResponse(200, """
            {"issues":[{"id":"1","key":"RS-1","fields":{"summary":"Active round"}},{"id":"2","key":"RS-2","fields":{"summary":"Finished round"}}],"isLast":true}
            """));
        var searched = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, $"/browse?{session}&hidePointedOrPointing=true&q=round"));
        Assert.Equal(
            ["RS-1 pointing", "RS-2 pointed"],
            Rows(searched).Select(r => $"{r["key"]} {r["roundState"]}"));
    }

    [Fact]
    public async Task Browse_results_are_shared_by_members_for_30_seconds_and_dropped_by_a_write() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await TeamAsync(host);
        var grace = await host.UserAsync("Grace");
        await host.ExecuteAsync("insert into team_members (team_id, user_id, role) values ($1, $2, 'member')", team.Value, grace.Id.Value);
        host.Jira.AddSprint(203, "Sprint 11", "active", "2026-10-12T09:00:00.000+0000");
        host.Jira.AddIssue("RS-1", "Pay", sprintId: 203);
        host.Jira.AddIssue("RS-2", "Refund");

        var first = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/browse?status=not_done"));
        var before = host.Jira.Requests.Count;
        var second = await host.JsonAsync(HttpMethod.Get, grace, JiraHttpHost.Url(team, "/browse?status=not_done"));

        Assert.Equal(before, host.Jira.Requests.Count);
        Assert.Equal(first.ToJsonString(), second.ToJsonString());

        host.Time.Advance(TimeSpan.FromSeconds(31));
        await host.JsonAsync(HttpMethod.Get, grace, JiraHttpHost.Url(team, "/browse?status=not_done"));
        Assert.True(host.Jira.Requests.Count > before);

        await host.JsonAsync(HttpMethod.Put, ada, JiraHttpHost.Url(team, "/issues/RS-2/points"), new { points = 5 });
        before = host.Jira.Requests.Count;
        var after = await host.JsonAsync(HttpMethod.Get, grace, JiraHttpHost.Url(team, "/browse?status=not_done"));
        Assert.True(host.Jira.Requests.Count > before);
        Assert.Equal(5m, (decimal)Rows(after).Single(r => (string)r["key"]! == "RS-2")["points"]!);
    }

    [Fact]
    public async Task Filters_reach_Jira_as_JQL_on_the_configured_points_field() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await TeamAsync(host);
        host.Jira.AddIssue("RS-1", "Pay");

        await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/browse?scope=open&status=indeterminate&points=8"));
        await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/browse?q=pay%22%20OR%20x&scope=all"));

        var searches = host.Jira.Requests.Where(r => r.Path == "/rest/api/3/search/jql").Select(r => (string)JsonNode.Parse(r.Body)!["jql"]!).ToList();
        Assert.Contains(
            "project = \"RS\" AND issuetype not in (Epic, subtaskIssueTypes()) AND statusCategory = \"In Progress\" AND cf[10016] = 8 AND (sprint in openSprints() OR sprint in futureSprints() OR sprint is EMPTY) ORDER BY Rank ASC",
            searches);
        Assert.Contains("project = \"RS\" AND issuetype not in (Epic, subtaskIssueTypes()) AND summary ~ \"pay\\\" OR x\" ORDER BY Rank ASC", searches);
        Assert.DoesNotContain(searches, jql => jql.Contains("Story Points", StringComparison.Ordinal));

        var (status, problem) = await host.ProblemAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/browse?status=sideways"));
        Assert.Equal((400, "invalid", "invalid"), (status, (string)problem["code"]!, (string)problem["fields"]!["status"]!));
    }

    [Fact]
    public async Task Browse_filters_select_the_issues_Jira_would_return_in_rank_order() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await TeamAsync(host);
        host.Jira.AddSprint(203, "Sprint 11", "active", "2026-10-12T09:00:00.000+0000");
        host.Jira.AddSprint(204, "Sprint 12", "future", "2026-10-26T09:00:00.000+0000");
        host.Jira.AddSprint(205, "Sprint 10", "closed", "2026-09-28T09:00:00.000+0000");
        host.Jira.AddIssue("RS-1", "Pay invoices", sprintId: 203, points: 3, status: "In Progress");
        host.Jira.AddIssue("RS-2", "Pay refunds", sprintId: 204);
        host.Jira.AddIssue("RS-3", "Ship docs", points: 5, status: "Done");
        host.Jira.AddIssue("RS-4", "Pay old debts", sprintId: 205, points: 8);
        host.Jira.AddIssue("RS-5", "Payments epic", issueType: "Epic");
        host.Jira.AddIssue("RS-6", "Pay sub-task", issueType: "Sub-task");
        host.Jira.AddIssue("RS-7", "Pay later");
        host.Jira.AddIssue("RS-8", "Pay now");
        host.Jira.AddIssue("OT-1", "Pay elsewhere");
        host.Jira.RankFirst("RS-8");

        async Task<List<string>> BrowseAsync(string query) {
            var browse = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/browse?" + query));
            return [.. Rows(browse).Select(r => (string)r["key"]!).Order(StringComparer.Ordinal)];
        }

        Assert.Equal(["RS-1", "RS-2", "RS-3", "RS-7", "RS-8"], await BrowseAsync("scope=open"));
        Assert.Equal(["RS-1", "RS-2", "RS-4", "RS-7", "RS-8"], await BrowseAsync("scope=all&status=not_done"));
        Assert.Equal(["RS-1"], await BrowseAsync("scope=open&status=indeterminate"));
        Assert.Equal(["RS-3"], await BrowseAsync("scope=all&status=done"));
        Assert.Equal(["RS-2", "RS-7", "RS-8"], await BrowseAsync("scope=open&points=unpointed"));
        Assert.Equal(["RS-4"], await BrowseAsync("scope=all&points=8"));
        Assert.Equal(["RS-1", "RS-2", "RS-7", "RS-8"], await BrowseAsync("scope=open&q=pay"));
        Assert.Equal(["RS-1", "RS-4"], await BrowseAsync("scope=all&q=pay&points=pointed"));

        var ranked = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/browse?status=not_done"));
        var buckets = ranked["buckets"]!.AsArray();
        Assert.Equal(["RS-1", "RS-2", "RS-4", "RS-7", "RS-8"], Rows(ranked).Select(r => (string)r["key"]!).Order(StringComparer.Ordinal));
        Assert.Equal(["RS-8", "RS-7"], Keys(buckets[^1]!));
    }

    [Fact]
    public async Task A_typed_key_fetches_that_issue_and_ignores_the_filters() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await TeamAsync(host);
        host.Jira.AddIssue("RS-12", "Saved card", points: 5);
        var from = host.Jira.Requests.Count;

        var browse = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/browse?q=rs-12&points=unpointed"));

        Assert.Equal(["RS-12"], Rows(browse).Select(r => (string)r["key"]!));
        var requests = host.JiraRequestsSince(from);
        Assert.Equal("/rest/api/3/issue/RS-12", Assert.Single(requests, r => r.Path.StartsWith("/rest/api/3/issue/", StringComparison.Ordinal)).Path);
        Assert.DoesNotContain(requests, r => r.Path == "/rest/api/3/search/jql" || r.Path.EndsWith("/issue", StringComparison.Ordinal) || r.Path.EndsWith("/backlog", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_sprint_makes_the_result_partial_instead_of_ending_it() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await TeamAsync(host);
        host.Jira.AddSprint(203, "Sprint 11", "active", "2026-10-12T09:00:00.000+0000");
        host.Jira.AddSprint(204, "Sprint 12", "future", "2026-10-26T09:00:00.000+0000");
        host.Jira.AddIssue("RS-1", "In 11", sprintId: 203);
        host.Jira.AddIssue("RS-2", "In 12", sprintId: 204);
        host.Jira.AddIssue("RS-3", "Backlog");
        host.Jira.Script("GET", "/rest/agile/1.0/sprint/203/issue", new FakeJiraResponse(500));

        var browse = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/browse"));

        Assert.True((bool)browse["partial"]!);
        Assert.Equal("jira_unavailable", (string)browse["code"]!);
        Assert.Contains("RS-2", Rows(browse).Select(r => (string)r["key"]!));
        Assert.Contains("RS-3", Rows(browse).Select(r => (string)r["key"]!));
    }

    [Fact]
    public async Task Meta_is_cached_per_issue_and_limited_to_100_keys() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await TeamAsync(host);
        var parent = host.Jira.AddIssue("RS-1", "Checkout epic");
        var child = host.Jira.AddIssue("RS-12", "Saved card", points: 2.5m);
        child["fields"]!["parent"] = new JsonObject { ["id"] = "1", ["key"] = "RS-1", ["fields"] = new JsonObject { ["summary"] = "Checkout epic" } };

        var meta = await host.JsonAsync(HttpMethod.Post, ada, JiraHttpHost.Url(team, "/meta"), """{"keys":["RS-12","RS-404","not a key"]}""");
        var item = Assert.Single(meta["items"]!.AsArray())!;
        Assert.Equal(
            """{"key":"RS-12","summary":"Saved card","issueType":"Story","status":"To Do","statusCategory":"new","points":2.5,"parentKey":"RS-1","parentSummary":"Checkout epic"}""",
            item.ToJsonString());

        var before = host.Jira.Requests.Count;
        await host.JsonAsync(HttpMethod.Post, ada, JiraHttpHost.Url(team, "/meta"), """{"keys":["RS-12","RS-404"]}""");
        Assert.Equal(before, host.Jira.Requests.Count);

        var (status, _) = await host.ProblemAsync(HttpMethod.Post, ada, JiraHttpHost.Url(team, "/meta"), new JsonObject { ["keys"] = new JsonArray([.. Enumerable.Range(1, 101).Select(i => (JsonNode)$"RS-{i}")]) }.ToJsonString());
        Assert.Equal(400, status);
        _ = parent;
    }

    [Fact]
    public async Task Unconfigured_teams_and_outsiders_are_refused() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var ada = await host.UserAsync("Ada");
        var team = await host.TeamAsync((ada, "member"));
        var outsider = await host.UserAsync("Mallory");

        var (status, problem) = await host.ProblemAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/browse"));
        Assert.Equal((409, "jira_not_configured"), (status, (string)problem["code"]!));

        await host.ConfigureAsync(team);
        (status, problem) = await host.ProblemAsync(HttpMethod.Get, outsider, JiraHttpHost.Url(team, "/browse"));
        Assert.Equal((404, "not_found"), (status, (string)problem["code"]!));
        (status, _) = await host.ProblemAsync(HttpMethod.Get, outsider, JiraHttpHost.Url(new TeamId(Guid.CreateVersion7()), "/browse"));
        Assert.Equal(404, status);
    }

    internal static async Task<(Principal Ada, TeamId Team)> TeamAsync(JiraHttpHost host, string role = "member", bool membersCanEdit = true) {
        var ada = await host.UserAsync("Ada");
        var team = await host.TeamAsync((ada, role));
        await host.ConfigureAsync(team, membersCanEdit);
        host.AddBoard();
        return (ada, team);
    }

    private static IEnumerable<JsonNode> Rows(JsonNode browse) =>
        browse["buckets"]!.AsArray().SelectMany(b => b!["issues"]!.AsArray()).Select(r => r!);

    private static List<string> Keys(JsonNode bucket) => [.. bucket["issues"]!.AsArray().Select(r => (string)r!["key"]!)];
}
