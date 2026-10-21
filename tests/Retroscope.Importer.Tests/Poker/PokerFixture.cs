using Npgsql;
using Retroscope.Importer.Archive;
using Retroscope.Importer.Map;
using Retroscope.Importer.Source;
using Retroscope.Importer.Tests.Infrastructure;

namespace Retroscope.Importer.Tests.Poker;

/// <summary>
/// A fake source with users and a team, plus the runner that imports it through the identity and team stages (U39)
/// and the poker stage.
/// </summary>
public sealed class PokerFixture {
    public static readonly Guid TeamId = new("7e000000-0000-0000-0000-000000000001");
    public static readonly Guid UserA = new("a0000000-0000-0000-0000-00000000000a");
    public static readonly Guid UserB = new("b0000000-0000-0000-0000-00000000000b");
    public static readonly Guid Observer = new("0b000000-0000-0000-0000-000000000001");
    public static readonly Guid MergedLoser = new("d0000000-0000-0000-0000-000000000001");
    public static readonly Guid MergedKept = new("d0000000-0000-0000-0000-000000000002");
    public static readonly DateTime T0 = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);

    private PokerFixture(ImportHarness harness) => Harness = harness;

    public ImportHarness Harness { get; }

    public static async Task<PokerFixture> CreateAsync(ImporterDatabases databases) {
        var harness = await ImportHarness.CreateAsync(databases);
        await harness.SourceSqlAsync($"""
            insert into auth.users (id, email, last_sign_in_at) values
              ('{UserA}', 'a@example.com', null),
              ('{UserB}', 'b@example.com', null),
              ('{Observer}', 'observer@example.com', null),
              ('{MergedLoser}', 'Dup@Example.com', '2026-01-01T00:00:00Z'),
              ('{MergedKept}', 'dup@example.com', '2026-02-01T00:00:00Z');
            set session_replication_role = replica;
            insert into public.teams (id, name) values ('{TeamId}', 'Team');
            """);
        return new PokerFixture(harness);
    }

    public static string At(double hours) => T0.AddHours(hours).ToString("O");

    public Task SourceSqlAsync(string sql) => Harness.SourceSqlAsync("set session_replication_role = replica;\n" + sql);

    public static IReadOnlyList<IImportStage> IdentityStages => [new UsersMap(), new TeamsMap(), new TeamMembersMap()];

    public ImportRunner Runner() => Harness.Runner(stages: [.. IdentityStages, new PokerMap(), new ArchiveStage()]);

    public async Task<ImportReport> RunAsync() => await Runner().RunAsync(new RunOptions(), TestContext.Current.CancellationToken);

    public async Task<List<Dictionary<string, object?>>> RowsAsync(string sql) {
        await using var connection = new NpgsqlConnection(Harness.Target);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) {
            rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, x => reader.IsDBNull(x) ? null : reader.GetValue(x)));
        }
        return rows;
    }

    public async Task<Dictionary<string, object?>> RowAsync(string sql) => Assert.Single(await RowsAsync(sql));
}
