using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using static Retroscope.Core.Tests.Poker.PokerAssert;
using static Retroscope.Core.Tests.Poker.PokerHarness;

namespace Retroscope.Core.Tests.Poker;

[Trait("Category", "Poker")]
public sealed class RoundCommandTests {
    [Fact]
    public void Create_allocates_the_next_number_and_moves_the_pointer_for_everyone() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerVoteSet(R(1), 5));

        var events = room.Accepted(Bob, new PokerRoundCreate(R(2), "abc-12"));

        Assert.Equal(["RoundCreated", "PointerMoved"], Kinds(events));
        var created = Assert.IsType<RoundCreated>(events[0]);
        Assert.Equal(2, created.Round.Number);
        Assert.Equal(1, created.SortKey);
        Assert.Equal("ABC-12", created.Round.TicketKey);
        Assert.Equal(new PointerMoved(R(2), PointerCause.Create, true), events[1]);
        Assert.Equal(R(2), room.State.CurrentRoundId);
        Assert.Equal([R(1), R(2)], room.State.Order);
        Assert.Equal(3, room.State.NextRoundNumber);
    }

    [Fact]
    public void Create_fills_a_placeholder_round_instead_unless_forced() {
        var room = PokerHarness.Team();

        var filled = room.Accepted(Bob, new PokerRoundCreate(R(2), "ABC-1"));

        Assert.Equal(["RoundUpdated"], Kinds(filled));
        Assert.Equal("ABC-1", room.Round(1).TicketKey);
        Assert.Equal(2, room.Round(1).Version);
        Assert.Single(room.State.Rounds);

        room.Accepted(Bob, new PokerRoundSetTicket(R(1), null, 2));
        var forced = room.Accepted(Bob, new PokerRoundCreate(R(2), "ABC-2", ForceNew: true));
        Assert.Equal(["RoundCreated", "PointerMoved"], Kinds(forced));
    }

    [Fact]
    public void A_placeholder_with_votes_or_a_revealed_one_is_not_filled() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerAbstainSet(R(1), true));

        Assert.Equal(["RoundCreated", "PointerMoved"], Kinds(room.Accepted(Bob, new PokerRoundCreate(R(2), "ABC-2"))));
        room.Accepted(Bob, new PokerRoundReveal(R(2)));
        Assert.Equal(["RoundCreated", "PointerMoved"], Kinds(room.Accepted(Bob, new PokerRoundCreate(R(3), "ABC-3"))));
        Assert.Equal(["RoundCreated", "PointerMoved"], Kinds(room.Accepted(Bob, new PokerRoundCreate(R(4)))));
    }

    [Fact]
    public void A_bare_number_gets_the_team_prefix_and_bad_keys_are_invalid() {
        var room = PokerHarness.Team();

        room.Accepted(Bob, new PokerRoundCreate(R(2), " 42 ", ForceNew: true));
        Assert.Equal("ABC-42", room.Round(2).TicketKey);

        Rejected(room.Send(Bob, new PokerRoundCreate(R(3), "not a key")), NackCode.Invalid);
        Rejected(room.Send(Bob, new PokerRoundCreate(R(3), "ABC-0")), NackCode.Invalid);
        Rejected(room.Send(Bob, new PokerRoundCreate(R(2), "ABC-3")), NackCode.Invalid);
        Rejected(PokerHarness.Team(jira: false).Send(Bob, new PokerRoundCreate(R(3), "42")), NackCode.Invalid);
    }

    [Fact]
    public void A_pending_create_needs_the_spotlight_and_moves_the_pointer_without_navigation() {
        var room = PokerHarness.Team();

        Rejected(room.Send(Bob, new PokerRoundCreate(R(2), "ABC-2", Pending: true, ForceNew: true)), NackCode.NotHolder);

        room.Join(Bob);
        room.Accepted(Bob, new PokerSpotlightTake(R(1), false));
        var events = room.Accepted(Bob, new PokerRoundCreate(R(2), "ABC-2", Pending: true, ForceNew: true));

        Assert.Equal(["RoundCreated", "PointerMoved", "SpotlightChanged"], Kinds(events));
        Assert.Equal(new PointerMoved(R(2), PointerCause.Create, false), events[1]);
        Assert.True(room.Round(2).Pending);
        Assert.Equal(Bob.Who.Id, room.Round(2).PendingOwner);
        Assert.Equal(R(2), room.State.Spotlight!.RoundId);
    }

    [Fact]
    public void A_pending_create_never_fills_the_placeholder() {
        var room = PokerHarness.Team();
        room.Join(Bob);
        room.Accepted(Bob, new PokerSpotlightTake(R(1), false));

        var events = room.Accepted(Bob, new PokerRoundCreate(R(2), "ABC-2", Pending: true));

        Assert.Equal(["RoundCreated", "PointerMoved", "SpotlightChanged"], Kinds(events));
        Assert.Null(room.Round(1).TicketKey);
        Assert.Equal(1, room.Round(1).Version);
        Assert.True(room.Round(2).Pending);
        Assert.Equal("ABC-2", room.Round(2).TicketKey);
    }

    [Fact]
    public void Commit_and_discard_pending_belong_to_the_previewer() {
        var room = PokerHarness.Team();
        room.Join(Bob);
        room.Accepted(Bob, new PokerSpotlightTake(R(1), false));
        room.Accepted(Bob, new PokerRoundCreate(R(2), "ABC-2", Pending: true, ForceNew: true));
        room.Accepted(Bob, new PokerRoundCreate(R(3), "ABC-3", Pending: true, ForceNew: true));

        Rejected(room.Send(Carol, new PokerRoundCommitPending(R(2))), NackCode.NotHolder);
        Rejected(room.Send(Carol, new PokerRoundDiscardPending(R(2))), NackCode.NotHolder);

        var committed = room.Accepted(Bob, new PokerRoundCommitPending(R(2)));
        Assert.Equal(["RoundUpdated", "PointerMoved"], Kinds(committed));
        Assert.Equal(new PointerMoved(R(2), PointerCause.Commit, true), committed[1]);
        Assert.False(room.Round(2).Pending);
        NoOp(room.Send(Bob, new PokerRoundCommitPending(R(2))));
        Rejected(room.Send(Bob, new PokerRoundDiscardPending(R(2))), NackCode.Invalid);

        Assert.Equal(R(3), room.State.Spotlight!.RoundId);
        var discarded = room.Accepted(Bob, new PokerRoundDiscardPending(R(3)));
        Assert.Contains(discarded, x => x is RoundDeleted { RoundId: var r } && r == R(3));
        Assert.Equal(R(2), room.State.Spotlight!.RoundId);
        Assert.False(room.State.Rounds.ContainsKey(R(3)));
    }

    [Fact]
    public void Discarding_the_pointer_round_repairs_the_pointer_without_navigation() {
        var room = PokerHarness.Team();
        room.Join(Bob);
        room.Accepted(Bob, new PokerSpotlightTake(R(1), false));
        room.Accepted(Bob, new PokerRoundCreate(R(2), "ABC-2", Pending: true, ForceNew: true));

        var events = room.Accepted(Bob, new PokerRoundDiscardPending(R(2)));

        Assert.Equal(["SpotlightChanged", "RoundDeleted", "PointerMoved"], Kinds(events));
        Assert.Equal(new SpotlightChanged(Bob.Who.Id, Bob.Client, R(1)), events[0]);
        Assert.Equal(new PointerMoved(R(1), PointerCause.Repair, false), events[2]);
        Assert.Equal(R(1), room.State.CurrentRoundId);
    }

    [Fact]
    public void Create_many_fills_the_placeholder_first_and_points_at_the_last() {
        var room = PokerHarness.Team();

        var events = room.Accepted(Bob, new PokerRoundCreateMany([
            new NewRound(R(2), "ABC-1"), new NewRound(R(3), "ABC-2"), new NewRound(R(4), "3"),
        ]));

        Assert.Equal(["RoundUpdated", "RoundCreated", "RoundCreated", "PointerMoved"], Kinds(events));
        Assert.Equal("ABC-1", room.Round(1).TicketKey);
        Assert.Equal("ABC-3", room.State.Rounds[R(4)].TicketKey);
        Assert.False(room.State.Rounds.ContainsKey(R(2)));
        Assert.Equal(new PointerMoved(R(4), PointerCause.CreateMany, true), events[^1]);
        Assert.Equal([2, 3], room.State.Order.Skip(1).Select(x => room.State.Rounds[x].Number));
    }

    [Fact]
    public void Create_many_validates_the_whole_batch() {
        var room = PokerHarness.Team();

        Rejected(room.Send(Bob, new PokerRoundCreateMany([])), NackCode.Invalid);
        var tooMany = Enumerable.Range(2, PokerDecider.MaxBatchRounds + 1).Select(x => new NewRound(R(x), $"ABC-{x}")).ToArray();
        Rejected(room.Send(Bob, new PokerRoundCreateMany(tooMany)), NackCode.Invalid);
        Rejected(room.Send(Bob, new PokerRoundCreateMany([new NewRound(R(2), "ABC-2"), new NewRound(R(2), "ABC-3")])), NackCode.Invalid);
        Rejected(room.Send(Bob, new PokerRoundCreateMany([new NewRound(R(1), "ABC-2")])), NackCode.Invalid);
        Rejected(room.Send(Bob, new PokerRoundCreateMany([new NewRound(R(2), "ABC-2"), new NewRound(R(3), "x")])), NackCode.Invalid);
        Assert.Single(room.State.Rounds);
    }

    [Fact]
    public void Next_deactivates_every_other_round_discards_previews_and_navigates_everyone() {
        var room = PokerHarness.Team();
        room.Join(Carol);
        room.Accepted(Bob, new PokerRoundCreate(R(2), "ABC-2", ForceNew: true));
        room.Accepted(Carol, new PokerSpotlightTake(R(2), false));
        room.Accepted(Carol, new PokerRoundCreate(R(3), "ABC-3", Pending: true, ForceNew: true));

        var events = room.Accepted(Bob, new PokerRoundNext(R(1), R(4), "ABC-4"));

        Assert.Equal(new PointerMoved(R(4), PointerCause.Next, true), events.OfType<PointerMoved>().Single());
        Assert.Equal(R(3), events.OfType<RoundDeleted>().Single().RoundId);
        Assert.Equal([R(1), R(2), R(4)], room.State.Order);
        Assert.Equal([R(4)], room.State.Order.Where(x => room.State.Rounds[x].Active));
        Assert.Equal(4, room.State.Rounds[R(4)].Number);
        Assert.Equal(R(4), room.State.Spotlight!.RoundId);
        Assert.Equal(Carol.Who.Id, room.State.Spotlight.Holder);
    }

    [Fact]
    public void Next_moves_the_spotlight_only_when_its_holder_issued_it() {
        var room = PokerHarness.Team();
        room.Join(Carol);
        room.Accepted(Carol, new PokerSpotlightTake(R(1), false));

        var byOther = room.Accepted(Bob, new PokerRoundNext(R(1), R(2)));
        Assert.DoesNotContain(byOther, x => x is SpotlightChanged);
        Assert.Equal(R(1), room.State.Spotlight!.RoundId);

        var byHolder = room.Accepted(Carol, new PokerRoundNext(R(2), R(3)));
        Assert.Contains(new SpotlightChanged(Carol.Who.Id, Carol.Client, R(3)), byHolder);
    }

    [Fact]
    public void A_second_next_from_the_same_round_is_stale() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerRoundNext(R(1), R(2)));

        var reject = Rejected(room.Send(Carol, new PokerRoundNext(R(1), R(3))), NackCode.Stale);

        Assert.Equal(R(2).Value, Current(reject).GetProperty("roundId").GetGuid());
        Rejected(room.Send(Carol, new PokerRoundNext(R(9), R(3))), NackCode.Stale);
        Rejected(room.Send(Carol, new PokerRoundNext(R(2), R(2))), NackCode.Invalid);
        Assert.Equal(2, room.State.Rounds.Count);
    }

    [Fact]
    public void End_and_activate_are_set_semantics_on_voting_rounds() {
        var room = PokerHarness.Team();

        room.Accepted(Bob, new PokerRoundEnd(R(1)));
        Assert.False(room.Round(1).Active);
        NoOp(room.Send(Bob, new PokerRoundEnd(R(1))));
        room.Accepted(Bob, new PokerRoundActivate(R(1)));
        Assert.True(room.Round(1).Active);
        NoOp(room.Send(Bob, new PokerRoundActivate(R(1))));

        room.Accepted(Bob, new PokerRoundReveal(R(1)));
        room.Accepted(Bob, new PokerRoundEnd(R(1)));
        Rejected(room.Send(Bob, new PokerRoundActivate(R(1))), NackCode.RoundNotVoting);
        Rejected(room.Send(Bob, new PokerRoundEnd(R(7))), NackCode.NotFound);
    }

    [Fact]
    public void End_and_activate_of_a_pending_round_are_round_pending() {
        var room = PokerHarness.Team();
        room.Join(Bob);
        room.Accepted(Bob, new PokerSpotlightTake(R(1), false));
        room.Accepted(Bob, new PokerRoundCreate(R(2), "ABC-2", Pending: true, ForceNew: true));

        Rejected(room.Send(Bob, new PokerRoundEnd(R(2))), NackCode.RoundPending);
        Rejected(room.Send(Bob, new PokerRoundActivate(R(2))), NackCode.RoundPending);
    }

    [Fact]
    public void Set_ticket_carries_a_version_precondition() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerRoundSetTicket(R(1), "ABC-5", 1));
        room.Send(null, new JiraMetaLoaded([new JiraMeta("ABC-5", ParentKey: "ABC-1")]));
        Assert.Equal("ABC-1", room.Round(1).ParentKey);

        var reject = Rejected(room.Send(Carol, new PokerRoundSetTicket(R(1), "ABC-6", 1)), NackCode.Stale);
        Assert.Equal("ABC-5", Current(reject).GetProperty("ticketKey").GetString());
        Assert.Equal(2, Current(reject).GetProperty("version").GetInt32());

        NoOp(room.Send(Carol, new PokerRoundSetTicket(R(1), "abc-5", 1)));
        var events = room.Accepted(Carol, new PokerRoundSetTicket(R(1), "", 2));
        var updated = Assert.IsType<RoundUpdated>(Assert.Single(events));
        Assert.Null(updated.Round.TicketKey);
        Assert.Null(updated.Round.ParentKey);
        Assert.Equal(3, updated.Round.Version);
    }

    [Fact]
    public void Set_ticket_needs_an_active_voting_round_and_a_valid_key() {
        var room = PokerHarness.Team();

        Rejected(room.Send(Bob, new PokerRoundSetTicket(R(1), "nope", 1)), NackCode.Invalid);
        room.Accepted(Bob, new PokerRoundEnd(R(1)));
        Rejected(room.Send(Bob, new PokerRoundSetTicket(R(1), "ABC-1", 1)), NackCode.RoundInactive);
        room.Accepted(Bob, new PokerRoundReveal(R(1)));
        Rejected(room.Send(Bob, new PokerRoundSetTicket(R(1), "ABC-1", 1)), NackCode.RoundRevealed);
    }

    [Fact]
    public void Delete_needs_an_admin_in_team_sessions_and_keeps_one_round() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerRoundCreate(R(2), ForceNew: true));

        Rejected(room.Send(Bob, new PokerRoundDelete(R(2))), NackCode.Forbidden);
        var events = room.Accepted(Dave, new PokerRoundDelete(R(2)));

        Assert.Equal(["RoundDeleted", "PointerMoved"], Kinds(events));
        Assert.Equal(new PointerMoved(R(1), PointerCause.Repair, false), events[^1]);
        Rejected(room.Send(Alice, new PokerRoundDelete(R(1))), NackCode.Invalid);
        Rejected(room.Send(Alice, new PokerRoundDelete(R(2))), NackCode.NotFound);
    }

    [Fact]
    public void Deleting_the_spotlight_round_moves_the_spotlight_to_the_pointer_round_before_the_delete() {
        var room = PokerHarness.Team();
        room.Join(Carol);
        room.Accepted(Bob, new PokerRoundCreate(R(2), ForceNew: true));
        room.Accepted(Bob, new PokerRoundCreate(R(3), ForceNew: true));
        room.Accepted(Carol, new PokerSpotlightTake(R(2), false));

        var events = room.Accepted(Alice, new PokerRoundDelete(R(2)));

        Assert.Equal(["SpotlightChanged", "RoundDeleted"], Kinds(events));
        Assert.Equal(new SpotlightChanged(Carol.Who.Id, Carol.Client, R(3)), events[0]);
    }

    [Fact]
    public void Deleting_the_pointer_and_spotlight_round_moves_both_to_the_highest_remaining_round() {
        var room = PokerHarness.Team();
        room.Join(Carol);
        room.Accepted(Bob, new PokerRoundCreate(R(2), ForceNew: true));
        room.Accepted(Bob, new PokerRoundCreate(R(3), ForceNew: true));
        room.Accepted(Bob, new PokerRoundReorder([R(3), R(1), R(2)]));
        room.Accepted(Carol, new PokerSpotlightTake(R(3), false));

        var events = room.Accepted(Alice, new PokerRoundDelete(R(3)));

        Assert.Equal(["SpotlightChanged", "RoundDeleted", "PointerMoved"], Kinds(events));
        Assert.Equal(new SpotlightChanged(Carol.Who.Id, Carol.Client, R(2)), events[0]);
        Assert.Equal(new PointerMoved(R(2), PointerCause.Repair, false), events[2]);
    }

    [Fact]
    public void Reorder_must_be_a_permutation_of_the_current_rounds() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerRoundCreate(R(2), ForceNew: true));

        var reject = Rejected(room.Send(Bob, new PokerRoundReorder([R(2)])), NackCode.Stale);
        Assert.Equal([R(1).Value, R(2).Value], Current(reject).GetProperty("order").EnumerateArray().Select(x => x.GetGuid()));
        Rejected(room.Send(Bob, new PokerRoundReorder([R(2), R(2)])), NackCode.Stale);
        Rejected(room.Send(Bob, new PokerRoundReorder([R(2), R(9)])), NackCode.Stale);
        NoOp(room.Send(Bob, new PokerRoundReorder([R(1), R(2)])));

        room.Accepted(Bob, new PokerRoundReorder([R(2), R(1)]));
        Assert.Equal([R(2), R(1)], room.State.Order);
    }

    [Fact]
    public void Clear_all_resets_to_round_one_and_keeps_the_holder_on_it() {
        var room = PokerHarness.Team();
        room.Join(Carol);
        room.Accepted(Bob, new PokerVoteSet(R(1), 5));
        room.Accepted(Bob, new PokerRoundCreate(R(2), ForceNew: true));
        room.Accepted(Carol, new PokerSpotlightTake(R(2), false));

        Rejected(room.Send(Bob, new PokerRoundsClearAll(R(9))), NackCode.Forbidden);
        Rejected(room.Send(Alice, new PokerRoundsClearAll(R(2))), NackCode.Invalid);
        var events = room.Accepted(Alice, new PokerRoundsClearAll(R(9)));

        Assert.Equal(["RoundsReset", "PointerMoved", "SpotlightChanged"], Kinds(events));
        Assert.Equal(new PointerMoved(R(9), PointerCause.Reset, true), events[1]);
        Assert.Equal(new SpotlightChanged(Carol.Who.Id, Carol.Client, R(9)), events[2]);
        Assert.Equal([R(9)], room.State.Order);
        Assert.Equal(1, room.State.Rounds[R(9)].Number);
        Assert.Equal(2, room.State.NextRoundNumber);
        Assert.Empty(room.State.Rounds[R(9)].Votes);
    }

    [Fact]
    public void Clear_all_without_a_spotlight_leaves_it_released() {
        var room = PokerHarness.Team();

        Rejected(room.Send(Guest, new PokerRoundsClearAll(R(9))), NackCode.Forbidden);
        var events = room.Accepted(Dave, new PokerRoundsClearAll(R(9)));

        Assert.Equal(["RoundsReset", "PointerMoved"], Kinds(events));
        Assert.Null(room.State.Spotlight);
    }
}
