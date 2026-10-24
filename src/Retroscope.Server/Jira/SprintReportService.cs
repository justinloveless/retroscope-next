using System.Globalization;
using Retroscope.Core.Ids;
using Retroscope.Core.Sprints;
using Retroscope.Core.Text;

namespace Retroscope.Server.Jira;

/// <summary>One issue of a sprint report as Jira shows it now, with the classifier's verdict.</summary>
public sealed record ReportRow(
    string Key,
    string? Summary,
    string? IssueType,
    string? Status,
    string? StatusCategory,
    string? Assignee,
    decimal? Points,
    IssueVerdict Verdict);

/// <param name="Truncated">The sprint held more issues than the search cap; totals cover the rows read.</param>
public sealed record SprintReport(
    long SprintId,
    string? Goal,
    bool PointsKnown,
    bool Truncated,
    IReadOnlyList<OutcomeTotal> Totals,
    IReadOnlyList<ReportRow> Rows);

public enum ReportEmpty {
    NoBoard,
    NoSprints,
}

/// <param name="Sprints">The picker's slice: active sprints, then closed ones, newest first.</param>
/// <param name="HasMoreSprints">The picker holds older closed sprints than the slice.</param>
public sealed record SprintReportResult(IReadOnlyList<JiraSprint> Sprints, bool HasMoreSprints, SprintReport? Report, ReportEmpty? Empty);

/// <summary>
/// The sprint report of a team retro board (UX17): a snapshot of one sprint of the team's board, read with a single
/// JQL search and sorted by <see cref="SprintClassifier"/>. The picker of recent sprints is cached for 5 minutes and is
/// the allow-list of sprints a report can be asked for, so no other sprint id is ever sent to Jira; each answer lists
/// only a slice of it, which the client widens on demand. A closed sprint's report is cached for 15 minutes, an active
/// one's for 60 s; both carry the issue keys they show.
/// </summary>
public sealed class SprintReportService(JiraClient client, Credentials credentials, JiraCache cache) {
    public static readonly TimeSpan PickerTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ClosedTtl = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ActiveTtl = TimeSpan.FromSeconds(60);
    public const int PickerSize = 100;
    public const int DefaultClosedSprints = 4;
    public const int PageSize = 100;
    public const int MaxPages = 10;

    private static readonly IComparer<JiraSprint> PickerOrder = Comparer<JiraSprint>.Create((a, b) => {
        var byState = IsClosed(a).CompareTo(IsClosed(b));
        if (byState != 0) {
            return byState;
        }
        var byDate = Nullable.Compare(Recency(b), Recency(a));
        return byDate != 0 ? byDate : NaturalCompare.Compare(b.Name ?? "", a.Name ?? "");
    });

    public static bool IsClosed(JiraSprint sprint) => string.Equals(sprint.State, "closed", StringComparison.OrdinalIgnoreCase);

