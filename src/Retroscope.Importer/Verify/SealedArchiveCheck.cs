using Retroscope.Importer.Map;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Verify;

/// <summary>
/// No archive row holds a plain value of a column in the explicit sealed lists (§h.3, S25): every such value is
/// <c>{"sealed": …}</c> or <c>{"stripped": true}</c>, whether the row was archived whole or as its dropped columns.
/// </summary>
public sealed class SealedArchiveCheck : IVerifyCheck {
    public string Name => "sealed-archive";

    public ExpectedSchema Expected { get; init; } = ExpectedSchema.Embedded;

    public async Task<IReadOnlyList<string>> RunAsync(VerifyContext context, CancellationToken cancellationToken) {
        var failures = new List<string>();
        foreach (var (table, expected) in Expected.Tables.Where(x => x.Value.Seal.Count > 0).OrderBy(x => x.Key, StringComparer.Ordinal)) {
            var rows = await TargetQueries.RowsAsync(context.Target, """
                select r.source_table || ' ' || r.source_pk || ': ' || e.key || ' is not sealed'
                from archive.rows r, jsonb_each(r.row) e
                where r.source_table = any($1) and e.key = any($2) and jsonb_typeof(e.value) <> 'null'
                  and not (jsonb_typeof(e.value) = 'object'
                           and (e.value = '{"stripped": true}'::jsonb
                                or (e.value ?& array['sealed'] and (select count(*) from jsonb_object_keys(e.value)) = 1
                                    and jsonb_typeof(e.value -> 'sealed') = 'string')))
                order by 1
                """, cancellationToken, new[] { table, table + expected.ArchiveSuffix }, expected.Seal.ToArray());
            failures.AddRange(rows.Select(x => (string)x[0]!));
        }
        return TargetQueries.Summarize(failures);
    }
}
