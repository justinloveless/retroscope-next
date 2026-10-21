using Npgsql;
using Retroscope.Importer.Archive;
using Retroscope.Importer.Source;

const string Usage = """
    usage: retroscope-import <verb> [options]

    verbs:
      plan            introspect the source, HTML tag histogram, per-table counts (no writes)
      fetch-storage   stage avatar bytes in archive.storage_cache (resumable)
      run             import into the target in one transaction
      verify          run the verification checks against the target
      cutover         record cutover_at; afterwards run needs --force-reimport

    run options:
      --force-reimport --i-have-a-dump=<path>   re-import after cutover (asks for the database name)

    environment: IMPORT_SOURCE_PG, DATABASE_OWNER_URL, APP_KEYS, ANON_TAG_KEY, IMPORT_SEAL_PUBKEY,
                 IMPORT_ADMIN_EMAILS, IMPORT_STORAGE_URL, JIRA_HOST_ALLOWLIST
    """;

if (args.Length == 0 || args[0] is "-h" or "--help") {
    Console.Error.WriteLine(Usage);
    return 2;
}

var force = false;
string? dump = null;
foreach (var option in args.Skip(1)) {
    if (option == "--force-reimport") {
        force = true;
    } else if (option.StartsWith("--i-have-a-dump=", StringComparison.Ordinal)) {
        dump = option["--i-have-a-dump=".Length..];
    } else {
        Console.Error.WriteLine($"unknown option {option}");
        Console.Error.WriteLine(Usage);
        return 2;
    }
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => {
    e.Cancel = true;
    cancellation.Cancel();
};

try {
    var runner = new ImportRunner(ImporterSettings.FromEnvironment(Environment.GetEnvironmentVariable), Console.Out, Console.In);
    switch (args[0]) {
        case "plan":
            Console.WriteLine((await runner.PlanAsync(cancellation.Token)).ToJsonString(indented: true));
            return 0;
        case "fetch-storage":
            using (var client = HttpObjectFetcher.CreateClient()) {
                await runner.FetchStorageAsync(new HttpObjectFetcher(client), cancellationToken: cancellation.Token);
            }
            return 0;
        case "run":
            await runner.RunAsync(new RunOptions(force, dump), cancellation.Token);
            return 0;
        case "verify":
            return (await runner.VerifyAsync(cancellation.Token)).Count == 0 ? 0 : 1;
        case "cutover":
            await runner.CutoverAsync(cancellation.Token);
            return 0;
        default:
            Console.Error.WriteLine($"unknown verb {args[0]}");
            Console.Error.WriteLine(Usage);
            return 2;
    }
} catch (Exception exception) when (exception is SettingsException or PreflightFailedException or UnknownHtmlTagsException
    or ReimportRefusedException or LeaseUnavailableException or VerifyFailedException or NpgsqlException or OperationCanceledException) {
    Console.Error.WriteLine($"retroscope-import: {exception.Message}");
    return 1;
}
