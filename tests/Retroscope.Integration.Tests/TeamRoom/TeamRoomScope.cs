using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Core.Team;
using Retroscope.Integration.Tests.Actors;
using Retroscope.Integration.Tests.Leadership;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Rooms;
using Retroscope.Server.Realtime.Stores;
using static Retroscope.Integration.Tests.Actors.ActorHarness;

namespace Retroscope.Integration.Tests.TeamRoom;

/// <summary>
/// A migrated database with one team, a leader, the real team store and binding, and live poker and board rooms of
/// that team on in-memory stores (their own stores belong to U21/U22).
/// </summary>
internal sealed class TeamRoomScope : IAsyncDisposable {
    public static readonly TeamId Team = new(Id(0x7000));
    public static readonly RoomKey TeamKey = RoomKey.Team(Team);
    public static readonly SessionId Session = new(Id(0x7100));
    public static readonly BoardId Board = new(Id(0x7200));
    public static readonly ColumnId ActionColumn = new(Id(0x7300));

    private TeamRoomScope(TestDatabase database, Leader leader, NpgsqlDataSource dataSource) {
        Database = database;
        Leader = leader;
        DataSource = dataSource;
        Cache = new MembershipCache(dataSource);
        Store = new TeamStore(dataSource, leader, new CursorRepo(dataSource), Cache);
        Registry = NewRegistry();
        Notices = new TeamNotices(() => Registry);
    }

    public TestDatabase Database { get; }

    public Leader Leader { get; }

    public NpgsqlDataSource DataSource { get; }

    public FakeTimeProvider Time { get; } = Clock();

    public MembershipCache Cache { get; }

    public TeamStore Store { get; }

    public ActorRegistry Registry { get; private set; }

    public TeamNotices Notices { get; }

    public static async Task<TeamRoomScope> StartAsync(PostgresFixture postgres) {
        var database = await postgres.CreateDatabaseAsync();
        var leader = LeaderHarness.Create(database);
        await leader.StartAsync(Token);
        var scope = new TeamRoomScope(database, leader, Db.CreateDataSource(database.AppConnectionString));
        await scope.ExecAsync("insert into teams (id, name, point_descriptions) values ($1, 'Platform', '{\"1\":\"tiny\"}')", Team.Value);
        await scope.ExecAsync("insert into retro_boards (id, code, team_id, title, visibility) values ($1, 'BOARD1', $2, 'Sprint 1', 'link')", Board.Value, Team.Value);
        await scope.ExecAsync("""
            insert into retro_columns (id, board_id, title, color, sort_key, is_action_items) values ($1, $2, 'Actions', 'green', 0, true)
            """, ActionColumn.Value, Board.Value);
        await scope.ExecAsync("insert into poker_sessions (id, code, team_id) values ($1, 'POKER1', $2)", Session.Value, Team.Value);
        return scope;
    }

    /// <summary>A second process on the same database: nothing cached, nothing live.</summary>
    public void Restart() => Registry = NewRegistry();

    public async Task AddUserAsync(Viewer viewer, TeamRole? role = null) {
        var id = viewer.Who.Id.Value;
        await ExecAsync("insert into principals (id, kind) values ($1, 'user')", id);
        await ExecAsync("insert into users (id, email, full_name) values ($1, $2, $3)", id, $"u{id:N}@example.com", viewer.Who.DisplayName);
        if (role is { } member) {
            await ExecAsync("insert into team_members (team_id, user_id, role) values ($1, $2, $3)", Team.Value, id, MembershipCache.RoleName(member));
        }
    }

    public async Task<CardId> AddCardAsync(int n, string body, int version = 1) {
        var card = new CardId(Id(0x7400 + n));
        await ExecAsync(
            "insert into retro_cards (id, board_id, column_id, body, version) values ($1, $2, $3, $4, $5)",
            card.Value, Board.Value, ActionColumn.Value, body, version);
        return card;
    }

    /// <summary>The board actor's create in place (§c.2): the row exists before the team actor hears of it.</summary>
    public async Task<ActionItemInfo> AddActionItemAsync(int n, CardId? card, string text, int sourceVersion = 1) {
        var item = new ActionItemInfo(
            new ActionItemId(Id(0x7500 + n)),
            text,
            sourceVersion,
            Start.AddMinutes(n).ToUnixTimeMilliseconds(),
            0,
            Board,
            card);
        await ExecAsync("""
            insert into team_action_items (id, team_id, text, source_board_id, source_card_id, source_version, created_at)
            values ($1, $2, $3, $4, $5, $6, $7)
            """, item.Id.Value, Team.Value, text, Board.Value, card?.Value, sourceVersion, DateTimeOffset.FromUnixTimeMilliseconds(item.CreatedAt));
        return item;
    }

    public async Task<TestPeer> SubscribeAsync(Viewer viewer, RoomKey? room = null) {
        var peer = new TestPeer(viewer);
        Assert.Equal(PostResult.Accepted, await Registry.SubscribeAsync(room ?? TeamKey, peer, viewer, null, null, Token));
        return peer;
    }

    public static async Task<TeamSnapshot> JoinAsync(TestPeer peer) {
        var snap = await peer.NextAsync<SnapFrame>();
        return Assert.IsType<TeamSnapshot>(snap.D);
    }

    public Task<AskResult> AskAsync(TeamInput input) => Registry.AskAsync<TeamState, TeamInput>(TeamKey, input, null, Token);

