using Npgsql;
using Retroscope.Importer.Archive;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Tests.Infrastructure;

[Trait("Category", "Source")]
[Collection(ImporterPostgres.Name)]
public sealed class PreflightTests(ImporterDatabases databases) {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_fake_source_matches_expected_columns() {
        var source = await databases.CreateSourceAsync();

        var (result, report) = await PreflightAsync(source, ExpectedSchema.Embedded);

        Assert.Equal(ExpectedSchema.Embedded.Tables.Keys.Order(), result.Tables.Keys.Order());
        Assert.Equal(0L, report.Fact("autoconfirmProbe")!.GetValue<long>());
        Assert.Single(report.Fact("cronJobs")!.AsArray());
        Assert.NotEmpty(report.Fact("policies")!.AsArray());
    }

    [Fact]
    public async Task An_unknown_column_fails() {
        var source = await databases.CreateSourceAsync();
        await ImporterDatabases.ExecuteAsync(source, "alter table public.teams add column vanity_url text");

        var failure = await Assert.ThrowsAsync<PreflightFailedException>(() => PreflightAsync(source, ExpectedSchema.Embedded));

        Assert.Equal(["unknown column public.teams.vanity_url"], failure.Errors);
    }

    [Fact]
    public async Task An_allow_listed_unknown_column_is_archived() {
        var source = await databases.CreateSourceAsync();
        await ImporterDatabases.ExecuteAsync(source, "alter table public.teams add column mascot text");
        var expected = ExpectedSchema.Embedded with { AllowArchiveOnly = ["public.teams.mascot"] };

        var (result, _) = await PreflightAsync(source, expected);

        Assert.Contains("mascot", result.Plan("public.teams").Expected.Archive);
    }

    [Fact]
    public async Task An_unknown_table_fails_unless_allow_listed() {
        var source = await databases.CreateSourceAsync();
        await ImporterDatabases.ExecuteAsync(source, "create table public.side_project (id uuid primary key, secret_token text)");

        var failure = await Assert.ThrowsAsync<PreflightFailedException>(() => PreflightAsync(source, ExpectedSchema.Embedded));
        var (allowed, _) = await PreflightAsync(source, ExpectedSchema.Embedded with { AllowArchiveOnly = ["public.side_project"] });

        Assert.Equal(["unknown table public.side_project"], failure.Errors);
        Assert.Equal(TableDisposition.Archive, allowed.Plan("public.side_project").Expected.Disposition);
    }

    [Fact]
    public void The_production_auth_column_types_are_expected() {
        var users = ExpectedSchema.Embedded.Find("auth.users")!;

        Assert.All(
            ["aud", "role", "email", "encrypted_password", "confirmation_token", "recovery_token", "email_change_token_new",
                "email_change", "phone_change_token", "email_change_token_current", "reauthentication_token"],
            column => Assert.Equal("varchar", users.Columns[column]));
        Assert.Equal("text", users.Columns["phone"]);
        Assert.Equal("text", users.Columns["phone_change"]);
    }

    [Fact]
    public async Task A_late_team_action_item_comments_table_must_be_allow_listed() {
        var source = await databases.CreateSourceAsync();
        await ImporterDatabases.ExecuteAsync(source, await File.ReadAllTextAsync(Path.Combine(
            ImporterDatabases.SupabaseDirectory, "migrations", "20250822130500_create_team_action_item_comments.sql"), Ct));

        var failure = await Assert.ThrowsAsync<PreflightFailedException>(() => PreflightAsync(source, ExpectedSchema.Embedded));
        var (allowed, _) = await PreflightAsync(source, ExpectedSchema.Embedded with { AllowArchiveOnly = ["public.team_action_item_comments"] });

        Assert.Equal(["unknown table public.team_action_item_comments"], failure.Errors);
        Assert.Equal(TableDisposition.Archive, allowed.Plan("public.team_action_item_comments").Expected.Disposition);
    }

    [Fact]
    public async Task An_unlisted_auth_column_fails_even_when_allow_listed() {
        var source = await databases.CreateSourceAsync();
        await ImporterDatabases.ExecuteAsync(source, "alter table auth.users add column backup_codes text");
        var expected = ExpectedSchema.Embedded with { AllowArchiveOnly = ["auth.users.backup_codes"] };

        var failure = await Assert.ThrowsAsync<PreflightFailedException>(() => PreflightAsync(source, expected));

        Assert.Equal(["unlisted auth column auth.users.backup_codes: add it to the mapped or sealed lists"], failure.Errors);
    }

    [Fact]
    public async Task An_unlisted_auth_table_fails_even_when_allow_listed() {
        var source = await databases.CreateSourceAsync();
        await ImporterDatabases.ExecuteAsync(source, "create table auth.passkey_vault (id uuid primary key, public_key bytea)");
        var expected = ExpectedSchema.Embedded with { AllowArchiveOnly = ["auth.passkey_vault"] };

        var failure = await Assert.ThrowsAsync<PreflightFailedException>(() => PreflightAsync(source, expected));

        Assert.Equal(["unlisted auth table auth.passkey_vault: add it to expected-columns.json (exclude, or map with its sealed columns)"], failure.Errors);
    }

    [Fact]
    public async Task A_missing_column_a_changed_type_and_a_changed_key_fail() {
        var source = await databases.CreateSourceAsync();
        await ImporterDatabases.ExecuteAsync(source, """
            alter table public.retro_items drop column session_id;
            alter table public.retro_columns alter column position type bigint;
            alter table public.user_favorite_teams drop constraint user_favorite_teams_pkey;
            alter table public.user_favorite_teams add primary key (user_id, team_id);
            """);

        var failure = await Assert.ThrowsAsync<PreflightFailedException>(() => PreflightAsync(source, ExpectedSchema.Embedded));

        Assert.Equal([
            "column public.retro_columns.position is int8, expected int4",
            "expected column public.retro_items.session_id is missing",
            "primary key of public.user_favorite_teams is (user_id, team_id), expected (id)",
        ], failure.Errors.Order());
    }

    [Fact]
    public async Task Columns_hidden_by_privileges_are_still_seen() {
        var source = await databases.CreateSourceAsync();
        await ImporterDatabases.ExecuteAsync(source, """
            alter table public.teams add column hidden_secret text;
            do $$ begin
              if not exists (select from pg_roles where rolname = 'importer_reader') then
                create role importer_reader login password 'reader';
              end if;
            end $$;
            grant usage on schema auth, public, storage to importer_reader;
            grant select (id, name) on public.teams to importer_reader;
            """);
        var reader = new NpgsqlConnectionStringBuilder(source) { Username = "importer_reader", Password = "reader" }.ConnectionString;

        var failure = await Assert.ThrowsAsync<PreflightFailedException>(async () => {
            await using var connection = new NpgsqlConnection(reader);
            await connection.OpenAsync(Ct);
            Preflight.Compare(await SourceCatalog.ReadAsync(connection, Ct), ExpectedSchema.Embedded);
        });

        Assert.Contains("unknown column public.teams.hidden_secret", failure.Errors);
    }

    private static async Task<(PreflightResult, ImportReport)> PreflightAsync(string source, ExpectedSchema expected) {
        var report = new ImportReport();
        await using var connection = new NpgsqlConnection(source);
        await connection.OpenAsync(Ct);
        return (await Preflight.RunAsync(connection, expected, report, Ct), report);
    }
}
