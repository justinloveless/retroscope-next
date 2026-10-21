using Npgsql;
using Retroscope.Server.Data;
using Testcontainers.PostgreSql;

namespace Retroscope.Integration.Tests.Schema;

[CollectionDefinition(Name)]
public sealed class SharedPostgres : ICollectionFixture<PostgresFixture> {
    public const string Name = "Postgres";
}

public sealed record TestDatabase(string Name, string OwnerConnectionString, string AppConnectionString);

// One container per test run; every test gets its own database, so tests never see each other's rows.
// The roles are cluster-wide, which is why databases are migrated one at a time.
public sealed class PostgresFixture : IAsyncLifetime {
    public const string Image = "postgres:18-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873";
    public const string Owner = "retroscope_owner";
    public const string AppPassword = "app-password-for-tests";

    private readonly SemaphoreSlim _migrating = new(1, 1);
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithDatabase("retroscope")
        .WithUsername(Owner)
        .WithPassword("owner-password-for-tests")
        .Build();

    // The container can report ready before the host port forward accepts connections (seen with Rancher Desktop).
    public async ValueTask InitializeAsync() {
        await _container.StartAsync();
        for (var attempt = 1; ; attempt++) {
            try {
                await using var connection = new NpgsqlConnection(_container.GetConnectionString());
                await connection.OpenAsync();
                return;
            } catch (NpgsqlException) when (attempt < 60) {
                await Task.Delay(500);
            }
        }
    }

    public async ValueTask DisposeAsync() {
        await _container.DisposeAsync();
        _migrating.Dispose();
    }

    public async Task<TestDatabase> CreateDatabaseAsync(Migrator? migrator = null) {
        var database = await CreateEmptyDatabaseAsync();
        await MigrateAsync(database, migrator ?? Migrator.FromEmbeddedScripts());
        return database;
    }

    public async Task<TestDatabase> CreateEmptyDatabaseAsync() {
        var name = $"t_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(_container.GetConnectionString())) {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"create database {name}", connection);
            await create.ExecuteNonQueryAsync();
        }

        var owner = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name };
        var app = new NpgsqlConnectionStringBuilder(owner.ConnectionString) {
            Username = Db.AppRole,
            Password = AppPassword,
        };
        return new TestDatabase(name, owner.ConnectionString, app.ConnectionString);
    }

    public async Task<int> MigrateAsync(TestDatabase database, Migrator migrator) {
        await _migrating.WaitAsync();
        try {
            await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
            await connection.OpenAsync();
            var applied = await migrator.RunAsync(connection, AppPassword);
            // Every database has its own pool; an idle owner connection left in each would use up max_connections.
            NpgsqlConnection.ClearPool(connection);
            return applied;
        } finally {
            _migrating.Release();
        }
    }

    // pg_dump 18 brackets its output with \restrict lines carrying a random key; they are not schema.
    public async Task<string> DumpSchemaAsync(TestDatabase database) {
        var result = await _container.ExecAsync(["pg_dump", "--schema-only", "-U", Owner, database.Name]);
        Assert.True(result.ExitCode == 0, result.Stderr);
        var lines = result.Stdout
            .Split('\n')
            .Where(x => !x.StartsWith("\\restrict ", StringComparison.Ordinal) && !x.StartsWith("\\unrestrict ", StringComparison.Ordinal));
        return string.Join('\n', lines);
    }
}
