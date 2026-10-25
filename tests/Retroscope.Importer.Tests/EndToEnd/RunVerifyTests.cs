using System.Text.Json.Nodes;
using Retroscope.Importer.Map;
using Retroscope.Importer.Source;
using Retroscope.Importer.Tests.Infrastructure;
using Retroscope.Importer.Verify;
using static Retroscope.Importer.Tests.EndToEnd.EndToEndImport;

namespace Retroscope.Importer.Tests.EndToEnd;

[Trait("Category", "EndToEnd")]
[Collection(ImporterPostgres.Name)]
public sealed class RunVerifyTests(ImporterDatabases databases) {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Tables a run rewrites, and the columns whose value is new on every run by design.
    private static readonly string[] DumpedTables = [
        "archive.excluded_tables", "archive.rows", "archive.storage_objects",
    ];

    private static readonly HashSet<string> RandomColumns = [
        "import_run",
        "team_jira_config.credentials_box",
        "team_invites.token_box",
    ];

    [Fact]
    public async Task Run_then_verify_is_green() {
        var import = await CreateAsync(databases);
        await import.FetchStorageAsync();
        var runner = import.Runner();

        var report = await runner.RunAsync(new RunOptions(), Ct);
        var failures = await import.VerifyAsync();

        Assert.Empty(failures);
        Assert.Contains($"verify: {ImportStages.DiscoverChecks().Count} checks, 0 failures", import.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal("succeeded", await import.ScalarAsync<string>("select status from archive.import_runs"));
        Assert.All(report.Tables, x => Assert.Equal(x.Value.Source, x.Value.Imported + x.Value.Archived + x.Value.Excluded + x.Value.Skipped.Values.Sum()));
        Assert.Equal(2, report.Fact(PasswordHashCheck.Fact)!["count"]!.GetValue<int>());
        Assert.Equal(1L, await import.ScalarAsync<long>("select count(*) from users where password_hash is not null and is_admin"));
        Assert.Equal(0L, await import.ScalarAsync<long>(
            $"select count(*) from team_members where team_id = '{Beta}'"));
        Assert.Contains(Beta, report.Findings("teamsWithoutMembers").Select(x => x!.ToString()));
        Assert.Equal(1L, report.Table("public.retro_boards").Skipped[RetroMap.NoTeam]);
        Assert.Equal(1L, report.Table(PokerMap.SessionsTable).Skipped["no_team"]);
        Assert.Equal(0L, await import.ScalarAsync<long>(
            $"select (select count(*) from retro_boards where id = '{QuickBoard}') + (select count(*) from poker_sessions where id = '{QuickSession}')"));

        var foreign = runner.SourceStatements.Where(x => x.Contains("\"consultation_requests\"", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(foreign);
        Assert.All(foreign, x => Assert.StartsWith("select count(*) from ", x, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_run_imports_the_same_rows() {
        var import = await CreateAsync(databases);
        await import.FetchStorageAsync();

        await import.Runner().RunAsync(new RunOptions(), Ct);
        var first = await DumpAsync(import);
        await import.Runner().RunAsync(new RunOptions(), Ct);
        var second = await DumpAsync(import);

        Assert.Equal(2L, await import.ScalarAsync<long>("select count(*) from archive.import_runs where status = 'succeeded'"));
        Assert.True(first.Count > 30, $"only {first.Count} tables dumped");
        Assert.All(first.Keys, x => Assert.True(first[x].Count == second[x].Count, $"{x}: {first[x].Count} rows, then {second[x].Count}"));
        foreach (var (table, rows) in first) {
            Assert.Equal(rows, second[table]);
        }
        Assert.Empty(await import.VerifyAsync());
    }

    [Fact]
    public async Task A_dangling_deferred_reference_is_named_before_commit_and_rolls_the_run_back() {
        var import = await CreateAsync(databases);
        // Only a deferred key can dangle until COMMIT; an immediate one already fails its statement.
        var dangling = new DelegateStage(800, async (context, token) => {
            await using var command = new Npgsql.NpgsqlCommand(
                $"update retro_boards set focus_card_id = '00000000-0000-4000-8000-00000000dead' where id = '{TeamBoard}'", context.Target);
            await command.ExecuteNonQueryAsync(token);
        });

        var failure = await Assert.ThrowsAsync<VerifyFailedException>(() => import.Runner(null, dangling).RunAsync(new RunOptions(), Ct));

        var message = Assert.Single(failure.Failures);
        Assert.Equal("foreign-keys: retro_boards_focus_fk: 1 retro_boards row(s) reference a missing retro_cards (00000000-0000-4000-8000-00000000dead)", message);
        Assert.Equal(0L, await import.ScalarAsync<long>("select count(*) from retro_boards"));
        Assert.Equal("failed", await import.ScalarAsync<string>("select status from archive.import_runs"));
        Assert.Contains("retro_boards_focus_fk", await import.ScalarAsync<string>("select report -> 'facts' ->> 'error' from archive.import_runs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anonymous_posts_reach_no_principal_in_any_table() {
        var import = await CreateAsync(databases);

        await import.FetchAndRunAsync();

        // Image AA is in the anonymous card (and the action item copied from it); image BB is in a named card and in an
        // anonymous comment, so it is one blob that must not name the card's author either.
        Assert.Equal(2L, await import.ScalarAsync<long>("select count(*) from blobs where purpose = 'board_image'"));
        Assert.Equal(0L, await import.ScalarAsync<long>("select count(*) from blobs where purpose = 'board_image' and created_by is not null"));
        Assert.Equal(DBNull.Value, await import.ScalarAsync<object>($"select created_by from team_action_items where id = '{AnonAction}'"));
        Assert.Equal(DBNull.Value, await import.ScalarAsync<object>($"select author_id from retro_cards where id = '{AnonCard}'"));
        Assert.Equal(DBNull.Value, await import.ScalarAsync<object>($"select author_id from retro_comments where id = '{AnonComment}'"));
        Assert.Equal(new Guid(Bob), await import.ScalarAsync<Guid>($"select author_id from retro_cards where id = '{NamedCard}'"));
        Assert.Empty(await import.VerifyAsync());
    }

    private static async Task<SortedDictionary<string, List<string>>> DumpAsync(EndToEndImport import) {
        var window = (await RowsAsync(import.Target,
            "select started_at, finished_at from archive.import_runs where status = 'succeeded' order by finished_at desc limit 1"))[0];
        var (from, to) = ((DateTime)window[0]!, (DateTime)window[1]!);
        var tables = (await RowsAsync(import.Target, """
            select tablename from pg_tables where schemaname = 'public' and tablename not in ('schema_migrations', 'system_state')
            """)).Select(x => (string)x[0]!).Concat(DumpedTables);

        var dump = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var table in tables) {
            var rows = await RowsAsync(import.Target, $"select to_jsonb(t)::text from {table} t");
            dump[table] = [.. rows.Select(x => Mask(table, JsonNode.Parse((string)x[0]!)!.AsObject(), from, to)).Order(StringComparer.Ordinal)];
        }
        return dump;
    }

    // Values the run stamps with its own time (or its time plus the 7-day link invite cap), and the boxes that are random
    // per run, are masked.
    private static string Mask(string table, JsonObject row, DateTime from, DateTime to) {
        foreach (var (column, value) in row.ToList()) {
            if (RandomColumns.Contains(column) || RandomColumns.Contains($"{table}.{column}")) {
                row[column] = value is null ? null : "<random>";
            } else if (value is JsonValue text && text.TryGetValue<string>(out var stamp) && DateTimeOffset.TryParse(stamp, out var at)
                && (InRun(at.UtcDateTime) || InRun(at.UtcDateTime - TimeSpan.FromDays(7)))) {
                row[column] = "<run>";
            }
        }
        return row.ToJsonString();

        bool InRun(DateTime at) => at >= from && at <= to;
    }
}
