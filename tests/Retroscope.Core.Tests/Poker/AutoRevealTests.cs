using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using static Retroscope.Core.Tests.Poker.PokerAssert;
using static Retroscope.Core.Tests.Poker.PokerHarness;

namespace Retroscope.Core.Tests.Poker;

[Trait("Category", "Poker")]
public sealed class AutoRevealTests {
    [Fact]
    public void Fires_600_ms_after_every_present_voter_locked() {
        var room = Team();
        room.Join(Bob);
        room.Join(Carol);
        room.Accepted(Bob, new PokerVoteSet(R(1), 5, Lock: true));
        Assert.Empty(room.Timers());

        room.Accepted(Carol, new PokerVoteSet(R(1), 8, Lock: true));
        var timer = Assert.Single(room.Timers());
        Assert.Equal(PokerReactions.AutoRevealTimer(R(1)), timer.Key);
        Assert.Equal(room.Now + PokerTiming.AutoRevealDelay, timer.DueAt);

        room.Advance(TimeSpan.FromMilliseconds(599));
        Assert.True(room.Round(1).IsVoting);
        room.Advance(TimeSpan.FromMilliseconds(1));
        var revealed = Assert.IsType<RoundRevealed>(room.Log[^1].Single());
        Assert.Null(revealed.By);
        Assert.Equal(8, revealed.Winner);
        Assert.Empty(room.Timers());
    }

    [Fact]
    public void A_vote_change_bumps_the_generation_so_an_older_timer_is_ignored() {
        var room = Team();
        room.Join(Bob);
        room.Accepted(Bob, new PokerVoteSet(R(1), 5, Lock: true));
        var stale = Assert.Single(room.Timers());

        room.Now += TimeSpan.FromMilliseconds(400);
        room.Accepted(Bob, new PokerLockSet(R(1), false));
        room.Accepted(Bob, new PokerLockSet(R(1), true));
        room.Now += TimeSpan.FromMilliseconds(300);

        NoOp(room.Send(null, stale.Command));
        var current = Assert.Single(room.Timers());
        Assert.Equal(room.Now - TimeSpan.FromMilliseconds(300) + PokerTiming.AutoRevealDelay, current.DueAt);
        NoOp(room.Send(null, current.Command));
        room.Now = current.DueAt;
        Assert.IsType<RoundRevealed>(Assert.Single(Accepted(room.Send(null, current.Command))));
    }

    [Fact]
    public void Absent_voters_with_unlocked_votes_do_not_block_but_present_ones_do() {
        var room = Team();
        room.Join(Bob);
        room.Join(Carol);
        room.Accepted(Bob, new PokerVoteSet(R(1), 5, Lock: true));
        room.Accepted(Carol, new PokerVoteSet(R(1), 3));
        Assert.False(AutoReveal.IsReady(room.State, room.Round(1), room.Presence));

        room.Disconnect(Carol);

        Assert.True(AutoReveal.IsReady(room.State, room.Round(1), room.Presence));
        Assert.Equal([Bob.Who.Id], AutoReveal.Eligible(room.State, room.Round(1), room.Presence));
    }

    [Fact]
    public void Absent_locked_voters_stay_eligible_and_observers_never_are() {
        var room = Team();
        room.Join(Bob);
        room.Join(Carol);
        room.Join(Dave);
        room.Accepted(Bob, new PokerVoteSet(R(1), 5, Lock: true));
        room.Disconnect(Bob);
        room.Accepted(Carol, new PokerObserverSet(Dave.Who.Id, true));

        Assert.Equal(
            new HashSet<Ids.PrincipalId> { Bob.Who.Id, Carol.Who.Id },
            AutoReveal.Eligible(room.State, room.Round(1), room.Presence));

        room.Accepted(Carol, new PokerAbstainSet(R(1), true));
        Assert.True(AutoReveal.IsReady(room.State, room.Round(1), room.Presence));
    }

