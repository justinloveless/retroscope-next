using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using Retroscope.Core.Security;
using Retroscope.Importer.Archive;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Tests.Infrastructure;

[Trait("Category", "Source")]
[Collection(ImporterPostgres.Name)]
public sealed class RunTests(ImporterDatabases databases) {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string UserId = "00000000-0000-0000-0000-0000000000a1";

    private static readonly string[] SealedAuthColumns = [
        "recovery_token", "confirmation_token", "email_change_token_new", "email_change_token_current",
        "reauthentication_token", "phone", "phone_change", "phone_change_token",
    ];

    [Fact]
    public async Task Foreign_tables_are_counted_and_never_read() {
        var harness = await ImportHarness.CreateAsync(databases);
        await harness.SourceSqlAsync("""
            insert into public.consultation_requests (name, email, phone, property_type, service_type, description)
              select 'n' || i, 'n' || i || '@example.com', '555', 'house', 'audit', 'private' from generate_series(1, 3) i;
            insert into public.realtime_demo (payload) values ('demo');
            """);
        var runner = harness.Runner();

        var report = await runner.RunAsync(new RunOptions(), Ct);

        Assert.Equal(3L, await harness.TargetScalarAsync<long>(
            "select row_count from archive.excluded_tables where source_table = 'public.consultation_requests'"));
        Assert.Equal("foreign application data", await harness.TargetScalarAsync<string>(
            "select reason from archive.excluded_tables where source_table = 'public.realtime_demo'"));
        Assert.Equal(0L, await harness.TargetScalarAsync<long>(
            "select count(*) from archive.rows where source_table in ('public.consultation_requests', 'public.github_installations', 'public.realtime_demo')"));
        Assert.Equal(3, report.Table("public.consultation_requests").Excluded);
        foreach (var foreign in new[] { "consultation_requests", "github_installations", "realtime_demo", "refresh_tokens", "sessions" }) {
            var statements = runner.SourceStatements.Where(x => x.Contains($".\"{foreign}\"", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(statements);
            Assert.All(statements, x => Assert.StartsWith("select count(*) from ", x, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Auth_secrets_are_sealed_to_the_offline_key_and_open_with_the_private_key() {
        var harness = await ImportHarness.CreateAsync(databases);
        await SeedAuthUserAsync(harness);
        await harness.SourceSqlAsync("""
            set session_replication_role = replica;
            insert into public.app_config (key, value) values ('SLACK_SIGNING_SECRET', 'shh'), ('tier_limit', '5');
            insert into public.teams (id, name, jira_api_key, slack_channel_id) values (gen_random_uuid(), 'T', 'jira-token', 'C1');
            """);

        var report = await harness.Runner(sealPublicKeyPem: ImportHarness.SealPublicKeyPem).RunAsync(new RunOptions(), Ct);

        var wrappedKey = Convert.FromBase64String(report.Fact("seal")!["wrappedKey"]!.GetValue<string>());
        var meta = await ArchiveRowAsync(harness, "auth.users#meta", UserId);
        foreach (var column in SealedAuthColumns) {
            var sealedValue = meta[column]!["sealed"]!.GetValue<string>();
            Assert.True(Sealer.TryOpen(ImportHarness.SealKey, wrappedKey, "auth.users#meta", UserId, column, sealedValue, out var plain), column);
            Assert.Equal($"{column}-secret", Encoding.UTF8.GetString(plain));
        }
        Assert.Equal("""{"locale":"en"}""", meta["raw_user_meta_data"]!.ToJsonString());

        var identity = await ArchiveRowAsync(harness, "auth.identities#meta", "00000000-0000-0000-0000-0000000000b1");
        Assert.True(Sealer.TryOpen(ImportHarness.SealKey, wrappedKey, "auth.identities#meta", "00000000-0000-0000-0000-0000000000b1",
            "identity_data", identity["identity_data"]!["sealed"]!.GetValue<string>(), out var identityData));
        Assert.Contains("squatter@example.com", Encoding.UTF8.GetString(identityData), StringComparison.Ordinal);

        var signing = await ArchiveRowAsync(harness, "public.app_config", "SLACK_SIGNING_SECRET");
        Assert.NotNull(signing["value"]!["sealed"]);
        Assert.Equal("SLACK_SIGNING_SECRET", signing["key"]!.GetValue<string>());
        Assert.Equal("5", (await ArchiveRowAsync(harness, "public.app_config", "tier_limit"))["value"]!.GetValue<string>());

        var team = await harness.TargetScalarAsync<string>("select row::text from archive.rows where source_table = 'public.teams#dropped'");
        Assert.DoesNotContain("jira-token", team, StringComparison.Ordinal);
        Assert.Contains("\"slack_channel_id\": \"C1\"", team, StringComparison.Ordinal);

        var everything = await harness.TargetScalarAsync<string>("select string_agg(row::text, ' ') from archive.rows");
        Assert.All(SealedAuthColumns, x => Assert.DoesNotContain($"{x}-secret", everything, StringComparison.Ordinal));
        Assert.Equal(Sealer.Fingerprint(ImportHarness.SealKey), await harness.TargetScalarAsync<string>("select seal_key_id from archive.import_runs"));
    }

    [Fact]
    public async Task Without_a_seal_key_secrets_are_stripped() {
        var harness = await ImportHarness.CreateAsync(databases);
        await SeedAuthUserAsync(harness);

        var report = await harness.Runner().RunAsync(new RunOptions(), Ct);

        var meta = await ArchiveRowAsync(harness, "auth.users#meta", UserId);
        Assert.All(SealedAuthColumns, x => Assert.True(meta[x]!["stripped"]!.GetValue<bool>()));
        Assert.True(report.Fact("seal")!["stripped"]!.GetValue<bool>());
        Assert.Null(await harness.TargetScalarAsync<object>("select seal_key_id from archive.import_runs") as string);
    }

    [Fact]
    public async Task An_unknown_html_tag_fails_before_anything_is_written() {
        var harness = await ImportHarness.CreateAsync(databases);
        await harness.SourceSqlAsync("""
            insert into public.retro_items (text, author) values
              ('<p>fine &lt;div&gt; text</p>', 'a'),
              ('<p>ok</p><marquee onclick="x()">spin</marquee>', 'b');
            """);
        await harness.TargetSqlAsync("insert into principals (id, kind, display_name) values (gen_random_uuid(), 'guest', 'Kept')");

        var failure = await Assert.ThrowsAsync<UnknownHtmlTagsException>(() => harness.Runner().RunAsync(new RunOptions(), Ct));

        Assert.StartsWith("<marquee> × 1 (e.g. public.retro_items.text#", Assert.Single(failure.Errors), StringComparison.Ordinal);
        Assert.Equal(1L, await harness.TargetScalarAsync<long>("select count(*) from principals"));
        Assert.Equal(0L, await harness.TargetScalarAsync<long>("select leader_epoch from system_state"));
        Assert.Equal(0L, await harness.TargetScalarAsync<long>("select count(*) from archive.import_runs"));
    }

    [Fact]
    public async Task The_epoch_is_bumped_before_anything_is_truncated() {
        var harness = await ImportHarness.CreateAsync(databases);
        await harness.TargetSqlAsync("insert into principals (id, kind, display_name) values (gen_random_uuid(), 'guest', 'Kept')");
        await using var app = new NpgsqlConnection(harness.Target);
        await app.OpenAsync(Ct);
        var appTransaction = await app.BeginTransactionAsync(Ct);
        await ExecuteAsync(app, "select rs_fence(0)");

        var run = harness.Runner().RunAsync(new RunOptions(), Ct);
        await WaitUntilAsync(() => harness.TargetScalarAsync<bool>("""
            select exists (select from pg_stat_activity
                           where application_name = 'retroscope-import' and wait_event_type = 'Lock' and wait_event = 'advisory')
            """));
        var principalsWhileFenceWaits = await harness.TargetScalarAsync<long>("select count(*) from principals");
        var epochWhileFenceWaits = await harness.TargetScalarAsync<long>("select leader_epoch from system_state");
        await appTransaction.CommitAsync(Ct);
        var report = await run;

        Assert.Equal(1L, principalsWhileFenceWaits);
        Assert.Equal(0L, epochWhileFenceWaits);
        Assert.Equal(0L, await harness.TargetScalarAsync<long>("select count(*) from principals"));
        Assert.Equal(1L, report.Fact("leaderEpoch")!.GetValue<long>());
        var fenced = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app, "select rs_fence(0)"));
        Assert.Equal("RS001", fenced.SqlState);
    }

    [Fact]
    public async Task A_running_app_that_holds_the_lease_blocks_the_import() {
        var harness = await ImportHarness.CreateAsync(databases);
        await using var app = new NpgsqlConnection(harness.Target);
        await app.OpenAsync(Ct);
        await ExecuteAsync(app, $"select pg_advisory_lock({TargetDatabase.LeaseKey})");

        await Assert.ThrowsAsync<LeaseUnavailableException>(() => harness.Runner().RunAsync(new RunOptions(), Ct));

        Assert.Equal(0L, await harness.TargetScalarAsync<long>("select leader_epoch from system_state"));
    }

    [Fact]
    public async Task After_cutover_run_needs_force_a_dump_and_the_typed_database_name() {
        var harness = await ImportHarness.CreateAsync(databases);
        await harness.Runner().CutoverAsync(Ct);
        var database = new NpgsqlConnectionStringBuilder(harness.Target).Database!;
        var dump = Path.GetTempFileName();
        var empty = Path.GetTempFileName();
        await File.WriteAllTextAsync(dump, "PGDMP", Ct);
        try {
            var withoutForce = await Assert.ThrowsAsync<ReimportRefusedException>(() => harness.Runner().RunAsync(new RunOptions(), Ct));
            await Assert.ThrowsAsync<ReimportRefusedException>(() => harness.Runner().RunAsync(new RunOptions(ForceReimport: true), Ct));
            await Assert.ThrowsAsync<ReimportRefusedException>(() => harness.Runner().RunAsync(new RunOptions(true, empty), Ct));
            await Assert.ThrowsAsync<ReimportRefusedException>(() => harness.Runner(typed: "wrong").RunAsync(new RunOptions(true, dump), Ct));
            Assert.Equal(0L, await harness.TargetScalarAsync<long>("select count(*) from archive.import_runs"));

            await harness.Runner(typed: database).RunAsync(new RunOptions(true, dump), Ct);

            Assert.Contains("--force-reimport", withoutForce.Message, StringComparison.Ordinal);
            Assert.Equal("succeeded", await harness.TargetScalarAsync<string>("select status from archive.import_runs"));
            Assert.Contains($"Type the database name to continue", harness.Output.ToString(), StringComparison.Ordinal);
        } finally {
            File.Delete(dump);
            File.Delete(empty);
        }
    }

    [Fact]
    public async Task Cutover_is_recorded_once() {
        var harness = await ImportHarness.CreateAsync(databases);

        var first = await harness.Runner().CutoverAsync(Ct);
        var second = await harness.Runner().CutoverAsync(Ct);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task A_long_transaction_survives_database_level_timeouts() {
        var harness = await ImportHarness.CreateAsync(databases);
        var database = new NpgsqlConnectionStringBuilder(harness.Target).Database;
        await harness.TargetSqlAsync($"""
            alter database "{database}" set statement_timeout = '500ms';
            alter database "{database}" set idle_in_transaction_session_timeout = '500ms';
            """);
        var slow = new DelegateStage(100, async (context, token) => {
            await ExecuteAsync(context.Target, "select pg_sleep(1.5)");
            await Task.Delay(TimeSpan.FromSeconds(1.5), token);
            await ExecuteAsync(context.Target, "insert into principals (id, kind, display_name) values (gen_random_uuid(), 'guest', 'Slow')");
        });

        await harness.Runner([slow, new ArchiveStage()]).RunAsync(new RunOptions(), Ct);

        Assert.Equal(1L, await harness.TargetScalarAsync<long>("select count(*) from principals"));
        var fresh = new NpgsqlConnectionStringBuilder(harness.Target) { Pooling = false }.ConnectionString;
        var probe = await Assert.ThrowsAsync<PostgresException>(() => ImporterDatabases.ExecuteAsync(fresh, "select pg_sleep(1.5)"));
        Assert.Equal(PostgresErrorCodes.QueryCanceled, probe.SqlState);
    }

    [Fact]
    public async Task A_failing_stage_rolls_everything_back_and_records_a_failed_run() {
        var harness = await ImportHarness.CreateAsync(databases);
        await harness.TargetSqlAsync("insert into principals (id, kind, display_name) values (gen_random_uuid(), 'guest', 'Kept')");
        var failing = new DelegateStage(100, (_, _) => throw new InvalidOperationException("mapping exploded"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Runner([new ArchiveStage(), failing]).RunAsync(new RunOptions(), Ct));

        Assert.Equal(1L, await harness.TargetScalarAsync<long>("select count(*) from principals"));
        Assert.Equal(0L, await harness.TargetScalarAsync<long>("select count(*) from archive.rows"));
        Assert.Equal("failed", await harness.TargetScalarAsync<string>("select status from archive.import_runs"));
        Assert.Equal("mapping exploded", await harness.TargetScalarAsync<string>("select report -> 'facts' ->> 'error' from archive.import_runs"));
    }

    [Fact]
    public async Task A_failing_check_rolls_everything_back() {
        var harness = await ImportHarness.CreateAsync(databases);
        await harness.SourceSqlAsync("insert into public.feature_flags (flag_name, is_enabled) values ('sentiment', true)");

        var failure = await Assert.ThrowsAsync<VerifyFailedException>(
            () => harness.Runner(checks: [new FixedCheck("broken invariant")]).RunAsync(new RunOptions(), Ct));

        Assert.Equal(["fixed: broken invariant"], failure.Failures);
        Assert.Equal(0L, await harness.TargetScalarAsync<long>("select count(*) from archive.rows"));
        Assert.Equal("failed", await harness.TargetScalarAsync<string>("select status from archive.import_runs"));
    }

    [Fact]
    public async Task Running_twice_reloads_the_archive() {
        var harness = await ImportHarness.CreateAsync(databases);
        await harness.SourceSqlAsync("insert into public.feature_flags (flag_name, is_enabled) values ('sentiment', true)");

        await harness.Runner().RunAsync(new RunOptions(), Ct);
        var first = await harness.TargetScalarAsync<string>("select string_agg(source_table || source_pk || row::text, ',' order by source_table, source_pk) from archive.rows");
        var report = await harness.Runner().RunAsync(new RunOptions(), Ct);
        var second = await harness.TargetScalarAsync<string>("select string_agg(source_table || source_pk || row::text, ',' order by source_table, source_pk) from archive.rows");

        Assert.Equal(first, second);
        Assert.Equal(report.Table("public.feature_flags").Source, report.Table("public.feature_flags").Archived);
        Assert.True(await harness.TargetScalarAsync<bool>("select exists (select from archive.rows where source_pk = 'sentiment')"));
        Assert.Equal(2L, await harness.TargetScalarAsync<long>("select count(*) from archive.import_runs where status = 'succeeded'"));
        Assert.Equal(2L, await harness.TargetScalarAsync<long>("select leader_epoch from system_state"));
    }

    [Fact]
    public async Task Actor_references_resolve_merged_users_and_count_dangling_ids() {
        var harness = await ImportHarness.CreateAsync(databases);
        await harness.SourceSqlAsync("""
            insert into auth.users (id, email, last_sign_in_at) values
              ('00000000-0000-0000-0000-0000000000a1', 'dup@example.com', '2026-01-01'),
              ('00000000-0000-0000-0000-0000000000a2', 'DUP@example.com', '2026-02-01');
            insert into auth.users (id, is_anonymous) values ('00000000-0000-0000-0000-0000000000a3', true);
            """);
        var resolved = new List<Guid?>();
        var stage = new DelegateStage(100, (context, _) => {
            resolved.Add(context.UserRef("public.teams", "creator_id", new Guid("00000000-0000-0000-0000-0000000000a1")));
            resolved.Add(context.UserRef("public.teams", "creator_id", new Guid("00000000-0000-0000-0000-0000000000ff")));
            resolved.Add(context.UserRef("public.teams", "creator_id", null));
            resolved.Add(context.UserRef("public.endorsements", "from_user_id", new Guid("00000000-0000-0000-0000-0000000000a3")));
            resolved.Add(context.PrincipalRef("public.retro_votes", "user_id", new Guid("00000000-0000-0000-0000-0000000000a3")));
            return Task.CompletedTask;
        });

        var report = await harness.Runner([stage]).RunAsync(new RunOptions(), Ct);

        Assert.Equal([
            new Guid("00000000-0000-0000-0000-0000000000a2"), null, null, null, new Guid("00000000-0000-0000-0000-0000000000a3"),
        ], resolved);
        Assert.Equal(1, report.Counter("nulledActorRefs.public.teams.creator_id"));
        Assert.Equal(1, report.Counter("nulledActorRefs.public.endorsements.from_user_id"));
        Assert.Equal(0, report.Counter("nulledActorRefs.public.retro_votes.user_id"));
    }

    [Fact]
    public async Task Plan_reports_counts_and_the_tag_histogram_without_writing() {
        var harness = await ImportHarness.CreateAsync(databases);
        await harness.SourceSqlAsync("insert into public.retro_items (text, author) values ('<p><strong>a</strong></p>', 'x'), ('plain', 'y')");

        var report = await harness.Runner().PlanAsync(Ct);

        Assert.Equal(2, report.Table("public.retro_items").Source);
        Assert.Equal(1L, report.Fact("htmlHistogram")!["tags"]!["strong"]!.GetValue<long>());
        Assert.StartsWith("sha256:", report.Fact("sourceFingerprint")!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0L, await harness.TargetScalarAsync<long>("select count(*) from archive.import_runs"));
        Assert.Equal(0L, await harness.TargetScalarAsync<long>("select leader_epoch from system_state"));
    }

    [Fact]
    public async Task Verify_runs_the_checks_against_the_target() {
        var harness = await ImportHarness.CreateAsync(databases);

        var clean = await harness.Runner().VerifyAsync(Ct);
        var broken = await harness.Runner(checks: [new FixedCheck("orphan card")]).VerifyAsync(Ct);

        Assert.Empty(clean);
        Assert.Equal(["fixed: orphan card"], broken);
    }

    [Fact]
    public async Task Source_and_target_must_differ() {
        var source = await databases.CreateSourceAsync();
        var runner = new ImportRunner(ImporterDatabases.Settings(source, source), TextWriter.Null, TextReader.Null);

        await Assert.ThrowsAsync<SettingsException>(() => runner.RunAsync(new RunOptions(), Ct));
    }

    private static async Task SeedAuthUserAsync(ImportHarness harness) => await harness.SourceSqlAsync($"""
        insert into auth.users (id, email, raw_user_meta_data, recovery_token, confirmation_token, email_change_token_new,
                                email_change_token_current, reauthentication_token, phone, phone_change, phone_change_token)
          values ('{UserId}', 'squatter@example.com', '{"{"}"locale":"en"{"}"}', 'recovery_token-secret', 'confirmation_token-secret',
                  'email_change_token_new-secret', 'email_change_token_current-secret', 'reauthentication_token-secret',
                  'phone-secret', 'phone_change-secret', 'phone_change_token-secret');
        insert into auth.identities (id, provider_id, user_id, identity_data, provider)
          values ('00000000-0000-0000-0000-0000000000b1', 'g-1', '{UserId}', '{"{"}"email":"squatter@example.com"{"}"}', 'google');
        """);

    private static async Task<JsonObject> ArchiveRowAsync(ImportHarness harness, string table, string primaryKey) =>
        JsonNode.Parse(await harness.TargetScalarAsync<string>(
            $"select row::text from archive.rows where source_table = '{table}' and source_pk = '{primaryKey}'"))!.AsObject();

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql) {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition) {
        for (var attempt = 0; attempt < 200; attempt++) {
            if (await condition()) {
                return;
            }
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException("Condition not met within 10 s.");
    }

    private sealed class FixedCheck(string failure) : Map.IVerifyCheck {
        public string Name => "fixed";

        public Task<IReadOnlyList<string>> RunAsync(Map.VerifyContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([failure]);
    }
}
