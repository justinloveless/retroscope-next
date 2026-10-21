using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Server.Data.Repos;

/// <summary>A change to an endorsement type; a null member is left as it is, an empty <see cref="Description"/> clears it.</summary>
public sealed record EndorsementTypePatch(string? Name, string? Emoji, string? Description);

/// <param name="Counts">Endorsements received per type.</param>
public sealed record LeaderboardEntry(
    PrincipalId Pid,
    string Name,
    BlobId? Avatar,
    int Total,
    IReadOnlyDictionary<EndorsementTypeId, int> Counts);

public sealed record ReceivedEndorsement(string Emoji, string TypeName, string FromName, string TeamName, long CreatedAt);

/// <summary><c>endorsement_types</c> (written by the team's admins over HTTP) and reads of <c>endorsements</c>.</summary>
public sealed class EndorsementsRepo(NpgsqlDataSource dataSource) {
    private const string TypeColumns = "id, name, emoji, archived_at is not null, description";

    /// <summary>The original's three default types (<c>seed_default_endorsement_types</c>).</summary>
    private static readonly (string Name, string Description, string Emoji)[] DefaultTypes = [
        ("Problem Solver", "Tackles tough technical challenges head-on", "🧩"),
        ("Team Player", "Goes above and beyond to help teammates", "🤝"),
        ("Innovator", "Brings creative ideas and fresh perspectives", "💡"),
    ];

    public static IEnumerable<NpgsqlBatchCommand> SeedDefaultTypes(TeamId team, DateTimeOffset now) {
        return DefaultTypes.Select((type, i) => TeamsRepo.Command(
            """
            insert into endorsement_types (id, team_id, name, description, emoji, sort_key, is_default, created_at)
            values ($1, $2, $3, $4, $5, $6, true, $7)
            """,
            Guid.CreateVersion7(now),
            team.Value,
            type.Name,
            type.Description,
            type.Emoji,
            i + 1,
            now));
    }

    /// <summary>Every type of the team in display order, archived ones included (history keeps their names).</summary>
    public async Task<IReadOnlyList<EndorsementTypeInfo>> TypesAsync(TeamId team, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(
            $"select {TypeColumns} from endorsement_types where team_id = $1 order by sort_key, id");
        command.Parameters.Add(new() { Value = team.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var types = new List<EndorsementTypeInfo>();
        while (await reader.ReadAsync(cancellationToken)) {
            types.Add(ReadType(reader));
        }
        return types;
    }

    public async Task<EndorsementTypeInfo> CreateTypeAsync(
        TeamId team,
        string name,
        string emoji,
        string? description,
        DateTimeOffset now,
        CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand($"""
            insert into endorsement_types (id, team_id, name, description, emoji, sort_key, created_at)
            select $1, $2, $3, $4, $5, coalesce(max(sort_key), 0) + 1, $6 from endorsement_types where team_id = $2
            returning {TypeColumns}
            """);
        command.Parameters.Add(new() { Value = Guid.CreateVersion7(now) });
        command.Parameters.Add(new() { Value = team.Value });
        command.Parameters.Add(new() { Value = name });
        command.Parameters.Add(TeamsRepo.Text(description));
        command.Parameters.Add(new() { Value = emoji });
        command.Parameters.Add(new() { Value = now });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadType(reader);
    }

    /// <returns>The type after the change; null when it is not the team's.</returns>
    public async Task<EndorsementTypeInfo?> UpdateTypeAsync(
        TeamId team,
        EndorsementTypeId type,
        EndorsementTypePatch patch,
        CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand($"""
            update endorsement_types set
              name = coalesce($3, name),
              emoji = coalesce($4, emoji),
              description = case when $5::text is null then description else nullif($5, '') end
            where id = $1 and team_id = $2
            returning {TypeColumns}
            """);
        command.Parameters.Add(new() { Value = type.Value });
        command.Parameters.Add(new() { Value = team.Value });
        command.Parameters.Add(TeamsRepo.Text(patch.Name));
        command.Parameters.Add(TeamsRepo.Text(patch.Emoji));
        command.Parameters.Add(TeamsRepo.Text(patch.Description));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadType(reader) : null;
    }

    /// <summary>"Delete" archives (§d): endorsements already given keep their type.</summary>
    /// <returns>False when the type is not the team's.</returns>
    public async Task<bool> ArchiveTypeAsync(TeamId team, EndorsementTypeId type, DateTimeOffset now, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            update endorsement_types set archived_at = coalesce(archived_at, $3) where id = $1 and team_id = $2
            """);
        command.Parameters.Add(new() { Value = type.Value });
        command.Parameters.Add(new() { Value = team.Value });
        command.Parameters.Add(new() { Value = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Current members ranked by endorsements received since <paramref name="since"/> (all time when null).</summary>
    public async Task<IReadOnlyList<LeaderboardEntry>> LeaderboardAsync(
        TeamId team,
        DateTimeOffset? since,
        CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand($"""
            select e.to_user_id, {UsersRepo.DisplayNameSql}, u.avatar_blob_id, e.type_id, count(*)::int
            from endorsements e
            join team_members m on m.team_id = e.team_id and m.user_id = e.to_user_id
            join users u on u.id = e.to_user_id
            where e.team_id = $1 and ($2::timestamptz is null or e.created_at >= $2)
            group by e.to_user_id, u.nickname, u.full_name, u.avatar_blob_id, e.type_id
            """);
        command.Parameters.Add(new() { Value = team.Value });
        command.Parameters.Add(new() { Value = (object?)since ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var people = new Dictionary<PrincipalId, (string Name, BlobId? Avatar, Dictionary<EndorsementTypeId, int> Counts)>();
        while (await reader.ReadAsync(cancellationToken)) {
            var pid = new PrincipalId(reader.GetGuid(0));
            if (!people.TryGetValue(pid, out var person)) {
                person = (reader.GetString(1), reader.IsDBNull(2) ? null : new BlobId(reader.GetGuid(2)), []);
                people[pid] = person;
            }
            person.Counts[new EndorsementTypeId(reader.GetGuid(3))] = reader.GetInt32(4);
        }
        return [
            .. people
                .Select(x => new LeaderboardEntry(x.Key, x.Value.Name, x.Value.Avatar, x.Value.Counts.Values.Sum(), x.Value.Counts))
                .OrderByDescending(x => x.Total)
                .ThenBy(x => x.Pid.Value),
        ];
    }

    /// <summary>What <paramref name="user"/> received, newest first, from teams that still exist.</summary>
    public async Task<IReadOnlyList<ReceivedEndorsement>> ReceivedAsync(PrincipalId user, int limit, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand($"""
            select t.emoji, t.name, {UsersRepo.DisplayNameSql}, tm.name, e.created_at
            from endorsements e
            join endorsement_types t on t.id = e.type_id
            join users u on u.id = e.from_user_id
            join teams tm on tm.id = e.team_id and tm.deleted_at is null
            where e.to_user_id = $1
            order by e.created_at desc, e.id
            limit $2
            """);
        command.Parameters.Add(new() { Value = user.Value });
        command.Parameters.Add(new() { Value = limit });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var received = new List<ReceivedEndorsement>();
        while (await reader.ReadAsync(cancellationToken)) {
            received.Add(new ReceivedEndorsement(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4).ToUnixTimeMilliseconds()));
        }
        return received;
    }

    private static EndorsementTypeInfo ReadType(NpgsqlDataReader reader) => new(
        new EndorsementTypeId(reader.GetGuid(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetBoolean(3),
        reader.IsDBNull(4) ? null : reader.GetString(4));
}
