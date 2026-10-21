using Npgsql;
using Retroscope.Server.Data;

namespace Retroscope.Integration.Tests.Schema;

[Trait("Category", "Schema")]
[Collection(SharedPostgres.Name)]
public sealed class FenceTests(PostgresFixture postgres) {
    [Fact]
    public async Task Fence_with_the_current_epoch_lets_the_batch_commit() {
        var database = await postgres.CreateDatabaseAsync();
        await SetEpochAsync(database, 7);

        await RunFencedInsertAsync(database, 7);

        Assert.Equal(1, await CountPrincipalsAsync(database));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public async Task Fence_with_another_epoch_raises_RS001_and_nothing_commits(long epoch) {
        var database = await postgres.CreateDatabaseAsync();
        await SetEpochAsync(database, 7);

        var error = await Assert.ThrowsAsync<PostgresException>(() => RunFencedInsertAsync(database, epoch));

        Assert.Equal("RS001", error.SqlState);
        Assert.Equal("fencing_lost", error.MessageText);
        Assert.Equal(0, await CountPrincipalsAsync(database));
    }

    private static async Task RunFencedInsertAsync(TestDatabase database, long epoch) {
        await using var dataSource = Db.CreateDataSource(database.AppConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var tx = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var batch = new NpgsqlBatch(connection, tx) {
            BatchCommands = {
                new("select rs_fence($1)") { Parameters = { new() { Value = epoch } } },
                new("insert into principals (id, kind, display_name) values (gen_random_uuid(), 'guest', 'Ada')"),
            },
        };
        await batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await tx.CommitAsync(TestContext.Current.CancellationToken);
    }

    private static async Task SetEpochAsync(TestDatabase database, long epoch) {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("update system_state set leader_epoch = $1", connection) {
            Parameters = { new() { Value = epoch } },
        };
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> CountPrincipalsAsync(TestDatabase database) {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select count(*) from principals", connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
