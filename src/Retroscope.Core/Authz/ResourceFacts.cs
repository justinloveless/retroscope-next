using Retroscope.Core.Ids;

namespace Retroscope.Core.Authz;

/// <summary>
/// What the policy needs to know about the resource, gathered by the caller from actor state or a row.
/// Roles are not facts: the policy resolves them through <see cref="Room.IAccessView"/> at decide time (R12).
/// </summary>
/// <param name="Room">The room the capability is checked against (a team, session or board).</param>
/// <param name="Team">The team the room belongs to; a team room's team is the room itself. Every room has one (UX18).</param>
/// <param name="IsObserver">Poker only: the principal is an observer of the session.</param>
public sealed record ResourceFacts(
    RoomKey Room,
    TeamId Team,
    bool Archived = false,
    bool Deleted = false,
    bool TeamDeleted = false,
    bool IsObserver = false,
    bool JiraConfigured = false,
    bool MembersCanEditJira = true);
