using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Integration.Tests.Actors;
using Retroscope.Integration.Tests.Leadership;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Rooms;
using Retroscope.Server.Realtime.Stores;

using static Retroscope.Integration.Tests.Actors.ActorHarness;

namespace Retroscope.Integration.Tests.RetroRooms;

/// <summary>
/// One migrated database with a team (Alice owns it, Bob is a member), a non-member (Carol), a legacy guest principal
/// (Gina), a team board with a plain and an action-items column, and a board of another team that only Alice belongs
/// to. Registries run the real retro binding and store against it; a new registry is a restarted process.
/// </summary>
internal sealed class RetroScope : IAsyncDisposable {
    public static readonly Viewer Gina = ActorHarness.Viewer(4, "Gina", kind: PrincipalKind.Guest);

    public static readonly TeamId Team = new(ActorHarness.Id(0x100));
    public static readonly TeamId OtherTeam = new(ActorHarness.Id(0x102));
    public static readonly BoardId TeamBoard = new(ActorHarness.Id(0x200));
    public static readonly BoardId OtherBoard = new(ActorHarness.Id(0x201));
    public static readonly ColumnId Good = new(ActorHarness.Id(0x300));
    public static readonly ColumnId Actions = new(ActorHarness.Id(0x301));
    public static readonly ColumnId OtherColumn = new(ActorHarness.Id(0x302));
    public static readonly EndorsementTypeId Kudos = new(ActorHarness.Id(0x400));
    public static readonly BlobId Image1 = new(ActorHarness.Id(0x500));
    public static readonly BlobId Image2 = new(ActorHarness.Id(0x501));
    public static readonly BlobId OtherImage = new(ActorHarness.Id(0x502));
    public static readonly BlobId Avatar = new(ActorHarness.Id(0x503));

    public static readonly RoomKey TeamBoardRoom = RoomKey.Board(TeamBoard);
    public static readonly RoomKey OtherBoardRoom = RoomKey.Board(OtherBoard);

    private readonly List<ActorRegistry> _registries = [];

    private RetroScope(TestDatabase database, Leader leader, NpgsqlDataSource dataSource) {
        Database = database;
        Leader = leader;
        DataSource = dataSource;
        Access.Roles[(Team, Alice.Who.Id)] = TeamRole.Owner;
        Access.Roles[(Team, Bob.Who.Id)] = TeamRole.Member;
        Access.Roles[(OtherTeam, Alice.Who.Id)] = TeamRole.Owner;
    }

    public TestDatabase Database { get; }

    public Leader Leader { get; }

    public NpgsqlDataSource DataSource { get; }

    public FakeTimeProvider Time { get; } = ActorHarness.Clock();

    public TestAccess Access { get; } = new();

    public RecordingNotices Notices { get; } = new();

    public AnonTagger Tagger { get; } = new(Enumerable.Range(0, AnonTagger.KeySize).Select(x => (byte)x).ToArray());

    public static async Task<RetroScope> StartAsync(PostgresFixture postgres) {
        var database = await postgres.CreateDatabaseAsync();
        var leader = LeaderHarness.Create(database);
        await leader.StartAsync(ActorHarness.Token);
        var scope = new RetroScope(database, leader, Db.CreateDataSource(database.AppConnectionString));
        await scope.ExecuteAsync(Seed);
        return scope;
    }

    /// <summary>A process: its own runtime and actors over the shared database and clock, or <paramref name="time"/>.</summary>
    public ActorRegistry NewRegistry(TimeProvider? time = null) {
        var store = new RetroStore(DataSource, Leader, new CursorRepo(DataSource), Notices, NullLogger<RetroStore>.Instance);
        var registry = new ActorRegistry(ActorHarness.Runtime(time ?? Time), [RoomType.Of(new RetroBinding(Tagger), store, Access)]);
        _registries.Add(registry);
        return registry;
    }

