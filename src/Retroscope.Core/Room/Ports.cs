using System.Collections.Frozen;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Room;

// Ports decouple the waves (DESIGN §k.1): a unit that needs another unit's behaviour codes against one of
// these and tests with a fake; the implementing unit registers the real one in its AddServices.

/// <summary>Jira calls a poker actor makes off-actor; the result comes back as an internal command.</summary>
public interface IPokerJiraPort {
    /// <summary>Writes story points to the issue with the team's Jira credentials (idempotent PUT).</summary>
    Task<JiraSubmitResult> SubmitPointsAsync(
        TeamId team,
        string issueKey,
        decimal points,
        PrincipalId by,
        CancellationToken cancellationToken);

    /// <summary>Title, type, status and parent of each key, through the shared Jira cache.</summary>
    Task<IReadOnlyList<JiraMeta>> LoadMetaAsync(
        TeamId team,
        IReadOnlyCollection<string> issueKeys,
        CancellationToken cancellationToken);
}

/// <param name="Code">The mapped Jira error code when the submit failed.</param>
public sealed record JiraSubmitResult(bool Ok, string? Code = null);

/// <summary>
/// Posts from board and poker actors (and HTTP handlers) to the team's actor. Every method is a synchronous,
/// non-blocking enqueue: actors never wait on each other.
/// </summary>
public interface ITeamNotices {
    void BoardsChanged(TeamId team, BoardId board);

    void PokerChanged(TeamId team, SessionId session, int pointed, int total);

    void ActionItemCommentsChanged(TeamId team, ActionItemId item, int count);

    /// <summary>The board actor inserted the row in its own transaction; the team actor applies it idempotently by id.</summary>
    void ActionItemCreated(TeamId team, ActionItemInfo item);

    /// <summary>Applied only when <paramref name="cardVersion"/> is newer than the copy's source version.</summary>
    void ActionItemTextChanged(TeamId team, CardId sourceCard, string text, int cardVersion);

    void SourceCardDeleted(TeamId team, CardId sourceCard);
}

/// <summary>An immutable view of one team's roles; a new version replaces it on every membership change.</summary>
public sealed record TeamMembership(long Version, FrozenDictionary<PrincipalId, TeamRole> Roles) {
    public TeamRole? RoleOf(PrincipalId principal) => Roles.TryGetValue(principal, out var role) ? role : null;
}

/// <summary>The process-wide, versioned team membership cache.</summary>
public interface IMembershipLookup {
    /// <summary>The cached membership, without IO; false when the team is not loaded yet.</summary>
    bool TryGet(TeamId team, out TeamMembership membership);

    ValueTask<TeamMembership> LoadAsync(TeamId team, CancellationToken cancellationToken);
}

/// <summary>Closes live sockets with 4401 when access is revoked.</summary>
public interface IConnectionRevoker {
    /// <summary>A user was disabled or deleted: every socket of the principal closes.</summary>
    void RevokePrincipal(PrincipalId principal);

    /// <summary>A sign-in session was revoked: only sockets authenticated by it close.</summary>
    void RevokeAuthSession(Guid authSessionId);
}

/// <summary>The leader epoch this process holds; every actor transaction is fenced with it (§c.9).</summary>
public interface ILeaderEpoch {
    bool IsLeader { get; }

    /// <summary>The value of <c>system_state.leader_epoch</c> this process wrote when it became leader.</summary>
    long Current { get; }
}
