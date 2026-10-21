using Npgsql;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data;
using static Retroscope.Integration.Tests.Leadership.LeaderHarness;

namespace Retroscope.Integration.Tests.Leadership;

[Trait("Category", "Leader")]
[Collection(SharedPostgres.Name)]
public sealed class LeaseTests(PostgresFixture postgres) {
    [Fact]
    public async Task First_instance_on_an_empty_database_migrates_and_takes_epoch_one() {
        var database = await postgres.CreateEmptyDatabaseAsync();
        await using var leader = Create(database);

        await leader.StartAsync(Token);

        Assert.True(leader.IsLeader);
        Assert.Equal(1, leader.Current);
        Assert.Equal(1, await EpochAsync(database));
        Assert.Equal(leader.InstanceId, await ScalarAsync<Guid>(database.OwnerConnectionString, "select leader_instance from system_state"));
        Assert.True(await ScalarAsync<long>(database.OwnerConnectionString, "select count(*) from schema_migrations") > 0);
    }

    [Fact]
    public async Task Leader_epoch_increases_by_one_with_every_new_leader() {
        var database = await postgres.CreateDatabaseAsync();
        await SetEpochAsync(database, 41);

        var epochs = new List<long>();
        for (var i = 0; i < 3; i++) {
            await using var leader = Create(database);
            await leader.StartAsync(Token);
            epochs.Add(leader.Current);
            await leader.StopAsync(Token);
        }

        Assert.Equal([42, 43, 44], epochs);
        Assert.Equal(44, await EpochAsync(database));
    }

    [Fact]
    public async Task Second_instance_cannot_acquire_while_the_lease_is_held() {
        var database = await postgres.CreateDatabaseAsync();
        await using var first = Create(database);
        await first.StartAsync(Token);
        await using var second = Create(database);

        var error = await Assert.ThrowsAsync<LeaderUnavailableException>(() => second.StartAsync(Token));

        Assert.Contains("another instance holds it", error.Message, StringComparison.Ordinal);
        Assert.False(second.IsLeader);
        Assert.True(first.IsLeader);
        Assert.Equal(first.Current, await EpochAsync(database));
    }

    [Fact]
    public async Task Graceful_stop_releases_the_lease_without_failing() {
        var database = await postgres.CreateDatabaseAsync();
        var lifetime = new FakeLifetime();
        await using var leader = Create(database, lifetime);
        await leader.StartAsync(Token);

        await leader.StopAsync(Token);

        Assert.False(leader.IsLeader);
        Assert.False(lifetime.StopRequested);
        Assert.Null(await LeaseHolderPidAsync(database));
    }

    [Fact]
    public async Task Killing_the_lease_connection_hands_over_and_stops_the_old_leader() {
        var database = await postgres.CreateDatabaseAsync();
        var oldLifetime = new FakeLifetime();
        await using var old = Create(database, oldLifetime);
        await old.StartAsync(Token);
        var oldEpoch = old.Current;
        await using var successor = Create(database, options: Fast with { AcquireWindow = TimeSpan.FromSeconds(30) });
        var acquiring = successor.StartAsync(Token);

        await TerminateLeaseHolderAsync(database);
        await acquiring.WaitAsync(TimeSpan.FromSeconds(20), Token);
        await WaitUntilAsync(() => Task.FromResult(old.LeadershipLost.IsCancellationRequested), TimeSpan.FromSeconds(10));

        Assert.Equal(oldEpoch + 1, successor.Current);
        Assert.False(old.IsLeader);
        Assert.True(oldLifetime.StopRequested);
        await Assert.ThrowsAsync<LeadershipLostException>(() => old.StopAsync(Token));
    }

    [Fact]
    public async Task New_leaders_epoch_bump_waits_for_an_in_flight_fenced_transaction() {
        var database = await postgres.CreateDatabaseAsync();
        var old = Create(database);
        await old.StartAsync(Token);
        var oldEpoch = old.Current;
        await using var app = await OpenAsync(database.AppConnectionString);
        await using var inFlight = await app.BeginTransactionAsync(Token);
        await using (var batch = new NpgsqlBatch(app, inFlight) {
            BatchCommands = { Leader.FenceCommand(oldEpoch), InsertGuest(Guid.NewGuid()) },
        }) {
            await batch.ExecuteNonQueryAsync(Token);
        }
        await old.DisposeAsync();
        await using var successor = Create(database, options: Fast with { AcquireWindow = TimeSpan.FromSeconds(30) });

        var acquiring = successor.StartAsync(Token);
        await WaitUntilAsync(async () => await FenceWaitersAsync(database) == 1, TimeSpan.FromSeconds(10));
        Assert.False(acquiring.IsCompleted);
        Assert.Equal(oldEpoch, await EpochAsync(database));

        await inFlight.CommitAsync(Token);
        await acquiring.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Equal(oldEpoch + 1, successor.Current);
        Assert.Equal(1, await PrincipalCountAsync(database));
    }
}
