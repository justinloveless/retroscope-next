using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;

namespace Retroscope.Server.Data.Repos;

[JsonConverter(typeof(StrictEnumConverter<InviteKind>))]
public enum InviteKind : byte {
    [JsonStringEnumMemberName("email")] Email,
    [JsonStringEnumMemberName("link")] Link,
}

/// <param name="TokenBox">Link invites only: the token sealed so admins can copy the link again.</param>
public sealed record InviteRow(
    Guid Id,
    TeamId Team,
    InviteKind Kind,
    string? Email,
    TeamRole Role,
    byte[]? TokenBox,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>An invite found by its token, with what accepting it depends on.</summary>
public sealed record InviteLookup(
    InviteRow Invite,
    string TeamName,
    bool TeamDeleted,
    bool Revoked,
    PrincipalId? AcceptedBy);

/// <summary><c>team_invites</c>. Tokens are stored as SHA-256 hashes; only link invites keep a sealed copy.</summary>
public sealed class InvitesRepo(NpgsqlDataSource dataSource) {
    private const string Columns = "i.id, i.team_id, i.kind, i.email, i.role, i.token_box, i.created_at, i.expires_at";

    public static NpgsqlBatchCommand Insert(InviteRow invite, byte[] tokenHash, PrincipalId createdBy) => TeamsRepo.Command(
        """
        insert into team_invites (id, team_id, kind, email, role, token_hash, token_box, created_by, created_at, expires_at)
        values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
        """,
        invite.Id,
        invite.Team.Value,
        KindName(invite.Kind),
        TeamsRepo.Text(invite.Email),
        MembershipCache.RoleName(invite.Role),
        tokenHash,
        new NpgsqlParameter { Value = (object?)invite.TokenBox ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bytea },
        createdBy.Value,
        invite.CreatedAt,
        invite.ExpiresAt);

    /// <summary>Invites that can still be used: not revoked, not expired, and (email) not accepted yet.</summary>
    public async Task<IReadOnlyList<InviteRow>> LiveAsync(TeamId team, DateTimeOffset now, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand($"""
            select {Columns}
            from team_invites i
            where i.team_id = $1 and i.revoked_at is null and i.expires_at > $2 and (i.kind = 'link' or i.accepted_at is null)
            order by i.created_at desc, i.id
            """);
        command.Parameters.Add(new() { Value = team.Value });
        command.Parameters.Add(new() { Value = now });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var invites = new List<InviteRow>();
        while (await reader.ReadAsync(cancellationToken)) {
            invites.Add(ReadInvite(reader));
        }
        return invites;
    }

    public async Task<InviteLookup?> FindByTokenAsync(byte[] tokenHash, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand($"""
            select {Columns}, t.name, t.deleted_at is not null, i.revoked_at is not null, i.accepted_by
            from team_invites i join teams t on t.id = i.team_id
            where i.token_hash = $1
            """);
        command.Parameters.Add(new() { Value = tokenHash });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }
        return new InviteLookup(
            ReadInvite(reader),
            reader.GetString(8),
            reader.GetBoolean(9),
            reader.GetBoolean(10),
            reader.IsDBNull(11) ? null : new PrincipalId(reader.GetGuid(11)));
    }

    /// <returns>False when the invite is not the team's or was already revoked.</returns>
    public Task<bool> RevokeAsync(TeamId team, Guid invite, PrincipalId by, DateTimeOffset now, CancellationToken cancellationToken) {
        return TeamsRepo.WriteIfAsync(
            dataSource,
            TeamsRepo.Command(
                "update team_invites set revoked_at = $3 where id = $1 and team_id = $2 and revoked_at is null",
                invite,
                team.Value,
                now),
            TeamsRepo.Audit("team.invite.revoke", by, team, invite.ToString(), new Dictionary<string, string>(), now),
            cancellationToken);
    }

    /// <summary>
    /// Takes a single-use email invite for <paramref name="user"/>: true only for the one request that marks it, while
    /// it is unrevoked and unexpired.
    /// </summary>
    public async Task<bool> ClaimEmailInviteAsync(Guid invite, PrincipalId user, DateTimeOffset now, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            update team_invites set accepted_at = $3, accepted_by = $2, use_count = use_count + 1
            where id = $1 and kind = 'email' and accepted_at is null and revoked_at is null and expires_at > $3
            """);
        command.Parameters.Add(new() { Value = invite });
        command.Parameters.Add(new() { Value = user.Value });
        command.Parameters.Add(new() { Value = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Gives an email invite back when the membership could not be written after <see cref="ClaimEmailInviteAsync"/>.</summary>
    public async Task ReleaseEmailInviteAsync(Guid invite, PrincipalId user, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            update team_invites set accepted_at = null, accepted_by = null, use_count = use_count - 1
            where id = $1 and accepted_by = $2
            """);
        command.Parameters.Add(new() { Value = invite });
        command.Parameters.Add(new() { Value = user.Value });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// The associated data of <c>token_box</c>: the invite id's 16 bytes in RFC 4122 order, the layout the importer seals
    /// with (<c>InvitesMap.InviteAssociatedData</c>, U39 decision 2).
    /// </summary>
    public static byte[] AssociatedData(Guid invite) {
        var aad = new byte[16];
        invite.TryWriteBytes(aad, bigEndian: true, out _);
        return aad;
    }

    public static NpgsqlBatchCommand CountUse(Guid invite) =>
        TeamsRepo.Command("update team_invites set use_count = use_count + 1 where id = $1", invite);

    public static string KindName(InviteKind kind) => kind == InviteKind.Email ? "email" : "link";

    private static InviteRow ReadInvite(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        new TeamId(reader.GetGuid(1)),
        reader.GetString(2) == "email" ? InviteKind.Email : InviteKind.Link,
        reader.IsDBNull(3) ? null : reader.GetString(3),
        MembershipCache.ParseRole(reader.GetString(4)),
        reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5),
        reader.GetFieldValue<DateTimeOffset>(6),
        reader.GetFieldValue<DateTimeOffset>(7));
}
