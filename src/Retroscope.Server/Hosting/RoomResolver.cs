using Npgsql;
using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Server.Hosting;

public enum RoomAccess {
    Granted,

    /// <summary>A password board the caller holds no current grant for: anyone with the code may try the password.</summary>
    Password,
}

public abstract record RoomResolution {
    private RoomResolution() {
    }

    public sealed record Found(RoomKey Room, ResourceFacts Facts, RoomAccess Access) : RoomResolution;

    /// <summary>Missing, deleted or forbidden: callers must not be able to tell which (S9).</summary>
    public sealed record Hidden : RoomResolution;

    public sealed record Limited(TimeSpan RetryAfter) : RoomResolution;
}

/// <summary>Whether a caller may subscribe to a room; the default asks <see cref="Policy"/> (U9).</summary>
public interface IRoomAccess {
    ValueTask<bool> CanSubscribeAsync(Principal? who, ResourceFacts facts, CancellationToken cancellationToken);
}

/// <summary>
/// The one place a room code becomes a room (S9): shell routes, <c>/api/boot</c>, <c>by-code</c> and WebSocket
/// <c>sub</c> by code all come here. Lookups are limited to 30/min per IP, and a missing room and a forbidden one
/// produce the same <see cref="RoomResolution.Hidden"/> and the same <see cref="HiddenResult"/> response.
/// </summary>
public sealed class RoomResolver(NpgsqlDataSource dataSource, IRoomAccess access, RateLimiter limiter) {
    public const string HiddenCode = "room_not_found";

    // Exact match first, then case-insensitive (§d); upper(code) is indexed.
    private const string PokerSql = """
        select s.id, s.team_id, s.created_by, t.deleted_at is not null
        from poker_sessions s left join teams t on t.id = s.team_id
        where upper(s.code) = $1
        order by s.code = $2 desc, s.created_at, s.id
        limit 1
        """;

    private const string BoardSql = """
        select b.id, b.team_id, b.created_by, b.visibility, b.password_set_at,
               b.archived_at is not null, b.deleted_at is not null, t.deleted_at is not null
        from retro_boards b left join teams t on t.id = b.team_id
        where upper(b.code) = $1
        order by b.code = $2 desc, b.created_at, b.id
        limit 1
        """;

    public static IResult HiddenResult() => Problem.Result(StatusCodes.Status404NotFound, HiddenCode);

    /// <summary>The response for a non-<see cref="RoomResolution.Found"/> outcome.</summary>
    public static IResult Result(RoomResolution resolution) => resolution switch {
        RoomResolution.Limited limited => RateLimits.LimitedResult(limited.RetryAfter),
        _ => HiddenResult(),
    };

    /// <param name="kind">Poker or board; team rooms are addressed by id, never by code.</param>
    public async Task<RoomResolution> ResolveAsync(
        HttpContext context,
        RoomKind kind,
        string code,
        CancellationToken cancellationToken) {
        if (!limiter.TryAcquire(RateLimits.RoomLookup, new RateKeys(ClientIp.LimitKey(context), null), out var retryAfter)) {
            return new RoomResolution.Limited(retryAfter);
        }
        if (!RoomCode.TryParse(code, out var parsed)) {
            return new RoomResolution.Hidden();
        }
        var facts = kind switch {
            RoomKind.Poker => await LookupPokerAsync(parsed, cancellationToken),
            RoomKind.Board => await LookupBoardAsync(parsed, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only poker and board rooms have codes."),
        };
        if (facts is null || facts.Deleted || facts.TeamDeleted) {
            return new RoomResolution.Hidden();
        }

        var caller = await context.GetCallerAsync();
        if (await access.CanSubscribeAsync(caller?.Principal, facts, cancellationToken)) {
            return new RoomResolution.Found(facts.Room, facts, RoomAccess.Granted);
        }
        return facts.Visibility == BoardVisibility.Password
            ? new RoomResolution.Found(facts.Room, facts, RoomAccess.Password)
            : new RoomResolution.Hidden();
    }

    private async Task<ResourceFacts?> LookupPokerAsync(RoomCode code, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(PokerSql);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = code.LookupKey });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = code.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }
        return new ResourceFacts(
            RoomKey.Poker(new SessionId(reader.GetGuid(0))),
            Team: reader.IsDBNull(1) ? null : new TeamId(reader.GetGuid(1)),
            CreatedBy: reader.IsDBNull(2) ? null : new PrincipalId(reader.GetGuid(2)),
            TeamDeleted: reader.GetBoolean(3));
    }

    private async Task<ResourceFacts?> LookupBoardAsync(RoomCode code, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(BoardSql);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = code.LookupKey });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = code.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }
        return new ResourceFacts(
            RoomKey.Board(new BoardId(reader.GetGuid(0))),
            Team: reader.IsDBNull(1) ? null : new TeamId(reader.GetGuid(1)),
            CreatedBy: reader.IsDBNull(2) ? null : new PrincipalId(reader.GetGuid(2)),
            Visibility: reader.GetString(3) switch {
                "link" => BoardVisibility.Link,
                "team" => BoardVisibility.Team,
                _ => BoardVisibility.Password,
            },
            PasswordSetAt: reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            Archived: reader.GetBoolean(5),
            Deleted: reader.GetBoolean(6),
            TeamDeleted: reader.GetBoolean(7));
    }
}

/// <summary>
/// <see cref="Policy.Can(Principal?, Capability, ResourceFacts, IAccessView)"/> with <c>Subscribe</c>. Team
/// membership is loaded into the cache first, because <see cref="IAccessView"/> never does IO. Until the membership
/// cache (U23) registers an access view, every role and grant reads as absent.
/// </summary>
public sealed class PolicyRoomAccess(IServiceProvider services) : IRoomAccess {
    public async ValueTask<bool> CanSubscribeAsync(Principal? who, ResourceFacts facts, CancellationToken cancellationToken) {
        if (facts.Team is { } team && services.GetService<IMembershipLookup>() is { } memberships) {
            await memberships.LoadAsync(team, cancellationToken);
        }
        var view = services.GetService<IAccessView>() ?? NoAccess.Instance;
        return Policy.Can(who, Capability.Subscribe, facts, view);
    }

    private sealed class NoAccess : IAccessView {
        public static readonly NoAccess Instance = new();

        public TeamRole? RoleIn(TeamId team, PrincipalId principal) => null;

        public bool HasCurrentGrant(BoardId board, PrincipalId principal, DateTimeOffset? passwordSetAt) => false;
    }
}