    public static async Task<TestPeer> JoinAsync(ActorRegistry registry, RoomKey room, Viewer viewer) {
        var peer = new TestPeer(viewer);
        Assert.Equal(PostResult.Accepted, await registry.SubscribeAsync(room, peer, viewer, null, null, ActorHarness.Token));
        Assert.IsType<SnapFrame>(await peer.NextAsync());
        return peer;
    }

    /// <summary>Sends one command and returns its ack or nack, skipping the tx frames in between.</summary>
    public static async Task<ServerFrame> SendAsync(ActorRegistry registry, RoomKey room, TestPeer peer, long q, Command command) {
        await registry.CommandAsync(room, peer, peer.Viewer, ActorHarness.Cmd(q, command, room), ActorHarness.Token);
        while (true) {
            switch (await peer.NextAsync()) {
                case AckFrame ack when ack.Q == q:
                    return ack;
                case NackFrame nack when nack.Q == q:
                    return nack;
            }
        }
    }

    public static async Task AckAsync(ActorRegistry registry, RoomKey room, TestPeer peer, long q, Command command) {
        var reply = await SendAsync(registry, room, peer, q, command);
        Assert.True(reply is AckFrame { Dup: false }, $"{command.GetType().Name} q{q} was answered with {reply}");
    }

    public static async Task<NackFrame> NackAsync(ActorRegistry registry, RoomKey room, TestPeer peer, long q, Command command) {
        return Assert.IsType<NackFrame>(await SendAsync(registry, room, peer, q, command));
    }

    /// <summary>The shared snapshot (without presence) and the overlays of the given viewers, as JSON.</summary>
    public Task<string> DescribeAsync(ActorRegistry registry, RoomKey room, params Viewer[] viewers) {
        var projection = new RetroProjection(Tagger);
        return registry.QueryAsync<RetroState, RetroInput, string>(room, view => {
            var state = view.State!;
            var parts = new List<string> {
                JsonSerializer.Serialize(RetroProjection.Snapshot(state, []), WireJsonContext.Default.RetroSnapshot),
            };
            parts.AddRange(viewers.Select(viewer => JsonSerializer.Serialize(projection.Overlay(state, viewer.Who.Id), WireJsonContext.Default.RetroOverlay)));
            return ValueTask.FromResult(string.Join('\n', parts));
        }, ActorHarness.Token);
    }

    public static Task SettleAsync(ActorRegistry registry, RoomKey room) {
        return registry.QueryAsync<RetroState, RetroInput, int>(room, _ => ValueTask.FromResult(0), ActorHarness.Token);
    }

