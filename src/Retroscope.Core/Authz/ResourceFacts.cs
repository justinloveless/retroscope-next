using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Authz;

/// <summary>
/// What the policy needs to know about the resource, gathered by the caller from actor state or a row.
/// Roles and grants are not facts: the policy resolves them through <see cref="Room.IAccessView"/> at
/// decide time (R12).
/// </summary>
/// <param name="Room">The room the capability is checked against (a team, session or board).</param>
/// <param name="Visibility">Boards only.</param>
/// <param name="PasswordSetAt">Boards with a password; a grant for an older password is not current.</param>
/// <param name="IsObserver">Poker only: the principal is an observer of the session.</param>
public sealed record ResourceFacts(
    RoomKey Room,
    TeamId? Team = null,
    PrincipalId? CreatedBy = null,
    BoardVisibility? Visibility = null,
    DateTimeOffset? PasswordSetAt = null,
    bool Archived = false,
    bool Deleted = false,
    bool TeamDeleted = false,
    bool IsObserver = false,
    bool JiraConfigured = false,
    bool MembersCanEditJira = true);
