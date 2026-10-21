using Retroscope.Core.Protocol;
using static Retroscope.Core.Tests.Poker.PokerAssert;
using static Retroscope.Core.Tests.Poker.PokerHarness;

namespace Retroscope.Core.Tests.Poker;

/// <summary>A create resent under a new q after a cid_reuse renumbering must not apply twice.</summary>
[Trait("Category", "Poker")]
public sealed class IdempotentCreateTests {
    [Fact]
    public void A_repeated_create_acks_without_a_second_round() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerRoundCreate(R(2), "ABC-2", ForceNew: true));
        var before = room.State.Rounds.Count;

        NoOp(room.Send(Bob, new PokerRoundCreate(R(2), "ABC-2", ForceNew: true)));
        NoOp(room.Send(Bob, new PokerRoundCreate(R(2), "2", ForceNew: true)));

        Assert.Equal(before, room.State.Rounds.Count);
        Rejected(room.Send(Bob, new PokerRoundCreate(R(2), "ABC-3", ForceNew: true)), NackCode.Invalid);
        Rejected(room.Send(Bob, new PokerRoundCreate(R(2), ForceNew: true)), NackCode.Invalid);
    }

    [Fact]
    public void A_repeated_create_still_needs_the_permission() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerRoundCreate(R(2), ForceNew: true));

        NoOp(room.Send(Bob, new PokerRoundCreate(R(2), ForceNew: true)));
        Rejected(room.Send(Outsider, new PokerRoundCreate(R(2), ForceNew: true)), NackCode.Forbidden);
    }

    [Fact]
    public void A_repeated_batch_acks_including_a_first_round_that_filled_the_placeholder() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerRoundCreateMany([new(R(2), "ABC-2"), new(R(3), "ABC-3")]));
        Assert.False(room.State.Rounds.ContainsKey(R(2)));
        var before = room.State.Rounds.Count;

        NoOp(room.Send(Bob, new PokerRoundCreateMany([new(R(2), "ABC-2"), new(R(3), "ABC-3")])));

        Assert.Equal(before, room.State.Rounds.Count);
        Rejected(room.Send(Bob, new PokerRoundCreateMany([new(R(2), "ABC-2"), new(R(3), "ABC-4")])), NackCode.Invalid);
        Rejected(room.Send(Bob, new PokerRoundCreateMany([new(R(4), "ABC-4"), new(R(3), "ABC-3")])), NackCode.Invalid);
        Rejected(room.Send(Bob, new PokerRoundCreateMany([new(R(2), "ABC-9"), new(R(3), "ABC-3")])), NackCode.Invalid);
    }

    [Fact]
    public void A_repeated_next_acks_even_though_its_from_round_has_ended() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerRoundNext(R(1), R(2), "ABC-2"));

        NoOp(room.Send(Bob, new PokerRoundNext(R(1), R(2), "ABC-2")));

        Assert.Equal(2, room.State.Rounds.Count);
        Assert.Equal(R(2), room.State.CurrentRoundId);
        Rejected(room.Send(Bob, new PokerRoundNext(R(1), R(2), "ABC-7")), NackCode.Invalid);
    }

    [Fact]
    public void A_repeated_clear_all_does_not_reset_the_rounds_added_since() {
        var room = PokerHarness.Team();
        room.Accepted(Alice, new PokerRoundsClearAll(R(9)));
        room.Accepted(Alice, new PokerRoundCreate(R(10), ForceNew: true));

        NoOp(room.Send(Alice, new PokerRoundsClearAll(R(9))));

        Assert.Equal([R(9), R(10)], room.State.Order);
        Rejected(room.Send(Alice, new PokerRoundsClearAll(R(10))), NackCode.Invalid);
    }
}
