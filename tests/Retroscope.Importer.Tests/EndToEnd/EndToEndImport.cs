using System.Collections.Concurrent;
using Npgsql;
using Retroscope.Importer.Archive;
using Retroscope.Importer.Map;
using Retroscope.Importer.Source;
using Retroscope.Importer.Tests.Infrastructure;

namespace Retroscope.Importer.Tests.EndToEnd;

/// <summary>
/// The whole importer against <c>Fixtures/source.sql</c>: every discovered stage and check, fixed production keys (so
/// <c>verify</c> after <c>run</c> opens the same Jira boxes), and storage staged by a fake fetcher.
/// </summary>
public sealed class EndToEndImport {
    public const string Alice = "a1000000-0000-4000-8000-000000000001";
    public const string Bob = "b0b00000-0000-4000-8000-000000000001";
    public const string Carol = "ca401000-0000-4000-8000-000000000001";
    public const string DaveOld = "d0000000-0000-4000-8000-000000000001";
    public const string Dave = "d0000000-0000-4000-8000-000000000002";
    public const string Alpha = "7ea70000-0000-4000-8000-000000000001";
    public const string Beta = "7ea70000-0000-4000-8000-000000000002";
    public const string TeamBoard = "b0a40000-0000-4000-8000-000000000001";
    public const string QuickBoard = "b0a40000-0000-4000-8000-000000000002";
    public const string AnonCard = "17e00000-0000-4000-8000-000000000001";
    public const string NamedCard = "17e00000-0000-4000-8000-000000000002";
    public const string AnonComment = "c0aa0000-0000-4000-8000-000000000001";
    public const string AnonAction = "ac710000-0000-4000-8000-000000000001";
    public const string TeamSession = "5e000000-0000-4000-8000-000000000001";
    public const string QuickSession = "5e000000-0000-4000-8000-000000000002";
    public const string RevealedRound = "50000000-0000-4000-8000-000000000001";
    public const string VotingRound = "50000000-0000-4000-8000-000000000002";

    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];

    private static readonly string AppKeys = "k1:" + Convert.ToBase64String([.. Enumerable.Range(1, 32).Select(x => (byte)x)]);
    private static readonly string AnonTagKey = Convert.ToBase64String([.. Enumerable.Range(101, 32).Select(x => (byte)x)]);

    private EndToEndImport(ImportHarness harness) => Harness = harness;

    public ImportHarness Harness { get; }

    public string Target => Harness.Target;

    public StringWriter Output => Harness.Output;

    public ImporterSettings Settings => ImporterDatabases.Settings(Harness.Source, Harness.Target) with {
        AppKeys = AppKeys,
        AnonTagKey = AnonTagKey,
        AdminEmails = new HashSet<string>(StringComparer.Ordinal) { "alice@example.com" },
    };

    public static async Task<EndToEndImport> CreateAsync(ImporterDatabases databases) {
        var harness = await ImportHarness.CreateAsync(databases);
        await harness.SourceSqlAsync(await File.ReadAllTextAsync(
            Path.Combine(ImporterDatabases.RepositoryRoot, "tests", "Retroscope.Importer.Tests", "EndToEnd", "Fixtures", "source.sql")));
        return new EndToEndImport(harness);
    }

    /// <summary>The production runner: every stage and check the importer assembly has, plus any test stages.</summary>
    public ImportRunner Runner(string? target = null, params IImportStage[] extra) =>
        new(Settings with { TargetConnectionString = target ?? Target }, Output, TextReader.Null) {
            Stages = [.. ImportStages.Discover().Concat(extra).OrderBy(x => x.Order)],
            LeaseWait = TimeSpan.FromSeconds(2),
        };

    public Task<StagingResult> FetchStorageAsync(string? target = null, IObjectFetcher? fetcher = null) =>
        Runner(target).FetchStorageAsync(fetcher ?? new FakeFetcher(), concurrency: 1, TestContext.Current.CancellationToken);

    public async Task<ImportReport> FetchAndRunAsync() {
        await FetchStorageAsync();
        return await Runner().RunAsync(new RunOptions(), TestContext.Current.CancellationToken);
    }

    public Task<IReadOnlyList<string>> VerifyAsync(string? target = null) =>
        Runner(target).VerifyAsync(TestContext.Current.CancellationToken);

    /// <summary>A copy of the target database (the imported state), for a test that corrupts it.</summary>
    public async Task<string> CloneTargetAsync() {
        var target = new NpgsqlConnectionStringBuilder(Target);
        var name = $"cpy_{Guid.NewGuid():N}";
        await ImporterDatabases.ExecuteAsync(new NpgsqlConnectionStringBuilder(Target) { Database = "postgres" }.ConnectionString,
            $"create database {name} template {target.Database}");
        return new NpgsqlConnectionStringBuilder(Target) { Database = name }.ConnectionString;
    }

    public Task<T> ScalarAsync<T>(string sql) => Harness.TargetScalarAsync<T>(sql);

    public static async Task<List<object?[]>> RowsAsync(string connectionString, string sql) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Serves every object as a small PNG; <paramref name="failAfter"/> requests in, it dies like a killed process.</summary>
    public sealed class FakeFetcher(int failAfter = int.MaxValue) : IObjectFetcher {
        private readonly ConcurrentQueue<Uri> _requested = new();
        private int _calls;

        public IReadOnlyList<Uri> Requested => [.. _requested];

        public Task<byte[]?> FetchAsync(Uri url, int maxBytes, CancellationToken cancellationToken) {
            if (Interlocked.Increment(ref _calls) > failAfter) {
                throw new HttpRequestException("killed");
            }
            _requested.Enqueue(url);
            return Task.FromResult<byte[]?>(Png);
        }
    }
}
