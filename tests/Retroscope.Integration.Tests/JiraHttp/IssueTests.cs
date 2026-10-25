using System.Text.Json.Nodes;
using Retroscope.Fakes.FakeJira;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.JiraHttp;

/// <summary>The issue drawer's reads and writes through the real proxy and FakeJira.</summary>
[Trait("Category", "JiraHttp")]
[Collection(SharedPostgres.Name)]
public sealed class IssueTests(PostgresFixture postgres) {
    private const string BoardPointsField = "customfield_10028";

    private const string Table = """{"type":"table","attrs":{"layout":"default"},"content":[{"type":"tableRow","content":[{"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"Visa"}]}]}]}]}""";

    private static readonly JsonNode Description = JsonNode.Parse($$"""
        {"type":"doc","version":1,"content":[
          {"type":"paragraph","content":[{"type":"text","text":"Pay with a saved card."}]},
          {{Table}},
          {"type":"paragraph","content":[{"type":"text","text":"End."}]}]}
        """)!;

    [Fact]
    public async Task Detail_splits_the_description_into_markup_and_islands_and_a_save_splices_them_back_byte_for_byte() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        var issue = host.Jira.AddIssue("RS-12", "Saved card", points: 5);
        issue["fields"]!["description"] = Description.DeepClone();
        var table = issue["fields"]!["description"]!["content"]![1]!.ToJsonString();

        var detail = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/issues/RS-12"));

        Assert.True((bool)detail["editable"]!);
        var island = (string)Assert.Single(detail["islandIds"]!.AsArray())!;
        Assert.Equal($"Pay with a saved card.\n\n[[island:{island}]]\n\nEnd.", (string)detail["descriptionMarkup"]!);
        Assert.Equal("island", (string)detail["description"]!["c"]![1]!["t"]!);
        Assert.Equal(5m, (decimal)detail["fields"]!["points"]!);
        Assert.Equal("Saved card", (string)detail["fields"]!["summary"]!);

