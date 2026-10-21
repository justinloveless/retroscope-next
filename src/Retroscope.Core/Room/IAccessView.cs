using Retroscope.Core.Ids;

namespace Retroscope.Core.Room;

/// <summary>Backed by the versioned team membership cache and the board grant table.</summary>
public interface IAccessView {
    TeamRole? RoleIn(TeamId team, PrincipalId principal);

    /// <summary>True when the principal holds a password grant issued for the board's current password.</summary>
    bool HasCurrentGrant(BoardId board, PrincipalId principal, DateTimeOffset? passwordSetAt);
}
