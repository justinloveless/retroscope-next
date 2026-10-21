using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using static Retroscope.Core.Tests.Poker.PokerAssert;
using static Retroscope.Core.Tests.Poker.PokerHarness;

namespace Retroscope.Core.Tests.Poker;

[Trait("Category", "Poker")]
public sealed class VoteCommandTests {
    [Fact]
    public void Vote_records_the_selection_and_arms_auto_reveal() {
        var room = PokerHarness.Team();

        var events = room.Accepted(Bob, new PokerVoteSet(R(1), 5));

        Assert.Equal(new Vote(5, null, false, false), room.Round(1).Votes[Bob.Who.Id]);
        Assert.Equal(["VoteChanged"], Kinds(events));
        Assert.Contains(events, x => x is AutoRevealArmed { RoundId: var r } && r == R(1));
        Assert.Equal(1, room.Round(1).AutoRevealGen);
    }

    [Fact]
    public void Drag_to_play_selects_and_locks_at_once_and_between_takes_the_next_deck_value() {
        var room = PokerHarness.Team();

        room.Accepted(Bob, new PokerVoteSet(R(1), 3, High: 5, Lock: true));

        Assert.Equal(new Vote(3, 5, true, false), room.Round(1).Votes[Bob.Who.Id]);
    }

    [Theory]
    [InlineData(4, null)]
    [InlineData(0, null)]
    [InlineData(34, null)]
    [InlineData(3, 8)]
    [InlineData(5, 3)]
    [InlineData(21, 34)]
    public void Points_off_the_deck_or_a_wrong_between_end_are_invalid(short points, int? high) {
        var room = PokerHarness.Team();

        Rejected(room.Send(Bob, new PokerVoteSet(R(1), points, (short?)high)), NackCode.Invalid);
    }

