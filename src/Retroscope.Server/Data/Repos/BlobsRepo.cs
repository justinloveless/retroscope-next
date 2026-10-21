using Npgsql;
using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Server.Endpoints;

namespace Retroscope.Server.Data.Repos;

public enum BlobPurpose : byte {
    Avatar,
    BoardImage,
}

public enum BoardImageInsert : byte {
    Inserted,

    /// <summary>The board already holds <see cref="BlobsRepo.BoardQuotaBytes"/> of images.</summary>
    BoardFull,

    /// <summary>All board images together would pass the global ceiling (<c>BLOB_BYTES_MAX</c>).</summary>
    StoreFull,
}

/// <param name="Board">Board images only: the board's facts and the caller's access, read with the blob.</param>
public sealed record StoredBlob(BlobPurpose Purpose, string ContentType, byte[] Bytes, BoardView? Board);

/// <summary>A board as one caller sees it, read in a single query: what <see cref="Policy"/> and the stage gates need.</summary>
public sealed record BoardView(ResourceFacts Facts, IAccessView Access, RetroStage Stage, bool StagesEnabled, bool HasActionItemsColumn);

/// <summary>
/// Avatars and pasted board images (§b). Board images count against a per-board quota and a global ceiling; the
/// ones no committed card or comment references are deleted after <see cref="UnattachedTtl"/> (S7).
/// </summary>
public sealed class BlobsRepo(NpgsqlDataSource dataSource) {
    public const int BoardImageMaxBytes = 2 * 1024 * 1024;

    public const int AvatarMaxBytes = 256 * 1024;

    public const long BoardQuotaBytes = 50L * 1024 * 1024;

    public static readonly TimeSpan UnattachedTtl = TimeSpan.FromHours(24);

    // Columns 0-11 of every board read; the caller's principal is $2 (null for anonymous callers).
    private const string BoardColumns = """
        r.id, r.team_id, r.created_by, r.visibility, r.password_set_at, r.archived_at is not null,
        r.deleted_at is not null, coalesce(t.deleted_at is not null, false), m.role, g.password_epoch,
        r.stage, r.stages_enabled,
        exists (select 1 from retro_columns c where c.board_id = r.id and c.is_action_items)
        """;

    private const string BoardJoins = """
        left join teams t on t.id = r.team_id
        left join team_members m on m.team_id = r.team_id and m.user_id = $2
        left join board_access_grants g on g.board_id = r.id and g.principal_id = $2
        """;

