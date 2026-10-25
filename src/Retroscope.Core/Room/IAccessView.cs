using Retroscope.Core.Ids;

namespace Retroscope.Core.Room;

/// <summary>Backed by the versioned team membership cache.</summary>
public interface IAccessView {
    TeamRole? RoleIn(TeamId team, PrincipalId principal);
}
