using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Retroscope.Importer.Source;

/// <summary>
/// Phase 2 (DESIGN §h.1): one source connection inside <c>BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY</c>, so every
/// table is read from the same instant. Tables are streamed with binary <c>COPY (SELECT … ORDER BY pk)</c> and cached,
/// because several mapping stages read the same table. Excluded tables can only be counted.
/// Every statement sent to the source is kept in <see cref="Statements"/> (the "foreign tables never read" check).
/// </summary>
public sealed class SourceSnapshot : IAsyncDisposable {
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly Dictionary<string, IReadOnlyList<SourceRow>> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _statements = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SourceSnapshot(NpgsqlConnection connection, NpgsqlTransaction transaction, PreflightResult layout) {
        _connection = connection;
        _transaction = transaction;
        Layout = layout;
    }

    public PreflightResult Layout { get; }

    public IReadOnlyCollection<string> Statements => _statements;

    public static async Task<SourceSnapshot> BeginAsync(NpgsqlConnection source, PreflightResult layout, CancellationToken cancellationToken) {
        var transaction = await source.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var snapshot = new SourceSnapshot(source, transaction, layout);
        // Npgsql's BEGIN does not say READ ONLY; this must be the first statement of the transaction.
        await snapshot.ExecuteAsync("set transaction read only", cancellationToken);
        return snapshot;
    }

    public async Task<IReadOnlyList<SourceRow>> ReadAsync(string table, CancellationToken cancellationToken = default) {
        var plan = Layout.Plan(table);
        if (plan.Expected.Disposition is TableDisposition.Exclude or TableDisposition.Manifest) {
            throw new InvalidOperationException($"{table} is {plan.Expected.Disposition}; its rows are never read.");
        }

        await _gate.WaitAsync(cancellationToken);
        try {
            if (!_cache.TryGetValue(table, out var rows)) {
                _cache[table] = rows = await CopyAsync(plan, cancellationToken);
            }
            return rows;
        } finally {
            _gate.Release();
        }
    }

    public async Task<long> CountAsync(string table, CancellationToken cancellationToken = default) {
        var plan = Layout.Plan(table);
        await _gate.WaitAsync(cancellationToken);
        try {
            return await ScalarAsync<long>($"select count(*) from {Quote(plan.Source)}", cancellationToken);
        } finally {
            _gate.Release();
        }
    }

