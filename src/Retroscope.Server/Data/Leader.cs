using System.Globalization;
using Npgsql;
using Retroscope.Core.Room;
using Retroscope.Server.Data.Repos;

namespace Retroscope.Server.Data;

public sealed record LeaderOptions(TimeSpan AcquireWindow, TimeSpan RetryInterval, TimeSpan WatchdogInterval) {
    public const string AcquireSecondsKey = "LEADER_ACQUIRE_SECONDS";

    public static LeaderOptions Default { get; } = new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

    public static LeaderOptions FromConfiguration(IConfiguration configuration) {
        var seconds = configuration[AcquireSecondsKey];
        if (string.IsNullOrWhiteSpace(seconds)) {
            return Default;
        }
        if (!int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1) {
            throw new InvalidOperationException($"{AcquireSecondsKey} must be a positive whole number of seconds.");
        }
        return Default with { AcquireWindow = TimeSpan.FromSeconds(value) };
    }
}

public sealed class LeaderUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class LeadershipLostException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>The fence refused a transaction: another instance became leader, so nothing in it committed.</summary>
public sealed class FencingLostException(Exception? innerException = null)
    : Exception("The leader epoch changed; the transaction was fenced off and rolled back.", innerException);

/// <summary>COMMIT was sent but no answer came back, so the transaction may or may not have committed.</summary>
public sealed class CommitOutcomeUnknownException(Exception innerException)
    : Exception("The connection failed during COMMIT; the outcome of the transaction is unknown.", innerException);

