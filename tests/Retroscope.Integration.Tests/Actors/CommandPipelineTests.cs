using Npgsql;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Server.Data;
using Retroscope.Server.Realtime.Actors;
using static Retroscope.Integration.Tests.Actors.ActorHarness;

namespace Retroscope.Integration.Tests.Actors;

[Trait("Category", "Actors")]
public sealed class CommandPipelineTests {
    private static PokerVoteSet Vote(short points) => new(Round(1), points);

    [Fact]
    public async Task Accepted_command_commits_then_fans_out_the_tx_then_acks() {
        var room = new CounterRoom();
        var (alice, aliceSnap) = await room.JoinAsync(Alice);
        var (bob, _) = await room.JoinAsync(Bob);
        await room.SettleAsync();
        await alice.DrainAsync();

        var actor = await room.ActorAsync();
        actor.Command(alice, Alice, Cmd(1, Vote(5)));

        var tx = await alice.NextAsync<TxFrame>();
        var ack = await alice.NextAsync<AckFrame>();
        Assert.Equal(tx.S, ack.S);
        Assert.Equal(1, ack.Q);
        Assert.False(ack.Dup);
        Assert.Equal("add:5:5:mine", Assert.IsType<PresenceUpdate>(Assert.Single(tx.Ev)).Name);
        var bobTx = await bob.UntilTxAsync("add");
        Assert.Equal(tx.S, bobTx.S);
        Assert.Equal("add:5:5", Assert.IsType<PresenceUpdate>(Assert.Single(bobTx.Ev)).Name);
        Assert.Equal(5, room.Store.Persisted!.Value);
        Assert.Equal(1, room.Store.CursorOf(Alice.Client));
        Assert.True(aliceSnap.S < tx.S);
    }

    [Fact]
    public async Task Internal_queue_is_drained_before_the_next_external_message() {
        var room = new CounterRoom();
        var (alice, _) = await room.JoinAsync(Alice);
        var actor = await room.ActorAsync();
        var release = room.Store.HoldNextCommit();

        actor.Command(alice, Alice, Cmd(1, Vote(1)));
        await room.Store.CommitEntered;
        actor.Command(alice, Alice, Cmd(2, Vote(2)));
        actor.Command(alice, Alice, Cmd(3, Vote(3)));
        var commitsSeenByQuery = actor.QueryAsync(_ => ValueTask.FromResult(room.Store.Commits.Count), Token);
        release.SetResult();

        Assert.Equal(1, await commitsSeenByQuery);
        await alice.UntilAsync<AckFrame>();
        Assert.Equal(2, (await alice.UntilAsync<AckFrame>()).Q);
        Assert.Equal(3, (await alice.UntilAsync<AckFrame>()).Q);
    }

    [Fact]
    public async Task Full_external_queue_nacks_server_busy_at_once_and_keeps_the_entry_retryable() {
        var room = new CounterRoom(options: ActorOptions.Default with { ExternalQueueCapacity = 2 });
        var (alice, _) = await room.JoinAsync(Alice);
        await alice.UntilAsync<TxFrame>();
        var actor = await room.ActorAsync();
        var release = room.Store.HoldNextCommit();
        actor.Command(alice, Alice, Cmd(1, Vote(1)));
        await room.Store.CommitEntered;

        Assert.Equal(PostResult.Accepted, actor.Command(alice, Alice, Cmd(2, Vote(1))));
        Assert.Equal(PostResult.Accepted, actor.Command(alice, Alice, Cmd(3, Vote(1))));
        Assert.Equal(PostResult.Busy, actor.Command(alice, Alice, Cmd(4, Vote(1))));

        var nack = await alice.NextAsync<NackFrame>();
        Assert.Equal((4, NackCode.ServerBusy, true), (nack.Q, nack.Code, nack.Retry));
        release.SetResult();
        Assert.Equal(1, (await alice.UntilAsync<AckFrame>()).Q);
    }

    [Fact]
    public async Task Resent_command_is_acked_dup_and_a_different_payload_under_the_same_q_is_cid_reuse() {
        var room = new CounterRoom();
        var (alice, _) = await room.JoinAsync(Alice);
        var actor = await room.ActorAsync();
        actor.Command(alice, Alice, Cmd(7, Vote(5)));
        var first = await alice.UntilAsync<AckFrame>();

        actor.Command(alice, Alice, Cmd(7, Vote(5)));
        var dup = await alice.NextAsync<AckFrame>();
        actor.Command(alice, Alice, Cmd(7, Vote(8)));
        var reuse = await alice.NextAsync<NackFrame>();
        actor.Command(alice, Alice, Cmd(6, Vote(3)));
        var older = await alice.NextAsync<AckFrame>();

        Assert.Equal((7, first.S, true), (dup.Q, dup.S, dup.Dup));
        Assert.Equal((NackCode.CidReuse, false), (reuse.Code, reuse.Retry));
        Assert.True(older.Dup);
        Assert.Single(room.Store.Commits);
        Assert.Equal(5, room.Store.Persisted!.Value);
    }

