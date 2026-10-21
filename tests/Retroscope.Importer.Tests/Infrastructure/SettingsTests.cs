using Npgsql;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Tests.Infrastructure;

[Trait("Category", "Source")]
public sealed class SettingsTests {
    [Fact]
    public void Postgres_uris_become_keyword_strings() {
        var keywords = ImporterSettings.ToKeywords("postgresql://postgres.ref:p%40ss@aws-0.pooler.supabase.com:5432/postgres?sslmode=verify-full");

        var builder = new NpgsqlConnectionStringBuilder(keywords);
        Assert.Equal("aws-0.pooler.supabase.com", builder.Host);
        Assert.Equal(5432, builder.Port);
        Assert.Equal("postgres", builder.Database);
        Assert.Equal("postgres.ref", builder.Username);
        Assert.Equal("p@ss", builder.Password);
        Assert.Equal(SslMode.VerifyFull, builder.SslMode);
    }

    [Fact]
    public void The_source_defaults_to_verify_full_unless_it_says_otherwise() {
        var defaulted = ImporterSettings.FromEnvironment(Env(("IMPORT_SOURCE_PG", "Host=db;Database=postgres;Username=u")));
        var explicitMode = ImporterSettings.FromEnvironment(Env(("IMPORT_SOURCE_PG", "Host=db;Database=postgres;SSL Mode=Disable")));

        Assert.Equal(SslMode.VerifyFull, new NpgsqlConnectionStringBuilder(defaulted.SourceConnectionString).SslMode);
        Assert.Equal(SslMode.Disable, new NpgsqlConnectionStringBuilder(explicitMode.SourceConnectionString).SslMode);
    }

    [Fact]
    public void The_same_database_on_the_same_host_is_refused() {
        var same = ImporterSettings.FromEnvironment(Env(
            ("IMPORT_SOURCE_PG", "postgres://u:p@DB.example:5432/app"),
            ("DATABASE_OWNER_URL", "Host=db.example;Port=5432;Database=app;Username=retroscope_owner")));
        var other = ImporterSettings.FromEnvironment(Env(
            ("IMPORT_SOURCE_PG", "postgres://u:p@db.example:5432/postgres"),
            ("DATABASE_OWNER_URL", "Host=db.example;Port=5432;Database=app;Username=retroscope_owner")));

        Assert.Throws<SettingsException>(same.EnsureDistinctDatabases);
        other.EnsureDistinctDatabases();
    }

    [Fact]
    public void Admin_emails_are_normalized_and_missing_settings_are_named() {
        var settings = ImporterSettings.FromEnvironment(Env(("IMPORT_ADMIN_EMAILS", " Ops@Example.com, ,b@example.com")));

        Assert.Equal(["b@example.com", "ops@example.com"], settings.AdminEmails.Order(StringComparer.Ordinal));
        Assert.Equal("APP_KEYS is not set.", Assert.Throws<SettingsException>(() => settings.RequireKeys()).Message);
        Assert.Equal("IMPORT_SOURCE_PG is not set.", Assert.Throws<SettingsException>(settings.RequireSource).Message);
        Assert.Throws<SettingsException>(() => ImporterSettings.FromEnvironment(Env(("IMPORT_STORAGE_URL", "http://x.example"))));
        Assert.Equal("ImporterSettings", settings.ToString());
    }

    [Fact]
    public void Expected_columns_reject_lists_naming_unknown_columns() {
        var failure = Assert.Throws<InvalidDataException>(() => ExpectedSchema.Parse("""
            {"tables": {"public.t": {"disposition": "archive", "primaryKey": ["id"], "columns": {"id": "uuid"}, "seal": ["token"]}}}
            """));

        Assert.Contains("public.t lists unknown column token", failure.Message, StringComparison.Ordinal);
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(x => x.Name == name).Value;
}
