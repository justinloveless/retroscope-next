using Npgsql;
using Retroscope.Server.Data;

namespace Retroscope.Integration.Tests.Schema;

[Trait("Category", "Schema")]
[Collection(SharedPostgres.Name)]
public sealed class RoleTests(PostgresFixture postgres) {
    [Fact]
    public async Task App_role_logs_in_without_elevated_attributes() {
        var database = await postgres.CreateDatabaseAsync();

        await using var app = await OpenAsync(database.AppConnectionString);
        await using var command = new NpgsqlCommand(
            "select rolsuper or rolcreatedb or rolcreaterole or rolreplication or rolbypassrls from pg_roles where rolname = current_user",
            app);

        Assert.False((bool)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    [Theory]
    [InlineData("statement_timeout", "5s")]
    [InlineData("idle_in_transaction_session_timeout", "30s")]
    public async Task App_role_carries_its_own_timeouts(string setting, string expected) {
        var database = await postgres.CreateDatabaseAsync();

        await using var app = await OpenAsync(database.AppConnectionString);

        Assert.Equal(expected, await ShowAsync(app, setting));
    }

    [Theory]
    [InlineData("statement_timeout")]
    [InlineData("idle_in_transaction_session_timeout")]
    public async Task Owner_role_is_never_cut_off(string setting) {
        var database = await postgres.CreateDatabaseAsync();

        await using var owner = await OpenAsync(database.OwnerConnectionString);

        Assert.Equal("0", await ShowAsync(owner, setting));
    }

    [Fact]
    public async Task App_role_writes_public_tables_including_identity_columns() {
        var database = await postgres.CreateDatabaseAsync();
        await using var app = await OpenAsync(database.AppConnectionString);

        await using var command = new NpgsqlCommand("""
            insert into principals (id, kind, display_name) values (gen_random_uuid(), 'guest', 'Ada');
            update principals set display_name = 'Grace';
            insert into email_outbox (to_address, subject) values ('a@example.com', 'hi');
            insert into audit_log (action) values ('probe');
            delete from principals;
            """, app);

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("select * from archive.rows")]
    [InlineData("select * from archive.import_runs")]
    [InlineData("insert into schema_migrations (version, name, sha256) values (999, 'x', '\\x00')")]
    [InlineData("delete from schema_migrations")]
    public async Task App_role_is_denied_the_archive_and_the_migration_ledger(string sql) {
        var database = await postgres.CreateDatabaseAsync();
        await using var app = await OpenAsync(database.AppConnectionString);

        await using var command = new NpgsqlCommand(sql, app);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task Only_the_app_role_may_execute_the_fence() {
        var database = await postgres.CreateDatabaseAsync();
        await using var owner = await OpenAsync(database.OwnerConnectionString);

        await using var command = new NpgsqlCommand($"""
            select has_function_privilege('{Db.AppRole}', 'rs_fence(bigint)', 'execute'),
                   has_function_privilege('public', 'rs_fence(bigint)', 'execute')
            """, owner);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
    }

    [Fact]
    public async Task Rerunning_the_migrator_resets_the_app_password() {
        var database = await postgres.CreateDatabaseAsync();
        await using (var owner = await OpenAsync(database.OwnerConnectionString)) {
            await using var change = new NpgsqlCommand($"alter role {Db.AppRole} password 'drifted'", owner);
            await change.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await postgres.MigrateAsync(database, Migrator.FromEmbeddedScripts());

        await using var app = await OpenAsync(database.AppConnectionString);
        Assert.Equal(System.Data.ConnectionState.Open, app.State);
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString) {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task<string> ShowAsync(NpgsqlConnection connection, string setting) {
        await using var command = new NpgsqlCommand($"show {setting}", connection);
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
