using System.Text;
using Npgsql;
using Retroscope.Server.Data;

namespace Retroscope.Integration.Tests.Schema;

[Trait("Category", "Schema")]
[Collection(SharedPostgres.Name)]
public sealed class MigratorTests(PostgresFixture postgres) {
    [Fact]
    public void Embedded_scripts_start_with_the_init_migration() {
        var scripts = Migrator.FromEmbeddedScripts().Scripts;

        Assert.Equal(1, scripts[0].Version);
        Assert.Equal("init", scripts[0].Name);
    }

    [Fact]
    public void Versions_with_a_gap_are_rejected() {
        var scripts = new[] { Script(1, "select 1"), Script(3, "select 3") };

        Assert.Throws<ArgumentException>(() => new Migrator(scripts));
    }

    [Fact]
    public async Task Second_run_applies_nothing_and_changes_nothing() {
        var database = await postgres.CreateEmptyDatabaseAsync();
        var migrator = Migrator.FromEmbeddedScripts();

        var first = await postgres.MigrateAsync(database, migrator);
        var schemaAfterFirst = await postgres.DumpSchemaAsync(database);
        var second = await postgres.MigrateAsync(database, migrator);

        Assert.Equal(migrator.Scripts.Count, first);
        Assert.Equal(0, second);
        Assert.Equal(schemaAfterFirst, await postgres.DumpSchemaAsync(database));
        Assert.Equal(migrator.Scripts.Count, await ScalarAsync<long>(database, "select count(*) from schema_migrations"));
    }

    [Fact]
    public async Task Applied_rows_record_the_script_hash() {
        var database = await postgres.CreateDatabaseAsync();
        var init = Migrator.FromEmbeddedScripts().Scripts[0];

        var sha = await ScalarAsync<byte[]>(database, "select sha256 from schema_migrations where version = 1");

        Assert.Equal(init.Sha256, sha);
    }

    [Fact]
    public async Task A_changed_applied_file_is_refused_and_nothing_commits() {
        var database = await postgres.CreateEmptyDatabaseAsync();
        var init = Migrator.FromEmbeddedScripts().Scripts[0];
        await postgres.MigrateAsync(database, new Migrator([init]));
        var edited = init with { Bytes = [.. init.Bytes, .. "\n-- edited\n"u8] };
        var next = Script(2, "create table sha_guard_probe (id int);");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => postgres.MigrateAsync(database, new Migrator([edited, next])));

        Assert.Contains("0001_init changed", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, await ScalarAsync<long>(database, "select count(*) from schema_migrations"));
        Assert.False(await ScalarAsync<bool>(database, "select to_regclass('public.sha_guard_probe') is not null"));
    }

    [Fact]
    public async Task A_later_migration_applies_alone_and_its_tables_are_granted_to_the_app() {
        var database = await postgres.CreateEmptyDatabaseAsync();
        var init = Migrator.FromEmbeddedScripts().Scripts[0];
        await postgres.MigrateAsync(database, new Migrator([init]));
        var next = Script(2, "create table later_probe (id int primary key);");

        var applied = await postgres.MigrateAsync(database, new Migrator([init, next]));

        Assert.Equal(1, applied);
        await using var app = new NpgsqlConnection(database.AppConnectionString);
        await app.OpenAsync(TestContext.Current.CancellationToken);
        await using var insert = new NpgsqlCommand("insert into later_probe values (1)", app);
        Assert.Equal(1, await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Binary_tolerates_a_newer_applied_migration_it_does_not_know() {
        var database = await postgres.CreateEmptyDatabaseAsync();
        var init = Migrator.FromEmbeddedScripts().Scripts[0];
        await postgres.MigrateAsync(database, new Migrator([init, Script(2, "create table newer_probe (id int);")]));

        var applied = await postgres.MigrateAsync(database, new Migrator([init]));

        Assert.Equal(0, applied);
    }

    private static MigrationScript Script(int version, string sql) =>
        new(version, $"probe{version}", Encoding.UTF8.GetBytes(sql));

    private static async Task<T> ScalarAsync<T>(TestDatabase database, string sql) {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
