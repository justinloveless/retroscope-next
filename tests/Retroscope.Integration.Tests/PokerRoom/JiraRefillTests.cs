using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Actors;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Rooms;
using static Retroscope.Integration.Tests.Actors.ActorHarness;

namespace Retroscope.Integration.Tests.PokerRoom;

/// <summary>
/// <see cref="PokerJiraLoop.Track"/> runs after every message a poker actor handles, so a parent refill it posts must
/// not be posted again by the messages that arrive before the actor applies it.
/// </summary>
[Trait("Category", "PokerRoom")]
public sealed class JiraRefillTests {
    private static readonly TeamId Team = new(Id(0x6000));
    private static readonly RoundId R1 = Round(1);
    private static readonly RoundId R2 = Round(2);

    [Fact]
    public async Task A_refill_is_posted_once_per_cached_issue_until_the_mismatch_ends() {
        var (loop, domain, time, state) = await StartAsync();
        loop.Track(state);
        time.Advance(PokerJiraLoop.Coalesce);
        await domain.WaitForAsync(1);

        // The fetch posted its result; this state never applied it, so the next message refills it once.
        for (var i = 0; i < 3; i++) {
            loop.Track(state);
        }
        await domain.SettleAsync();
        Assert.Equal(2, domain.MetaInputs);

        state.Rounds[R1].ParentKey = "RS-100";
        state.Rounds[R2].ParentKey = "RS-100";
        loop.Track(state);
        // A ticket set back to the fetched key clears its parent: that mismatch is new and refilled once.
        state.Rounds[R1].ParentKey = null;
        loop.Track(state);
        loop.Track(state);
        await domain.SettleAsync();
        Assert.Equal(3, domain.MetaInputs);
    }

    [Fact]
    public async Task A_deleted_session_is_not_tracked() {
        var (loop, domain, time, state) = await StartAsync();
        loop.Track(state);
        time.Advance(PokerJiraLoop.Coalesce);
        await domain.WaitForAsync(1);

        state.Deleted = true;
        state.Rounds[R1].TicketKey = "RS-2";
        loop.Track(state);
        time.Advance(PokerJiraLoop.Coalesce);
        await domain.SettleAsync();
        Assert.Equal(["RS-1"], domain.Fetched);
        Assert.Equal(1, domain.MetaInputs);

        state.Deleted = false;
        loop.Track(state);
        time.Advance(PokerJiraLoop.Coalesce);
        await domain.WaitForAsync(3);
        Assert.Equal(["RS-1", "RS-2"], domain.Fetched);
    }

    private static async Task<(PokerJiraLoop Loop, RecordingDomain Domain, FakeTimeProvider Time, PokerState State)> StartAsync() {
        var time = Clock();
        var jira = new FakeJiraPort();
        jira.Issues["RS-1"] = new JiraMeta("RS-1", "Pay with a card", "Story", "To Do", "RS-100", "Payments");
        jira.Issues["RS-2"] = new JiraMeta("RS-2", "Refunds", "Bug", "Done");
        var state = Session();
        var domain = new RecordingDomain(jira);
        var type = RoomType.Of(new PokerBinding(domain), new MemoryStore<PokerState, PokerEvent>(Session(), x => x.Clone(), PokerApply.Apply), new TestAccess());
        var registry = new ActorRegistry(Runtime(time), [type]);
        var gateway = new RoomGateway(registry, [type], [], NullLogger<RoomGateway>.Instance);
        var loop = new PokerJiraLoop(jira, time, () => registry, () => gateway, NullLogger<PokerJiraLoop>.Instance);
        domain.Registry = registry;
        await registry.GetOrStartAsync(Room(state), Token);
        return (loop, domain, time, state);
    }

    private static RoomKey Room(PokerState state) => RoomKey.Poker(state.SessionId);

    // Two rounds on the same key, neither with its parent yet: one refill covers both.
    private static PokerState Session() {
        var state = new PokerState {
            SessionId = new SessionId(Id(0x6100)),
            Code = "JIRA0001",
            TeamId = Team,
            WarmUntil = Start,
            Jira = new TeamJiraSettings(true, "RS", true),
            NextRoundNumber = 3,
        };
        foreach (var (id, number) in new[] { (R1, 1), (R2, 2) }) {
            state.Rounds[id] = new Round { Id = id, Number = number, TicketKey = "RS-1" };
            state.Order.Add(id);
        }
        state.CurrentRoundId = R1;
        return state;
    }

    /// <summary>The real poker domain, counting the metadata inputs it is asked to decide.</summary>
    private sealed class RecordingDomain(FakeJiraPort jira) : IRoomDomain<PokerState, PokerInput, PokerEvent> {
        private readonly PokerDomain _inner = new();
        private int _metaInputs;

        public ActorRegistry Registry { get; set; } = null!;

        public int MetaInputs => Volatile.Read(ref _metaInputs);

        public IReadOnlyList<string> Fetched => [.. jira.MetaCalls.SelectMany(x => x)];

        public Decision<PokerEvent> Decide(PokerState s, PokerInput c, CommandContext ctx) {
            if (c is JiraMetaLoaded) {
                Interlocked.Increment(ref _metaInputs);
            }
            return _inner.Decide(s, c, ctx);
        }

        public void Apply(PokerState s, PokerEvent e) => _inner.Apply(s, e);

        public IReadOnlyList<PokerEvent> OnPresence(PokerState s, PresenceDelta d, PresenceView p, DateTimeOffset now) => _inner.OnPresence(s, d, p, now);

        public IReadOnlyList<TimerRequest<PokerInput>> Timers(PokerState s, PresenceView p, DateTimeOffset now) => _inner.Timers(s, p, now);

        public IReadOnlyList<PokerEvent> OnWarm(PokerState s, PresenceView p, DateTimeOffset now) => _inner.OnWarm(s, p, now);

        public async Task WaitForAsync(int count) {
            var deadline = DateTime.UtcNow + Wait;
            while (MetaInputs < count) {
                Assert.True(DateTime.UtcNow < deadline, $"Only {MetaInputs} metadata inputs arrived.");
                await Task.Delay(10, Token);
            }
        }

        public async Task SettleAsync() {
            await Registry.QueryAsync<PokerState, PokerInput, int>(RoomKey.Poker(new SessionId(Id(0x6100))), _ => ValueTask.FromResult(0), Token);
        }
    }
}
