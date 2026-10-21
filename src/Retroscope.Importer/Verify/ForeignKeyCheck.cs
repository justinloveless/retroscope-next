using Retroscope.Importer.Map;

namespace Retroscope.Importer.Verify;

/// <summary>
/// "No FK target missing" (§h.5, S3), one explicit query per foreign key of the target, generated from the catalog.
/// The run defers constraints, so without this a dangling reference would surface only as a COMMIT failure naming one
/// constraint; this lists every dangling row before COMMIT. Like the constraints (MATCH SIMPLE), a key with any null
/// column references nothing.
/// </summary>
public sealed class ForeignKeyCheck : IVerifyCheck {
    private const string ForeignKeysSql = """
        select c.conname, c.conrelid::regclass::text, c.confrelid::regclass::text,
               array(select quote_ident(a.attname) from unnest(c.conkey) with ordinality k(n, o)
                     join pg_attribute a on a.attrelid = c.conrelid and a.attnum = k.n order by k.o),
               array(select quote_ident(a.attname) from unnest(c.confkey) with ordinality k(n, o)
                     join pg_attribute a on a.attrelid = c.confrelid and a.attnum = k.n order by k.o)
        from pg_constraint c join pg_namespace n on n.oid = c.connamespace
        where c.contype = 'f' and n.nspname in ('public', 'archive')
        order by c.conname
        """;

    public string Name => "foreign-keys";

    public async Task<IReadOnlyList<string>> RunAsync(VerifyContext context, CancellationToken cancellationToken) {
        var failures = new List<string>();
        foreach (var key in await TargetQueries.RowsAsync(context.Target, ForeignKeysSql, cancellationToken)) {
            var (name, child, parent) = ((string)key[0]!, (string)key[1]!, (string)key[2]!);
            var columns = (string[])key[3]!;
            var referenced = (string[])key[4]!;
            var notNull = string.Join(" and ", columns.Select(x => $"c.{x} is not null"));
            var matches = string.Join(" and ", columns.Zip(referenced, (x, y) => $"p.{y} = c.{x}"));
            var shown = columns.Length == 1 ? $"c.{columns[0]}::text" : $"concat_ws('|', {string.Join(", ", columns.Select(x => $"c.{x}"))})";
            var rows = await TargetQueries.RowsAsync(context.Target, $"""
                select count(*), (array_agg({shown} order by {shown}))[1:3]
                from {child} c
                where {notNull} and not exists (select from {parent} p where {matches})
                """, cancellationToken);
            var missing = (long)rows[0][0]!;
            if (missing > 0) {
                failures.Add($"{name}: {missing} {child} row(s) reference a missing {parent} ({string.Join(", ", (string[])rows[0][1]!)})");
            }
        }
        return failures;
    }
}
