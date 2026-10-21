using Retroscope.Core.Ids;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using static Retroscope.Integration.Tests.Leadership.LeaderHarness;

namespace Retroscope.Integration.Tests.Leadership;

[Trait("Category", "Leader")]
[Collection(SharedPostgres.Name)]
public sealed class CursorRepoTests(PostgresFixture postgres) {
    private static readonly RoomKey Room = new(RoomKind.Board, Guid.NewGuid());
    private static readonly ClientId Client = new(Guid.NewGuid());
    private static readonly PrincipalId Ada = new(Guid.NewGuid());
    private static readonly PrincipalId Grace = new(Guid.NewGuid());

    [Fact]
    public async Task Unknown_client_has_no_cursor() {
        var database = await postgres.CreateDatabaseAsync();

        Assert.Equal(new ClientCursor(CursorOwner.None, 0), await LoadAsync(database, Room, Ada));
    }

    [Theory]
    [InlineData(new long[] { 5, 3 }, 5)]
    [InlineData(new long[] { 5, 5 }, 5)]
    [InlineData(new long[] { 5, 9 }, 9)]
    [InlineData(new long[] { 9, 1, 4, 12, 11 }, 12)]
    public async Task Cursor_upsert_never_regresses(long[] writes, long expected) {
        var database = await postgres.CreateDatabaseAsync();

        foreach (var q in writes) {
            await UpsertAsync(database, Room, Ada, q);
        }

        Assert.Equal(new ClientCursor(CursorOwner.Caller, expected), await LoadAsync(database, Room, Ada));
    }

    [Fact]
    public async Task Cid_bound_to_another_principal_is_refused_and_never_rebound() {
        var database = await postgres.CreateDatabaseAsync();
        await UpsertAsync(database, Room, Ada, 4);

        await UpsertAsync(database, Room, Grace, 10);

        Assert.Equal(new ClientCursor(CursorOwner.OtherPrincipal, 0), await LoadAsync(database, Room, Grace));
        Assert.Equal(new ClientCursor(CursorOwner.Caller, 4), await LoadAsync(database, Room, Ada));
    }

    [Fact]
    public async Task Cursors_are_kept_per_room() {
        var database = await postgres.CreateDatabaseAsync();
        var otherRoom = new RoomKey(RoomKind.Team, Guid.NewGuid());
        await UpsertAsync(database, Room, Ada, 4);

        await UpsertAsync(database, otherRoom, Grace, 2);

        Assert.Equal(new ClientCursor(CursorOwner.Caller, 4), await LoadAsync(database, Room, Ada));
        Assert.Equal(new ClientCursor(CursorOwner.Caller, 2), await LoadAsync(database, otherRoom, Grace));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Cursor_positions_start_at_one(long q) {
        Assert.Throws<ArgumentOutOfRangeException>(() => CursorRepo.Upsert(Room, Client, Ada, q));
    }

    private static async Task UpsertAsync(TestDatabase database, RoomKey room, PrincipalId principal, long q) {
        await using var dataSource = Db.CreateDataSource(database.AppConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(Token);
        await using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(CursorRepo.Upsert(room, Client, principal, q));
        await batch.ExecuteNonQueryAsync(Token);
    }

    private static async Task<ClientCursor> LoadAsync(TestDatabase database, RoomKey room, PrincipalId principal) {
        await using var dataSource = Db.CreateDataSource(database.AppConnectionString);
        return await new CursorRepo(dataSource).LoadAsync(room, Client, principal, Token);
    }
}
