using System.Text.Json.Nodes;
using Retroscope.Importer.Archive;
using Retroscope.Importer.Map;

namespace Retroscope.Importer.Verify;

/// <summary>
/// Counts per source table (§h.5): <c>source = imported + skipped{reason} + archived</c> (+ excluded for tables that
/// are only counted). <c>source</c> is the snapshot's own <c>count(*)</c>, the figure <c>plan</c> prints for the same
/// instant, so a row a stage neither imported, skipped nor archived fails the run. The <c>verify</c> verb repeats the
/// arithmetic on the report of the last successful run.
/// </summary>
public sealed class CountsCheck : IVerifyCheck {
    public string Name => "counts";

    public async Task<IReadOnlyList<string>> RunAsync(VerifyContext context, CancellationToken cancellationToken) {
        if (context.Report is { } report) {
            return Check(report.Tables.Select(x => (x.Key, x.Value.Source, Accounted(x.Value))));
        }

        var rows = await TargetQueries.RowsAsync(context.Target, """
            select report -> 'tables' from archive.import_runs where status = 'succeeded' order by finished_at desc limit 1
            """, cancellationToken);
        if (rows.Count == 0 || rows[0][0] is not string json || JsonNode.Parse(json) is not JsonObject tables) {
            return ["no successful import run recorded its counts"];
        }
        return Check(tables.Select(x => (x.Key, Number(x.Value, "source"), Number(x.Value, "imported") + Number(x.Value, "archived")
            + Number(x.Value, "excluded") + (x.Value?["skipped"] as JsonObject ?? []).Sum(y => y.Value!.GetValue<long>()))));
    }

    private static long Accounted(TableTally tally) => tally.Imported + tally.Archived + tally.Excluded + tally.Skipped.Values.Sum();

    private static long Number(JsonNode? table, string name) => table?[name]?.GetValue<long>() ?? 0;

    private static IReadOnlyList<string> Check(IEnumerable<(string Table, long Source, long Accounted)> tables) =>
        TargetQueries.Summarize([.. tables
            .Where(x => x.Source != x.Accounted)
            .OrderBy(x => x.Table, StringComparer.Ordinal)
            .Select(x => $"{x.Table}: {x.Source} source rows, {x.Accounted} imported, skipped, archived or excluded")]);
}