        var from = host.Jira.Requests.Count;
        var saved = await host.JsonAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"), new {
            description = $"Pay with a **saved** card.\n\n[[island:{island}]]\n\nDone.",
            descriptionBase = (string)detail["updated"]!,
        });

        var put = Assert.Single(host.JiraRequestsSince(from), r => r.Method == "PUT");
        Assert.Contains(table, put.Body, StringComparison.Ordinal);
        Assert.Contains("\"strong\"", put.Body, StringComparison.Ordinal);
        Assert.Contains("Done.", put.Body, StringComparison.Ordinal);
        Assert.NotEqual((string)detail["updated"]!, (string)saved["updated"]!);
        Assert.Equal(island, (string)Assert.Single(saved["islandIds"]!.AsArray())!);
        Assert.Equal(["jira.update RS-12"], await host.AuditAsync(team));
    }

    [Fact]
    public async Task A_description_save_is_refused_as_stale_after_an_edit_in_Jira_or_with_an_unknown_island() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        var issue = host.Jira.AddIssue("RS-12", "Saved card");
        issue["fields"]!["description"] = Description.DeepClone();
        var detail = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/issues/RS-12"));
        var island = (string)detail["islandIds"]![0]!;
        var markup = $"Edited.\n\n[[island:{island}]]";

        // Someone edits the issue in Jira within the 60 s the drawer's copy is cached.
        issue["fields"]!["updated"] = "2026-02-01T10:00:00.000+0000";
        var from = host.Jira.Requests.Count;
        var (status, problem) = await host.ProblemAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"),
            new { description = markup, descriptionBase = (string)detail["updated"]! });

        Assert.Equal((409, "stale"), (status, (string)problem["code"]!));
        Assert.Equal("2026-02-01T10:00:00.000+0000", (string)problem["current"]!["updated"]!);
        Assert.DoesNotContain(host.JiraRequestsSince(from), r => r.Method == "PUT");

        (status, problem) = await host.ProblemAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"),
            new { description = "[[island:AAAAAAAAAAAAAAAA]]", descriptionBase = "2026-02-01T10:00:00.000+0000" });
        Assert.Equal((409, "stale"), (status, (string)problem["code"]!));
        Assert.DoesNotContain(host.JiraRequestsSince(from), r => r.Method == "PUT");
        Assert.Empty(await host.AuditAsync(team));
    }

    [Fact]
    public async Task A_description_that_is_not_an_ADF_document_is_read_only() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        var issue = host.Jira.AddIssue("RS-12", "Saved card");
        issue["fields"]!["description"] = new JsonArray(new JsonObject { ["type"] = "paragraph" });

        var detail = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/issues/RS-12"));
        Assert.False((bool)detail["editable"]!);
        Assert.Null(detail["descriptionMarkup"]);

        var (status, _) = await host.ProblemAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"),
            new { description = "Replaced.", descriptionBase = (string)detail["updated"]! });
        Assert.Equal(409, status);
    }

    [Fact]
    public async Task Attachments_are_served_only_when_they_belong_to_the_issue() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        host.Jira.AddIssue("RS-12", "Saved card");
        host.Jira.AddIssue("RS-13", "Other");
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        host.Jira.AddAttachment("RS-12", "30001", "shot.png", "image/png", png);
        host.Jira.AddAttachment("RS-13", "30002", "secret.png", "image/png", png);

        using (var response = await host.GetAsync(ada, JiraHttpHost.Url(team, "/issues/RS-12/attachments/30001"))) {
            Assert.Equal(200, (int)response.StatusCode);
            Assert.Equal(png, await response.Content.ReadAsByteArrayAsync(JiraHttpHost.Token));
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("private, max-age=300", response.Headers.NonValidated["Cache-Control"].ToString());
            Assert.Equal("sandbox", string.Join(',', response.Headers.GetValues("Content-Security-Policy")));
        }

        var from = host.Jira.Requests.Count;
        var (status, problem) = await host.ProblemAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/issues/RS-12/attachments/30002"));
        Assert.Equal((404, "jira_not_found"), (status, (string)problem["code"]!));
        Assert.DoesNotContain(host.JiraRequestsSince(from), r => r.Path.Contains("/attachment/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_converts_markup_and_clone_copies_the_description_islands_verbatim() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        var source = host.Jira.AddIssue("RS-12", "Saved card");
        source["fields"]!["description"] = Description.DeepClone();
        source["fields"]!["issuetype"] = new JsonObject { ["id"] = "10002", ["name"] = "Bug", ["subtask"] = false };

        var created = await host.JsonAsync(HttpMethod.Post, ada, JiraHttpHost.Url(team, "/issues"),
            new { summary = " Remember cards ", description = "Store the **last** card." }, expected: 201);
        var key = (string)created["key"]!;
        var fields = host.Jira.Issues[key]["fields"]!;
        Assert.Equal("Remember cards", (string)fields["summary"]!);
        Assert.Contains("\"strong\"", fields["description"]!.ToJsonString(), StringComparison.Ordinal);

        var from = host.Jira.Requests.Count;
        var clone = await host.JsonAsync(HttpMethod.Post, ada, JiraHttpHost.Url(team, "/issues"),
            new { summary = "Clone: Saved card", cloneFrom = "RS-12" }, expected: 201);
        var post = Assert.Single(host.JiraRequestsSince(from), r => r.Method == "POST" && r.Path == "/rest/api/3/issue");
        Assert.Contains(Table, post.Body, StringComparison.Ordinal);
        Assert.Contains("Cloned from RS-12", post.Body, StringComparison.Ordinal);
        Assert.Contains("\"issuetype\":{\"id\":\"10002\"}", post.Body, StringComparison.Ordinal);
        Assert.Contains("\"project\":{\"key\":\"RS\"}", post.Body, StringComparison.Ordinal);

        Assert.Equal([$"jira.create {key}", $"jira.create {clone["key"]}"], await host.AuditAsync(team));
    }

    [Fact]
    public async Task Create_from_a_retro_action_item_writes_mentions_as_names() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        var text = $"Ask [[mention:{ada.Id}:Ada]] about **refunds**";

        var created = await host.JsonAsync(HttpMethod.Post, ada, JiraHttpHost.Url(team, "/issues"),
            new { summary = "Ask Ada about refunds", description = text, fromRetro = true }, expected: 201);

        var description = host.Jira.Issues[(string)created["key"]!]["fields"]!["description"]!.ToJsonString();
        Assert.Contains("@Ada", description, StringComparison.Ordinal);
        Assert.DoesNotContain("mention:", description, StringComparison.Ordinal);
        Assert.Contains("\"strong\"", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_follow_members_can_edit_while_reads_stay_open_to_members() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (member, team) = await BrowseTests.TeamAsync(host, membersCanEdit: false);
        var admin = await host.UserAsync("Grace");
        await host.ExecuteAsync("insert into team_members (team_id, user_id, role) values ($1, $2, 'admin')", team.Value, admin.Id.Value);
        host.Jira.AddIssue("RS-12", "Saved card");

        await host.JsonAsync(HttpMethod.Get, member, JiraHttpHost.Url(team, "/issues/RS-12"));
        var (status, problem) = await host.ProblemAsync(HttpMethod.Patch, member, JiraHttpHost.Url(team, "/issues/RS-12"), new { summary = "Mine" });
        Assert.Equal((403, "forbidden"), (status, (string)problem["code"]!));
        (status, _) = await host.ProblemAsync(HttpMethod.Post, member, JiraHttpHost.Url(team, "/issues"), new { summary = "New" });
        Assert.Equal(403, status);

        var saved = await host.JsonAsync(HttpMethod.Patch, admin, JiraHttpHost.Url(team, "/issues/RS-12"), new { summary = "Admins may" });
        Assert.Equal("Admins may", (string)saved["fields"]!["summary"]!);
        Assert.Equal(["jira.update RS-12"], await host.AuditAsync(team));
    }

    [Fact]
    public async Task Points_are_written_to_the_boards_estimation_field_when_the_site_has_two_story_points_fields() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        host.Jira.BoardEstimation = FakeJiraServer.FieldEstimation(BoardPointsField, "Story Points");
        host.Jira.EditableFields.Add(BoardPointsField);
        host.Jira.AddIssue("RS-12", "Saved card");

        var result = await host.JsonAsync(HttpMethod.Put, ada, JiraHttpHost.Url(team, "/issues/RS-12/points"), """{"points":5}""");

        Assert.Equal(5m, (decimal?)host.Jira.Issues["RS-12"]["fields"]![BoardPointsField]);
        Assert.Null(host.Jira.Issues["RS-12"]["fields"]![JiraHttpHost.PointsField]);
        Assert.Equal(5m, (decimal?)result["points"]);
    }

    [Fact]
    public async Task A_points_field_an_admin_chose_takes_the_write_over_the_boards() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var ada = await host.UserAsync("Ada");
        var team = await host.TeamAsync((ada, "member"));
        await host.ConfigureAsync(team, pointsOverridden: true);
        host.AddBoard();
        host.Jira.BoardEstimation = FakeJiraServer.FieldEstimation(BoardPointsField, "Story Points");
        host.Jira.EditableFields.Add(BoardPointsField);
        host.Jira.AddIssue("RS-12", "Saved card");

        await host.JsonAsync(HttpMethod.Put, ada, JiraHttpHost.Url(team, "/issues/RS-12/points"), """{"points":5}""");

        Assert.Equal(5m, (decimal?)host.Jira.Issues["RS-12"]["fields"]![JiraHttpHost.PointsField]);
        Assert.Null(host.Jira.Issues["RS-12"]["fields"]![BoardPointsField]);
        Assert.DoesNotContain(host.Jira.Requests, r => r.Path == "/rest/agile/1.0/board/7/configuration");
    }

    [Theory]
    [InlineData("""{"type":"issueCount"}""")]
    [InlineData("""{"type":"none"}""")]
    [InlineData("""{"type":"field","field":{"fieldId":"timeoriginalestimate","displayName":"Original Time Estimate"}}""")]
    [InlineData(null)]
    public async Task A_board_that_does_not_estimate_with_a_custom_field_leaves_writes_on_the_stored_field(string? estimation) {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        host.Jira.BoardEstimation = estimation is null ? null : JsonNode.Parse(estimation)!.AsObject();
        host.Jira.AddIssue("RS-12", "Saved card");

        await host.JsonAsync(HttpMethod.Put, ada, JiraHttpHost.Url(team, "/issues/RS-12/points"), """{"points":2}""");

        Assert.Equal(2m, (decimal?)host.Jira.Issues["RS-12"]["fields"]![JiraHttpHost.PointsField]);
    }

    [Fact]
    public async Task A_failing_board_configuration_leaves_writes_on_the_stored_field() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        host.Jira.Script("GET", "/rest/agile/1.0/board/7/configuration", new FakeJiraResponse(404, "{}"));
        host.Jira.AddIssue("RS-12", "Saved card");

        await host.JsonAsync(HttpMethod.Put, ada, JiraHttpHost.Url(team, "/issues/RS-12/points"), """{"points":2}""");

        Assert.Equal(2m, (decimal?)host.Jira.Issues["RS-12"]["fields"]![JiraHttpHost.PointsField]);
    }

    [Fact]
    public async Task Points_are_written_as_decimals_through_the_configured_field() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        host.Jira.AddIssue("RS-12", "Saved card");

        var result = await host.JsonAsync(HttpMethod.Put, ada, JiraHttpHost.Url(team, "/issues/RS-12/points"), """{"points":3.25}""");
        Assert.Equal(3.25m, (decimal)result["points"]!);
        Assert.Equal(3.25m, (decimal)host.Jira.Issues["RS-12"]["fields"]![JiraHttpHost.PointsField]!);

        await host.JsonAsync(HttpMethod.Put, ada, JiraHttpHost.Url(team, "/issues/RS-12/points"), """{"points":null}""");
        Assert.Null(host.Jira.Issues["RS-12"]["fields"]![JiraHttpHost.PointsField]);

        var (status, _) = await host.ProblemAsync(HttpMethod.Put, ada, JiraHttpHost.Url(team, "/issues/RS-12/points"), """{"points":1.125}""");
        Assert.Equal(400, status);
        (status, _) = await host.ProblemAsync(HttpMethod.Put, ada, JiraHttpHost.Url(team, "/issues/RS-12/points"), """{"points":3,"extra":1}""");
        Assert.Equal(400, status);
        Assert.Equal(["jira.points RS-12", "jira.points RS-12"], await host.AuditAsync(team));
    }

    [Fact]
    public async Task Comments_transitions_sprints_and_links_reach_Jira_and_are_audited() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        host.Jira.AddSprint(203, "Sprint 11", "active", "2026-10-12T09:00:00.000+0000");
        host.Jira.AddIssue("RS-12", "Saved card");
        host.Jira.AddIssue("RS-13", "Other");

        var from = host.Jira.Requests.Count;
        var comment = await host.JsonAsync(HttpMethod.Post, ada, JiraHttpHost.Url(team, "/issues/RS-12/comments"), new { body = "Looks **good**" }, expected: 201);
        var posted = Assert.Single(host.JiraRequestsSince(from), r => r.Method == "POST");
        Assert.Contains("Sent from Retroscope by Ada", posted.Body, StringComparison.Ordinal);
        Assert.Equal("p", (string)comment["body"]!["c"]![0]!["t"]!);

        await host.JsonAsync(HttpMethod.Post, ada, JiraHttpHost.Url(team, "/issues/RS-12/transition"), new { transitionId = "31" }, expected: 204);
        await host.JsonAsync(HttpMethod.Put, ada, JiraHttpHost.Url(team, "/issues/RS-12/sprint"), new { sprintId = 203 }, expected: 204);
        Assert.NotNull(host.Jira.Issues["RS-12"]["fields"]![JiraHttpHost.SprintField]);
        await host.JsonAsync(HttpMethod.Put, ada, JiraHttpHost.Url(team, "/issues/RS-12/sprint"), """{"sprintId":null}""", expected: 204);
        Assert.Null(host.Jira.Issues["RS-12"]["fields"]![JiraHttpHost.SprintField]);

        from = host.Jira.Requests.Count;
        var link = await host.JsonAsync(HttpMethod.Post, ada, JiraHttpHost.Url(team, "/issues/RS-12/links"),
            new { typeId = "10000", targetKey = "rs-13" }, expected: 201);
        var linked = Assert.Single(host.JiraRequestsSince(from), r => r.Path == "/rest/api/3/issueLink");
        Assert.Contains("\"outwardIssue\":{\"key\":\"RS-12\"}", linked.Body, StringComparison.Ordinal);
        Assert.Equal(["Blocks", "blocks", "RS-13"], [(string)link["type"]!, (string)link["label"]!, (string)link["key"]!]);
        var linkId = (string)link["id"]!;
        Assert.Equal([linkId], host.Jira.LinkIds);
        await host.JsonAsync(HttpMethod.Delete, ada, JiraHttpHost.Url(team, $"/issues/RS-12/links/{linkId}"), expected: 204);
        Assert.Empty(host.Jira.LinkIds);

        var options = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/issues/RS-12/options"));
        Assert.Equal(["21", "31"], options["transitions"]!.AsArray().Select(t => (string)t!["id"]!));
        Assert.Equal("Blocks", (string)options["linkTypes"]![0]!["name"]!);

        Assert.Equal(
            ["jira.comment RS-12", "jira.transition RS-12", "jira.sprint RS-12", "jira.sprint RS-12", "jira.link.create RS-12", $"jira.link.delete {linkId}"],
            await host.AuditAsync(team));
    }

    [Fact]
    public async Task A_link_is_deleted_only_through_an_issue_it_belongs_to() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        host.Jira.AddIssue("RS-12", "Saved card");
        host.Jira.AddIssue("RS-13", "Other");
        host.Jira.AddIssue("RS-14", "Unrelated");
        var link = await host.JsonAsync(HttpMethod.Post, ada, JiraHttpHost.Url(team, "/issues/RS-12/links"),
            new { typeId = "10000", targetKey = "RS-13" }, expected: 201);
        var linkId = (string)link["id"]!;
        var from = host.Jira.Requests.Count;

        var (status, problem) = await host.ProblemAsync(HttpMethod.Delete, ada, JiraHttpHost.Url(team, $"/issues/RS-14/links/{linkId}"));
        Assert.Equal((404, "jira_not_found"), (status, (string)problem["code"]!));
        using (var unscoped = await host.SendAsync(HttpMethod.Delete, ada, JiraHttpHost.Url(team, $"/links/{linkId}"), null)) {
            Assert.Equal(404, (int)unscoped.StatusCode);
        }
        Assert.DoesNotContain(host.JiraRequestsSince(from), r => r.Method == "DELETE");
        Assert.Equal([linkId], host.Jira.LinkIds);

        await host.JsonAsync(HttpMethod.Delete, ada, JiraHttpHost.Url(team, $"/issues/RS-13/links/{linkId}"), expected: 204);

        Assert.Empty(host.Jira.LinkIds);
        var drawer = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/issues/RS-12"));
        Assert.Empty(drawer["links"]!.AsArray());
    }

    [Fact]
    public async Task Jira_errors_map_to_the_proxy_codes() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        host.Jira.AddIssue("RS-12", "Saved card");
        host.Jira.Script("PUT", "/rest/api/3/issue/RS-12", new FakeJiraResponse(400, """{"errorMessages":[],"errors":{"summary":"Summary is too long."}}"""));
        host.Jira.Script("GET", "/rest/api/3/issue/RS-13", new FakeJiraResponse(429, Headers: new Dictionary<string, string> { ["Retry-After"] = "60" }));

        var (status, problem) = await host.ProblemAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"), new { summary = "x" });
        Assert.Equal((422, "jira_validation", "Summary is too long."), (status, (string)problem["code"]!, (string)problem["fields"]!["summary"]!));

        (status, problem) = await host.ProblemAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/issues/RS-13"));
        Assert.Equal((503, "jira_rate_limited", 60), (status, (string)problem["code"]!, (int)problem["retryAfter"]!));

        (status, problem) = await host.ProblemAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/issues/RS-404"));
        Assert.Equal((404, "jira_not_found"), (status, (string)problem["code"]!));
        (status, _) = await host.ProblemAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/issues/..%2Fmyself"));
        Assert.Equal(404, status);
    }
}
