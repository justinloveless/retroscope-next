using Npgsql;

namespace Retroscope.Importer.Source;

public sealed class LeaseUnavailableException()
    : Exception("Another process holds the leader lease on the target. Stop the app before importing.");

/// <summary>
/// The target side of a run, connected as <c>retroscope_owner</c>. The owner role's timeouts are 0 (U1), and every
/// session also sets them to 0 so a database-level default cannot cut a long import short (S2).
/// </summary>
public static class TargetDatabase {
    /// <summary>The app's lease key (DESIGN §c.9): <c>pg_try_advisory_lock(0x52455452)</c>.</summary>
    public const long LeaseKey = 0x52455452;

    /// <summary>The exclusive fence key that waits for every in-flight fenced transaction (<c>rs_fence</c>).</summary>
    public const long FenceKey = 1381123153;

    public const string ApplicationName = "retroscope-import";

    public static async Task<NpgsqlConnection> OpenAsync(string ownerConnectionString, CancellationToken cancellationToken) {
        var builder = new NpgsqlConnectionStringBuilder(ownerConnectionString) {
            ApplicationName = ApplicationName,
            CommandTimeout = 0,
            Pooling = false,
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        try {
            await ExecuteAsync(connection, "set statement_timeout = 0; set idle_in_transaction_session_timeout = 0; set lock_timeout = 0", cancellationToken);
        } catch {
            await connection.DisposeAsync();
            throw;
        }
        return connection;
    }

    public static async Task<DateTime?> ReadCutoverAsync(NpgsqlConnection target, CancellationToken cancellationToken) {
        await using var command = new NpgsqlCommand("select cutover_at from system_state", target);
        return await command.ExecuteScalarAsync(cancellationToken) as DateTime?;
    }

    /// <summary>Sets <c>cutover_at</c> once; a second call returns the first timestamp.</summary>
    public static async Task<DateTime> SetCutoverAsync(NpgsqlConnection target, CancellationToken cancellationToken) {
        await using var command = new NpgsqlCommand(
            "update system_state set cutover_at = coalesce(cutover_at, now()) returning cutover_at", target);
        return (DateTime)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Phase 4 (S24): take the lease lock for the life of <paramref name="target"/>, then bump <c>leader_epoch</c>
    /// under the exclusive fence key. The bump waits for any in-flight fenced transaction, and afterwards every
    /// fenced write of a still-running app raises <c>RS001</c>. Returns the new epoch.
    /// </summary>
    public static async Task<long> FenceAsync(NpgsqlConnection target, Guid runId, TimeSpan leaseWait, CancellationToken cancellationToken) {
        var deadline = DateTime.UtcNow + leaseWait;
        while (!await TryLeaseAsync(target, cancellationToken)) {
            if (DateTime.UtcNow >= deadline) {
                throw new LeaseUnavailableException();
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        await using var transaction = await target.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(target, $"select pg_advisory_xact_lock({FenceKey})", cancellationToken);
        await using var bump = new NpgsqlCommand(
            "update system_state set leader_epoch = leader_epoch + 1, leader_instance = $1, leader_since = now() returning leader_epoch",
            target) {
            Parameters = { new() { Value = runId } },
        };
        var epoch = (long)(await bump.ExecuteScalarAsync(cancellationToken))!;
        await transaction.CommitAsync(cancellationToken);
        return epoch;
    }

    /// <summary>Every app table except the migration ledger and the system row, plus the per-run archive tables.</summary>
    public static async Task TruncateAsync(NpgsqlConnection target, CancellationToken cancellationToken) {
        await using var list = new NpgsqlCommand("""
            select string_agg(format('%I.%I', schemaname, tablename), ', ' order by schemaname, tablename)
            from pg_tables
            where (schemaname = 'public' and tablename not in ('schema_migrations', 'system_state'))
               or (schemaname = 'archive' and tablename in ('rows', 'storage_objects', 'excluded_tables'))
            """, target);
        var tables = (string)(await list.ExecuteScalarAsync(cancellationToken))!;
        await ExecuteAsync(target, $"truncate {tables}", cancellationToken);
    }

    public static async Task ExecuteAsync(NpgsqlConnection target, string sql, CancellationToken cancellationToken) {
        await using var command = new NpgsqlCommand(sql, target);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TryLeaseAsync(NpgsqlConnection target, CancellationToken cancellationToken) {
        await using var command = new NpgsqlCommand($"select pg_try_advisory_lock({LeaseKey})", target);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
