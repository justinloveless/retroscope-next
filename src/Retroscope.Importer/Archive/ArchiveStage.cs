using Retroscope.Importer.Map;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Archive;

/// <summary>
/// Phase 7 for everything the layout decides on its own (DESIGN §h.2, §h.3): archive-only tables go to
/// <c>archive.rows</c> whole, excluded tables are only counted into <c>archive.excluded_tables</c>, and the dropped
/// columns of every kept table go to <c>&lt;table&gt;#dropped</c> (<c>#meta</c> for <c>auth.*</c>). Rows that mapping
/// stages archive for their own reasons (expired invites, merged accounts) are added by those stages.
/// </summary>
public sealed class ArchiveStage : IImportStage {
    public int Order => 900;

    public string Name => "archive";

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken) {
        foreach (var plan in context.Source.Layout.WithDisposition(TableDisposition.Archive)) {
            var rows = await context.Source.ReadAsync(plan.Name, cancellationToken);
            foreach (var row in rows) {
                context.Archive.AddRow(row);
            }
            context.Report.Table(plan.Name).Archived += rows.Count;
        }

        foreach (var plan in context.Source.Layout.WithDisposition(TableDisposition.Exclude)) {
            var count = await context.Source.CountAsync(plan.Name, cancellationToken);
            context.Archive.AddExcluded(plan.Name, count, plan.Expected.Reason ?? "excluded");
            context.Report.Table(plan.Name).Excluded = count;
        }

        foreach (var plan in context.Source.Layout.WithDisposition(TableDisposition.Map).Where(x => x.Expected.Archive.Count > 0)) {
            foreach (var row in await context.Source.ReadAsync(plan.Name, cancellationToken)) {
                context.Archive.AddDroppedColumns(row);
            }
        }
    }
}
