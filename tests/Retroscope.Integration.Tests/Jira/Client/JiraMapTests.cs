using System.Net;
using System.Text;
using System.Text.Json;
using Retroscope.Core.Ids;
using Retroscope.Core.Sprints;
using Retroscope.Integration.Tests.Scaffold;
using Retroscope.Server.Jira;
using Retroscope.Server.Json;

namespace Retroscope.Integration.Tests.Jira.Client;

[Trait("Category", "JiraClient")]
public sealed class JiraMapTests {
    [Fact]
    public void Discovery_prefers_the_story_points_schema_over_a_field_named_like_it() {
        var fields = new List<JiraField> {
            Field("customfield_10028", "Story Points", "com.atlassian.jira.plugin.system.customfieldtypes:float"),
            Field("customfield_10016", "Story point estimate", "com.pyxis.greenhopper.jira:jsw-story-points"),
            Field("customfield_10020", "Sprint", "com.pyxis.greenhopper.jira:gh-sprint"),
        };

        Assert.Equal(new JiraFieldIds("customfield_10016", "customfield_10020"), JiraMap.DiscoverFields(fields));
    }

    [Fact]
    public void Discovery_falls_back_to_the_field_name() {
        var fields = new List<JiraField> {
            Field("summary", "Summary", null),
            Field("customfield_10041", "Story Points", "com.atlassian.jira.plugin.system.customfieldtypes:float"),
        };

        Assert.Equal(new JiraFieldIds("customfield_10041", null), JiraMap.DiscoverFields(fields));
    }

    [Fact]
    public void Discovery_never_assumes_customfield_10020_is_points() {
        var fields = new List<JiraField> {
            Field("customfield_10020", "Sprint", "com.pyxis.greenhopper.jira:gh-sprint"),
            Field("customfield_10004", "Estimate", "com.atlassian.jira.plugin.system.customfieldtypes:float"),
        };

        Assert.Equal(new JiraFieldIds(null, "customfield_10020"), JiraMap.DiscoverFields(fields));
    }

    [Fact]
    public void Discovery_reads_the_real_field_catalogue_shape() {
        const string json = """
            [{"id":"customfield_10016","key":"customfield_10016","name":"Story point estimate","custom":true,"orderable":true,
              "navigable":true,"searchable":true,"clauseNames":["cf[10016]","Story point estimate"],
              "schema":{"type":"number","custom":"com.pyxis.greenhopper.jira:jsw-story-points","customId":10016}},
             {"id":"statusCategory","name":"Status Category","custom":false,"schema":{"type":"statusCategory","system":"statusCategory"}}]
            """;

        var fields = JsonSerializer.Deserialize(json, JiraClientJsonContext.Default.IReadOnlyListJiraField)!;

        Assert.Equal("customfield_10016", JiraMap.DiscoverFields(fields).PointsFieldId);
    }

    [Theory]
    [InlineData("customfield_10016", "cf[10016]")]
    [InlineData("customfield_1", "cf[1]")]
    public void Custom_fields_are_referenced_in_JQL_by_cf_id(string fieldId, string expected) =>
        Assert.Equal(expected, JiraMap.JqlFieldRef(fieldId));

    [Theory]
    [InlineData("Story Points")]
    [InlineData("customfield_")]
    [InlineData("customfield_10016) OR (1=1")]
    [InlineData("summary")]
    public void Only_custom_field_ids_become_JQL_references(string fieldId) =>
        Assert.Throws<ArgumentException>(() => JiraMap.JqlFieldRef(fieldId));

    [Fact]
    public void Points_are_read_from_the_configured_field_as_numbers_or_numeric_strings() {
        var issue = Issue("""{"customfield_10016":3.5,"customfield_10028":"8","customfield_10020":13}""");

        Assert.Equal(3.5m, JiraMap.ReadPoints(issue, "customfield_10016"));
        Assert.Equal(8m, JiraMap.ReadPoints(issue, "customfield_10028"));
        Assert.Null(JiraMap.ReadPoints(issue, null));
        Assert.Null(JiraMap.ReadPoints(issue, "customfield_99999"));
    }

    [Fact]
    public void The_sprint_is_the_last_open_one_then_the_last_closed_one() {
        var open = Issue("""
            {"customfield_10020":[{"id":1,"name":"Old","state":"closed"},{"id":2,"name":"Now","state":"active","startDate":"2026-10-01T00:00:00.000Z"},{"id":3,"name":"Gone","state":"closed"}]}
            """);
        var closed = Issue("""{"customfield_10020":[{"id":1,"name":"Old","state":"closed"},{"id":3,"name":"Gone","state":"closed"}]}""");

        Assert.Equal(new JiraSprintRef("Now", "2026-10-01T00:00:00.000Z", 2), JiraMap.ReadSprint(open, "customfield_10020"));
        Assert.Equal(new JiraSprintRef("Gone", null, 3), JiraMap.ReadSprint(closed, "customfield_10020"));
    }

