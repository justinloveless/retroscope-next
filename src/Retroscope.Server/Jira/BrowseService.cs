using Npgsql;
using Retroscope.Core.Ids;

namespace Retroscope.Server.Jira;

/// <summary>One issue as the browse panel and the round chips show it; the sprint fields only place it in a bucket.</summary>
public sealed record IssueRow(
    string Key,
    string? Summary,
    string? IssueType,
    string? Status,
    string? StatusCategory,
    string? Priority,
    string? Assignee,
    decimal? Points,
    string? ParentKey,
    string? ParentSummary,
    long? SprintId,
    string? Sprint,
    string? SprintState,
    string? SprintStartDate);

/// <param name="Text">Search text; while present, the scope's ranking is not used and the hide filter is ignored.</param>
public sealed record BrowseQuery(string? Text, BrowseStatus Status, BrowsePoints Points, BrowseScope Scope) {
    public bool Searching => !string.IsNullOrWhiteSpace(Text);

    public string CacheId => $"{Scope}|{Status}|{Points.Kind}:{Points.Value}|{Text?.Trim()}";
}

/// <param name="Code">The first Jira error met while <paramref name="Partial"/> results were gathered.</param>
public sealed record BrowseResult(IReadOnlyList<IssueRow> Rows, bool Partial, string? Code);

/// <summary>A sprint bucket, or the backlog (always last); rows keep Jira's rank order.</summary>
public sealed record IssueBucket(string Id, bool Backlog, string Name, string? State, string? StartDate, IReadOnlyList<IssueRow> Rows);

/// <summary>Why a browse row cannot be added to the session again (parity with the original's disabled reasons).</summary>
public static class RoundState {
    public const string Pointing = "pointing";
    public const string Pointed = "pointed";
}

/// <summary>
/// The Jira browse panel and issue metadata (DESIGN §f). Browse results are cached per team and filters for 30 s and
/// shared by every member; metadata is cached per issue for 5 minutes. Both are tagged with their issue keys, so a
/// write through the proxy drops every entry that shows the issue.
/// </summary>
public sealed class BrowseService(JiraClient client, Credentials credentials, JiraCache cache, NpgsqlDataSource dataSource) {
    public static readonly TimeSpan BrowseTtl = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MetaTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SprintsTtl = TimeSpan.FromMinutes(5);
    public const int MaxMetaKeys = 100;
    public const int MetaChunk = 50;

    private const int PageSize = 100;
    private const int MaxPages = 10;
    private const int SprintParallelism = 3;

    /// <summary>Every cached browse result of the team carries it; a new issue may belong in any of them.</summary>
    public static string BrowseTag(TeamId team) => $"{team.Value:N}:browse";

