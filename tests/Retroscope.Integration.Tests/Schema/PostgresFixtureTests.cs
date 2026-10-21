using Npgsql;

namespace Retroscope.Integration.Tests.Schema;

[Trait("Category", "Schema")]
[Collection(SharedPostgres.Name)]
public sealed class PostgresFixtureTests(PostgresFixture postgres) {
    [Fact]
    public async Task More_databases_than_postgres_has_connection_slots_can_be_created() {
        var databases = new List<TestDatabase>();
        for (var i = 0; i < 120; i++) {
            databases.Add(await postgres.CreateDatabaseAsync());
        }

        await using var connection = new NpgsqlConnection(databases[^1].AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var count = new NpgsqlCommand(
            "select count(*) from pg_stat_activity where datname = any($1) and pid <> pg_backend_pid()", connection);
        count.Parameters.Add(new NpgsqlParameter { Value = databases.Select(x => x.Name).ToArray() });
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);

        NpgsqlConnection.ClearPool(connection);
        await connection.CloseAsync();
        var admin = new NpgsqlConnectionStringBuilder(databases[0].OwnerConnectionString) { Database = "postgres" };
        await using var drop = new NpgsqlConnection(admin.ConnectionString);
        await drop.OpenAsync(TestContext.Current.CancellationToken);
        foreach (var database in databases) {
            await using var command = new NpgsqlCommand($"drop database {database.Name} with (force)", drop);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        NpgsqlConnection.ClearPool(drop);
    }
}
