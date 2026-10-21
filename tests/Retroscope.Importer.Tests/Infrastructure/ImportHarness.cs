using System.Security.Cryptography;
using Retroscope.Importer.Archive;
using Retroscope.Importer.Map;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Tests.Infrastructure;

/// <summary>A fresh source/target pair plus a runner whose stages default to the archive stage only.</summary>
public sealed class ImportHarness {
    private static readonly Lazy<RSA> SealKeyPair = new(() => RSA.Create(3072));

    private ImportHarness(string source, string target) {
        Source = source;
        Target = target;
    }

    public string Source { get; }

    public string Target { get; }

    public StringWriter Output { get; } = new();

    public static RSA SealKey => SealKeyPair.Value;

    public static string SealPublicKeyPem => SealKey.ExportSubjectPublicKeyInfoPem();

    public static async Task<ImportHarness> CreateAsync(ImporterDatabases databases) =>
        new(await databases.CreateSourceAsync(), await databases.CreateTargetAsync());

    public ImportRunner Runner(
        IReadOnlyList<IImportStage>? stages = null,
        string? sealPublicKeyPem = null,
        string typed = "",
        IReadOnlyList<IVerifyCheck>? checks = null) =>
        new(ImporterDatabases.Settings(Source, Target, sealPublicKeyPem), Output, new StringReader(typed + Environment.NewLine)) {
            Stages = stages ?? [new ArchiveStage()],
            Checks = checks ?? [],
            LeaseWait = TimeSpan.FromSeconds(2),
        };

    public Task SourceSqlAsync(string sql) => ImporterDatabases.ExecuteAsync(Source, sql);

    public Task TargetSqlAsync(string sql) => ImporterDatabases.ExecuteAsync(Target, sql);

    public Task<T> SourceScalarAsync<T>(string sql) => ImporterDatabases.ScalarAsync<T>(Source, sql);

    public Task<T> TargetScalarAsync<T>(string sql) => ImporterDatabases.ScalarAsync<T>(Target, sql);
}

/// <summary>A stage built from a delegate, for tests that need to observe or slow down the main transaction.</summary>
public sealed class DelegateStage(int order, Func<ImportContext, CancellationToken, Task> run) : IImportStage {
    public int Order => order;

    public string Name => "test";

    public Task RunAsync(ImportContext context, CancellationToken cancellationToken) => run(context, cancellationToken);
}