    private static bool IsActive(JiraSprint sprint) => string.Equals(sprint.State, "active", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The report of <paramref name="sprintId"/>, or of the most recently closed sprint (else the newest active one) when
    /// it is null, with the picker's slice of <see cref="Slice"/>. Throws <see cref="JiraException"/>: not found for a
    /// sprint the picker does not list.
    /// </summary>
    /// <param name="closedSprints">How many closed sprints the slice lists, 1 to <see cref="PickerSize"/>.</param>
    public async Task<SprintReportResult> GetAsync(TeamId team, long? sprintId, int closedSprints, CancellationToken cancellationToken) {
        ArgumentOutOfRangeException.ThrowIfLessThan(closedSprints, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(closedSprints, PickerSize);
        var config = await credentials.LoadAsync(team, cancellationToken) ?? throw new JiraException(JiraError.NotConfigured);
        if (config.BoardId is not { } board) {
            return new SprintReportResult([], false, null, ReportEmpty.NoBoard);
        }

        var picker = await PickerAsync(team, board, cancellationToken);
        if (picker.Count == 0) {
            return new SprintReportResult([], false, null, ReportEmpty.NoSprints);
        }

        var id = sprintId ?? (picker.FirstOrDefault(IsClosed) ?? picker[0]).Id;
        var sprint = picker.FirstOrDefault(s => s.Id == id) ?? throw new JiraException(JiraError.NotFound);
        var report = await cache.GetOrCreateAsync(
            JiraCache.Key(team, "sprint-report", id.ToString(CultureInfo.InvariantCulture)),
            IsClosed(sprint) ? ClosedTtl : ActiveTtl,
            async () => {
                var built = await BuildAsync(team, config, board, sprint);
                IReadOnlyCollection<string> tags = [JiraCache.TeamTag(team), .. built.Rows.Select(row => JiraCache.IssueTag(team, row.Key))];
                return (built, 256L + built.Rows.Sum(row => 160L + 2L * (row.Summary?.Length ?? 0)), tags);
            },
            cancellationToken);
        var (sprints, hasMore) = Slice(picker, id, closedSprints);
        return new SprintReportResult(sprints, hasMore, report, null);
    }

    /// <summary>
    /// The active sprints of <paramref name="picker"/> and its newest <paramref name="closedSprints"/> closed ones,
    /// stretched to reach <paramref name="selected"/> so the picker can name the sprint it shows; and whether the picker
    /// holds more.
    /// </summary>
    private static (IReadOnlyList<JiraSprint> Sprints, bool HasMore) Slice(IReadOnlyList<JiraSprint> picker, long selected, int closedSprints) {
        var active = picker.Count(s => !IsClosed(s));
        var shown = Math.Min(picker.Count, Math.Max(active + closedSprints, picker.TakeWhile(s => s.Id != selected).Count() + 1));
        return ([.. picker.Take(shown)], shown < picker.Count);
    }

    /// <summary>The fields the report reads; both done dates are named, since a search returns only what it asks for.</summary>
    public static IReadOnlyList<string> ReportFields(JiraTeamConfig config) {
        var fields = new List<string> { "summary", "status", "assignee", "issuetype", "resolutiondate", "statuscategorychangedate" };
        if (config.PointsFieldId is { } points) {
            fields.Add(points);
        }
        if (config.SprintFieldId is { } sprint) {
            fields.Add(sprint);
        }
        return fields;
    }

    private Task<IReadOnlyList<JiraSprint>> PickerAsync(TeamId team, long board, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync<IReadOnlyList<JiraSprint>>(
            JiraCache.Key(team, "report-sprints", board.ToString(CultureInfo.InvariantCulture)),
            PickerTtl,
            async () => {
                var recent = await client.GetRecentSprintsAsync(team, board, PickerSize, CancellationToken.None);
                // A sprint that closed between the active and the closed read is listed twice; its closed copy is kept.
                IReadOnlyList<JiraSprint> picker = [.. recent
                    .Where(s => (IsActive(s) || IsClosed(s)) && (s.OriginBoardId is null || s.OriginBoardId == board))
                    .OrderByDescending(IsClosed)
                    .DistinctBy(s => s.Id)
                    .Order(PickerOrder)];
                return (picker, 64L + picker.Sum(s => 160L + 2L * ((s.Name?.Length ?? 0) + (s.Goal?.Length ?? 0))), [JiraCache.TeamTag(team)]);
            },
            cancellationToken);

    private async Task<SprintReport> BuildAsync(TeamId team, JiraTeamConfig stored, long board, JiraSprint sprint) {
        var config = await WithReportFieldsAsync(team, stored, board);
        var reported = Reported(sprint);
        var fields = ReportFields(config);
        var jql = Jql.InSprint(sprint.Id);
        var pointsKnown = config.PointsFieldId is not null;
        var rows = new List<ReportRow>();
        var facts = new List<(IssueFacts, IssueVerdict)>();
        var truncated = false;
        string? token = null;
        for (var page = 0; page < MaxPages; page++) {
            var result = await client.SearchAsync(team, jql, fields, PageSize, token, CancellationToken.None);
            foreach (var issue in result.Issues ?? []) {
                if (issue.Key is null) {
                    continue;
                }
                var issueFacts = JiraMap.ToIssueFacts(issue, config);
                var verdict = SprintClassifier.Classify(reported, issueFacts);
                facts.Add((issueFacts, verdict));
                var issueFields = issue.Fields;
                rows.Add(new ReportRow(
                    issue.Key,
                    issueFields?.Summary,
                    issueFields?.Issuetype?.Name,
                    issueFields?.Status?.Name,
                    issueFields?.Status?.StatusCategory?.Key,
                    issueFields?.Assignee?.DisplayName,
                    issueFacts.Points,
                    verdict));
            }
            token = result.NextPageToken;
            if (result.IsLast == true || token is null) {
                break;
            }
            truncated = page == MaxPages - 1;
        }

        return new SprintReport(sprint.Id, sprint.Goal, pointsKnown, truncated, SprintClassifier.Totals(facts, pointsKnown), rows);
    }

    /// <summary>
    /// The fields this report reads, never stored. Points follow the board's estimation field, as Jira's own sprint
    /// report does, unless an admin chose a points field; a site can carry several story points fields and discovery
    /// may pick one the board does not use. A missing field falls back to the stored one, then to the site's field
    /// catalogue, which imported teams need until an admin saves the settings.
    /// </summary>
    private async Task<JiraTeamConfig> WithReportFieldsAsync(TeamId team, JiraTeamConfig stored, long board) {
        var config = stored.PointsFieldOverridden
            ? stored
            : stored with { PointsFieldId = await BoardEstimationFieldAsync(team, board) ?? stored.PointsFieldId };
        if (config.PointsFieldId is not null && config.SprintFieldId is not null) {
            return config;
        }
        var discovered = JiraMap.DiscoverFields(await client.GetFieldsAsync(team, CancellationToken.None));
        return config with {
            PointsFieldId = config.PointsFieldId ?? discovered.PointsFieldId,
            SprintFieldId = config.SprintFieldId ?? discovered.SprintFieldId,
        };
    }

    // Reading the board's configuration only refines the points field, so its failure must not fail the report.
    private async Task<string?> BoardEstimationFieldAsync(TeamId team, long board) {
        try {
            return await client.GetBoardEstimationFieldAsync(team, board, CancellationToken.None);
        } catch (JiraException) {
            return null;
        }
    }

    // A closed sprint without a readable completion date falls back to its end date; with neither, nothing counts as late.
    private static ReportedSprint Reported(JiraSprint sprint) {
        var start = Date(sprint.StartDate);
        return IsClosed(sprint)
            ? new ReportedSprint.Closed(sprint.Id, start, Date(sprint.CompleteDate) ?? Date(sprint.EndDate) ?? long.MaxValue)
            : new ReportedSprint.Active(sprint.Id, start);
    }

    private static long? Recency(JiraSprint sprint) =>
        IsClosed(sprint) ? Date(sprint.CompleteDate) ?? Date(sprint.EndDate) ?? Date(sprint.StartDate) : Date(sprint.StartDate);

    private static long? Date(string? value) => JiraMap.TryParseDate(value, out var at) ? at : null;
}
