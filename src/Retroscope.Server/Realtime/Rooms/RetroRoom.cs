using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Stores;

namespace Retroscope.Server.Realtime.Rooms;

public static class RetroRoom {
    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.TryAddSingleton<IAnonTagger>(_ => new ConfiguredAnonTagger(configuration));
        services.AddRoom(sp => new RetroBinding(sp.GetRequiredService<IAnonTagger>()), Store);
    }

    public static void Map(IEndpointRouteBuilder app) {
    }

    /// <summary>
    /// A comment posted from the team page on an action item (<c>POST /api/teams/{id}/action-items/{itemId}/comments</c>):
    /// the item must belong to <paramref name="team"/> and still have its source card, and the comment goes to that
    /// card's board actor, which checks the caller's membership at decide time.
    /// </summary>
    public static async Task<AskResult> AddActionItemCommentAsync(
        ActorRegistry registry,
        NpgsqlDataSource dataSource,
        TeamId team,
        ActionItemId item,
        CommentId comment,
        string body,
        Viewer viewer,
        CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(
            "select source_board_id, source_card_id from team_action_items where id = $1 and team_id = $2");
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = item.Value });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = team.Value });
        BoardId board;
        CardId card;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)) {
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0) || reader.IsDBNull(1)) {
                return new AskResult(AskStatus.NotFound);
            }
            board = new BoardId(reader.GetGuid(0));
            card = new CardId(reader.GetGuid(1));
        }
        return await registry.AskAsync<RetroState, RetroInput>(
            RoomKey.Board(board),
            new RetroInput.ActionItemCommentAdd(comment, card, body),
            viewer,
            cancellationToken);
    }

    private static RetroStore Store(IServiceProvider services) {
        return new RetroStore(
            services.GetRequiredService<NpgsqlDataSource>(),
            services.GetRequiredService<Leader>(),
            services.GetRequiredService<CursorRepo>(),
            services.GetService<ITeamNotices>(),
            services.GetRequiredService<ILogger<RetroStore>>());
    }
}

/// <summary>The board room kind (§c.4 Retro) on the actor runtime.</summary>
public sealed class RetroBinding(IAnonTagger tagger) : IRoomBinding<RetroState, RetroInput, RetroEvent> {
    private readonly RetroProjection _projection = new(tagger);

    public RoomKind Kind => RoomKind.Board;

    public IRoomDomain<RetroState, RetroInput, RetroEvent> Domain { get; } = new RetroDecider(tagger);

    public static ResourceFacts Facts(RetroState state) {
        return new ResourceFacts(RoomKey.Board(state.BoardId), state.TeamId, Archived: state.Archived);
    }

    public RetroInput? FromClient(Command command) => command is RetroCommand retro ? new RetroInput.Client(retro) : null;

    public bool IsCreate(Command command) => command is RetroCardAdd or RetroCommentAdd or RetroColumnAdd or RetroEndorseGive;

    /// <summary>
    /// Team membership and settings are written by the team actor, so the board only sequences those changes. The
    /// deletion route soft-deletes the row first, and the kick that follows is all subscribers see of it.
    /// </summary>
    public EventDurability DurabilityOf(RetroEvent evt) {
        return evt switch {
            MembersReplaced or MaxEndorsementsChanged => EventDurability.Ephemeral,
            BoardDeleted => EventDurability.MemoryOnly,
            _ => EventDurability.Committed,
        };
    }

    public bool IsDeleted(RetroState state) => state.Deleted;

    public bool CanSubscribe(RetroState state, Viewer viewer, IAccessView access) {
        return Policy.Can(viewer, Capability.Subscribe, Facts(state), access);
    }

    // Every subscriber is a member of the board's team (UX18), so the members-only snapshot variant is the only one.
    public RoomSnapshot Snapshot(RetroState state, IReadOnlyList<PrincipalInfo> presence, bool forMembers) {
        return RetroProjection.Snapshot(state, presence);
    }

    public RoomOverlay? Overlay(RetroState state, Viewer viewer) => _projection.Overlay(state, viewer.Who.Id);

    public IReadOnlyList<WireEvent> Project(RetroState state, IReadOnlyList<RetroEvent> events, PrincipalId viewer, bool viewerIsMember) {
        return _projection.ForViewer(state, events, viewer);
    }
}

/// <summary>
/// <c>ANON_TAG_KEY</c>, read on first use: a host that never serves a board (the health probe, the route inventory)
/// starts without it, and the first anonymous post or ownership check fails loudly when it is missing.
/// </summary>
internal sealed class ConfiguredAnonTagger(IConfiguration configuration) : IAnonTagger {
    private readonly Lazy<AnonTagger> _tagger = new(() => AnonTagger.Parse(
        configuration["ANON_TAG_KEY"] ?? throw new InvalidOperationException("ANON_TAG_KEY is not set.")));

    public byte[] Tag(BoardId boardId, PrincipalId principalId) => _tagger.Value.Tag(boardId, principalId);

    public bool Owns(ReadOnlySpan<byte> tag, BoardId boardId, PrincipalId principalId) => _tagger.Value.Owns(tag, boardId, principalId);
}