    public async Task ExecuteAsync(string sql, params object?[] parameters) {
        await using var connection = await LeaderHarness.OpenAsync(Database.OwnerConnectionString);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter ?? DBNull.Value });
        }
        await command.ExecuteNonQueryAsync(ActorHarness.Token);
    }

    public async Task<T?> ScalarAsync<T>(string sql, params object[] parameters) {
        await using var connection = await LeaderHarness.OpenAsync(Database.OwnerConnectionString);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }
        var value = await command.ExecuteScalarAsync(ActorHarness.Token);
        return value is null or DBNull ? default : (T)value;
    }

    public async Task<List<object?[]>> RowsAsync(string sql, params object[] parameters) {
        await using var connection = await LeaderHarness.OpenAsync(Database.OwnerConnectionString);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }
        await using var reader = await command.ExecuteReaderAsync(ActorHarness.Token);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(ActorHarness.Token)) {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }

    public async ValueTask DisposeAsync() {
        foreach (var registry in _registries) {
            await registry.ShutdownAsync(CancellationToken.None);
        }
        await DataSource.DisposeAsync();
        await Leader.DisposeAsync();
    }

    public static string Mention(Viewer viewer) => $"[[mention:{viewer.Who.Id.Value}:{viewer.Who.DisplayName}]]";

    public static string Image(BlobId blob) => $"![](blob:{blob.Value})";

    private static readonly string Seed = $"""
        insert into principals (id, kind, display_name) values
          ('{Alice.Who.Id.Value}', 'user', null), ('{Bob.Who.Id.Value}', 'user', null),
          ('{Carol.Who.Id.Value}', 'user', null), ('{Gina.Who.Id.Value}', 'guest', 'Gina');
        insert into users (id, email, full_name, nickname) values
          ('{Alice.Who.Id.Value}', 'alice@example.com', 'Alice Liddell', 'Alice'),
          ('{Bob.Who.Id.Value}', 'bob@example.com', 'Bob', null),
          ('{Carol.Who.Id.Value}', 'carol@example.com', 'Carol', null);
        insert into teams (id, name, created_by) values
          ('{Team.Value}', 'Team', '{Alice.Who.Id.Value}'), ('{OtherTeam.Value}', 'Other team', '{Alice.Who.Id.Value}');
        insert into team_members (team_id, user_id, role) values
          ('{Team.Value}', '{Alice.Who.Id.Value}', 'owner'), ('{Team.Value}', '{Bob.Who.Id.Value}', 'member'),
          ('{OtherTeam.Value}', '{Alice.Who.Id.Value}', 'owner');
        insert into endorsement_types (id, team_id, name, emoji, sort_key) values ('{Kudos.Value}', '{Team.Value}', 'Kudos', '🎉', 0);
        insert into retro_boards (id, code, team_id, title, created_by, visibility) values
          ('{TeamBoard.Value}', 'TEAMBOARD1', '{Team.Value}', 'Sprint 1', '{Alice.Who.Id.Value}', 'team'),
          ('{OtherBoard.Value}', 'OTHERBOARD', '{OtherTeam.Value}', 'Other', '{Alice.Who.Id.Value}', 'team');
        insert into retro_columns (id, board_id, title, color, sort_key, is_action_items) values
          ('{Good.Value}', '{TeamBoard.Value}', 'Good', 'green', 0, false),
          ('{Actions.Value}', '{TeamBoard.Value}', 'Actions', 'blue', 1, true),
          ('{OtherColumn.Value}', '{OtherBoard.Value}', 'Notes', 'gray', 0, false);
        insert into blobs (id, purpose, board_id, content_type, byte_size, sha256, bytes, created_by) values
          ('{Image1.Value}', 'board_image', '{TeamBoard.Value}', 'image/png', 1, sha256('\x01'), '\x01', '{Alice.Who.Id.Value}'),
          ('{Image2.Value}', 'board_image', '{TeamBoard.Value}', 'image/png', 1, sha256('\x02'), '\x02', '{Alice.Who.Id.Value}'),
          ('{OtherImage.Value}', 'board_image', '{OtherBoard.Value}', 'image/png', 1, sha256('\x03'), '\x03', '{Alice.Who.Id.Value}'),
          ('{Avatar.Value}', 'avatar', null, 'image/png', 1, sha256('\x04'), '\x04', '{Alice.Who.Id.Value}');
        """;
}

internal sealed class RecordingNotices : ITeamNotices {
    private readonly Lock _lock = new();
    private readonly List<string> _log = [];

    public List<string> Log {
        get {
            lock (_lock) {
                return [.. _log];
            }
        }
    }

    public void BoardsChanged(TeamId team, BoardId board) => Add($"boards {team.Value} {board.Value}");

    public void PokerChanged(TeamId team, SessionId session, int pointed, int total) => Add($"poker {session.Value}");

    public void ActionItemCommentsChanged(TeamId team, ActionItemId item, int count) => Add($"comments {item.Value} {count}");

    public void ActionItemCreated(TeamId team, ActionItemInfo item) => Add($"created {item.Id.Value} {item.SourceCardId?.Value}");

    public void ActionItemTextChanged(TeamId team, CardId sourceCard, string text, int cardVersion) => Add($"text {sourceCard.Value} {cardVersion} {text}");

    public void SourceCardDeleted(TeamId team, CardId sourceCard) => Add($"deleted {sourceCard.Value}");

    private void Add(string entry) {
        lock (_lock) {
            _log.Add(entry);
        }
    }
}