    [Fact]
    public async Task Persisted_cursor_dedupes_after_a_restart_and_a_foreign_cid_is_forbidden() {
        var room = new CounterRoom();
        room.Store.SetCursor(Alice.Client, Alice.Who.Id, 40);
        room.Store.SetCursor(Bob.Client, Carol.Who.Id, 3);
        var (alice, snap) = await room.JoinAsync(Alice);
        var (bob, _) = await room.JoinAsync(Bob);
        var actor = await room.ActorAsync();

        actor.Command(alice, Alice, Cmd(40, Vote(5)));
        var dup = await alice.UntilAsync<AckFrame>();
        actor.Command(bob, Bob, Cmd(4, Vote(5)));
        var forbidden = await bob.UntilAsync<NackFrame>();

        Assert.Equal(40, snap.LastQ);
        Assert.True(dup.Dup);
        Assert.Equal(NackCode.Forbidden, forbidden.Code);
        Assert.Empty(room.Store.Commits);
    }

    [Fact]
    public async Task Reject_nacks_with_code_and_data_and_leaves_the_cursor_alone() {
        var room = new CounterRoom();
        var (alice, _) = await room.JoinAsync(Alice);
        var actor = await room.ActorAsync();

        actor.Command(alice, Alice, Cmd(1, new PokerRoundReveal(Round(1))));
        var nack = await alice.UntilAsync<NackFrame>();
        actor.Command(alice, Alice, Cmd(1, Vote(2)));
        var ack = await alice.UntilAsync<AckFrame>();

        Assert.Equal((NackCode.RoundRevealed, "already revealed", false), (nack.Code, nack.Msg, nack.Retry));
        Assert.Equal(0, nack.D!.Value.GetInt64());
        Assert.False(ack.Dup);
        Assert.Equal(2, room.Store.Persisted!.Value);
    }

    [Fact]
    public async Task NoOp_acks_at_the_current_seq_without_a_transaction_or_frame() {
        var room = new CounterRoom();
        var (alice, _) = await room.JoinAsync(Alice);
        var join = await alice.NextAsync<TxFrame>();
        var actor = await room.ActorAsync();

        actor.Command(alice, Alice, Cmd(1, new PokerSpotlightRelease()));
        var ack = await alice.NextAsync<AckFrame>();

        Assert.Equal(join.S, ack.S);
        Assert.Empty(room.Store.Commits);
        Assert.Null(room.Store.CursorOf(Alice.Client));
    }

    [Fact]
    public async Task Command_without_a_subscription_or_for_another_room_kind_is_nacked() {
        var room = new CounterRoom();
        var (alice, _) = await room.JoinAsync(Alice);
        var stranger = new TestPeer(Bob);
        var actor = await room.ActorAsync();

        actor.Command(stranger, Bob, Cmd(1, Vote(1)));
        // The parser refuses a kind of another room (WrongRoom); the actor still guards against one built in memory.
        actor.Command(alice, Alice, new CmdFrame(Room, 1, new RetroVoteSet(new Core.Ids.CardId(Id(9)), true)));

        Assert.Equal(NackCode.NotSubscribed, (await stranger.NextAsync<NackFrame>()).Code);
        Assert.Equal(NackCode.Invalid, (await alice.UntilAsync<NackFrame>()).Code);
    }

    [Fact]
    public async Task Ephemeral_decision_is_sequenced_without_a_transaction_or_cursor_write() {
        var room = new CounterRoom();
        var (alice, _) = await room.JoinAsync(Alice);
        var join = await alice.NextAsync<TxFrame>();
        var actor = await room.ActorAsync();

        actor.Command(alice, Alice, Cmd(1, new PokerLockSet(Round(1), true)));
        var tx = await alice.NextAsync<TxFrame>();
        var ack = await alice.NextAsync<AckFrame>();

        Assert.Equal(join.S + 1, tx.S);
        Assert.Equal(tx.S, ack.S);
        Assert.Empty(room.Store.Commits);
        Assert.Null(room.Store.CursorOf(Alice.Client));
    }