    /// <summary>Storage objects (bucket, name, size from metadata) for the manifest; no other storage column is read.</summary>
    public async Task<IReadOnlyList<(string Bucket, string Name, long Size)>> StorageObjectsAsync(CancellationToken cancellationToken = default) {
        await _gate.WaitAsync(cancellationToken);
        try {
            const string Sql = """
                select bucket_id, name, coalesce((metadata ->> 'size')::bigint, 0)
                from storage.objects where bucket_id is not null and name is not null order by bucket_id, name
                """;
            _statements.Enqueue(Sql);
            var result = new List<(string, string, long)>();
            await using var command = new NpgsqlCommand(Sql, _connection, _transaction);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) {
                result.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }
            return result;
        } finally {
            _gate.Release();
        }
    }

    /// <summary>Row counts of every table in the layout, excluded ones included, all from the snapshot instant.</summary>
    public async Task<IReadOnlyDictionary<string, long>> CountAllAsync(CancellationToken cancellationToken = default) {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var plan in Layout.Tables.Values) {
            counts[plan.Name] = await CountAsync(plan.Name, cancellationToken);
        }
        return counts;
    }

    /// <summary>
    /// sha256(system identifier ‖ server version ‖ per-table counts). Preflight reads the system identifier outside the
    /// snapshot, because a host that refuses <c>pg_control_system()</c> would otherwise abort this transaction.
    /// </summary>
    public static string Fingerprint(string? systemIdentifier, string serverVersion, IReadOnlyDictionary<string, long> counts) {
        var text = new StringBuilder().Append(systemIdentifier).Append('\n').Append(serverVersion).Append('\n');
        foreach (var (table, count) in counts.OrderBy(x => x.Key, StringComparer.Ordinal)) {
            text.Append(table).Append('=').Append(count).Append('\n');
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public async ValueTask DisposeAsync() {
        await _transaction.DisposeAsync();
        _gate.Dispose();
    }

    internal async Task ExecuteAsync(string sql, CancellationToken cancellationToken) {
        _statements.Enqueue(sql);
        await using var command = new NpgsqlCommand(sql, _connection, _transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<T> ScalarAsync<T>(string sql, CancellationToken cancellationToken) {
        _statements.Enqueue(sql);
        await using var command = new NpgsqlCommand(sql, _connection, _transaction);
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<IReadOnlyList<SourceRow>> CopyAsync(TablePlan plan, CancellationToken cancellationToken) {
        var columns = plan.ReadableColumns.Select(x => plan.Source.Column(x)!).ToList();
        var select = string.Join(", ", columns.Select(x => ReadType(x.Type) is null ? $"{Ident(x.Name)}::text" : Ident(x.Name)));
        var order = string.Join(", ", plan.Expected.PrimaryKey.Select(Ident));
        var sql = $"copy (select {select} from {Quote(plan.Source)} order by {order}) to stdout (format binary)";
        _statements.Enqueue(sql);

        var ordinals = columns.Select((x, i) => (x.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);
        var rows = new List<SourceRow>();
        await using var export = await _connection.BeginBinaryExportAsync(sql, cancellationToken);
        while (await export.StartRowAsync(cancellationToken) != -1) {
            var values = new object?[columns.Count];
            for (var i = 0; i < columns.Count; i++) {
                values[i] = export.IsNull ? await SkipAsync(export, cancellationToken) : await ReadAsync(export, columns[i].Type, cancellationToken);
            }
            rows.Add(new SourceRow(plan, ordinals, values));
        }
        return rows;
    }

    private static async Task<object?> SkipAsync(NpgsqlBinaryExporter export, CancellationToken cancellationToken) {
        await export.SkipAsync(cancellationToken);
        return null;
    }

    private static async Task<object?> ReadAsync(NpgsqlBinaryExporter export, string type, CancellationToken cancellationToken) => ReadType(type) switch {
        NpgsqlDbType.Uuid => await export.ReadAsync<Guid>(NpgsqlDbType.Uuid, cancellationToken),
        NpgsqlDbType.Boolean => await export.ReadAsync<bool>(NpgsqlDbType.Boolean, cancellationToken),
        NpgsqlDbType.Smallint => await export.ReadAsync<short>(NpgsqlDbType.Smallint, cancellationToken),
        NpgsqlDbType.Integer => await export.ReadAsync<int>(NpgsqlDbType.Integer, cancellationToken),
        NpgsqlDbType.Bigint => await export.ReadAsync<long>(NpgsqlDbType.Bigint, cancellationToken),
        NpgsqlDbType.Numeric => await export.ReadAsync<decimal>(NpgsqlDbType.Numeric, cancellationToken),
        NpgsqlDbType.Double => await export.ReadAsync<double>(NpgsqlDbType.Double, cancellationToken),
        NpgsqlDbType.Real => await export.ReadAsync<float>(NpgsqlDbType.Real, cancellationToken),
        NpgsqlDbType.TimestampTz => await export.ReadAsync<DateTime>(NpgsqlDbType.TimestampTz, cancellationToken),
        NpgsqlDbType.Timestamp => await export.ReadAsync<DateTime>(NpgsqlDbType.Timestamp, cancellationToken),
        NpgsqlDbType.Jsonb => await export.ReadAsync<string>(NpgsqlDbType.Jsonb, cancellationToken),
        NpgsqlDbType.Json => await export.ReadAsync<string>(NpgsqlDbType.Json, cancellationToken),
        NpgsqlDbType.Bytea => await export.ReadAsync<byte[]>(NpgsqlDbType.Bytea, cancellationToken),
        NpgsqlDbType.Array | NpgsqlDbType.Uuid => await export.ReadAsync<Guid[]>(NpgsqlDbType.Array | NpgsqlDbType.Uuid, cancellationToken),
        NpgsqlDbType.Array | NpgsqlDbType.Text => await export.ReadAsync<string[]>(NpgsqlDbType.Array | NpgsqlDbType.Text, cancellationToken),
        NpgsqlDbType.Array | NpgsqlDbType.Integer => await export.ReadAsync<int[]>(NpgsqlDbType.Array | NpgsqlDbType.Integer, cancellationToken),
        _ => await export.ReadAsync<string>(NpgsqlDbType.Text, cancellationToken),
    };

    // Types without an entry are selected as ::text (enums, varchar, inet, …) and read as strings.
    private static NpgsqlDbType? ReadType(string type) => type switch {
        "uuid" => NpgsqlDbType.Uuid,
        "bool" => NpgsqlDbType.Boolean,
        "int2" => NpgsqlDbType.Smallint,
        "int4" => NpgsqlDbType.Integer,
        "int8" => NpgsqlDbType.Bigint,
        "numeric" => NpgsqlDbType.Numeric,
        "float8" => NpgsqlDbType.Double,
        "float4" => NpgsqlDbType.Real,
        "timestamptz" => NpgsqlDbType.TimestampTz,
        "timestamp" => NpgsqlDbType.Timestamp,
        "jsonb" => NpgsqlDbType.Jsonb,
        "json" => NpgsqlDbType.Json,
        "bytea" => NpgsqlDbType.Bytea,
        "text" => NpgsqlDbType.Text,
        "_uuid" => NpgsqlDbType.Array | NpgsqlDbType.Uuid,
        "_text" => NpgsqlDbType.Array | NpgsqlDbType.Text,
        "_int4" => NpgsqlDbType.Array | NpgsqlDbType.Integer,
        _ => null,
    };

    private static string Quote(SourceTable table) => $"{Ident(table.Schema)}.{Ident(table.Table)}";

    private static string Ident(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
