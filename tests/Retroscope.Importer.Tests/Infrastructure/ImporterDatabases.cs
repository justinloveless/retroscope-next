using System.Security.Cryptography;
using Npgsql;
using Retroscope.Importer.Source;
using Testcontainers.PostgreSql;

namespace Retroscope.Importer.Tests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class ImporterPostgres : ICollectionFixture<ImporterDatabases> {
    public const string Name = "ImporterPostgres";
}

/// <summary>
/// One Postgres container per run holding both sides of an import:
/// <list type="bullet">
/// <item>a fake Supabase source per test, cloned from a template built once from <c>Supabase/</c>: platform stubs
/// (auth, storage, cron), the historical schema dump, every migration the dump does not already contain, and the
/// foreign tables known only from <c>types.ts</c>, and the default tenant row;</item>
/// <item>a target per test, migrated with <c>db/migrations/*.sql</c>.</item>
/// </list>
/// Both connect as the container superuser, which stands in for <c>retroscope_owner</c> (no role-level timeouts, so
/// database-level settings apply unless the importer overrides them).
/// </summary>
public sealed class ImporterDatabases : IAsyncLifetime {
    public const string Image = "postgres:18-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873";
    private const string Template = "supabase_template";

    // Migrations that fail on top of the dump because the dump already contains what they create.
    private static readonly HashSet<string> InDump = [
        "20240627120000_update_slack_integration_settings.sql",
        "20250115000000_add_retro_stages.sql",
        "20250115000001_add_retro_stages_to_templates.sql",
        "20250115000002_add_user_readiness_tracking.sql",
        "20250115000003_add_enforce_stage_readiness.sql",
        "20250611161918-a9fe2d93-6d30-403c-b542-5d97e6b07e2c.sql",
        "20250611165935-9c790802-578d-4978-95f5-7cdbd73b77f6.sql",
        "20250822133000_create_feedback_reports.sql",
        "20250911104340_3cb5ba10-8e1d-494b-bf2e-657b3da0a551.sql",
        "20250911104552_9f62f8b4-ea83-483b-90c0-66d2c01fe2fb.sql",
        "20250921110000_create_notifications.sql",
        "20250921111500_update_notifications_policies.sql",
    ];

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithDatabase("postgres")
        .WithUsername("postgres")
        .WithPassword("importer-tests")
        .Build();

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string SupabaseDirectory => Path.Combine(RepositoryRoot, "tests", "Retroscope.Importer.Tests", "Infrastructure", "Supabase");

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

        await ExecuteAsync(Admin(), $"create database {Template}");
        var template = WithDatabase(Template);
        await ExecuteFileAsync(template, Path.Combine(SupabaseDirectory, "00-platform-stubs.sql"));
        await ExecuteFileAsync(template, Path.Combine(SupabaseDirectory, "schema-dump-99b8c3a.sql"));
        foreach (var migration in Directory.GetFiles(Path.Combine(SupabaseDirectory, "migrations"), "*.sql").Order(StringComparer.Ordinal)) {
            if (!InDump.Contains(Path.GetFileName(migration))) {
                await ExecuteFileAsync(template, migration);
            }
        }
        await ExecuteFileAsync(template, Path.Combine(SupabaseDirectory, "90-foreign-tables.sql"));
        await ExecuteFileAsync(template, Path.Combine(SupabaseDirectory, "95-baseline-rows.sql"));
        NpgsqlConnection.ClearAllPools();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>A fresh fake Supabase project. The connection string disables TLS (the importer defaults to VerifyFull).</summary>
    public async Task<string> CreateSourceAsync() {
        var name = $"src_{Guid.NewGuid():N}";
        await ExecuteAsync(Admin(), $"create database {name} template {Template}");
        return new NpgsqlConnectionStringBuilder(WithDatabase(name)) { SslMode = SslMode.Disable }.ConnectionString;
    }

    public async Task<string> CreateTargetAsync() {
        var name = $"dst_{Guid.NewGuid():N}";
        await ExecuteAsync(Admin(), $"create database {name}");
        var target = WithDatabase(name);
        foreach (var migration in Directory.GetFiles(Path.Combine(RepositoryRoot, "db", "migrations"), "*.sql").Order(StringComparer.Ordinal)) {
            await ExecuteFileAsync(target, migration);
        }
        return target;
    }

    public static ImporterSettings Settings(string source, string target, string? sealPublicKeyPem = null) => new() {
        SourceConnectionString = source,
        TargetConnectionString = target,
        SealPublicKeyPem = sealPublicKeyPem,
        AppKeys = $"k1:{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}",
        AnonTagKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        StorageUrl = new Uri("https://project.supabase.example/"),
    };

    public static async Task ExecuteAsync(string connectionString, string sql) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private string Admin() => _container.GetConnectionString();

    private string WithDatabase(string name) => new NpgsqlConnectionStringBuilder(Admin()) { Database = name }.ConnectionString;

    private static async Task ExecuteFileAsync(string connectionString, string path) {
        try {
            await ExecuteAsync(connectionString, await File.ReadAllTextAsync(path));
        } catch (PostgresException exception) {
            throw new InvalidOperationException($"{Path.GetFileName(path)}: {exception.MessageText}", exception);
        }
    }

    private static string FindRepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            if (File.Exists(Path.Combine(directory.FullName, "Retroscope.slnx"))) {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Cannot find the repository root (Retroscope.slnx).");
    }
}