    public Task<PostResult> CommandAsync(TestPeer peer, long q, Command command) {
        return Registry.CommandAsync(TeamKey, peer, peer.Viewer, Cmd(q, command, TeamKey), Token).AsTask();
    }

    public Task<T> QueryAsync<T>(Func<TeamState, T> query) {
        return Registry.QueryAsync<TeamState, TeamInput, T>(TeamKey, view => ValueTask.FromResult(query(view.State!)), Token);
    }

    /// <summary>Waits until every live room has handled what was queued on its internal queue.</summary>
    public async Task SettleAsync() {
        await QueryAsync(_ => 0);
        if (Registry.TryGet(RoomKey.Poker(Session), out _)) {
            await Registry.QueryAsync<PokerState, PokerInput, int>(RoomKey.Poker(Session), _ => ValueTask.FromResult(0), Token);
        }
        if (Registry.TryGet(RoomKey.Board(Board), out _)) {
            await Registry.QueryAsync<RetroState, RetroInput, int>(RoomKey.Board(Board), _ => ValueTask.FromResult(0), Token);
        }
        await QueryAsync(_ => 0);
    }

    public async Task ExecAsync(string sql, params object?[] values) {
        await using var connection = await LeaderHarness.OpenAsync(Database.OwnerConnectionString);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) {
            command.Parameters.Add(new() { Value = value ?? DBNull.Value });
        }
        await command.ExecuteNonQueryAsync(Token);
    }

    public async Task<T?> ScalarAsync<T>(string sql, params object[] values) {
        await using var connection = await LeaderHarness.OpenAsync(Database.OwnerConnectionString);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) {
            command.Parameters.Add(new() { Value = value });
        }
        var result = await command.ExecuteScalarAsync(Token);
        return result is DBNull or null ? default : (T)result;
    }

    public async ValueTask DisposeAsync() {
        await DataSource.DisposeAsync();
        await Leader.DisposeAsync();
    }

    private ActorRegistry NewRegistry() {
        var fanOut = new TeamFanOut(Store, Cache, () => Registry, NullLogger<TeamFanOut>.Instance);
        var poker = new PokerState { SessionId = Session, Code = "POKER1", TeamId = Team, WarmUntil = Start };
        var board = () => new RetroState { BoardId = Board, Code = "BOARD1", TeamId = Team, Title = "Sprint 1" };
        return new ActorRegistry(Runtime(Time), [
            RoomType.Of(new TeamBinding(Time), fanOut, Cache),
            RoomType.Of(new PokerBinding(), new MemoryStore<PokerState, PokerEvent>(poker, x => x.Clone(), PokerApply.Apply), Cache),
            RoomType.Of(new BoardBinding(), new MemoryStore<RetroState, RetroEvent>(board(), _ => board(), RetroApply.Apply), Cache),
        ]);
    }

    /// <summary>Enough of U21's binding to run a team session; anyone may subscribe.</summary>
    private sealed class PokerBinding : IRoomBinding<PokerState, PokerInput, PokerEvent> {
        public RoomKind Kind => RoomKind.Poker;

        public IRoomDomain<PokerState, PokerInput, PokerEvent> Domain { get; } = new PokerDomain();

        public PokerInput? FromClient(Command command) => command is PokerCommand poker ? (PokerInput)poker : null;

        public EventDurability DurabilityOf(PokerEvent evt) => evt.Durability;

        public bool CanSubscribe(PokerState state, Viewer viewer, IAccessView access) => true;

        public RoomSnapshot Snapshot(PokerState state, IReadOnlyList<PrincipalInfo> presence, bool forMembers) {
            return PokerProjection.Snapshot(state, presence);
        }

        public RoomOverlay? Overlay(PokerState state, Viewer viewer) => PokerProjection.Overlay(state, viewer.Who.Id, viewer.Client);

        public IReadOnlyList<WireEvent> Project(PokerState state, IReadOnlyList<PokerEvent> events, PrincipalId viewer, bool viewerIsMember) {
            return PokerProjection.Events(events, viewer);
        }
    }

    /// <summary>Enough of U22's binding for a link team board: anyone may subscribe, members get the roster variant.</summary>
    private sealed class BoardBinding : IRoomBinding<RetroState, RetroInput, RetroEvent> {
        private readonly RetroProjection _projection = new(NoTags.Instance);

        public RoomKind Kind => RoomKind.Board;

        public IRoomDomain<RetroState, RetroInput, RetroEvent> Domain { get; } = new RetroDecider(NoTags.Instance);

        public RetroInput? FromClient(Command command) => command is RetroCommand retro ? (RetroInput)retro : null;

        public bool CanSubscribe(RetroState state, Viewer viewer, IAccessView access) => true;

        public RoomSnapshot Snapshot(RetroState state, IReadOnlyList<PrincipalInfo> presence, bool forMembers) {
            return RetroProjection.Snapshot(state, presence);
        }

        public RoomOverlay? Overlay(RetroState state, Viewer viewer) => _projection.Overlay(state, viewer.Who.Id);

        public IReadOnlyList<WireEvent> Project(RetroState state, IReadOnlyList<RetroEvent> events, PrincipalId viewer, bool viewerIsMember) {
            return _projection.ForViewer(state, events, viewer);
        }
    }

    private sealed class NoTags : IAnonTagger {
        public static readonly NoTags Instance = new();

        public byte[] Tag(BoardId boardId, PrincipalId principalId) => new byte[32];

        public bool Owns(ReadOnlySpan<byte> tag, BoardId boardId, PrincipalId principalId) => false;
    }
}
