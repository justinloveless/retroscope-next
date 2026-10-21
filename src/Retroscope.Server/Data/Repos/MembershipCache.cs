using System.Collections.Concurrent;
using System.Collections.Frozen;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;

namespace Retroscope.Server.Data.Repos;

/// <summary>
/// The process-wide team membership cache (§c.2) and the <see cref="IAccessView"/> every decision resolves roles
/// and password grants through (R12). Only the team actor writes <c>team_members</c>, and it publishes each committed
/// roster here before anything else can observe it, so a load that read older rows never replaces a published
/// version.
/// </summary>
public sealed class MembershipCache(NpgsqlDataSource dataSource) : IMembershipLookup, IAccessView {
    private const int MaxGrants = 16_384;

    private const string MembersSql = "select user_id, role from team_members where team_id = $1";

    private const string GrantSql = "select password_epoch from board_access_grants where board_id = $1 and principal_id = $2";

    private readonly ConcurrentDictionary<TeamId, TeamMembership> _teams = new();
    private readonly ConcurrentDictionary<(BoardId Board, PrincipalId Principal), DateTimeOffset> _grants = new();

    public bool TryGet(TeamId team, out TeamMembership membership) => _teams.TryGetValue(team, out membership!);

    public async ValueTask<TeamMembership> LoadAsync(TeamId team, CancellationToken cancellationToken) {
        if (_teams.TryGetValue(team, out var cached)) {
            return cached;
        }
        await using var command = MembersCommand(team);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var roles = new Dictionary<PrincipalId, TeamRole>();
        while (await reader.ReadAsync(cancellationToken)) {
            roles[new PrincipalId(reader.GetGuid(0))] = ParseRole(reader.GetString(1));
        }
        return Seed(team, roles);
    }

    /// <summary>
    /// Caches roles read from the database unless a version is already cached: a cached version is never older
    /// than the rows (the team actor publishes after its commit), so a slow load must not overwrite it.
    /// </summary>
    public TeamMembership Seed(TeamId team, IReadOnlyDictionary<PrincipalId, TeamRole> roles) {
        return _teams.GetOrAdd(team, _ => new TeamMembership(0, roles.ToFrozenDictionary()));
    }

    /// <summary>The team actor's committed roster: always replaces the cached version and bumps it.</summary>
    public TeamMembership Publish(TeamId team, IReadOnlyDictionary<PrincipalId, TeamRole> roles) {
        var frozen = roles.ToFrozenDictionary();
        return _teams.AddOrUpdate(
            team,
            _ => new TeamMembership(1, frozen),
            (_, current) => new TeamMembership(current.Version + 1, frozen));
    }

    /// <summary>Drops the cached version; the next lookup reads the rows (after a commit whose outcome is unknown).</summary>
    public void Forget(TeamId team) => _teams.TryRemove(team, out _);

    public TeamRole? RoleIn(TeamId team, PrincipalId principal) {
        if (!_teams.TryGetValue(team, out var membership)) {
            // Callers preload through LoadAsync (rooms on load, the resolver before its check); this is the fallback
            // for one that did not, and it runs once per team per process.
            membership = Seed(team, ReadRoles(team));
        }
        return membership.RoleOf(principal);
    }

    /// <summary>
    /// A grant is current when its epoch equals the board's <c>password_set_at</c>. Only matching epochs are cached:
    /// a miss or an older epoch reads the row again, because the board actor may have granted or re-granted since.
    /// </summary>
    public bool HasCurrentGrant(BoardId board, PrincipalId principal, DateTimeOffset? passwordSetAt) {
        if (passwordSetAt is not { } current) {
            return false;
        }
        if (_grants.TryGetValue((board, principal), out var epoch) && epoch == current) {
            return true;
        }
        if (ReadGrant(board, principal) is not { } stored || stored != current) {
            return false;
        }
        if (_grants.Count >= MaxGrants) {
            _grants.Clear();
        }
        _grants[(board, principal)] = stored;
        return true;
    }

    private Dictionary<PrincipalId, TeamRole> ReadRoles(TeamId team) {
        using var command = MembersCommand(team);
        using var reader = command.ExecuteReader();
        var roles = new Dictionary<PrincipalId, TeamRole>();
        while (reader.Read()) {
            roles[new PrincipalId(reader.GetGuid(0))] = ParseRole(reader.GetString(1));
        }
        return roles;
    }

    private DateTimeOffset? ReadGrant(BoardId board, PrincipalId principal) {
        using var command = dataSource.CreateCommand(GrantSql);
        command.Parameters.Add(new() { Value = board.Value });
        command.Parameters.Add(new() { Value = principal.Value });
        using var reader = command.ExecuteReader();
        return reader.Read() ? reader.GetFieldValue<DateTimeOffset>(0) : null;
    }

    private NpgsqlCommand MembersCommand(TeamId team) {
        var command = dataSource.CreateCommand(MembersSql);
        command.Parameters.Add(new() { Value = team.Value });
        return command;
    }

    public static TeamRole ParseRole(string role) => role switch {
        "owner" => TeamRole.Owner,
        "admin" => TeamRole.Admin,
        "member" => TeamRole.Member,
        _ => throw new InvalidOperationException($"Unknown team role '{role}'."),
    };

    public static string RoleName(TeamRole role) => role switch {
        TeamRole.Owner => "owner",
        TeamRole.Admin => "admin",
        TeamRole.Member => "member",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}