/// <summary>
/// The global leader lease and the epoch fence (§c.9). Starting the host acquires the lease, runs the
/// migrations and bumps <c>system_state.leader_epoch</c>; losing the lease or the fence stops the host, and
/// <see cref="StopAsync"/> then throws so the process exits non-zero and the restart policy brings it back.
/// </summary>
public sealed partial class Leader(Db db, LeaderOptions options, IHostApplicationLifetime lifetime, ILogger<Leader> logger)
    : IHostedService, ILeaderEpoch, IAsyncDisposable {
    public const long LeaseKey = 0x52455452;
    public const long FenceKey = 1381123153;
    public const string LeaseApplicationName = "retroscope-lease";
    public const string FencingLostSqlState = "RS001";

    private const int Follower = 0;
    private const int Leading = 1;
    private const int Lost = 2;

    private readonly CancellationTokenSource _lost = new();
    private readonly CancellationTokenSource _stopping = new();
    private NpgsqlConnection? _lease;
    private Task _watchdog = Task.CompletedTask;
    private long _epoch;
    private int _state;

    public Guid InstanceId { get; } = Guid.NewGuid();

    public bool IsLeader => Volatile.Read(ref _state) == Leading;

    public long Current => IsLeader
        ? Volatile.Read(ref _epoch)
        : throw new InvalidOperationException("This instance does not hold the leader lease.");

    /// <summary>Cancelled once leadership is lost; actors stop taking commands.</summary>
    public CancellationToken LeadershipLost => _lost.Token;

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddSingleton(_ => LeaderOptions.FromConfiguration(configuration));
        services.AddSingleton<Leader>();
        services.AddSingleton<ILeaderEpoch>(sp => sp.GetRequiredService<Leader>());
        services.AddHostedService(sp => sp.GetRequiredService<Leader>());
        services.AddSingleton<CursorRepo>();
    }

    public static void Map(IEndpointRouteBuilder app) {
    }

    public static bool IsFencingLost(Exception exception) =>
        exception is PostgresException { SqlState: FencingLostSqlState };

    public async Task StartAsync(CancellationToken cancellationToken) {
        var lease = await AcquireLeaseAsync(cancellationToken);
        try {
            var applied = await Migrator.FromEmbeddedScripts().RunAsync(lease, db.AppPassword, cancellationToken);
            LogMigrated(applied);
            var epoch = await BumpEpochAsync(lease, cancellationToken);
            _lease = lease;
            Volatile.Write(ref _epoch, epoch);
            Volatile.Write(ref _state, Leading);
            LogAcquired(epoch, InstanceId);
        } catch {
            await lease.DisposeAsync();
            throw;
        }
        _watchdog = WatchLeaseAsync(lease, _stopping.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        await ReleaseAsync();
        if (_lost.IsCancellationRequested) {
            throw new LeadershipLostException("Leadership was lost; exiting so the restart policy re-acquires the lease.");
        }
    }

    public async ValueTask DisposeAsync() {
        await ReleaseAsync();
        _stopping.Dispose();
        _lost.Dispose();
    }

    /// <summary>An actor transaction raised <c>RS001</c>: another instance holds the epoch, so this one must stop.</summary>
    public void ReportFencingLost(Exception? cause = null) => LoseLeadership("the epoch fence refused a transaction", cause);

    /// <summary>
    /// Runs <paramref name="commands"/> as one explicit transaction behind <c>select rs_fence(epoch)</c>:
    /// the batch (Npgsql sends <c>BEGIN</c> with it), then a separate <c>COMMIT</c>, so a fence mismatch aborts
    /// the transaction before anything can commit (R1).
    /// </summary>
    /// <exception cref="FencingLostException">The fence refused the transaction; nothing committed.</exception>
    /// <exception cref="CommitOutcomeUnknownException">The connection broke during COMMIT.</exception>
    public async Task CommitFencedAsync(
        NpgsqlConnection connection,
        IEnumerable<NpgsqlBatchCommand> commands,
        CancellationToken cancellationToken) {
        if (!IsLeader) {
            throw new FencingLostException();
        }
        var epoch = Volatile.Read(ref _epoch);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using (var batch = new NpgsqlBatch(connection, tx)) {
            batch.BatchCommands.Add(FenceCommand(epoch));
            foreach (var command in commands) {
                batch.BatchCommands.Add(command);
            }
            try {
                await batch.ExecuteNonQueryAsync(cancellationToken);
            } catch (PostgresException e) when (IsFencingLost(e)) {
                ReportFencingLost(e);
                throw new FencingLostException(e);
            }
        }
        try {
            await tx.CommitAsync(CancellationToken.None);
        } catch (Exception e) when (e is not PostgresException) {
            // A PostgresException is the server's answer (a deferred constraint, say): it rolled back for certain.
            throw new CommitOutcomeUnknownException(e);
        }
    }

    public static NpgsqlBatchCommand FenceCommand(long epoch) =>
        new("select rs_fence($1)") { Parameters = { new() { Value = epoch } } };

    private async Task<NpgsqlConnection> AcquireLeaseAsync(CancellationToken cancellationToken) {
        var connectionString = new NpgsqlConnectionStringBuilder(db.OwnerConnectionString) {
            Pooling = false,
            KeepAlive = 10,
            ApplicationName = LeaseApplicationName,
        }.ConnectionString;
        var deadline = DateTimeOffset.UtcNow + options.AcquireWindow;
        Exception? lastError = null;
        while (true) {
            var connection = new NpgsqlConnection(connectionString);
            try {
                await connection.OpenAsync(cancellationToken);
                await using var command = new NpgsqlCommand("select pg_try_advisory_lock($1)", connection) {
                    Parameters = { new() { Value = LeaseKey } },
                };
                if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!) {
                    return connection;
                }
                lastError = null;
            } catch (NpgsqlException e) {
                lastError = e;
            }
            await connection.DisposeAsync();
            if (DateTimeOffset.UtcNow + options.RetryInterval > deadline) {
                var reason = lastError is null ? "another instance holds it" : "the database is unreachable";
                throw new LeaderUnavailableException(
                    $"Could not acquire the leader lease within {options.AcquireWindow.TotalSeconds:0} s: {reason}.",
                    lastError);
            }
            LogWaiting(lastError?.Message ?? "held by another instance");
            await Task.Delay(options.RetryInterval, cancellationToken);
        }
    }

    // The exclusive lock on the fence key waits until every fenced transaction of the previous leader has ended.
    private async Task<long> BumpEpochAsync(NpgsqlConnection lease, CancellationToken cancellationToken) {
        await using var tx = await lease.BeginTransactionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(lease, tx) {
            BatchCommands = {
                new("select pg_advisory_xact_lock($1)") { Parameters = { new() { Value = FenceKey } } },
                new("update system_state set leader_epoch = leader_epoch + 1, leader_instance = $1, leader_since = now() returning leader_epoch") {
                    Parameters = { new() { Value = InstanceId } },
                },
            },
        };
        long epoch;
        await using (var reader = await batch.ExecuteReaderAsync(cancellationToken)) {
            await reader.NextResultAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) {
                throw new InvalidOperationException("system_state has no row.");
            }
            epoch = reader.GetInt64(0);
        }
        await tx.CommitAsync(cancellationToken);
        return epoch;
    }

    private async Task WatchLeaseAsync(NpgsqlConnection lease, CancellationToken stopping) {
        try {
            while (true) {
                await Task.Delay(options.WatchdogInterval, stopping);
                await using var probe = new NpgsqlCommand("select 1", lease) { CommandTimeout = 5 };
                await probe.ExecuteScalarAsync(stopping);
            }
        } catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
        } catch (Exception e) {
            LoseLeadership("the lease connection failed", e);
        }
    }

    private void LoseLeadership(string reason, Exception? cause) {
        if (Interlocked.CompareExchange(ref _state, Lost, Leading) != Leading) {
            return;
        }
        LogLost(reason, cause);
        _lost.Cancel();
        lifetime.StopApplication();
    }

    // Closing the lease connection releases the session advisory lock.
    private async Task ReleaseAsync() {
        if (!_stopping.IsCancellationRequested) {
            await _stopping.CancelAsync();
        }
        await _watchdog;
        Interlocked.CompareExchange(ref _state, Follower, Leading);
        var lease = Interlocked.Exchange(ref _lease, null);
        if (lease is not null) {
            await lease.DisposeAsync();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Leader lease busy, retrying: {Reason}")]
    private partial void LogWaiting(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied {Count} migration(s)")]
    private partial void LogMigrated(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Acquired the leader lease at epoch {Epoch} as instance {InstanceId}")]
    private partial void LogAcquired(long epoch, Guid instanceId);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Leadership lost: {Reason}; stopping the host")]
    private partial void LogLost(string reason, Exception? cause);
}