    [Fact]
    public async Task Memory_only_decision_emits_no_tx_and_acks_at_the_current_seq() {
        var room = new CounterRoom();
        var (alice, _) = await room.JoinAsync(Alice);
        var join = await alice.NextAsync<TxFrame>();
        var actor = await room.ActorAsync();

        actor.Command(alice, Alice, Cmd(1, new PokerLockSet(Round(1), false)));
        var ack = await alice.NextAsync<AckFrame>();

        Assert.Equal(join.S, ack.S);
        Assert.Empty(room.Store.Commits);
        Assert.Equal(1, await room.QueryAsync(view => view.State!.Value));
        alice.AssertIdle();
    }

    [Fact]
    public async Task Definite_failure_nacks_internal_and_applies_nothing() {
        var room = new CounterRoom();
        var (alice, _) = await room.JoinAsync(Alice);
        var actor = await room.ActorAsync();
        room.Store.FailNextCommit(new CommitRejectedException("23505", new InvalidOperationException("unique violation")));

        actor.Command(alice, Alice, Cmd(1, Vote(5)));
        var nack = await alice.UntilAsync<NackFrame>();

        Assert.Equal((NackCode.Internal, false), (nack.Code, nack.Retry));
        Assert.Equal(0, await room.QueryAsync(view => view.State!.Value));
        Assert.Equal(0, alice.Restarts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unknown_outcome_poisons_the_actor_sends_bye_and_reloads_with_a_new_epoch(bool landed) {
        var room = new CounterRoom();
        var (alice, snap) = await room.JoinAsync(Alice);
        var (bob, _) = await room.JoinAsync(Bob);
        var actor = await room.ActorAsync();
        room.Store.FailNextCommit(new CommitOutcomeUnknownException(new IOException("socket closed during COMMIT")), landed);

        actor.Command(alice, Alice, Cmd(1, Vote(5)));
        await Task.WhenAll(alice.Restarted, bob.Restarted);

        var again = new TestPeer(Viewer(1, "Alice", connection: 1));
        actor.Subscribe(again, again.Viewer, snap.E, snap.S);
        var resnap = await again.NextAsync<SnapFrame>();
        actor.Command(again, again.Viewer, Cmd(1, Vote(5)));
        var ack = await again.UntilAsync<AckFrame>();

        Assert.NotEqual(snap.E, resnap.E);
        Assert.Equal(2, room.Store.Loads);
        Assert.Equal(landed, ack.Dup);
        Assert.Equal(5, room.Store.Persisted!.Value);
        Assert.Single(room.Store.Commits);
        Assert.DoesNotContain(await alice.DrainAsync(), x => x is AckFrame or NackFrame);
    }

    [Fact]
    public async Task Fencing_loss_and_infrastructure_failures_close_with_bye_and_never_nack() {
        var room = new CounterRoom();
        var (alice, _) = await room.JoinAsync(Alice);
        await alice.UntilAsync<TxFrame>();
        var actor = await room.ActorAsync();

        room.Store.FailNextCommit(new FencingLostException());
        actor.Command(alice, Alice, Cmd(1, Vote(5)));
        await alice.Restarted;
        var (bob, _) = await room.JoinAsync(Bob);
        await bob.UntilAsync<TxFrame>();
        room.Store.FailNextCommit(new NpgsqlException("connection refused"));
        actor.Command(bob, Bob, Cmd(1, Vote(5)));
        await bob.Restarted;

        alice.AssertIdle();
        bob.AssertIdle();
        Assert.Equal(0, room.Store.Persisted!.Value);
    }

    [Fact]
    public async Task Load_failure_restarts_the_subscriber_and_the_next_message_retries_the_load() {
        var room = new CounterRoom();
        room.Store.FailLoads(new NpgsqlException("the database is starting up"));
        var alice = new TestPeer(Alice);
        var actor = await room.ActorAsync();

        actor.Subscribe(alice, Alice);
        await alice.Restarted;
        room.Store.FailLoads(null);
        var (bob, _) = await room.JoinAsync(Bob);

        Assert.Equal(2, room.Store.Loads);
        Assert.NotNull(bob);
    }

    [Fact]
    public async Task A_throwing_decider_poisons_the_room_instead_of_killing_the_loop() {
        var room = new CounterRoom();
        var (alice, _) = await room.JoinAsync(Alice);
        var actor = await room.ActorAsync();

        var result = await actor.AskAsync(new CounterInput.Explode(), null, Token);
        await alice.Restarted;
        var (bob, snap) = await room.JoinAsync(Bob);

        Assert.Equal(AskStatus.Unavailable, result.Status);
        Assert.Equal("v0", Assert.IsType<PokerSnapshot>(snap.D).Code);
    }
}
