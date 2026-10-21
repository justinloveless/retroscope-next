using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using static Retroscope.Core.Tests.Poker.PokerAssert;
using static Retroscope.Core.Tests.Poker.PokerHarness;

namespace Retroscope.Core.Tests.Poker;

[Trait("Category", "Poker")]
public sealed class PresenceTests {
    [Fact]
    public void The_holders_bound_tab_going_away_unbinds_the_spotlight_for_another_tab() {
        var room = PokerHarness.Team();
        var bobTab2 = Viewer(2, "Bob", tab: 1);
        room.Join(Bob);
        room.Join(bobTab2);
        room.Accepted(Bob, new PokerSpotlightTake(R(1), false));

        room.Disconnect(Bob);

        Assert.Equal(new SpotlightChanged(Bob.Who.Id, null, R(1)), room.Log[^1].Single());
        Assert.Null(room.State.Spotlight!.HolderClient);
        room.Accepted(bobTab2, new PokerSpotlightMove(R(1)));
        Assert.Equal(bobTab2.Client, room.State.Spotlight.HolderClient);
    }

    [Fact]
    public void Another_tab_going_away_changes_nothing() {
        var room = PokerHarness.Team();
        var bobTab2 = Viewer(2, "Bob", tab: 1);
        room.Join(Bob);
        room.Join(bobTab2);
        room.Accepted(Bob, new PokerSpotlightTake(R(1), false));
        var logged = room.Log.Count;

        room.Disconnect(bobTab2);

        Assert.Equal(logged, room.Log.Count);
    }

    [Fact]
    public void The_spotlight_survives_three_minutes_of_holder_absence() {
        var room = PokerHarness.Team();
        room.Join(Bob);
        room.Accepted(Bob, new PokerSpotlightTake(R(1), false));

        room.Disconnect(Bob);
        Assert.Equal(room.Now, room.State.Spotlight!.HolderAbsentSince);
        var grace = Assert.Single(room.Timers(), x => x.Key == PokerReactions.SpotlightGraceTimer);
        Assert.Equal(room.Now + PokerTiming.SpotlightGrace, grace.DueAt);

        room.Advance(TimeSpan.FromMinutes(2));
        Assert.NotNull(room.State.Spotlight);
        room.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(room.State.Spotlight);
    }

    [Fact]
    public void The_holder_returning_within_the_grace_keeps_the_spotlight() {
        var room = PokerHarness.Team();
        room.Join(Bob);
        room.Accepted(Bob, new PokerSpotlightTake(R(1), false));
        room.Disconnect(Bob);
        var grace = Assert.Single(room.Timers());

        room.Now += TimeSpan.FromMinutes(2);
        room.Join(Bob);
        Assert.Null(room.State.Spotlight!.HolderAbsentSince);
        Assert.Empty(room.Timers());

        room.Now += TimeSpan.FromMinutes(2);
        NoOp(room.Send(null, grace.Command));
        Assert.NotNull(room.State.Spotlight);
    }

    [Fact]
    public void A_pending_owner_leaving_discards_their_previews_and_repairs_pointer_and_spotlight() {
        var room = PokerHarness.Team();
        room.Join(Bob);
        room.Join(Carol);
        room.Accepted(Bob, new PokerSpotlightTake(R(1), false));
        room.Accepted(Bob, new PokerRoundCreate(R(2), "ABC-2", Pending: true, ForceNew: true));

        room.Disconnect(Bob);

        Assert.Equal(new SpotlightChanged(Bob.Who.Id, null, R(2)), room.Log[^2].Single());
        Assert.Equal(["SpotlightChanged", "RoundDeleted", "PointerMoved"], Kinds(room.Log[^1]));
        Assert.False(room.State.Rounds.ContainsKey(R(2)));
        Assert.Equal(R(1), room.State.CurrentRoundId);
        Assert.Equal(R(1), room.State.Spotlight!.RoundId);
        Assert.Equal(Bob.Who.Id, room.State.Spotlight.Holder);
    }

    [Fact]
    public void Warm_up_releases_a_spotlight_whose_holder_did_not_come_back_and_discards_orphaned_previews() {
        var room = PokerHarness.Team();
        room.State.Rounds.Add(R(2), new Round { Id = R(2), Number = 2, TicketKey = "ABC-2", Pending = true, PendingOwner = Carol.Who.Id });
        room.State.Order.Add(R(2));
        room.State.Spotlight = new Spotlight { Holder = Bob.Who.Id, RoundId = R(1) };
        room.Join(Dave);

        room.Warm();

        Assert.Null(room.State.Spotlight);
        Assert.False(room.State.Rounds.ContainsKey(R(2)));
    }

    [Fact]
    public void Warm_up_keeps_a_present_holder_and_present_owners_previews() {
        var room = PokerHarness.Team();
        room.State.Rounds.Add(R(2), new Round { Id = R(2), Number = 2, TicketKey = "ABC-2", Pending = true, PendingOwner = Bob.Who.Id });
        room.State.Order.Add(R(2));
        room.State.Spotlight = new Spotlight { Holder = Bob.Who.Id, RoundId = R(2) };
        room.Join(Bob);

        room.Warm();

        Assert.Equal(Bob.Who.Id, room.State.Spotlight!.Holder);
        Assert.True(room.State.Rounds.ContainsKey(R(2)));
        Assert.All(room.Log.SelectMany(x => x), x => Assert.Equal(EventDurability.MemoryOnly, x.Durability));
    }

    [Fact]
    public void Updates_and_extra_tabs_produce_no_events() {
        var room = PokerHarness.Team();
        room.Join(Bob);

        Assert.Empty(room.Domain.OnPresence(room.State, new PresenceDelta(PresenceChange.Updated, Bob.Who.Id), room.Presence, room.Now));
        Assert.Empty(room.Domain.OnPresence(
            room.State, new PresenceDelta(PresenceChange.ClientConnected, Bob.Who.Id, Bob.Client), room.Presence, room.Now));
    }
}
