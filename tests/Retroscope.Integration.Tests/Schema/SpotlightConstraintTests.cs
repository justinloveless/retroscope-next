using Npgsql;

namespace Retroscope.Integration.Tests.Schema;

// The spotlight FKs are NO ACTION DEFERRABLE (R9): the store must repair the spotlight itself, and a
// missed repair fails at COMMIT instead of silently nulling one of the two paired columns.
[Trait("Category", "Schema")]
[Collection(SharedPostgres.Name)]
public sealed class SpotlightConstraintTests(PostgresFixture postgres) {
    private readonly Guid _holder = Guid.NewGuid();
    private readonly Guid _session = Guid.NewGuid();
    private readonly Guid _round = Guid.NewGuid();

    [Fact]
    public async Task Deleting_the_spotlit_round_without_repair_fails_at_commit() {
        await using var connection = await OpenSpotlitSessionAsync();

        await using var tx = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, await ExecuteAsync(connection, tx, "delete from poker_rounds where id = @id", _round));
        var error = await Assert.ThrowsAsync<PostgresException>(
            () => tx.CommitAsync(TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        Assert.Equal(1, await CountAsync(connection, "select count(*) from poker_rounds"));
    }

    [Fact]
    public async Task Deleting_the_spotlight_holder_without_repair_fails_at_commit() {
        await using var connection = await OpenSpotlitSessionAsync();

        await using var tx = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, await ExecuteAsync(connection, tx, "delete from principals where id = @id", _holder));
        var error = await Assert.ThrowsAsync<PostgresException>(
            () => tx.CommitAsync(TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        Assert.Equal(1, await CountAsync(connection, "select count(*) from principals"));
    }

    [Fact]
    public async Task Clearing_only_half_of_the_spotlight_violates_the_check() {
        await using var connection = await OpenSpotlitSessionAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => InTransactionAsync(connection, "update poker_sessions set spotlight_round_id = null where id = @id", _session));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task Repairing_the_spotlight_first_lets_the_delete_commit() {
        await using var connection = await OpenSpotlitSessionAsync();

        await InTransactionAsync(connection, """
            update poker_sessions set spotlight_holder_id = null, spotlight_round_id = null where spotlight_round_id = @id;
            delete from poker_rounds where id = @id;
            """, _round);

        Assert.Equal(0, await CountAsync(connection, "select count(*) from poker_rounds"));
    }

    private async Task<NpgsqlConnection> OpenSpotlitSessionAsync() {
        var database = await postgres.CreateDatabaseAsync();
        var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var seed = new NpgsqlCommand("""
            insert into principals (id, kind, display_name) values (@holder, 'guest', 'Ada');
            insert into poker_sessions (id, code) values (@session, 'ABCD');
            insert into poker_rounds (id, session_id, number, sort_key) values (@round, @session, 1, 1);
            update poker_sessions set spotlight_holder_id = @holder, spotlight_round_id = @round, current_round_id = @round
             where id = @session;
            """, connection) {
            Parameters = {
                new("holder", _holder),
                new("session", _session),
                new("round", _round),
            },
        };
        await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task InTransactionAsync(NpgsqlConnection connection, string sql, Guid id) {
        await using var tx = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(connection, tx, sql, id);
        await tx.CommitAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, Guid id) {
        await using var command = new NpgsqlCommand(sql, connection, tx) { Parameters = { new("id", id) } };
        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql) {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
