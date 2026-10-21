using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Security;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Archive;

/// <summary>
/// Collects <c>archive.rows</c> and <c>archive.excluded_tables</c> for one run and writes them with binary COPY inside
/// the main target transaction (DESIGN §h.1 phase 7). Secret values are sealed to the offline key, or replaced with
/// <c>{"stripped": true}</c> when the run has no seal key (§h.3). The seal coordinates are the archive row's own
/// <c>source_table</c> and <c>source_pk</c> plus the column name, so the operator can open a value from what sits
/// next to it.
/// </summary>
public sealed class ArchiveWriter(Guid runId, Sealer? sealer) {
    private readonly Dictionary<(string Table, string PrimaryKey), JsonObject> _rows = [];
    private readonly SortedDictionary<string, (long Count, string Reason)> _excluded = new(StringComparer.Ordinal);

    public Guid RunId { get; } = runId;

    public bool Seals => sealer is not null;

    public int RowCount => _rows.Count;

    /// <summary>Archives the whole row under its source table name (omitted columns are never read).</summary>
    public void AddRow(SourceRow row) => AddRow(row.Table, row, row.Columns);

    /// <summary>Archives the plan's dropped columns of a kept row as <c>&lt;table&gt;#dropped</c> (or <c>#meta</c>), if any is non-null.</summary>
    public void AddDroppedColumns(SourceRow row) {
        var columns = row.Plan.Expected.Archive.Where(x => row[x] is not null).ToList();
        if (columns.Count > 0) {
            AddRow(row.Table + row.Plan.Expected.ArchiveSuffix, row, columns);
        }
    }

    public void AddRow(string sourceTable, SourceRow row, IEnumerable<string> columns) {
        var primaryKey = row.PrimaryKey;
        var json = new JsonObject();
        foreach (var column in columns) {
            var value = row[column];
            json[column] = value is not null && SecretPolicy.IsSecret(row, column)
                ? Protect(sourceTable, primaryKey, column, SealText(row, column, value))
                : ToJson(value, row.TypeOf(column));
        }
        Add(sourceTable, primaryKey, json);
    }

    /// <summary>Adds an already-built archive row. Secret fields must already be protected with <see cref="Protect"/>.</summary>
    public void Add(string sourceTable, string primaryKey, JsonObject row) {
        if (!_rows.TryAdd((sourceTable, primaryKey), row)) {
            throw new InvalidOperationException($"Archive row {sourceTable} {primaryKey} was added twice.");
        }
    }

    public void AddExcluded(string sourceTable, long count, string reason) => _excluded[sourceTable] = (count, reason);

    /// <summary><c>{"sealed": "&lt;b64&gt;"}</c> under the run key, or <c>{"stripped": true}</c> without one.</summary>
    public JsonObject Protect(string sourceTable, string primaryKey, string field, string value) =>
        sealer is null
            ? new JsonObject { ["stripped"] = true }
            : new JsonObject { ["sealed"] = sealer.Seal(sourceTable, primaryKey, field, value) };

    public JsonObject? Get(string sourceTable, string primaryKey) => _rows.GetValueOrDefault((sourceTable, primaryKey));

    public async Task FlushAsync(NpgsqlConnection target, CancellationToken cancellationToken) {
        await using (var rows = await target.BeginBinaryImportAsync(
            "copy archive.rows (source_table, source_pk, row, import_run) from stdin (format binary)", cancellationToken)) {
            foreach (var ((table, primaryKey), row) in _rows.OrderBy(x => x.Key.Table, StringComparer.Ordinal).ThenBy(x => x.Key.PrimaryKey, StringComparer.Ordinal)) {
                await rows.StartRowAsync(cancellationToken);
                await rows.WriteAsync(table, NpgsqlDbType.Text, cancellationToken);
                await rows.WriteAsync(primaryKey, NpgsqlDbType.Text, cancellationToken);
                await rows.WriteAsync(row.ToJsonString(), NpgsqlDbType.Jsonb, cancellationToken);
                await rows.WriteAsync(RunId, NpgsqlDbType.Uuid, cancellationToken);
            }
            await rows.CompleteAsync(cancellationToken);
        }

        await using var excluded = await target.BeginBinaryImportAsync(
            "copy archive.excluded_tables (source_table, row_count, reason, import_run) from stdin (format binary)", cancellationToken);
        foreach (var (table, (count, reason)) in _excluded) {
            await excluded.StartRowAsync(cancellationToken);
            await excluded.WriteAsync(table, NpgsqlDbType.Text, cancellationToken);
            await excluded.WriteAsync(count, NpgsqlDbType.Bigint, cancellationToken);
            await excluded.WriteAsync(reason, NpgsqlDbType.Text, cancellationToken);
            await excluded.WriteAsync(RunId, NpgsqlDbType.Uuid, cancellationToken);
        }
        await excluded.CompleteAsync(cancellationToken);
    }

    public static JsonNode? ToJson(object? value, string type) => value switch {
        null => null,
        string text when type is "json" or "jsonb" => JsonNode.Parse(text),
        string text => text,
        Guid id => id.ToString(),
        DateTime time => time.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        bool flag => flag,
        short number => number,
        int number => number,
        long number => number,
        decimal number => number,
        double number => number,
        float number => number,
        byte[] bytes => Convert.ToBase64String(bytes),
        Array array => new JsonArray([.. array.Cast<object?>().Select(x => ToJson(x, type.TrimStart('_')))]),
        _ => value.ToString(),
    };

    private static string SealText(SourceRow row, string column, object value) =>
        value is Array ? ToJson(value, row.TypeOf(column))!.ToJsonString() : SourceRow.Format(value);
}
