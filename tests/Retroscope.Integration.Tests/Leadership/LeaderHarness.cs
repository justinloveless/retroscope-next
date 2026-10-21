using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data;

namespace Retroscope.Integration.Tests.Leadership;

internal sealed class FakeLifetime : IHostApplicationLifetime {
    private volatile bool _stopRequested;

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => CancellationToken.None;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public bool StopRequested => _stopRequested;

    public void StopApplication() => _stopRequested = true;
}

internal static class LeaderHarness {
    public static readonly LeaderOptions Fast = new(
        AcquireWindow: TimeSpan.FromSeconds(2),
        RetryInterval: TimeSpan.FromMilliseconds(100),
        WatchdogInterval: TimeSpan.FromMilliseconds(100));

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public static Leader Create(TestDatabase database, FakeLifetime? lifetime = null, LeaderOptions? options = null) =>
        new(DbFor(database), options ?? Fast, lifetime ?? new FakeLifetime(), NullLogger<Leader>.Instance);

    public static Db DbFor(TestDatabase database) {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["DATABASE_URL"] = database.AppConnectionString,
                ["DATABASE_OWNER_URL"] = database.OwnerConnectionString,
            })
            .Build();
        return Db.FromConfiguration(configuration);
    }

    public static async Task<NpgsqlConnection> OpenAsync(string connectionString) {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        return connection;
    }

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, params object[] parameters) {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new() { Value = parameter });
        }
        return (T)(await command.ExecuteScalarAsync(Token))!;
    }

    public static Task<long> EpochAsync(TestDatabase database) =>
        ScalarAsync<long>(database.OwnerConnectionString, "select leader_epoch from system_state");

    public static Task<long> SetEpochAsync(TestDatabase database, long epoch) =>
        ScalarAsync<long>(database.OwnerConnectionString, "update system_state set leader_epoch = $1 returning leader_epoch", epoch);

    /// <summary>The backend that holds the lease lock in this database, or null when nobody holds it.</summary>
    public static async Task<int?> LeaseHolderPidAsync(TestDatabase database) {
        await using var connection = await OpenAsync(database.OwnerConnectionString);
        await using var command = new NpgsqlCommand("""
            select pid from pg_locks
            where locktype = 'advisory' and granted and database = (select oid from pg_database where datname = current_database())
              and objsubid = 1 and ((classid::bigint << 32) | objid::bigint) = $1
            """, connection) {
            Parameters = { new() { Value = Leader.LeaseKey } },
        };
        return (int?)await command.ExecuteScalarAsync(Token);
    }

    /// <summary>Backends queued for the exclusive fence lock, i.e. a new leader waiting to bump the epoch.</summary>
    public static Task<long> FenceWaitersAsync(TestDatabase database) =>
        ScalarAsync<long>(database.OwnerConnectionString, """
            select count(*) from pg_locks
            where locktype = 'advisory' and not granted and mode = 'ExclusiveLock' and objsubid = 1
              and ((classid::bigint << 32) | objid::bigint) = $1
            """, Leader.FenceKey);

    public static async Task TerminateLeaseHolderAsync(TestDatabase database) {
        var pid = await LeaseHolderPidAsync(database) ?? throw new InvalidOperationException("Nobody holds the lease.");
        Assert.True(await ScalarAsync<bool>(database.OwnerConnectionString, "select pg_terminate_backend($1)", pid));
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout) {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition()) {
            if (DateTime.UtcNow > deadline) {
                throw new TimeoutException("The condition did not hold in time.");
            }
            await Task.Delay(50, Token);
        }
    }

    public static async Task<long> PrincipalCountAsync(TestDatabase database) =>
        await ScalarAsync<long>(database.OwnerConnectionString, "select count(*) from principals");

    public static NpgsqlBatchCommand InsertGuest(Guid id) =>
        new("insert into principals (id, kind, display_name) values ($1, 'guest', 'Ada')") {
            Parameters = { new() { Value = id } },
        };
}
