using Npgsql;

namespace Retroscope.Importer.Verify;

/// <summary>Small read helpers for the checks; every check reads the target only (the <c>verify</c> verb is read-only).</summary>
internal static class TargetQueries {
    /// <summary>At most this many offending rows are named per check; the rest are counted.</summary>
    public const int MaxListed = 20;

    /// <summary>A query whose first column is one failure message per offending row; the overflow is summarized.</summary>
    public static async Task<IReadOnlyList<string>> FailuresAsync(
        NpgsqlConnection target,
        string sql,
        CancellationToken cancellationToken,
        params object[] parameters) {
        var rows = await RowsAsync(target, sql, cancellationToken, parameters);
        return Summarize([.. rows.Select(x => (string)x[0]!)]);
    }

    public static IReadOnlyList<string> Summarize(IReadOnlyList<string> failures) =>
        failures.Count <= MaxListed ? failures : [.. failures.Take(MaxListed), $"… and {failures.Count - MaxListed} more"];

    public static async Task<List<object?[]>> RowsAsync(NpgsqlConnection target, string sql, CancellationToken cancellationToken, params object[] parameters) {
        await using var command = new NpgsqlCommand(sql, target);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(cancellationToken)) {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }
}