    [Fact]
    public void A_locked_vote_cannot_change_but_an_identical_resend_is_a_no_op() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerVoteSet(R(1), 5, Lock: true));

        NoOp(room.Send(Bob, new PokerVoteSet(R(1), 5, Lock: true)));
        Rejected(room.Send(Bob, new PokerVoteSet(R(1), 8)), NackCode.VoteLocked);
        Rejected(room.Send(Bob, new PokerVoteSet(R(1), 5)), NackCode.VoteLocked);
    }

    [Fact]
    public void An_abstained_vote_counts_as_locked() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerAbstainSet(R(1), true));

        Rejected(room.Send(Bob, new PokerVoteSet(R(1), 8)), NackCode.VoteLocked);
    }

    [Fact]
    public void Observers_cannot_vote_lock_or_abstain() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerVoteSet(R(1), 5));
        room.Accepted(Alice, new PokerObserverSet(Bob.Who.Id, true));

        Rejected(room.Send(Bob, new PokerVoteSet(R(1), 5)), NackCode.Observer);
        Rejected(room.Send(Bob, new PokerLockSet(R(1), true)), NackCode.Observer);
        Rejected(room.Send(Bob, new PokerAbstainSet(R(1), true)), NackCode.Observer);
    }

    [Fact]
    public void Vote_type_commands_on_a_pending_round_are_round_pending() {
        var room = PokerHarness.Team();
        room.Join(Alice);
        room.Accepted(Alice, new PokerSpotlightTake(R(1), false));
        room.Accepted(Alice, new PokerRoundCreate(R(2), "ABC-7", Pending: true, ForceNew: true));

        Rejected(room.Send(Bob, new PokerVoteSet(R(2), 5)), NackCode.RoundPending);
        Rejected(room.Send(Bob, new PokerLockSet(R(2), true)), NackCode.RoundPending);
        Rejected(room.Send(Bob, new PokerAbstainSet(R(2), true)), NackCode.RoundPending);
        Rejected(room.Send(Bob, new PokerRoundReveal(R(2))), NackCode.RoundPending);
    }

    [Fact]
    public void Votes_on_a_revealed_round_are_round_revealed() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerRoundReveal(R(1)));

        Rejected(room.Send(Bob, new PokerVoteSet(R(1), 5)), NackCode.RoundRevealed);
        Rejected(room.Send(Bob, new PokerLockSet(R(1), true)), NackCode.RoundRevealed);
        Rejected(room.Send(Bob, new PokerAbstainSet(R(1), true)), NackCode.RoundRevealed);
    }

    [Fact]
    public void Votes_on_an_ended_round_are_round_inactive() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerRoundEnd(R(1)));

        Rejected(room.Send(Bob, new PokerVoteSet(R(1), 5)), NackCode.RoundInactive);
        Rejected(room.Send(Bob, new PokerAbstainSet(R(1), true)), NackCode.RoundInactive);
    }

    [Fact]
    public void Unknown_rounds_are_not_found_and_strangers_are_forbidden() {
        var room = PokerHarness.Team();

        Rejected(room.Send(Bob, new PokerVoteSet(R(9), 5)), NackCode.NotFound);
        Rejected(room.Send(Outsider, new PokerVoteSet(R(1), 5)), NackCode.Forbidden);
        Rejected(room.Send(Guest, new PokerVoteSet(R(1), 5)), NackCode.Forbidden);
        Rejected(room.Send(null, new PokerVoteSet(R(1), 5)), NackCode.Forbidden);
    }

    [Fact]
    public void Lock_needs_a_vote_with_points() {
        var room = PokerHarness.Team();

        Rejected(room.Send(Bob, new PokerLockSet(R(1), true)), NackCode.Invalid);
        room.Accepted(Bob, new PokerAbstainSet(R(1), true));
        Rejected(room.Send(Bob, new PokerLockSet(R(1), true)), NackCode.Invalid);
    }

    [Fact]
    public void Lock_and_unlock_are_set_semantics() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerVoteSet(R(1), 8));

        room.Accepted(Bob, new PokerLockSet(R(1), true));
        NoOp(room.Send(Bob, new PokerLockSet(R(1), true)));
        room.Accepted(Bob, new PokerLockSet(R(1), false));

        Assert.Equal(new Vote(8, null, false, false), room.Round(1).Votes[Bob.Who.Id]);
    }

    [Fact]
    public void Abstain_locks_the_seat_and_un_abstain_empties_it() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerVoteSet(R(1), 8, Lock: true));

        room.Accepted(Bob, new PokerAbstainSet(R(1), true));
        Assert.Equal(Vote.Abstain, room.Round(1).Votes[Bob.Who.Id]);
        NoOp(room.Send(Bob, new PokerAbstainSet(R(1), true)));

        var events = room.Accepted(Bob, new PokerAbstainSet(R(1), false));
        Assert.Contains(events, x => x is VoteChanged { Vote: null });
        Assert.False(room.Round(1).Votes.ContainsKey(Bob.Who.Id));
        NoOp(room.Send(Bob, new PokerAbstainSet(R(1), false)));
    }

    [Fact]
    public void A_quick_session_seats_a_voter_on_their_first_vote() {
        var room = Quick();

        var events = room.Accepted(Guest, new PokerVoteSet(R(1), 3));
        room.Accepted(Guest, new PokerVoteSet(R(1), 5));

        Assert.Single(events.OfType<RosterSet>());
        Assert.Equal([Guest.Who.Id], room.State.Roster.Keys);
        Assert.Equal(PrincipalKind.Guest, room.State.Roster[Guest.Who.Id].Kind);
        Assert.Single(room.Log.SelectMany(x => x).OfType<RosterSet>());
    }

    [Fact]
    public void Reveal_turns_unlocked_votes_into_abstains_and_computes_the_winner() {
        var room = PokerHarness.Team();
        room.Accepted(Alice, new PokerVoteSet(R(1), 5, Lock: true));
        room.Accepted(Bob, new PokerVoteSet(R(1), 3, 5, Lock: true));
        room.Accepted(Carol, new PokerVoteSet(R(1), 13));

        var events = room.Accepted(Dave, new PokerRoundReveal(R(1)));

        var revealed = Assert.Single(events.OfType<RoundRevealed>());
        Assert.Equal(5, revealed.Winner);
        Assert.Equal(Dave.Who.Id, revealed.By);
        Assert.Equal(room.Now, revealed.At);
        Assert.Equal(new RevealedVote(Carol.Who.Id, true), revealed.Votes.Single(x => x.Pid == Carol.Who.Id));
        var round = room.Round(1);
        Assert.Equal(RoundState.Revealed, round.State);
        Assert.Equal((short)5, round.Winner);
        Assert.Equal(Vote.Abstain, round.Votes[Carol.Who.Id]);
        Assert.False(round.Votes.ContainsKey(Dave.Who.Id));
        NoOp(room.Send(Dave, new PokerRoundReveal(R(1))));
    }

    [Fact]
    public void Everyone_abstaining_wins_zero() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerAbstainSet(R(1), true));

        var revealed = Assert.Single(room.Accepted(Bob, new PokerRoundReveal(R(1))).OfType<RoundRevealed>());

        Assert.Equal(0, revealed.Winner);
    }

    [Fact]
    public void Replay_reopens_voting_with_votes_kept_and_locked_and_auto_reveal_off() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerVoteSet(R(1), 8, Lock: true));
        room.Accepted(Carol, new PokerVoteSet(R(1), 3));
        room.Accepted(Alice, new PokerRoundReveal(R(1)));
        room.Accepted(Alice, new PokerRoundEnd(R(1)));
        NoOp(room.Send(Alice, new PokerRoundReveal(R(1))));

        var events = room.Accepted(Carol, new PokerRoundReplay(R(1)));

        var replayed = Assert.Single(events.OfType<RoundReplayed>());
        Assert.Equal(2, replayed.Round.Version);
        var round = room.Round(1);
        Assert.Equal(RoundState.Voting, round.State);
        Assert.True(round.Active);
        Assert.False(round.AutoReveal);
        Assert.Null(round.Winner);
        Assert.Null(round.RevealedAt);
        Assert.Equal(new Vote(8, null, true, false), round.Votes[Bob.Who.Id]);
        Assert.Equal(Vote.Abstain, round.Votes[Carol.Who.Id]);
        NoOp(room.Send(Carol, new PokerRoundReplay(R(1))));
        Rejected(room.Send(Bob, new PokerVoteSet(R(1), 3)), NackCode.VoteLocked);
    }
}
