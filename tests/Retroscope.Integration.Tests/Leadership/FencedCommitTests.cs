using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using static Retroscope.Integration.Tests.Leadership.LeaderHarness;

namespace Retroscope.Integration.Tests.Leadership;

[Trait("Category", "Leader")]
[Collection(SharedPostgres.Name)]
public sealed class FencedCommitTests(PostgresFixture postgres) {
    private static readonly RoomKey Room = new(RoomKind.Poker, Guid.NewGuid());
    private static readonly ClientId Client = new(Guid.NewGuid());

    [Fact]
    public async Task Persist_batch_commits_the_change_and_the_cursor_together() {
        var database = await postgres.CreateDatabaseAsync();
        await using var leader = Create(database);
        await leader.StartAsync(Token);
        var principal = new PrincipalId(Guid.NewGuid());

        await using (var dataSource = Db.CreateDataSource(database.AppConnectionString)) {
            await using var connection = await dataSource.OpenConnectionAsync(Token);
            await leader.CommitFencedAsync(connection, PersistBatch(principal, q: 7), Token);
        }

        Assert.Equal(1, await PrincipalCountAsync(database));
        Assert.Equal(new ClientCursor(CursorOwner.Caller, 7), await LoadCursorAsync(database, principal));
    }

    [Fact]
    public async Task Persist_batch_with_a_stale_epoch_raises_RS001_commits_nothing_and_stops_the_host() {
        var database = await postgres.CreateDatabaseAsync();
        var lifetime = new FakeLifetime();
        await using var leader = Create(database, lifetime);
        await leader.StartAsync(Token);
        await SetEpochAsync(database, leader.Current + 1);
        var principal = new PrincipalId(Guid.NewGuid());

        FencingLostException error;
        await using (var dataSource = Db.CreateDataSource(database.AppConnectionString)) {
            await using var connection = await dataSource.OpenConnectionAsync(Token);
            error = await Assert.ThrowsAsync<FencingLostException>(
                () => leader.CommitFencedAsync(connection, PersistBatch(principal, q: 7), Token));
        }

        Assert.Equal(Leader.FencingLostSqlState, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        Assert.Equal(0, await PrincipalCountAsync(database));
        Assert.Equal(new ClientCursor(CursorOwner.None, 0), await LoadCursorAsync(database, principal));
        Assert.False(leader.IsLeader);
        Assert.True(leader.LeadershipLost.IsCancellationRequested);
        Assert.True(lifetime.StopRequested);
        await Assert.ThrowsAsync<LeadershipLostException>(() => leader.StopAsync(Token));
    }

    [Fact]
    public async Task A_follower_refuses_to_write_without_touching_the_database() {
        var database = await postgres.CreateDatabaseAsync();
        await using var follower = Create(database);
        await using var dataSource = Db.CreateDataSource(database.AppConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(Token);

        await Assert.ThrowsAsync<FencingLostException>(
            () => follower.CommitFencedAsync(connection, PersistBatch(new PrincipalId(Guid.NewGuid()), q: 1), Token));

        Assert.Equal(0, await PrincipalCountAsync(database));
    }

    // DESIGN §12: Npgsql sends BEGIN together with the first batch, so a command costs two round trips.
    [Fact]
    public async Task Begin_is_pipelined_with_the_batch_so_a_command_costs_two_round_trips() {
        var database = await postgres.CreateDatabaseAsync();
        await using var leader = Create(database);
        await leader.StartAsync(Token);
        var target = new NpgsqlConnectionStringBuilder(database.AppConnectionString);
        await using var proxy = new RoundTripProxy(target.Host!, target.Port);
        var viaProxy = new NpgsqlConnectionStringBuilder(target.ConnectionString) {
            Host = "127.0.0.1",
            Port = proxy.Port,
            Pooling = false,
            SslMode = SslMode.Disable,
            GssEncryptionMode = GssEncryptionMode.Disable,
        };
        await using var dataSource = Db.CreateDataSource(viaProxy.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(Token);
        var principal = new PrincipalId(Guid.NewGuid());
        await leader.CommitFencedAsync(connection, PersistBatch(principal, q: 1), Token);

        var before = proxy.RoundTrips;
        await leader.CommitFencedAsync(connection, PersistBatch(principal, q: 2), Token);

        Assert.Equal(2, proxy.RoundTrips - before);
        Assert.Equal(2, await PrincipalCountAsync(database));
        Assert.Equal(new ClientCursor(CursorOwner.Caller, 2), await LoadCursorAsync(database, principal));
    }

    private static NpgsqlBatchCommand[] PersistBatch(PrincipalId principal, long q) =>
        [InsertGuest(Guid.NewGuid()), CursorRepo.Upsert(Room, Client, principal, q)];

    private static async Task<ClientCursor> LoadCursorAsync(TestDatabase database, PrincipalId principal) {
        await using var dataSource = Db.CreateDataSource(database.AppConnectionString);
        return await new CursorRepo(dataSource).LoadAsync(Room, Client, principal, Token);
    }
}
