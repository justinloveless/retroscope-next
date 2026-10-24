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
// Migrating costs far more than copying, so the embedded migrations run once into a template and test databases are
// clones of it. A clone copies the schema, rows and object grants but not database-level settings or grants
// (alter database ... set, grant ... on database), so migrations must not rely on those. SchemaSnapshotTests checks
// that a clone dumps the same schema as a freshly migrated database.
public sealed class PostgresFixture : IAsyncLifetime {
    public const string Image = "postgres:18-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873";
    public const string Owner = "retroscope_owner";
    public const string AppPassword = "app-password-for-tests";
    private const string Template = "t_template";

    private readonly SemaphoreSlim _migrating = new(1, 1);
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithDatabase("retroscope")
        .WithUsername(Owner)
        .WithPassword("owner-password-for-tests")
        // Throwaway test data: skip fsync for far less disk and CPU work. Data stays on disk: a run creates thousands
        // of per-test databases, which overflow an in-memory volume on CI runners.
        .WithCommand("-c", "fsync=off", "-c", "synchronous_commit=off", "-c", "full_page_writes=off")
        .Build();

    // The container can report ready before the host port forward accepts connections (seen with Rancher Desktop).
    public async ValueTask InitializeAsync() {
        await _container.StartAsync();
        for (var attempt = 1; ; attempt++) {
            try {
                await using var connection = new NpgsqlConnection(_container.GetConnectionString());
                await connection.OpenAsync();
                break;
            } catch (NpgsqlException) when (attempt < 60) {
                await Task.Delay(500);
            }
        }

        var template = await CreateEmptyDatabaseAsync(Template);
        await MigrateAsync(template, Migrator.FromEmbeddedScripts());
        // Cloning fails while any session is connected to the template, so none may connect to it.
        await ExecuteAsync($"alter database {Template} with is_template true allow_connections false");
    }

    public async ValueTask DisposeAsync() {
        await _container.DisposeAsync();
        _migrating.Dispose();
    }

    /// <summary>
    /// A database with every embedded migration applied, cloned from the template. Given a migrator, an empty database
    /// migrated by it instead.
    /// </summary>
    public async Task<TestDatabase> CreateDatabaseAsync(Migrator? migrator = null) {
        if (migrator is not null) {
            var database = await CreateEmptyDatabaseAsync();
            await MigrateAsync(database, migrator);
            return database;
        }
        var name = NewName();
        await ExecuteAsync($"create database {name} template {Template}");
        return Describe(name);
    }

    public Task<TestDatabase> CreateEmptyDatabaseAsync() => CreateEmptyDatabaseAsync(NewName());

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

    private static string NewName() => $"t_{Guid.NewGuid():N}";

    private async Task<TestDatabase> CreateEmptyDatabaseAsync(string name) {
        await ExecuteAsync($"create database {name}");
        return Describe(name);
    }

    private TestDatabase Describe(string name) {
        // Test code opens owner connections ad hoc; pooled, each database would park one until max_connections ran out.
        var owner = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, Pooling = false };
        var app = new NpgsqlConnectionStringBuilder(owner.ConnectionString) {
            Username = Db.AppRole,
            Password = AppPassword,
            Pooling = true,
        };
        return new TestDatabase(name, owner.ConnectionString, app.ConnectionString);
    }

    private async Task ExecuteAsync(string sql) {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
