using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Realtime.Rooms;

namespace Retroscope.Integration.Tests.PokerRoom;

/// <summary>The <c>JiraMetaLoop</c> and Jira submits, driven through a fake <see cref="IPokerJiraPort"/>.</summary>
[Trait("Category", "PokerRoom")]
[Collection(SharedPostgres.Name)]
public sealed class JiraTests(PostgresFixture postgres) {
    [Fact]
    public async Task Meta_loop_fetches_each_new_key_once_per_room_and_persists_only_parent_keys() {
        var jira = new FakeJiraPort();
        jira.Issues["RS-1"] = new JiraMeta("RS-1", "Pay with a card", "Story", "To Do", "RS-100", "Payments");
        jira.Issues["RS-2"] = new JiraMeta("RS-2", "Refunds", "Bug", "Done");
        jira.Issues["RS-3"] = new JiraMeta("RS-3", "Receipts", "Story", "To Do", "RS-100", "Payments");
        await using var scope = await PokerScope.CreateAsync(postgres, jira);
        var members = new List<Principal>();
        for (var i = 0; i < 5; i++) {
            members.Add(await scope.Seed.UserAsync($"Member {i}"));
        }
        var team = await scope.Seed.TeamAsync([.. members.Select(x => (x, TeamRole.Member))]);
        await scope.Seed.JiraAsync(team);
        var room = await scope.Seed.SessionAsync(members[0], team, "RS-1", "RS-2");
        var r1 = (await scope.Seed.RoundsAsync(room))[0];
        var host = await scope.StartAsync();
        var tabs = new List<PokerSocket>();
        try {
            foreach (var member in members.Take(4)) {
                tabs.Add(await host.JoinAsync(member, room));
            }

            await host.AdvanceAsync(room, PokerJiraLoop.Coalesce);
            foreach (var tab in tabs) {
                await tab.WaitForAsync<XFrame>(x => x.D is JiraMeta { Key: "RS-2" });
            }
            var updated = (PokerRoundUpdated)await tabs[0].WaitForEventAsync<PokerRoundUpdated>();

            Assert.Equal(["RS-1", "RS-2"], Assert.Single(jira.MetaCalls));
            Assert.Equal((r1, "RS-100"), (updated.Round.Id, updated.Round.ParentKey));
            Assert.Equal("RS-100", await scope.Seed.ScalarAsync<string>("select parent_key from poker_rounds where id = $1", r1.Value));
            Assert.Equal(0L, await scope.Seed.ScalarAsync<long>(
                "select count(*) from poker_rounds where session_id = $1 and parent_key is not null and id <> $2", room.Id, r1.Value));

            var late = await host.JoinAsync(members[4], room);
            tabs.Add(late);
            var snapAt = late.Frames().ToList().FindIndex(x => x is SnapFrame);
            await late.WaitForAsync<XFrame>(x => x.D is JiraMeta { Key: "RS-2" }, snapAt);
            var fills = late.Frames().Skip(snapAt + 1).OfType<XFrame>().Select(x => ((JiraMeta)x.D).Summary);
            Assert.Equal(["Pay with a card", "Refunds"], fills);

            var from = tabs[1].Count;
            await tabs[1].AckedAsync(room, new PokerRoundCreate(new RoundId(Guid.CreateVersion7()), "RS-3", ForceNew: true));
            await host.AdvanceAsync(room, PokerJiraLoop.Coalesce);
            await tabs[1].WaitForAsync<XFrame>(x => x.D is JiraMeta { Key: "RS-3" }, from);
            Assert.Equal(["RS-3"], jira.MetaCalls[1]);

            jira.Issues["RS-1"] = jira.Issues["RS-1"] with { ParentKey = "RS-200", ParentSummary = "Billing" };
            from = tabs[0].Count;
            host.Jira.Invalidate(team, "RS-1");
            await host.AdvanceAsync(room, PokerJiraLoop.Coalesce);
            await tabs[0].WaitForAsync<TxFrame>(x => x.Ev.Any(e => e is PokerRoundUpdated { Round.ParentKey: "RS-200" }), from);
            Assert.Equal(["RS-1"], jira.MetaCalls[2]);
            Assert.Equal(3, jira.MetaCalls.Count);
            await host.AssertPersistedAsync(room);
        } finally {
            foreach (var tab in tabs) {
                await tab.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Rounds_set_to_an_already_fetched_key_get_its_parent_back_without_a_new_fetch() {
        var jira = new FakeJiraPort();
        jira.Issues["RS-1"] = new JiraMeta("RS-1", "Pay with a card", "Story", "To Do", "RS-100", "Payments");
        jira.Issues["RS-2"] = new JiraMeta("RS-2", "Refunds", "Bug", "Done");
        await using var scope = await PokerScope.CreateAsync(postgres, jira);
        var alice = await scope.Seed.UserAsync("Alice");
        var team = await scope.Seed.TeamAsync((alice, TeamRole.Member));
        await scope.Seed.JiraAsync(team);
        var room = await scope.Seed.SessionAsync(alice, team, "RS-1");
        var r1 = (await scope.Seed.RoundsAsync(room))[0];
        var host = await scope.StartAsync();
        await using var a = await host.JoinAsync(alice, room);
        // The snap is sent before the actor tracks the loaded keys; a query drains the join first.
        await host.QueryAsync(room, _ => 0);
        await host.AdvanceAsync(room, PokerJiraLoop.Coalesce);
        var round = await ParentFilledAsync(a, r1, 0);

        round = Updated(await a.AckedAsync(room, new PokerRoundSetTicket(r1, "RS-2", round.Version)));
        Assert.Null(round.ParentKey);
        await host.QueryAsync(room, _ => 0);
        await host.AdvanceAsync(room, PokerJiraLoop.Coalesce);
        await a.WaitForAsync<XFrame>(x => x.D is JiraMeta { Key: "RS-2" });

        var from = a.Count;
        round = Updated(await a.AckedAsync(room, new PokerRoundSetTicket(r1, "RS-1", round.Version)));
        Assert.Null(round.ParentKey);
        await ParentFilledAsync(a, r1, from);

        var r2 = new RoundId(Guid.CreateVersion7());
        from = a.Count;
        await a.AckedAsync(room, new PokerRoundCreate(r2, "RS-1", ForceNew: true));
        await ParentFilledAsync(a, r2, from);

        Assert.Equal([["RS-1"], ["RS-2"]], jira.MetaCalls);
        Assert.Equal(2L, await scope.Seed.ScalarAsync<long>(
            "select count(*) from poker_rounds where session_id = $1 and parent_key = 'RS-100'", room.Id));
        await host.AssertPersistedAsync(room);
    }

    [Fact]
    public async Task Quick_sessions_and_teams_without_jira_never_call_the_port() {
        var jira = new FakeJiraPort();
        await using var scope = await PokerScope.CreateAsync(postgres, jira);
        var alice = await scope.Seed.UserAsync("Alice");
        var team = await scope.Seed.TeamAsync((alice, TeamRole.Owner));
        var quick = await scope.Seed.SessionAsync(alice, null, "RS-1");
        var unconfigured = await scope.Seed.SessionAsync(alice, team, "RS-2");
        var host = await scope.StartAsync();
        await using var a = await host.JoinAsync(alice, quick);
        await using var b = await host.JoinAsync(alice, unconfigured);

        await host.AdvanceAsync(quick, TimeSpan.FromSeconds(1));
        await host.AdvanceAsync(unconfigured, TimeSpan.FromSeconds(1));

        Assert.Empty(jira.MetaCalls);
        Assert.DoesNotContain(a.Frames(), x => x is XFrame);
    }

    [Fact]
    public async Task Submitted_points_are_pending_for_everyone_then_recorded_or_failed() {
        var jira = new FakeJiraPort { SubmitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var scope = await PokerScope.CreateAsync(postgres, jira);
        var alice = await scope.Seed.UserAsync("Alice");
        var bob = await scope.Seed.UserAsync("Bob");
        var team = await scope.Seed.TeamAsync((alice, TeamRole.Member), (bob, TeamRole.Member));
        await scope.Seed.JiraAsync(team);
        var room = await scope.Seed.SessionAsync(alice, team, "RS-1");
        var r1 = (await scope.Seed.RoundsAsync(room))[0];
        var host = await scope.StartAsync();
        await using var a = await host.JoinAsync(alice, room);
        await using var b = await host.JoinAsync(bob, room);
        await a.AckedAsync(room, new PokerVoteSet(r1, 3, Lock: true));
        await a.AckedAsync(room, new PokerRoundReveal(r1));

        var from = b.Count;
        var pending = await a.AckedAsync(room, new PokerJiraSubmitPoints(r1, 3.5m));
        Assert.Contains(pending, x => x is PokerJiraPoints { Status: JiraSubmitStatus.Pending, Points: 3.5m });
        Assert.Equal(NackCode.JiraPending, (await b.NackedAsync(room, new PokerJiraSubmitPoints(r1, 5))).Code);
        jira.SubmitGate.SetResult();

        await b.WaitForAsync<TxFrame>(x => x.Ev.Any(e => e is PokerJiraPoints { Status: JiraSubmitStatus.Ok }), from);
        Assert.Equal([("RS-1", 3.5m)], jira.Submits);
        Assert.Equal(3.5m, await scope.Seed.ScalarAsync<decimal>("select jira_points from poker_rounds where id = $1", r1.Value));
        Assert.Equal(alice.Id.Value, await scope.Seed.ScalarAsync<Guid>("select jira_points_by from poker_rounds where id = $1", r1.Value));
        await host.AssertPersistedAsync(room);

        jira.SubmitGate = null;
        jira.SubmitResult = new JiraSubmitResult(false, "jira_forbidden");
        from = a.Count;
        await b.AckedAsync(room, new PokerJiraSubmitPoints(r1, 8));
        var failed = await a.WaitForAsync<TxFrame>(x => x.Ev.Any(e => e is PokerJiraPoints { Status: JiraSubmitStatus.Failed }), from);
        Assert.Equal("jira_forbidden", failed.Ev.OfType<PokerJiraPoints>().Single().Code);
        Assert.Equal(3.5m, await scope.Seed.ScalarAsync<decimal>("select jira_points from poker_rounds where id = $1", r1.Value));
    }

    private static RoundInfo Updated(IReadOnlyList<WireEvent> events) {
        return events.OfType<PokerRoundUpdated>().First().Round;
    }

    private static async Task<RoundInfo> ParentFilledAsync(PokerSocket socket, RoundId round, int from) {
        var tx = await socket.WaitForAsync<TxFrame>(
            x => x.Ev.Any(e => e is PokerRoundUpdated { Round.ParentKey: "RS-100" } u && u.Round.Id == round),
            from);
        return tx.Ev.OfType<PokerRoundUpdated>().Single(x => x.Round.Id == round).Round;
    }
}
