using System.Text.Json.Nodes;
using Npgsql;
using Retroscope.Importer.Archive;
using Retroscope.Importer.Map;

namespace Retroscope.Importer.Source;

public sealed class VerifyFailedException(IReadOnlyList<string> failures)
    : Exception("Verification failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures.Select(x => "  - " + x))) {
    public IReadOnlyList<string> Failures { get; } = failures;
}

public sealed record RunOptions(bool ForceReimport = false, string? DumpPath = null);

/// <summary>
/// The verbs of <c>retroscope-import</c> (DESIGN §h.1). <c>run</c> goes preflight → snapshot → HTML histogram →
/// fence → one target transaction (truncate, stages, archive, checks) → commit. Storage bytes are staged beforehand
/// by <c>fetch-storage</c>, outside that transaction.
/// </summary>
public sealed class ImportRunner(ImporterSettings settings, TextWriter output, TextReader input) {
    public IReadOnlyList<IImportStage> Stages { get; init; } = ImportStages.Discover();

    public IReadOnlyList<IVerifyCheck> Checks { get; init; } = ImportStages.DiscoverChecks();

    public ExpectedSchema Expected { get; init; } = ExpectedSchema.Embedded;

    /// <summary>How long <c>run</c> waits for the leader lease before refusing (an app may still hold it).</summary>
    public TimeSpan LeaseWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Every statement the last snapshot sent to the source (tests assert foreign tables are only counted).</summary>
    public IReadOnlyCollection<string> SourceStatements { get; private set; } = [];

    public async Task<ImportReport> PlanAsync(CancellationToken cancellationToken = default) {
        var report = new ImportReport();
        await using var source = await OpenSourceAsync(cancellationToken);
        var layout = await Preflight.RunAsync(source, Expected, report, cancellationToken);
        await using var snapshot = await SourceSnapshot.BeginAsync(source, layout, cancellationToken);
        try {
            await RecordCountsAsync(snapshot, report, cancellationToken);
            var histogram = await HtmlHistogram.BuildAsync(snapshot, cancellationToken);
            report.Set("htmlHistogram", histogram.ToJson());
            histogram.ThrowIfUnknown();
            return report;
        } finally {
            SourceStatements = [.. snapshot.Statements];
        }
    }

    public async Task<StagingResult> FetchStorageAsync(IObjectFetcher fetcher, int concurrency = StorageCache.DefaultConcurrency, CancellationToken cancellationToken = default) {
        settings.EnsureDistinctDatabases();
        var storageUrl = settings.RequireStorageUrl();
        IReadOnlyList<StagingItem> items;
        await using (var source = await OpenSourceAsync(cancellationToken)) {
            var layout = await Preflight.RunAsync(source, Expected, new ImportReport(), cancellationToken);
            await using var snapshot = await SourceSnapshot.BeginAsync(source, layout, cancellationToken);
            var principals = await ReadPrincipalsAsync(snapshot, cancellationToken);
            var profiles = (await snapshot.ReadAsync("public.profiles", cancellationToken)).Select(x => (x.Uuid("id")!.Value, x.Text("avatar_url")));
            items = StorageCache.SelectAvatars(await snapshot.StorageObjectsAsync(cancellationToken), profiles, principals, storageUrl);
        }

        await using var target = NpgsqlDataSource.Create(settings.RequireTarget());
        var result = await new StorageCache(target, fetcher).StageAsync(items, concurrency, cancellationToken);
        output.WriteLine($"storage: {items.Count} candidates, {result.Fetched} fetched, {result.AlreadyCached} already cached, {result.Skipped.Count} skipped");
        foreach (var (item, reason) in result.Skipped) {
            output.WriteLine($"  skipped {item.Bucket}/{item.Path}: {reason}");
        }
        return result;
    }

