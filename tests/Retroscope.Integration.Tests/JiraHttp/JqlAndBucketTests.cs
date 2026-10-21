using System.Text.Json;
using Retroscope.Integration.Tests.Scaffold;
using Retroscope.Server.Jira;

namespace Retroscope.Integration.Tests.JiraHttp;

[Trait("Category", "JiraHttp")]
public sealed class JqlAndBucketTests {
    [Fact]
    public void Values_are_quoted_and_escaped() {
        Assert.Equal("\"a \\\"b\\\" \\\\ c\"", Jql.Quote("a \"b\" \\ c"));
        Assert.Equal("\"line\"", Jql.Quote("li\nne"));
        Assert.Equal("summary ~ \"pay \\\" OR project = X\"", Jql.Text("pay \" OR project = X"));
    }

    [Fact]
    public void Text_that_looks_like_a_key_also_matches_the_key() {
        Assert.Equal("(summary ~ \"rs-12\" OR key = \"RS-12\")", Jql.Text(" rs-12 "));
    }

    [Fact]
    public void Filters_use_status_categories_and_the_configured_points_field() {
        Assert.Equal(
            "project = \"RS\" AND issuetype not in (Epic, subtaskIssueTypes()) AND statusCategory != \"Done\" AND cf[10016] is EMPTY",
            Jql.Filters("RS", BrowseStatus.NotDone, new BrowsePoints(BrowsePointsKind.Unpointed), "customfield_10016"));
        Assert.Equal(
            "project = \"RS\" AND issuetype not in (Epic, subtaskIssueTypes()) AND statusCategory = \"In Progress\" AND cf[10028] = 2.5",
            Jql.Filters("RS", BrowseStatus.InProgress, new BrowsePoints(BrowsePointsKind.Exactly, 2.5m), "customfield_10028"));
        Assert.DoesNotContain("Story Points", Jql.Filters("RS", BrowseStatus.All, new BrowsePoints(BrowsePointsKind.Pointed), "customfield_10016"));
        Assert.Throws<ArgumentException>(() => Jql.Filters("RS", BrowseStatus.All, new BrowsePoints(BrowsePointsKind.Pointed), null));
        Assert.Throws<ArgumentException>(() => Jql.Filters("RS\" OR 1=1", BrowseStatus.All, BrowsePoints.Any, null));
    }

    [Fact]
    public void Sprint_scopes_and_key_lists() {
        Assert.Equal("(sprint in (101, 102) OR sprint is EMPTY)", Jql.SprintClause(BrowseScope.Board, [101, 102]));
        Assert.Equal("(sprint in openSprints() OR sprint in futureSprints() OR sprint is EMPTY)", Jql.SprintClause(BrowseScope.Open, []));
        Assert.Null(Jql.SprintClause(BrowseScope.All, [101]));
        Assert.Equal("key in (\"RS-1\", \"AB_2-30\")", Jql.KeyIn(["RS-1", "AB_2-30"]));
        Assert.Throws<ArgumentException>(() => Jql.KeyIn(["RS-1", "RS-1\") OR (key = \"X-1"]));
        Assert.EndsWith(" ORDER BY Rank ASC", Jql.Browse("project = \"RS\"", null, "pay"));
    }

    [Fact]
    public void Buckets_match_the_golden_vectors() {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RepoRoot.Path, "tests/golden/sprint-buckets.json")));
        var cases = 0;
        foreach (var testCase in golden.RootElement.GetProperty("cases").EnumerateArray()) {
            var rows = testCase.GetProperty("issues").EnumerateArray().Select(issue => new IssueRow(
                issue.GetProperty("key").GetString()!,
                null, null, null, null, null, null, null, null, null, null,
                issue.TryGetProperty("sprint", out var sprint) && sprint.ValueKind == JsonValueKind.String ? sprint.GetString() : null,
                null,
                issue.TryGetProperty("sprintStartDate", out var start) && start.ValueKind == JsonValueKind.String ? start.GetString() : null));

            var buckets = BrowseService.Buckets(rows);

            var expected = testCase.GetProperty("expected");
            var sprints = expected.GetProperty("sprintBuckets").EnumerateArray()
                .Select(b => $"{b.GetProperty("name").GetString()}: {string.Join(' ', b.GetProperty("issues").EnumerateArray().Select(k => k.GetString()))}");
            var backlog = expected.GetProperty("backlog").EnumerateArray().Select(k => k.GetString()).ToList();
            Assert.Equal(sprints, buckets.Where(b => !b.Backlog).Select(b => $"{b.Name}: {string.Join(' ', b.Rows.Select(r => r.Key))}"));
            Assert.Equal(backlog, buckets.Where(b => b.Backlog).SelectMany(b => b.Rows).Select(r => r.Key));
            if (backlog.Count > 0) {
                Assert.True(buckets[^1].Backlog);
            }
            cases++;
        }
        Assert.True(cases >= 1000, $"only {cases} golden cases");
    }
}
