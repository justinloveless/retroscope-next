using Npgsql;
using NpgsqlTypes;

namespace Retroscope.Importer.Archive;

/// <summary>
/// <c>archive.import_runs</c>. A run row is committed before the main transaction, so a rolled-back run still leaves
/// a <c>failed</c> row behind; success is recorded inside the main transaction together with the data.
/// </summary>
public static class ImportRuns {
    public static async Task StartAsync(NpgsqlConnection target, Guid runId, string fingerprint, string? sealKeyId, CancellationToken cancellationToken) {
        await using var command = new NpgsqlCommand(
            "insert into archive.import_runs (id, status, source_fingerprint, seal_key_id) values ($1, 'running', $2, $3)", target) {
            Parameters = {
                new() { Value = runId },
                new() { Value = fingerprint },
                new() { Value = (object?)sealKeyId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text },
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static Task SucceedAsync(NpgsqlConnection target, Guid runId, string reportJson, CancellationToken cancellationToken) =>
        FinishAsync(target, runId, "succeeded", reportJson, cancellationToken);

    public static Task FailAsync(NpgsqlConnection target, Guid runId, string reportJson, CancellationToken cancellationToken) =>
        FinishAsync(target, runId, "failed", reportJson, cancellationToken);

    private static async Task FinishAsync(NpgsqlConnection target, Guid runId, string status, string reportJson, CancellationToken cancellationToken) {
        await using var command = new NpgsqlCommand(
            "update archive.import_runs set status = $2, finished_at = now(), report = $3 where id = $1", target) {
            Parameters = {
                new() { Value = runId },
                new() { Value = status },
                new() { Value = reportJson, NpgsqlDbType = NpgsqlDbType.Jsonb },
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
