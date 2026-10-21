using Npgsql;

namespace Retroscope.Importer.Source;

public sealed record SourceColumn(string Name, string Type);

public sealed record SourceTable(string Name, IReadOnlyList<SourceColumn> Columns, IReadOnlyList<string> PrimaryKey) {
    public string Schema => Name[..Name.IndexOf('.', StringComparison.Ordinal)];

    public string Table => Name[(Name.IndexOf('.', StringComparison.Ordinal) + 1)..];

    public SourceColumn? Column(string name) => Columns.FirstOrDefault(x => x.Name == name);
}

/// <summary>
/// The live shape of the source, read from <c>pg_catalog</c> rather than <c>information_schema</c>: the latter
/// hides columns the connecting role has no privilege on, which would let an unknown column slip past preflight.
/// Enum columns report the type <c>enum</c>.
/// </summary>
public sealed class SourceCatalog {
    private const string Query = """
        select n.nspname || '.' || c.relname,
               a.attname,
               case when t.typtype = 'e' then 'enum' else t.typname end,
               coalesce((select k.ord from unnest(i.indkey) with ordinality k(attnum, ord) where k.attnum = a.attnum), 0)::int
        from pg_class c
        join pg_namespace n on n.oid = c.relnamespace
        join pg_attribute a on a.attrelid = c.oid and a.attnum > 0 and not a.attisdropped
        join pg_type t on t.oid = a.atttypid
        left join pg_index i on i.indrelid = c.oid and i.indisprimary
        where c.relkind in ('r', 'p') and n.nspname in ('public', 'auth', 'storage')
        order by 1, a.attnum
        """;

    private readonly Dictionary<string, SourceTable> _tables;

    public SourceCatalog(IEnumerable<SourceTable> tables) {
        _tables = tables.ToDictionary(x => x.Name, StringComparer.Ordinal);
    }

    public IEnumerable<SourceTable> Tables => _tables.Values.OrderBy(x => x.Name, StringComparer.Ordinal);

    public SourceTable? Find(string name) => _tables.GetValueOrDefault(name);

    public SourceTable Get(string name) =>
        Find(name) ?? throw new InvalidOperationException($"Source table {name} does not exist.");

    public static async Task<SourceCatalog> ReadAsync(NpgsqlConnection source, CancellationToken cancellationToken) {
        var columns = new Dictionary<string, List<SourceColumn>>(StringComparer.Ordinal);
        var keys = new Dictionary<string, SortedList<int, string>>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(Query, source);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var table = reader.GetString(0);
            var column = new SourceColumn(reader.GetString(1), reader.GetString(2));
            if (!columns.TryGetValue(table, out var list)) {
                columns[table] = list = [];
                keys[table] = [];
            }
            list.Add(column);
            var keyPosition = reader.GetInt32(3);
            if (keyPosition > 0) {
                keys[table].Add(keyPosition, column.Name);
            }
        }

        return new SourceCatalog(columns.Select(x => new SourceTable(x.Key, x.Value, [.. keys[x.Key].Values])));
    }
}
