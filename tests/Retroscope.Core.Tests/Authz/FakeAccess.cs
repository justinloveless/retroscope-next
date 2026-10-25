using Retroscope.Core.Ids;
using Retroscope.Core.Room;

namespace Retroscope.Core.Tests.Authz;

/// <summary>A mutable access view: tests change roles between calls, as the membership cache does.</summary>
internal sealed class FakeAccess : IAccessView {
    private readonly Dictionary<(TeamId, PrincipalId), TeamRole> _roles = [];

    public FakeAccess SetRole(TeamId team, PrincipalId principal, TeamRole? role) {
        if (role is { } value) {
            _roles[(team, principal)] = value;
        } else {
            _roles.Remove((team, principal));
        }
        return this;
    }

    public TeamRole? RoleIn(TeamId team, PrincipalId principal) {
        return _roles.TryGetValue((team, principal), out var role) ? role : null;
    }
}