    public async Task<StoredBlob?> GetAsync(BlobId id, PrincipalId? caller, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand($"""
            select b.purpose, b.content_type, b.bytes, {BoardColumns}
            from blobs b
            left join retro_boards r on r.id = b.board_id
            {BoardJoins}
            where b.id = $1
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id.Value });
        command.Parameters.Add(Principal(caller));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }
        var purpose = reader.GetString(0) == "avatar" ? BlobPurpose.Avatar : BlobPurpose.BoardImage;
        var board = purpose == BlobPurpose.BoardImage ? ReadBoard(reader, 3, caller) : null;
        return new StoredBlob(purpose, reader.GetString(1), reader.GetFieldValue<byte[]>(2), board);
    }

    public async Task<BoardView?> GetBoardAsync(BoardId board, PrincipalId caller, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand($"""
            select {BoardColumns}
            from retro_boards r
            {BoardJoins}
            where r.id = $1
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = board.Value });
        command.Parameters.Add(Principal(caller));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBoard(reader, 0, caller) : null;
    }

    /// <summary>
    /// Inserts the image unless the board quota or the global ceiling would be passed. Both are checked in the
    /// insert statement itself, so only uploads racing each other can overshoot, by at most their own sizes.
    /// </summary>
    public async Task<BoardImageInsert> InsertBoardImageAsync(
        BlobId id,
        BoardId board,
        PrincipalId createdBy,
        SniffedImage image,
        long ceilingBytes,
        CancellationToken cancellationToken) {
        if (image.Bytes.Length > BoardImageMaxBytes) {
            throw new ArgumentOutOfRangeException(nameof(image), "Board images are at most 2 MiB.");
        }
        await using var command = dataSource.CreateCommand("""
            with usage as (
              select coalesce(sum(byte_size) filter (where board_id = $2), 0)::bigint as board_bytes,
                     coalesce(sum(byte_size), 0)::bigint as total_bytes
              from blobs where purpose = 'board_image'
            ), inserted as (
              insert into blobs (id, purpose, board_id, content_type, byte_size, sha256, bytes, created_by)
              select $1, 'board_image', $2, $3, $4, $5, $6, $7 from usage
              where board_bytes + $4 <= $8 and total_bytes + $4 <= $9
              returning id
            )
            select board_bytes + $4 <= $8, exists (select 1 from inserted) from usage
            """);
        AddImage(command, id, board.Value, image, createdBy);
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = BoardQuotaBytes });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = ceilingBytes });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        if (reader.GetBoolean(1)) {
            return BoardImageInsert.Inserted;
        }
        return reader.GetBoolean(0) ? BoardImageInsert.StoreFull : BoardImageInsert.BoardFull;
    }

    /// <summary>For the avatar upload (U16) and the Google profile picture (U18); setting <c>users.avatar_blob_id</c> is theirs.</summary>
    public async Task InsertAvatarAsync(BlobId id, PrincipalId createdBy, SniffedImage image, CancellationToken cancellationToken) {
        if (image.Bytes.Length > AvatarMaxBytes) {
            throw new ArgumentOutOfRangeException(nameof(image), "Avatars are at most 256 KiB.");
        }
        await using var command = dataSource.CreateCommand("""
            insert into blobs (id, purpose, board_id, content_type, byte_size, sha256, bytes, created_by)
            values ($1, 'avatar', $2, $3, $4, $5, $6, $7)
            """);
        AddImage(command, id, null, image, createdBy);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Total bytes of all board images, the figure the global ceiling applies to.</summary>
    public async Task<long> BoardImageBytesAsync(CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(
            "select coalesce(sum(byte_size), 0)::bigint from blobs where purpose = 'board_image'");
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// The GC query (S7): deletes up to <paramref name="limit"/> board images that no committed card or comment
    /// attached and that were uploaded before <paramref name="createdBefore"/>. Returns how many were deleted.
    /// </summary>
    public async Task<int> DeleteUnattachedAsync(DateTimeOffset createdBefore, int limit, CancellationToken cancellationToken) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var command = dataSource.CreateCommand("""
            delete from blobs where id in (
              select id from blobs
              where purpose = 'board_image' and attached_at is null and created_at < $1
              order by created_at
              limit $2)
            """);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = createdBefore });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter<Guid?> Principal(PrincipalId? caller) =>
        new NpgsqlParameter<Guid?> { TypedValue = caller?.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid };

    private static void AddImage(NpgsqlCommand command, BlobId id, Guid? board, SniffedImage image, PrincipalId createdBy) {
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id.Value });
        command.Parameters.Add(new NpgsqlParameter<Guid?> { TypedValue = board, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = image.ContentType });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = image.Bytes.Length });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = image.Sha256 });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = image.Bytes });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = createdBy.Value });
    }

    private static BoardView? ReadBoard(NpgsqlDataReader reader, int at, PrincipalId? caller) {
        if (reader.IsDBNull(at)) {
            return null;
        }
        var board = new BoardId(reader.GetGuid(at));
        var team = reader.IsDBNull(at + 1) ? (TeamId?)null : new TeamId(reader.GetGuid(at + 1));
        var passwordSetAt = reader.IsDBNull(at + 4) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(at + 4);
        var facts = new ResourceFacts(
            RoomKey.Board(board),
            Team: team,
            CreatedBy: reader.IsDBNull(at + 2) ? null : new PrincipalId(reader.GetGuid(at + 2)),
            Visibility: reader.GetString(at + 3) switch {
                "link" => BoardVisibility.Link,
                "team" => BoardVisibility.Team,
                _ => BoardVisibility.Password,
            },
            PasswordSetAt: passwordSetAt,
            Archived: reader.GetBoolean(at + 5),
            Deleted: reader.GetBoolean(at + 6),
            TeamDeleted: reader.GetBoolean(at + 7));
        var role = reader.IsDBNull(at + 8)
            ? (TeamRole?)null
            : reader.GetString(at + 8) switch {
                "owner" => TeamRole.Owner,
                "admin" => TeamRole.Admin,
                _ => TeamRole.Member,
            };
        var grant = reader.IsDBNull(at + 9) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(at + 9);
        var stage = reader.GetString(at + 10) switch {
            "thinking" => RetroStage.Thinking,
            "voting" => RetroStage.Voting,
            "discussing" => RetroStage.Discussing,
            _ => RetroStage.Closed,
        };
        var access = new CallerBoardAccess(team, board, caller, role, grant);
        return new BoardView(facts, access, stage, reader.GetBoolean(at + 11), reader.GetBoolean(at + 12));
    }

    /// <summary>
    /// The one caller's role and grant as the database holds them at read time. Membership and grants are written by
    /// the team and board actors before they are announced, so the row is never behind the actors.
    /// </summary>
    private sealed class CallerBoardAccess(
        TeamId? boardTeam,
        BoardId readBoard,
        PrincipalId? caller,
        TeamRole? role,
        DateTimeOffset? grantEpoch) : IAccessView {
        public TeamRole? RoleIn(TeamId team, PrincipalId principal) =>
            team == boardTeam && principal == caller ? role : null;

        public bool HasCurrentGrant(BoardId board, PrincipalId principal, DateTimeOffset? passwordSetAt) =>
            board == readBoard && principal == caller && grantEpoch is not null && grantEpoch == passwordSetAt;
    }
}