    public async Task<ImportReport> RunAsync(RunOptions options, CancellationToken cancellationToken = default) {
        settings.EnsureDistinctDatabases();
        var keys = settings.RequireKeys();
        await using var target = await TargetDatabase.OpenAsync(settings.RequireTarget(), cancellationToken);
        ReimportGuard.Check(
            await TargetDatabase.ReadCutoverAsync(target, cancellationToken),
            options.ForceReimport,
            options.DumpPath,
            target.Database,
            input,
            output);

        var report = new ImportReport();
        await using var source = await OpenSourceAsync(cancellationToken);
        var layout = await Preflight.RunAsync(source, Expected, report, cancellationToken);
        await using var snapshot = await SourceSnapshot.BeginAsync(source, layout, cancellationToken);
        try {
            var fingerprint = await RecordCountsAsync(snapshot, report, cancellationToken);
            var histogram = await HtmlHistogram.BuildAsync(snapshot, cancellationToken);
            report.Set("htmlHistogram", histogram.ToJson());
            histogram.ThrowIfUnknown();
            var principals = await ReadPrincipalsAsync(snapshot, cancellationToken);

            using var sealer = settings.CreateSealer();
            var runId = Guid.NewGuid();
            report.Set("runId", runId.ToString());
            report.Set("seal", sealer is null
                ? new JsonObject { ["stripped"] = true }
                : new JsonObject { ["keyId"] = sealer.KeyFingerprint, ["wrappedKey"] = Convert.ToBase64String(sealer.WrappedKey.Span) });

            report.Set("leaderEpoch", await TargetDatabase.FenceAsync(target, runId, LeaseWait, cancellationToken));
            await ImportRuns.StartAsync(target, runId, fingerprint, sealer?.KeyFingerprint, cancellationToken);
            try {
                await LoadAsync(target, new ImportContext {
                    RunId = runId,
                    ImportedAt = await NowAsync(target, cancellationToken),
                    Source = snapshot,
                    Target = target,
                    Archive = new ArchiveWriter(runId, sealer),
                    Report = report,
                    Keys = keys,
                    Principals = principals,
                    AdminEmails = settings.AdminEmails,
                    JiraHosts = settings.JiraHosts(),
                }, cancellationToken);
            } catch (Exception exception) {
                report.Set("error", exception.Message);
                await ImportRuns.FailAsync(target, runId, report.ToJsonString(), CancellationToken.None);
                throw;
            }
        } finally {
            SourceStatements = [.. snapshot.Statements];
        }

        report.WriteSummary(output);
        return report;
    }

    public async Task<IReadOnlyList<string>> VerifyAsync(CancellationToken cancellationToken = default) {
        await using var target = await TargetDatabase.OpenAsync(settings.RequireTarget(), cancellationToken);
        await using var transaction = await target.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        await TargetDatabase.ExecuteAsync(target, "set transaction read only", cancellationToken);
        var failures = await CheckAsync(new VerifyContext(target, null) { Keys = settings.RequireKeys() }, cancellationToken);
        output.WriteLine($"verify: {Checks.Count} checks, {failures.Count} failures");
        foreach (var failure in failures) {
            output.WriteLine("  - " + failure);
        }
        return failures;
    }

    public async Task<DateTime> CutoverAsync(CancellationToken cancellationToken = default) {
        await using var target = await TargetDatabase.OpenAsync(settings.RequireTarget(), cancellationToken);
        var cutoverAt = await TargetDatabase.SetCutoverAsync(target, cancellationToken);
        output.WriteLine($"cutover_at = {cutoverAt:O}; `run` now refuses without --force-reimport.");
        return cutoverAt;
    }

    // Phases 6–8 in one transaction; any exception rolls everything back (the transaction is disposed uncommitted).
    private async Task LoadAsync(NpgsqlConnection target, ImportContext context, CancellationToken cancellationToken) {
        await using var transaction = await target.BeginTransactionAsync(cancellationToken);
        await TargetDatabase.ExecuteAsync(target, "set local statement_timeout = 0; set constraints all deferred", cancellationToken);
        await TargetDatabase.TruncateAsync(target, cancellationToken);
        foreach (var stage in Stages) {
            output.WriteLine($"stage {stage.Order} {stage.Name}");
            await stage.RunAsync(context, cancellationToken);
        }
        await context.Archive.FlushAsync(target, cancellationToken);

        var failures = await CheckAsync(new VerifyContext(target, context.Report) { Keys = context.Keys, Run = context }, cancellationToken);
        if (failures.Count > 0) {
            throw new VerifyFailedException(failures);
        }
        await ImportRuns.SucceedAsync(target, context.RunId, context.Report.ToJsonString(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> CheckAsync(VerifyContext context, CancellationToken cancellationToken) {
        var failures = new List<string>();
        foreach (var check in Checks) {
            failures.AddRange((await check.RunAsync(context, cancellationToken)).Select(x => $"{check.Name}: {x}"));
        }
        return failures;
    }

    private async Task<NpgsqlConnection> OpenSourceAsync(CancellationToken cancellationToken) {
        var source = new NpgsqlConnection(settings.RequireSource());
        await source.OpenAsync(cancellationToken);
        return source;
    }

    private static async Task<string> RecordCountsAsync(SourceSnapshot snapshot, ImportReport report, CancellationToken cancellationToken) {
        var counts = await snapshot.CountAllAsync(cancellationToken);
        foreach (var (table, count) in counts) {
            report.Table(table).Source = count;
        }
        var fingerprint = SourceSnapshot.Fingerprint(
            report.Fact("systemIdentifier")?.GetValue<string>(),
            report.Fact("serverVersion")?.GetValue<string>() ?? "",
            counts);
        report.Set("sourceFingerprint", fingerprint);
        return fingerprint;
    }

    private static async Task<PrincipalDirectory> ReadPrincipalsAsync(SourceSnapshot snapshot, CancellationToken cancellationToken) =>
        PrincipalDirectory.Build((await snapshot.ReadAsync("auth.users", cancellationToken)).Select(AuthAccount.FromRow));

    private static async Task<DateTime> NowAsync(NpgsqlConnection target, CancellationToken cancellationToken) {
        await using var command = new NpgsqlCommand("select now()", target);
        return (DateTime)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