    [Fact]
    public void Legacy_sprint_strings_and_bare_ids_are_resolved() {
        var legacy = Issue("""
            {"customfield_10007":["com.atlassian.greenhopper.service.sprint.Sprint@1[id=4,rapidViewId=1,state=ACTIVE,name=Legacy 4,startDate=2026]"]}
            """);
        var bareId = Issue("""{"customfield_10020":[{"id":9,"state":"future"}]}""");
        var board = new Dictionary<long, JiraSprint> { [9] = new(9, "From board", "future", "2026-11-02T00:00:00.000Z", null) };

        Assert.Equal("Legacy 4", JiraMap.ReadSprint(legacy, null)!.Name);
        Assert.Equal(new JiraSprintRef("From board", "2026-11-02T00:00:00.000Z", 9), JiraMap.ReadSprint(bareId, "customfield_10020", board));
        Assert.Null(JiraMap.ReadSprint(Issue("{}"), "customfield_10020"));
    }

    [Fact]
    public void Sprint_marks_are_every_entry_of_the_sprint_field_in_order() {
        var issue = Issue("""
            {"customfield_10020":[
              {"id":1,"name":"Old","state":"closed","startDate":"2026-09-01T09:00:00.000+0000","completeDate":"2026-09-14T17:00:00.000+0000"},
              {"id":2,"name":"Now","state":"ACTIVE"},
              {"id":3,"state":"future"},
              {"id":4,"name":"Odd","state":"archived"},
              "com.atlassian.greenhopper.service.sprint.Sprint@1[id=5,rapidViewId=1,state=FUTURE,name=Legacy 5,startDate=<null>]",
              "com.atlassian.greenhopper.service.sprint.Sprint@2[rapidViewId=1,state=CLOSED,name=No id]"]}
            """);

        Assert.Equal(
            [
                new SprintMark(1, "Old", SprintPhase.Closed, DateTimeOffset.Parse("2026-09-01T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds()),
                new SprintMark(2, "Now", SprintPhase.Active, null),
                new SprintMark(5, "Legacy 5", SprintPhase.Future, null),
            ],
            JiraMap.ReadSprintMarks(issue, "customfield_10020"));
    }

    [Fact]
    public void Sprint_marks_are_empty_without_a_sprint_field_and_found_by_scanning_without_its_id() {
        var scanned = Issue("""{"customfield_10016":3,"customfield_10007":[{"id":9,"name":"Nine","state":"closed"}]}""");

        Assert.Empty(JiraMap.ReadSprintMarks(Issue("""{"customfield_10020":null}"""), "customfield_10020"));
        Assert.Empty(JiraMap.ReadSprintMarks(Issue("{}"), "customfield_10020"));
        Assert.Equal([new SprintMark(9, "Nine", SprintPhase.Closed, null)], JiraMap.ReadSprintMarks(scanned, null));
    }

    [Fact]
    public void An_issue_is_done_at_its_resolution_else_at_its_status_category_change_when_done() {
        var resolved = Issue("""
            {"status":{"name":"Done","statusCategory":{"key":"done"}},"resolutiondate":"2026-10-02T10:00:00.000+0000",
             "statuscategorychangedate":"2026-10-05T10:00:00.000+0000","customfield_10016":5}
            """);
        var unresolved = Issue("""
            {"status":{"name":"Closed","statusCategory":{"key":"done"}},"resolutiondate":null,"statuscategorychangedate":"2026-10-05T10:00:00.000+0000"}
            """);
        var reopened = Issue("""
            {"status":{"name":"In Review","statusCategory":{"key":"indeterminate"}},"statuscategorychangedate":"2026-10-05T10:00:00.000+0000"}
            """);

        var facts = JiraMap.ToIssueFacts(resolved, Config());

        Assert.Equal(("DUN-1", WorkCategory.Done, Ms("2026-10-02T10:00:00Z"), 5m), (facts.Key, facts.Category, facts.DoneAt, facts.Points));
        Assert.Equal(Ms("2026-10-05T10:00:00Z"), JiraMap.ToIssueFacts(unresolved, Config()).DoneAt);
        Assert.Equal((WorkCategory.InProgress, (long?)null), (JiraMap.ToIssueFacts(reopened, Config()).Category, JiraMap.ToIssueFacts(reopened, Config()).DoneAt));
        Assert.Equal(WorkCategory.ToDo, JiraMap.ToIssueFacts(Issue("{}"), Config()).Category);
        Assert.Null(JiraMap.ToIssueFacts(resolved, Config() with { PointsFieldId = null }).Points);
    }

    [Theory]
    [InlineData("2026-01-26T19:00:00.000+1000", "2026-01-26T09:00:00Z")]
    [InlineData("2026-01-26T19:00:00.000+10:00", "2026-01-26T09:00:00Z")]
    [InlineData("2026-02-01T09:30:00.123Z", "2026-02-01T09:30:00.123Z")]
    [InlineData("2026-02-01", "2026-02-01T00:00:00Z")]
    public void Jira_dates_parse_in_both_offset_forms(string value, string utc) {
        Assert.True(JiraMap.TryParseDate(value, out var ms));
        Assert.Equal(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), ms);
    }

    [Theory]
    [InlineData("not a date")]
    [InlineData("TBD")]
    [InlineData("2026-13-40T00:00:00.000Z")]
    [InlineData("Sprint 5")]
    public void Unparseable_dates_are_rejected(string value) => Assert.False(JiraMap.TryParseDate(value, out _));

    [Fact]
    public void Sprint_bucket_order_matches_the_golden_vectors() {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RepoRoot.Path, "tests/golden/sprint-buckets.json")));
        var cases = 0;
        foreach (var testCase in golden.RootElement.GetProperty("cases").EnumerateArray()) {
            var backlog = new List<string>();
            var buckets = new List<(string Name, List<string> Keys, string? Start)>();
            foreach (var issue in testCase.GetProperty("issues").EnumerateArray()) {
                var key = issue.GetProperty("key").GetString()!;
                var sprint = issue.TryGetProperty("sprint", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                if (string.IsNullOrEmpty(sprint) || sprint == "Backlog") {
                    backlog.Add(key);
                    continue;
                }

                var start = issue.TryGetProperty("sprintStartDate", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                var index = buckets.FindIndex(b => b.Name == sprint);
                if (index < 0) {
                    buckets.Add((sprint, [key], string.IsNullOrEmpty(start) ? null : start));
                } else {
                    buckets[index].Keys.Add(key);
                    if (buckets[index].Start is null && !string.IsNullOrEmpty(start)) {
                        buckets[index] = buckets[index] with { Start = start };
                    }
                }
            }

            var comparer = Comparer<(string Name, List<string> Keys, string? Start)>.Create(
                (a, b) => JiraMap.CompareSprints(a.Name, a.Start, b.Name, b.Start));
            var ordered = buckets.Order(comparer).ToList();

            var expected = testCase.GetProperty("expected");
            Assert.Equal(expected.GetProperty("backlog").EnumerateArray().Select(k => k.GetString()), backlog);
            Assert.Equal(
                expected.GetProperty("sprintBuckets").EnumerateArray().Select(b => $"{b.GetProperty("name").GetString()}: {string.Join(' ', b.GetProperty("issues").EnumerateArray().Select(k => k.GetString()))}"),
                ordered.Select(b => $"{b.Name}: {string.Join(' ', b.Keys)}"));
            cases++;
        }

        Assert.Equal(2000, cases);
    }

    [Fact]
    public void Field_errors_are_plain_text_and_capped() {
        var body = Encoding.UTF8.GetBytes($$$"""{"errors":{"summary":"line one\nline two","x":"{{{new string('é', 400)}}}","n":5}}""");

        var fields = JiraMap.ReadFieldErrors(body);

        Assert.Equal("line one line two", fields["summary"]);
        Assert.Equal(300, fields["x"].Length);
        Assert.False(fields.ContainsKey("n"));
        Assert.Empty(JiraMap.ReadFieldErrors("not json"u8));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, JiraErrorCode.Validation)]
    [InlineData(HttpStatusCode.TooManyRequests, JiraErrorCode.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, JiraErrorCode.Unavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout, JiraErrorCode.Unavailable)]
    public void Status_codes_map_to_problem_codes(HttpStatusCode status, string code) =>
        Assert.Equal(code, JiraMap.MapStatus(status, [], authenticationDenied: false, retryAfter: null).Code);

    [Theory]
    [InlineData("https://api.media.atlassian.com/file/1/binary?token=t", true)]
    [InlineData("https://acme.atlassian.net/secure/attachment/1/x.png", true)]
    [InlineData("https://API.MEDIA.ATLASSIAN.COM/file/1/binary", true)]
    [InlineData("https://api.media.atlassian.com:443/file/1/binary", true)]
    [InlineData("http://api.media.atlassian.com/file/1/binary", false)]
    [InlineData("https://media.atlassian.com/file/1/binary", false)]
    [InlineData("https://a.b.atlassian.net/x", false)]
    [InlineData("https://atlassian.net/x", false)]
    [InlineData("https://acme.atlassian.net.evil.com/x", false)]
    [InlineData("https://[::1]/x", false)]
    public void Media_redirect_targets_are_allow_listed(string location, bool allowed) =>
        Assert.Equal(allowed, JiraClient.IsAllowedMediaLocation(new Uri(location)));

    private static long Ms(string utc) => DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    private static JiraTeamConfig Config() => new(
        new TeamId(Guid.Empty), "acme.atlassian.net", [], "k1", "1234", "b***@acme.test", "DUN", 7, "DUN",
        "customfield_10016", false, "customfield_10020", JiraConfigStatus.Ok, true, null);

    private static JiraField Field(string id, string name, string? custom) =>
        new(id, name, custom is not null, custom is null ? null : new JiraFieldSchema("number", custom));

    private static JiraIssue Issue(string fieldsJson) =>
        JsonSerializer.Deserialize($$$"""{"id":"1","key":"DUN-1","fields":{{{fieldsJson}}}}""", JiraClientJsonContext.Default.JiraIssue)!;
}
