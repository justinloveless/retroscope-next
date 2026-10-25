using System.Collections.Concurrent;
using System.Collections.Frozen;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;

namespace Retroscope.Server.Data.Repos;

/// <summary>
/// The process-wide team membership cache (§c.2) and the <see cref="IAccessView"/> every decision resolves roles
/// through (R12). Only the team actor writes <c>team_members</c>, and it publishes each committed
/// roster here before anything else can observe it, so a load that read older rows never replaces a published
/// version.
/// </summary>
public sealed class MembershipCache(NpgsqlDataSource dataSource) : IMembershipLookup, IAccessView {
    // A soft-deleted team has no members: its rooms stay closed to the old roster after a restart or a Forget.
    private const string MembersSql = """
        select m.user_id, m.role from team_members m join teams t on t.id = m.team_id
        where m.team_id = $1 and t.deleted_at is null
        """;

    private readonly ConcurrentDictionary<TeamId, TeamMembership> _teams = new();

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

    private Dictionary<PrincipalId, TeamRole> ReadRoles(TeamId team) {
        using var command = MembersCommand(team);
        using var reader = command.ExecuteReader();
        var roles = new Dictionary<PrincipalId, TeamRole>();
        while (reader.Read()) {
            roles[new PrincipalId(reader.GetGuid(0))] = ParseRole(reader.GetString(1));
        }
        return roles;
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