    /// <summary>The configured board's active and future sprints in bucket order; empty without a board.</summary>
    public async Task<IReadOnlyList<JiraSprint>> SprintsAsync(TeamId team, CancellationToken cancellationToken) {
        var config = await ConfigAsync(team, cancellationToken);
        if (config.BoardId is not { } board) {
            return [];
        }
        return await cache.GetOrCreateAsync<IReadOnlyList<JiraSprint>>(
            JiraCache.Key(team, "sprints", board.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            SprintsTtl,
            async () => {
                // Poker never shows a sprint goal, so the cache does not keep it.
                IReadOnlyList<JiraSprint> sprints = [.. (await client.GetSprintsAsync(team, board, CancellationToken.None)).Select(s => s with { Goal = null })];
                return (sprints, 64L + sprints.Count * 160L, [JiraCache.TeamTag(team)]);
            },
            cancellationToken);
    }

    /// <summary>The issues a browse shows, in rank order, cached for 30 s per team and filters.</summary>
    public Task<BrowseResult> BrowseAsync(TeamId team, BrowseQuery query, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(
            JiraCache.Key(team, "browse", query.CacheId),
            BrowseTtl,
            async () => {
                var result = await FetchBrowseAsync(team, query);
                IReadOnlyCollection<string> tags = [JiraCache.TeamTag(team), BrowseTag(team), .. result.Rows.Select(row => JiraCache.IssueTag(team, row.Key))];
                return (result, 64L + result.Rows.Sum(Estimate), tags);
            },
            cancellationToken);

    /// <summary>
    /// Rows for the given keys, from the 5 minute cache or fetched together (<c>key in (…)</c>, 50 at a time). Keys
    /// Jira does not know are left out; invalid keys are ignored.
    /// </summary>
    public async Task<IReadOnlyList<IssueRow>> RowsAsync(TeamId team, IEnumerable<string> keys, CancellationToken cancellationToken) {
        var wanted = keys.Where(JiraMap.IsIssueKey).Distinct(StringComparer.Ordinal).ToList();
        var lookups = new List<Task<MetaEntry>>(wanted.Count);
        foreach (var chunk in wanted.Chunk(MetaChunk)) {
            // One fetch serves every key of the chunk that is neither cached nor already in flight.
            var fetch = new Lazy<Task<Dictionary<string, IssueRow>>>(() => FetchRowsAsync(team, chunk));
            foreach (var key in chunk) {
                lookups.Add(cache.GetOrCreateAsync(
                    JiraCache.Key(team, "meta", key),
                    MetaTtl,
                    async () => {
                        var rows = await fetch.Value;
                        var row = rows.GetValueOrDefault(key);
                        IReadOnlyCollection<string> tags = [JiraCache.TeamTag(team), JiraCache.IssueTag(team, key)];
                        return (new MetaEntry(row), row is null ? 64L : Estimate(row), tags);
                    },
                    cancellationToken));
            }
        }

        var entries = await Task.WhenAll(lookups);
        return [.. entries.Where(entry => entry.Row is not null).Select(entry => entry.Row!)];
    }

    /// <summary>
    /// For a session of the team, how each of its ticket keys is used: <see cref="RoundState.Pointing"/> when an
    /// active round carries it, else <see cref="RoundState.Pointed"/>. Pending preview rounds do not count.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> RoundStatesAsync(TeamId team, SessionId session, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            select r.ticket_key, bool_or(r.is_active)
            from poker_rounds r
            join poker_sessions s on s.id = r.session_id
            where s.id = $1 and s.team_id = $2 and not r.is_pending and r.ticket_key is not null
            group by r.ticket_key
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = session.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = team.Value });
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            states[reader.GetString(0)] = reader.GetBoolean(1) ? RoundState.Pointing : RoundState.Pointed;
        }
        return states;
    }

    /// <summary>
    /// Groups rows into sprint buckets and the backlog (port of <c>buildJiraBrowseIssuesBySprint</c>): a row without
    /// a sprint (or the "Backlog" label) is backlog; sprints are grouped by name and ordered by the first start date
    /// any of their rows carries, then by natural name order; the backlog comes last; rows keep their order.
    /// </summary>
    public static IReadOnlyList<IssueBucket> Buckets(IEnumerable<IssueRow> rows) {
        var backlog = new List<IssueRow>();
        var groups = new List<(string Name, List<IssueRow> Rows)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows) {
            if (string.IsNullOrEmpty(row.Sprint) || row.Sprint == "Backlog") {
                backlog.Add(row);
                continue;
            }
            if (!index.TryGetValue(row.Sprint, out var at)) {
                at = groups.Count;
                index[row.Sprint] = at;
                groups.Add((row.Sprint, []));
            }
            groups[at].Rows.Add(row);
        }

        var buckets = groups
            .Select(group => {
                var first = group.Rows[0];
                var start = group.Rows.FirstOrDefault(row => !string.IsNullOrEmpty(row.SprintStartDate))?.SprintStartDate;
                var id = group.Rows.FirstOrDefault(row => row.SprintId is not null)?.SprintId;
                var state = group.Rows.FirstOrDefault(row => row.SprintState is not null)?.SprintState;
                return new IssueBucket(id?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? first.Sprint!, false, group.Name, state, start, group.Rows);
            })
            .Order(Comparer<IssueBucket>.Create((a, b) => JiraMap.CompareSprints(a.Name, a.StartDate, b.Name, b.StartDate)))
            .ToList();
        if (backlog.Count > 0) {
            buckets.Add(new IssueBucket("backlog", true, "Backlog", null, null, backlog));
        }
        return buckets;
    }

    /// <summary>The fields a row needs, with the team's points and sprint fields.</summary>
    public static IReadOnlyList<string> RowFields(JiraTeamConfig config) {
        var fields = new List<string> { "summary", "status", "priority", "assignee", "parent", "issuetype" };
        if (config.PointsFieldId is { } points) {
            fields.Add(points);
        }
        if (config.SprintFieldId is { } sprint) {
            fields.Add(sprint);
        }
        return fields;
    }

    public static IssueRow ToRow(JiraIssue issue, JiraTeamConfig config, IReadOnlyDictionary<long, JiraSprint>? boardSprints, JiraSprint? inSprint = null) {
        var fields = issue.Fields;
        var sprint = inSprint is not null
            ? new JiraSprintRef(inSprint.Name ?? "", inSprint.StartDate, inSprint.Id)
            : JiraMap.ReadSprint(issue, config.SprintFieldId, boardSprints);
        var sprintState = inSprint?.State
            ?? (sprint?.Id is { } id && boardSprints is not null && boardSprints.TryGetValue(id, out var known) ? known.State : null);
        return new IssueRow(
            issue.Key!,
            fields?.Summary,
            fields?.Issuetype?.Name,
            fields?.Status?.Name,
            fields?.Status?.StatusCategory?.Key,
            fields?.Priority?.Name,
            fields?.Assignee?.DisplayName,
            JiraMap.ReadPoints(issue, config.PointsFieldId),
            fields?.Parent?.Key,
            fields?.Parent?.Fields?.Summary,
            sprint?.Id,
            sprint?.Name,
            sprintState,
            sprint?.StartDate);
    }

    private async Task<BrowseResult> FetchBrowseAsync(TeamId team, BrowseQuery query) {
        var config = await ConfigAsync(team, CancellationToken.None);
        var fields = RowFields(config);
        var text = query.Text?.Trim();

        // Typing a key means "this ticket": filters are ignored on purpose (parity).
        if (!string.IsNullOrEmpty(text) && Jql.LooksLikeKey(text) && JiraMap.IsIssueKey(text.ToUpperInvariant())) {
            try {
                var issue = await client.GetIssueAsync(team, text.ToUpperInvariant(), fields, CancellationToken.None);
                return new BrowseResult([ToRow(issue, config, await BoardSprintsAsync(team, config))], false, null);
            } catch (JiraException e) when (e.Error.Code == JiraErrorCode.NotFound) {
                return new BrowseResult([], false, null);
            }
        }

        var projectKey = config.ProjectKey ?? config.TicketPrefix ?? throw new JiraException(JiraError.NotConfigured);
        var filters = Jql.Filters(projectKey, query.Status, query.Points, config.PointsFieldId);
        var sprints = config.BoardId is null ? [] : await SprintsAsync(team, CancellationToken.None);
        var boardSprints = sprints.ToDictionary(sprint => sprint.Id);
        var scope = query.Scope == BrowseScope.Board && config.BoardId is null ? BrowseScope.Open : query.Scope;
        if (scope == BrowseScope.Board && !query.Searching) {
            return await RankedAsync(team, config, config.BoardId!.Value, filters, fields, sprints, boardSprints);
        }

        var jql = Jql.Browse(filters, Jql.SprintClause(scope, boardSprints.Keys), text);
        var (issues, error) = await SearchAllAsync(team, jql, fields);
        if (error is not null && issues.Count == 0) {
            throw new JiraException(error);
        }
        return new BrowseResult([.. issues.Select(issue => ToRow(issue, config, boardSprints))], error is not null, error?.Code);
    }

    // Board sprints in bucket order (three at a time), then issues in sprints of other boards, then the backlog. A
    // failing part makes the result partial with the error's code instead of silently ending the list.
    private async Task<BrowseResult> RankedAsync(
        TeamId team,
        JiraTeamConfig config,
        long board,
        string filters,
        IReadOnlyList<string> fields,
        IReadOnlyList<JiraSprint> sprints,
        Dictionary<long, JiraSprint> boardSprints) {
        var parts = new List<IssueRow>[sprints.Count];
        JiraError? firstError = null;
        void Fail(JiraError error) => Interlocked.CompareExchange(ref firstError, error, null);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, sprints.Count),
            new ParallelOptions { MaxDegreeOfParallelism = SprintParallelism },
            async (i, _) => {
                var sprint = sprints[i];
                parts[i] = [];
                try {
                    await foreach (var issue in AgilePagesAsync(startAt => client.GetSprintIssuesAsync(team, sprint.Id, filters, fields, startAt, CancellationToken.None))) {
                        parts[i].Add(ToRow(issue, config, boardSprints, sprint));
                    }
                } catch (JiraException e) {
                    Fail(e.Error);
                }
            });

        var rows = parts.SelectMany(part => part).ToList();
        var (others, othersError) = await SearchAllAsync(team, Jql.OtherSprints(filters, boardSprints.Keys), fields);
        if (othersError is not null) {
            Fail(othersError);
        }
        rows.AddRange(others.Select(issue => ToRow(issue, config, boardSprints)));

        try {
            await foreach (var issue in AgilePagesAsync(startAt => client.GetBacklogAsync(team, board, filters, fields, startAt, CancellationToken.None))) {
                rows.Add(ToRow(issue, config, boardSprints) with { SprintId = null, Sprint = null, SprintState = null, SprintStartDate = null });
            }
        } catch (JiraException e) {
            Fail(e.Error);
        }

        var unique = rows.DistinctBy(row => row.Key, StringComparer.Ordinal).ToList();
        if (firstError is { } error && unique.Count == 0) {
            throw new JiraException(error);
        }
        return new BrowseResult(unique, firstError is not null, firstError?.Code);
    }

    private static async IAsyncEnumerable<JiraIssue> AgilePagesAsync(Func<int, Task<JiraIssuePage>> fetch) {
        var startAt = 0;
        for (var page = 0; page < MaxPages; page++) {
            var result = await fetch(startAt);
            var issues = result.Issues ?? [];
            foreach (var issue in issues) {
                if (issue.Key is not null) {
                    yield return issue;
                }
            }
            startAt += issues.Count;
            if (issues.Count == 0 || startAt >= result.Total) {
                yield break;
            }
        }
    }

    // Every page of a JQL search (bounded); an error after some pages keeps what was read.
    private async Task<(List<JiraIssue> Issues, JiraError? Error)> SearchAllAsync(TeamId team, string jql, IReadOnlyList<string> fields) {
        var issues = new List<JiraIssue>();
        string? token = null;
        try {
            for (var page = 0; page < MaxPages; page++) {
                var result = await client.SearchAsync(team, jql, fields, PageSize, token, CancellationToken.None);
                issues.AddRange((result.Issues ?? []).Where(issue => issue.Key is not null));
                token = result.NextPageToken;
                if (result.IsLast == true || token is null) {
                    break;
                }
            }
        } catch (JiraException e) {
            return (issues, e.Error);
        }
        return (issues, null);
    }

    // A chunk of keys in one search. Jira refuses the whole query when one key does not exist, so a refused chunk
    // falls back to one GET per key (parity), and keys Jira cannot find are left out.
    private async Task<Dictionary<string, IssueRow>> FetchRowsAsync(TeamId team, string[] keys) {
        var config = await ConfigAsync(team, CancellationToken.None);
        var fields = RowFields(config);
        var boardSprints = await BoardSprintsAsync(team, config);
        List<JiraIssue> issues;
        try {
            var page = await client.SearchAsync(team, Jql.KeyIn(keys), fields, keys.Length, null, CancellationToken.None);
            issues = [.. (page.Issues ?? []).Where(issue => issue.Key is not null)];
        } catch (JiraException e) when (e.Error.Code == JiraErrorCode.Validation) {
            issues = [];
            foreach (var chunk in keys.Chunk(4)) {
                var found = await Task.WhenAll(chunk.Select(async key => {
                    try {
                        return await client.GetIssueAsync(team, key, fields, CancellationToken.None);
                    } catch (JiraException missing) when (missing.Error.Code is JiraErrorCode.NotFound or JiraErrorCode.Forbidden) {
                        return null;
                    }
                }));
                issues.AddRange(found.Where(issue => issue?.Key is not null)!);
            }
        }
        return issues.ToDictionary(issue => issue.Key!, issue => ToRow(issue, config, boardSprints), StringComparer.Ordinal);
    }

    private async Task<IReadOnlyDictionary<long, JiraSprint>?> BoardSprintsAsync(TeamId team, JiraTeamConfig config) {
        if (config.BoardId is null) {
            return null;
        }
        try {
            return (await SprintsAsync(team, CancellationToken.None)).ToDictionary(sprint => sprint.Id);
        } catch (JiraException) {
            // Only completes legacy sprint strings; a row without its sprint name lands in the backlog.
            return null;
        }
    }

    private async Task<JiraTeamConfig> ConfigAsync(TeamId team, CancellationToken cancellationToken) =>
        await credentials.LoadAsync(team, cancellationToken) ?? throw new JiraException(JiraError.NotConfigured);

    private static long Estimate(IssueRow row) =>
        160L + 2L * ((row.Summary?.Length ?? 0) + (row.ParentSummary?.Length ?? 0) + (row.Sprint?.Length ?? 0)
            + (row.Status?.Length ?? 0) + (row.Assignee?.Length ?? 0) + row.Key.Length);

    private sealed record MetaEntry(IssueRow? Row);
}
