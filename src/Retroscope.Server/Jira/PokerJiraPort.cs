using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Server.Jira;

/// <summary>
/// The poker room's way to Jira (<see cref="IPokerJiraPort"/>, DESIGN §f): submits write points through the team's
/// configured field, and metadata comes from the shared 5 minute cache, so a room's chips cost at most one search.
/// </summary>
/// <remarks>
/// The client and the browse service are resolved on first use: the poker room builds its port at startup, and the
/// client needs <c>APP_KEYS</c>, which a host without Jira traffic (tests, tools) may not set (U19 decision 17).
/// </remarks>
public sealed partial class PokerJiraPort(
    Func<JiraClient> client,
    Func<BrowseService> browse,
    JiraCache cache,
    JiraAudit audit,
    ILogger<PokerJiraPort> logger) : IPokerJiraPort {
    public async Task<JiraSubmitResult> SubmitPointsAsync(
        TeamId team,
        string issueKey,
        decimal points,
        PrincipalId by,
        CancellationToken cancellationToken) {
        if (!JiraMap.IsIssueKey(issueKey)) {
            return new JiraSubmitResult(false, JiraErrorCode.NotFound);
        }
        try {
            await client().SetPointsAsync(team, issueKey, points, cancellationToken);
        } catch (JiraException e) {
            return new JiraSubmitResult(false, e.Error.Code);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            LogSubmitTimedOut(issueKey);
            return new JiraSubmitResult(false, JiraErrorCode.Unavailable);
        }
        // Before the room's loop asks for the key again, so it reads the new value.
        cache.InvalidateTag(JiraCache.IssueTag(team, issueKey));
        await audit.WriteAsync(team, by, JiraAudit.Points, issueKey, CancellationToken.None);
        return new JiraSubmitResult(true);
    }

    public async Task<IReadOnlyList<JiraMeta>> LoadMetaAsync(
        TeamId team,
        IReadOnlyCollection<string> issueKeys,
        CancellationToken cancellationToken) {
        var rows = await browse().RowsAsync(team, issueKeys, cancellationToken);
        return [.. rows.Select(row => new JiraMeta(row.Key, row.Summary, row.IssueType, row.Status, row.ParentKey, row.ParentSummary))];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A Jira points submit for {IssueKey} timed out")]
    private partial void LogSubmitTimedOut(string issueKey);
}

/// <summary>
/// Every Jira write is recorded in <c>audit_log</c> (DESIGN §f): team, user, action and issue key, never content.
/// The write already happened in Jira, so a failure to record it is logged and not surfaced to the caller.
/// </summary>
public sealed partial class JiraAudit(NpgsqlDataSource dataSource, ILogger<JiraAudit> logger) {
    public const string Update = "jira.update";
    public const string Transition = "jira.transition";
    public const string Points = "jira.points";
    public const string Sprint = "jira.sprint";
    public const string Comment = "jira.comment";
    public const string LinkCreate = "jira.link.create";
    public const string LinkDelete = "jira.link.delete";
    public const string Create = "jira.create";
    public const string ConfigSet = "jira.config.set";
    public const string ConfigDelete = "jira.config.delete";

    public async Task WriteAsync(TeamId team, PrincipalId actor, string action, string? target, CancellationToken cancellationToken) {
        try {
            await using var command = dataSource.CreateCommand(
                "insert into audit_log (actor_id, team_id, action, target) values ($1, $2, $3, $4)");
            command.Parameters.Add(new NpgsqlParameter { Value = actor.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = team.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = action });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)target ?? DBNull.Value });
            await command.ExecuteNonQueryAsync(cancellationToken);
        } catch (NpgsqlException e) {
            LogAuditFailed(e, action);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record the audit row for {Action}")]
    private partial void LogAuditFailed(Exception exception, string action);
}