    [Fact]
    public void With_presence_disabled_the_whole_roster_must_lock() {
        var room = Team();
        room.Join(Bob);
        room.Accepted(Bob, new PokerSettingsUpdate(new PokerSettingsValues(PresenceEnabled: true), PresenceEnabled: false));
        room.Accepted(Bob, new PokerVoteSet(R(1), 5, Lock: true));
        Assert.False(AutoReveal.IsReady(room.State, room.Round(1), room.Presence));

        room.Accepted(Alice, new PokerAbstainSet(R(1), true));
        room.Accepted(Carol, new PokerAbstainSet(R(1), true));
        room.Accepted(Dave, new PokerVoteSet(R(1), 5, Lock: true));

        Assert.True(AutoReveal.IsReady(room.State, room.Round(1), room.Presence));
    }

    [Fact]
    public void Nobody_eligible_means_no_reveal() {
        var room = Team();

        Assert.False(AutoReveal.IsReady(room.State, room.Round(1), room.Presence));
        Assert.Empty(room.Timers());
    }

    [Fact]
    public void Pending_ended_replayed_and_revealed_rounds_are_not_watched() {
        var room = Team();
        room.Join(Bob);
        room.Accepted(Bob, new PokerVoteSet(R(1), 5, Lock: true));
        room.Accepted(Bob, new PokerRoundReveal(R(1)));
        Assert.False(AutoReveal.IsReady(room.State, room.Round(1), room.Presence));

        room.Accepted(Bob, new PokerRoundReplay(R(1)));
        Assert.False(AutoReveal.IsReady(room.State, room.Round(1), room.Presence));

        room.Accepted(Bob, new PokerRoundCreate(R(2), ForceNew: true));
        room.Accepted(Bob, new PokerVoteSet(R(2), 5, Lock: true));
        room.Accepted(Bob, new PokerRoundEnd(R(2)));
        Assert.False(AutoReveal.IsReady(room.State, room.Round(2), room.Presence));
        Assert.Empty(room.Timers());
    }

    [Fact]
    public void Every_active_round_is_watched_not_just_the_viewed_one() {
        var room = Team();
        room.Join(Bob);
        room.Accepted(Bob, new PokerRoundCreate(R(2), ForceNew: true));
        room.Accepted(Bob, new PokerVoteSet(R(1), 5, Lock: true));
        room.Accepted(Bob, new PokerVoteSet(R(2), 8, Lock: true));

        room.Advance(TimeSpan.FromSeconds(1));

        Assert.False(room.Round(1).IsVoting);
        Assert.False(room.Round(2).IsVoting);
    }

    [Fact]
    public void A_presence_change_re_arms_600_ms_later() {
        var room = Team();
        room.Join(Bob);
        room.Join(Carol);
        room.Accepted(Bob, new PokerVoteSet(R(1), 5, Lock: true));
        room.Accepted(Carol, new PokerVoteSet(R(1), 3));
        room.Now += TimeSpan.FromSeconds(5);

        room.Disconnect(Carol);

        Assert.Equal(room.Now + PokerTiming.AutoRevealDelay, Assert.Single(room.Timers()).DueAt);
    }

    [Fact]
    public void Nothing_fires_before_warm_up_and_on_warm_every_watched_round_is_armed() {
        var room = Team();
        room.State.WarmUntil = room.Now + PokerTiming.WarmUp;
        room.Join(Bob);
        room.Accepted(Bob, new PokerVoteSet(R(1), 5, Lock: true));
        Assert.Empty(room.Timers());
        var early = new AutoRevealDue(R(1), room.Round(1).AutoRevealGen);
        room.Now += TimeSpan.FromSeconds(1);
        NoOp(room.Send(null, early));

        room.Now = room.State.WarmUntil;
        room.Warm();

        var timer = Assert.Single(room.Timers());
        Assert.Equal(room.Now + PokerTiming.AutoRevealDelay, timer.DueAt);
        room.Advance(TimeSpan.FromSeconds(1));
        Assert.False(room.Round(1).IsVoting);
    }

    [Fact]
    public void An_auto_reveal_due_for_a_vanished_round_or_unready_round_is_a_no_op() {
        var room = Team();
        room.Join(Bob);

        NoOp(room.Send(null, new AutoRevealDue(R(9), 0)));
        NoOp(room.Send(null, new AutoRevealDue(R(1), room.Round(1).AutoRevealGen)));
    }
}
