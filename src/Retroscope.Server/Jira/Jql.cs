using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Retroscope.Server.Jira;

/// <summary>Which sprints a browse covers (parity with the original's sprint scope filter).</summary>
public enum BrowseScope {
    /// <summary>The configured board's active and future sprints, then its backlog, in Jira rank order.</summary>
    Board,

    /// <summary>Any open or future sprint of the project, plus issues in no sprint.</summary>
    Open,

    /// <summary>Every issue of the project, closed sprints included.</summary>
    All,
}

public enum BrowseStatus {
    All,
    NotDone,
    New,
    InProgress,
    Done,
}

/// <summary>The points filter: any, without points, with points, or exactly <see cref="Value"/>.</summary>
public readonly record struct BrowsePoints(BrowsePointsKind Kind, decimal Value = 0) {
    public static BrowsePoints Any { get; } = new(BrowsePointsKind.Any);
}

public enum BrowsePointsKind {
    Any,
    Unpointed,
    Pointed,
    Exactly,
}

/// <summary>
/// Builds the JQL the browse and metadata calls send (DESIGN §f). Every value is quoted and escaped, issue keys are
/// validated before they reach a query, points filters name the configured field as <c>cf[NNNNN]</c> (never
/// "Story Points"), and status filters use <c>statusCategory</c>, which does not depend on the site's workflow names.
/// </summary>
public static partial class Jql {
    // JQL matches status categories by name (the original's parity); the browse route takes the category keys.
    private const string CategoryToDo = "To Do";
    private const string CategoryInProgress = "In Progress";
    private const string CategoryDone = "Done";

    /// <summary>A JQL string literal: <c>"</c> and <c>\</c> escaped, control characters dropped.</summary>
    public static string Quote(string value) {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value) {
            if (c is '"' or '\\') {
                builder.Append('\\').Append(c);
            } else if (!char.IsControl(c)) {
                builder.Append(c);
            }
        }
        return builder.Append('"').ToString();
    }

    /// <summary>Whether <paramref name="text"/> is typed like an issue key (any case), e.g. <c>rs-12</c>.</summary>
    public static bool LooksLikeKey(string text) => KeyLike().IsMatch(text);

    /// <summary>The filter part of a browse query, without ordering: project, no epics or sub-tasks, status and points.</summary>
    public static string Filters(string projectKey, BrowseStatus status, BrowsePoints points, string? pointsFieldId) {
        var clauses = new List<string> {
            $"project = {Quote(RequireProjectKey(projectKey))}",
            "issuetype not in (Epic, subtaskIssueTypes())",
        };
        switch (status) {
            case BrowseStatus.NotDone:
                clauses.Add($"statusCategory != {Quote(CategoryDone)}");
                break;
            case BrowseStatus.New:
                clauses.Add($"statusCategory = {Quote(CategoryToDo)}");
                break;
            case BrowseStatus.InProgress:
                clauses.Add($"statusCategory = {Quote(CategoryInProgress)}");
                break;
            case BrowseStatus.Done:
                clauses.Add($"statusCategory = {Quote(CategoryDone)}");
                break;
        }
        if (points.Kind != BrowsePointsKind.Any) {
            var field = JiraMap.JqlFieldRef(pointsFieldId ?? throw new ArgumentException("A points filter needs the points field.", nameof(pointsFieldId)));
            clauses.Add(points.Kind switch {
                BrowsePointsKind.Unpointed => $"{field} is EMPTY",
                BrowsePointsKind.Pointed => $"{field} is not EMPTY",
                _ => $"{field} = {points.Value.ToString(CultureInfo.InvariantCulture)}",
            });
        }
        return string.Join(" AND ", clauses);
    }

    /// <summary>The sprint clause of a scope, or null for <see cref="BrowseScope.All"/>.</summary>
    public static string? SprintClause(BrowseScope scope, IReadOnlyCollection<long> boardSprintIds) => scope switch {
        BrowseScope.Board when boardSprintIds.Count > 0 =>
            $"(sprint in ({string.Join(", ", boardSprintIds.Select(id => id.ToString(CultureInfo.InvariantCulture)))}) OR sprint is EMPTY)",
        BrowseScope.Board => "sprint is EMPTY",
        BrowseScope.Open => "(sprint in openSprints() OR sprint in futureSprints() OR sprint is EMPTY)",
        _ => null,
    };

    /// <summary>Issues in some sprint other than <paramref name="knownSprintIds"/> (sprints of other boards).</summary>
    public static string OtherSprints(string filters, IReadOnlyCollection<long> knownSprintIds) {
        var clause = knownSprintIds.Count == 0
            ? "sprint is not EMPTY"
            : $"sprint is not EMPTY AND sprint not in ({string.Join(", ", knownSprintIds.Select(id => id.ToString(CultureInfo.InvariantCulture)))})";
        return $"{filters} AND {clause} ORDER BY Rank ASC";
    }

    /// <summary>The search-text clause: the summary contains the words, or the text is the issue's key.</summary>
    public static string Text(string text) {
        var trimmed = text.Trim();
        var summary = $"summary ~ {Quote(trimmed)}";
        return LooksLikeKey(trimmed) ? $"({summary} OR key = {Quote(trimmed.ToUpperInvariant())})" : summary;
    }

    /// <summary>A ranked JQL browse: filters, then the optional sprint and text clauses.</summary>
    public static string Browse(string filters, string? sprintClause, string? text) {
        var builder = new StringBuilder(filters);
        if (sprintClause is not null) {
            builder.Append(" AND ").Append(sprintClause);
        }
        if (!string.IsNullOrWhiteSpace(text)) {
            builder.Append(" AND ").Append(Text(text));
        }
        return builder.Append(" ORDER BY Rank ASC").ToString();
    }

    /// <summary><c>key in ("A-1", "B-2")</c>; every key must be a valid issue key.</summary>
    public static string KeyIn(IEnumerable<string> keys) {
        var quoted = keys.Select(key => JiraMap.IsIssueKey(key) ? Quote(key) : throw new ArgumentException($"Not an issue key: {key}", nameof(keys)));
        return $"key in ({string.Join(", ", quoted)})";
    }

    private static string RequireProjectKey(string projectKey) =>
        ProjectKey().IsMatch(projectKey) ? projectKey : throw new ArgumentException("Not a project key.", nameof(projectKey));

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,19}-[1-9][0-9]{0,9}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyLike();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,19}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectKey();
}
