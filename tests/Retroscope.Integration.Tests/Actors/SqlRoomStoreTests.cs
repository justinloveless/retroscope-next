using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Integration.Tests.Leadership;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Realtime.Actors;
using static Retroscope.Integration.Tests.Actors.ActorHarness;

namespace Retroscope.Integration.Tests.Actors;

[Trait("Category", "Actors")]
[Collection(SharedPostgres.Name)]
public sealed class SqlRoomStoreTests(PostgresFixture postgres) {
    [Fact]
    public async Task Commit_writes_the_planned_statements_and_the_cursor_in_one_fenced_transaction() {
        await using var scope = await StoreScope.StartAsync(postgres);

        await scope.Store.CommitAsync(Room, new Counter(), [Added(1), Added(2)], new CursorAdvance(Alice.Client, Alice.Who.Id, 7), Token);

        Assert.Equal(2, await scope.PrincipalsAsync());
        Assert.Equal(new ClientCursor(CursorOwner.Caller, 7), await scope.Store.LoadCursorAsync(Room, Alice.Client, Alice.Who.Id, Token));
    }

    [Fact]
    public async Task Statement_error_is_a_rejection_that_commits_nothing() {
        await using var scope = await StoreScope.StartAsync(postgres);

        var error = await Assert.ThrowsAsync<CommitRejectedException>(() => scope.Store.CommitAsync(
            Room, new Counter(), [Added(1), new CounterEvent("fail", 0, EventDurability.Committed)], new CursorAdvance(Alice.Client, Alice.Who.Id, 7), Token));

        Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
        Assert.Equal(0, await scope.PrincipalsAsync());
        Assert.Equal(new ClientCursor(CursorOwner.None, 0), await scope.Store.LoadCursorAsync(Room, Alice.Client, Alice.Who.Id, Token));
    }

    [Fact]
    public async Task Stale_epoch_is_fencing_loss_not_a_rejection() {
        await using var scope = await StoreScope.StartAsync(postgres);
        await LeaderHarness.SetEpochAsync(scope.Database, scope.Leader.Current + 1);

        await Assert.ThrowsAsync<FencingLostException>(() => scope.Store.CommitAsync(Room, new Counter(), [Added(1)], null, Token));

        Assert.Equal(0, await scope.PrincipalsAsync());
    }

    [Fact]
    public async Task Command_committed_before_a_restart_is_acked_dup_by_the_next_process() {
        await using var scope = await StoreScope.StartAsync(postgres);
        var first = scope.NewRegistry();
        var alice = new TestPeer(Alice);
        await first.SubscribeAsync(Room, alice, Alice, null, null, Token);
        await alice.NextAsync<SnapFrame>();
        await first.CommandAsync(Room, alice, Alice, Cmd(3, new PokerVoteSet(Round(1), 1)), Token);
        Assert.False((await alice.UntilAsync<AckFrame>()).Dup);

        var second = scope.NewRegistry();
        var again = new TestPeer(Viewer(1, "Alice", connection: 1));
        await second.SubscribeAsync(Room, again, again.Viewer, null, null, Token);
        var snap = await again.NextAsync<SnapFrame>();
        await second.CommandAsync(Room, again, again.Viewer, Cmd(3, new PokerVoteSet(Round(1), 1)), Token);

        Assert.Equal(3, snap.LastQ);
        Assert.True((await again.UntilAsync<AckFrame>()).Dup);
        Assert.Equal(1, await scope.PrincipalsAsync());
    }

    private static CounterEvent Added(long amount) => new("add", amount, EventDurability.Committed);

    private sealed class StoreScope : IAsyncDisposable {
        private StoreScope(TestDatabase database, Leader leader, NpgsqlDataSource dataSource) {
            Database = database;
            Leader = leader;
            DataSource = dataSource;
            Store = new SqlCounterStore(dataSource, leader, new CursorRepo(dataSource));
        }

        public TestDatabase Database { get; }

        public Leader Leader { get; }

        public NpgsqlDataSource DataSource { get; }

        public SqlCounterStore Store { get; }

        public static async Task<StoreScope> StartAsync(PostgresFixture postgres) {
            var database = await postgres.CreateDatabaseAsync();
            var leader = LeaderHarness.Create(database);
            await leader.StartAsync(Token);
            return new StoreScope(database, leader, Db.CreateDataSource(database.AppConnectionString));
        }

        public ActorRegistry NewRegistry() {
            var domain = new CounterDomain();
            return new ActorRegistry(Runtime(Clock()), [RoomType.Of(new CounterBinding(domain), Store, new TestAccess())]);
        }

        public Task<long> PrincipalsAsync() {
            return LeaderHarness.ScalarAsync<long>(Database.OwnerConnectionString, "select count(*) from principals");
        }

        public async ValueTask DisposeAsync() {
            await DataSource.DisposeAsync();
            await Leader.DisposeAsync();
        }
    }

    /// <summary>Plans one guest principal per counted event, and a failing statement for a "fail" event.</summary>
    private sealed class SqlCounterStore(NpgsqlDataSource dataSource, Leader leader, CursorRepo cursors)
        : SqlRoomStore<Counter, CounterEvent>(dataSource, leader, cursors) {
        public override Task<Counter?> LoadAsync(RoomKey room, DateTimeOffset now, CancellationToken cancellationToken) {
            return Task.FromResult<Counter?>(new Counter());
        }

        protected override IEnumerable<NpgsqlBatchCommand> Plan(RoomKey room, Counter state, IReadOnlyList<CounterEvent> events) {
            foreach (var evt in events) {
                yield return evt.Name == "fail"
                    ? new NpgsqlBatchCommand("select 1 / 0")
                    : new NpgsqlBatchCommand("insert into principals (id, kind, display_name) values ($1, 'guest', 'counted')") {
                        Parameters = { new() { Value = Guid.CreateVersion7() } },
                    };
            }
        }
    }
}
