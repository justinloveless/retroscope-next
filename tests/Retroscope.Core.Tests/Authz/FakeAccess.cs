using Retroscope.Core.Ids;
using Retroscope.Core.Room;

namespace Retroscope.Core.Tests.Authz;

/// <summary>A mutable access view: tests change roles and grants between calls, as the membership cache does.</summary>
internal sealed class FakeAccess : IAccessView {
    private readonly Dictionary<(TeamId, PrincipalId), TeamRole> _roles = [];
    private readonly Dictionary<(BoardId, PrincipalId), DateTimeOffset> _grants = [];

    public FakeAccess SetRole(TeamId team, PrincipalId principal, TeamRole? role) {
        if (role is { } value) {
            _roles[(team, principal)] = value;
        } else {
            _roles.Remove((team, principal));
        }
        return this;
    }

    /// <param name="passwordEpoch">The board's <c>password_set_at</c> when the grant was issued.</param>
    public FakeAccess Grant(BoardId board, PrincipalId principal, DateTimeOffset passwordEpoch) {
        _grants[(board, principal)] = passwordEpoch;
        return this;
    }

    public TeamRole? RoleIn(TeamId team, PrincipalId principal) {
        return _roles.TryGetValue((team, principal), out var role) ? role : null;
    }

    public bool HasCurrentGrant(BoardId board, PrincipalId principal, DateTimeOffset? passwordSetAt) {
        return passwordSetAt is { } current && _grants.TryGetValue((board, principal), out var epoch) && epoch == current;
    }
}
