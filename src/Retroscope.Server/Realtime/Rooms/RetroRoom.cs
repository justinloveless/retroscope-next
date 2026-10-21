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
        // The membership cache and the team notices come from the team room (U23). Resolved with GetService so the
        // board room still starts without them; until then every role and grant reads as absent.
        services.AddSingleton(sp => RoomType.Of(
            new RetroBinding(sp.GetRequiredService<IAnonTagger>()),
            Store(sp),
            sp.GetService<IAccessView>() ?? NoAccess.Instance));
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

    private sealed class NoAccess : IAccessView {
        public static readonly NoAccess Instance = new();

        public TeamRole? RoleIn(TeamId team, PrincipalId principal) => null;

        public bool HasCurrentGrant(BoardId board, PrincipalId principal, DateTimeOffset? passwordSetAt) => false;
    }
}

/// <summary>The board room kind (§c.4 Retro) on the actor runtime.</summary>
public sealed class RetroBinding(IAnonTagger tagger) : IRoomBinding<RetroState, RetroInput, RetroEvent> {
    private readonly RetroProjection _projection = new(tagger);

    public RoomKind Kind => RoomKind.Board;

    public IRoomDomain<RetroState, RetroInput, RetroEvent> Domain { get; } = new RetroDecider(tagger);

    public static ResourceFacts Facts(RetroState state) {
        return new ResourceFacts(
            RoomKey.Board(state.BoardId),
            state.TeamId,
            state.CreatedBy,
            state.Visibility,
            state.PasswordSetAt,
            state.Archived);
    }

    public RetroInput? FromClient(Command command) => command is RetroCommand retro ? new RetroInput.Client(retro) : null;

    /// <summary>
    /// Team membership and settings are written by the team actor, and visibility and password by their HTTP route
    /// before it asks the board, so the board only sequences those changes.
    /// </summary>
    public EventDurability DurabilityOf(RetroEvent evt) {
        return evt is MembersReplaced or MaxEndorsementsChanged or AccessChanged ? EventDurability.Ephemeral : EventDurability.Committed;
    }

    public bool CanSubscribe(RetroState state, Viewer viewer, IAccessView access) {
        return Policy.Can(viewer, Capability.Subscribe, Facts(state), access);
    }

    public bool IsMember(RetroState state, Viewer viewer, IAccessView access) {
        return Policy.Can(viewer, Capability.RetroViewRoster, Facts(state), access);
    }

    public RoomSnapshot Snapshot(RetroState state, IReadOnlyList<PrincipalInfo> presence, bool forMembers) {
        return RetroProjection.Snapshot(state, presence, forMembers);
    }

    public RoomOverlay? Overlay(RetroState state, Viewer viewer) => _projection.Overlay(state, viewer.Who.Id);

    public IReadOnlyList<WireEvent> Project(RetroState state, IReadOnlyList<RetroEvent> events, PrincipalId viewer, bool viewerIsMember) {
        return _projection.ForViewer(state, events, viewer, viewerIsMember);
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
