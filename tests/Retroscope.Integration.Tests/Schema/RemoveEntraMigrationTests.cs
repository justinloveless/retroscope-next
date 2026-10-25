using Npgsql;
using Retroscope.Server.Data;

namespace Retroscope.Integration.Tests.Schema;

/// <summary>
/// Migration 0004 (UX19) on a database that ran 0001 to 0003 and holds Entra sessions and identities beside Google,
/// password and magic-link ones, for an account that signed in only with Entra and one that also used Google.
/// </summary>
[Trait("Category", "Schema")]
[Collection(SharedPostgres.Name)]
public sealed class RemoveEntraMigrationTests(PostgresFixture postgres) {
    private readonly Guid _entraOnly = Guid.NewGuid();
    private readonly Guid _both = Guid.NewGuid();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Entra_sessions_and_identities_go_and_accounts_and_the_audit_log_stay() {
        var database = await SeededAt0003Async();

        Assert.Equal(1, await postgres.MigrateAsync(database, UpTo0004()));

        Assert.Equal(
            new[] { _entraOnly, _both }.Order(),
            await IdsAsync(database, "select id from users"));
        Assert.Equal(
            ["google:g-1"],
            await StringsAsync(database, "select provider || ':' || subject from external_identities"));
        Assert.Equal(
            ["google", "magic_link", "password"],
            await StringsAsync(database, "select auth_method from sessions order by auth_method"));
        Assert.Equal(1L, await ScalarAsync<long>(database, "select count(*) from audit_log where meta->>'method' = 'entra'"));
    }

    [Fact]
    public async Task Entra_can_no_longer_be_stored() {
        var database = await SeededAt0003Async();

        await postgres.MigrateAsync(database, UpTo0004());

        var identity = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, $"""
            insert into external_identities (provider, subject, user_id) values ('entra', 't:o2', '{_both}')
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, identity.SqlState);
        var session = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, $"""
            insert into sessions (id_hash, user_id, auth_method, idle_expires_at, abs_expires_at)
            values (sha256('late'), '{_both}', 'entra', now() + interval '1 day', now() + interval '1 day')
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, session.SqlState);
    }

    /// <summary>Stops at 0004, so a later migration neither changes the count applied nor what these tests check.</summary>
    private static Migrator UpTo0004() => new(Migrator.FromEmbeddedScripts().Scripts.Take(4));

    private async Task<TestDatabase> SeededAt0003Async() {
        var database = await postgres.CreateEmptyDatabaseAsync();
        await postgres.MigrateAsync(database, new Migrator(Migrator.FromEmbeddedScripts().Scripts.Take(3)));
        await ExecuteAsync(database, $$"""
            insert into principals (id, kind) values ('{{_entraOnly}}', 'user'), ('{{_both}}', 'user');
            insert into users (id, email, full_name, email_verified_at) values
              ('{{_entraOnly}}', 'entra-only@example.com', 'Entra Only', now()), ('{{_both}}', 'both@example.com', 'Both', now());
            insert into external_identities (provider, subject, user_id) values
              ('entra', 't:o1', '{{_entraOnly}}'), ('entra', 't:o2', '{{_both}}'), ('google', 'g-1', '{{_both}}');
            insert into sessions (id_hash, user_id, auth_method, idle_expires_at, abs_expires_at) values
              (sha256('s1'), '{{_entraOnly}}', 'entra', now() + interval '1 day', now() + interval '1 day'),
              (sha256('s2'), '{{_both}}', 'entra', now() + interval '1 day', now() + interval '1 day'),
              (sha256('s3'), '{{_both}}', 'google', now() + interval '1 day', now() + interval '1 day'),
              (sha256('s4'), '{{_both}}', 'password', now() + interval '1 day', now() + interval '1 day'),
              (sha256('s5'), '{{_entraOnly}}', 'magic_link', now() + interval '1 day', now() + interval '1 day');
            insert into audit_log (actor_id, action, target, meta) values
              ('{{_entraOnly}}', 'auth.login', '{{_entraOnly}}', '{"method": "entra"}');
            """);
        return database;
    }

    private static async Task ExecuteAsync(TestDatabase database, string sql) {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<T> ScalarAsync<T>(TestDatabase database, string sql) {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Token))!;
    }

    /// <summary>The query's first column, ordered here so the comparison does not depend on the database's uuid order.</summary>
    private static async Task<List<Guid>> IdsAsync(TestDatabase database, string sql) {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Token);
        var values = new List<Guid>();
        while (await reader.ReadAsync(Token)) {
            values.Add(reader.GetGuid(0));
        }
        return [.. values.Order()];
    }

    private static async Task<List<string>> StringsAsync(TestDatabase database, string sql) {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Token);
        var values = new List<string>();
        while (await reader.ReadAsync(Token)) {
            values.Add(reader.GetString(0));
        }
        return values;
    }
}
