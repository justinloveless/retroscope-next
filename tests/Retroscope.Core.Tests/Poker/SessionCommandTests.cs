using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using static Retroscope.Core.Tests.Poker.PokerAssert;
using static Retroscope.Core.Tests.Poker.PokerHarness;

namespace Retroscope.Core.Tests.Poker;

[Trait("Category", "Poker")]
public sealed class SessionCommandTests {
    [Fact]
    public void Settings_apply_only_with_matching_expected_values() {
        var room = PokerHarness.Team();

        room.Accepted(Bob, new PokerSettingsUpdate(new PokerSettingsValues(PresenceEnabled: true), PresenceEnabled: false));
        Assert.False(room.State.PresenceEnabled);

        var reject = Rejected(
            room.Send(Carol, new PokerSettingsUpdate(new PokerSettingsValues(PresenceEnabled: true), PresenceEnabled: false)),
            NackCode.Stale);
        Assert.False(Current(reject).GetProperty("presenceEnabled").GetBoolean());
        Assert.True(Current(reject).GetProperty("followSpotlight").GetBoolean());

        NoOp(room.Send(Carol, new PokerSettingsUpdate(new PokerSettingsValues(PresenceEnabled: false), PresenceEnabled: false)));
        room.Accepted(Carol, new PokerSettingsUpdate(new PokerSettingsValues(FollowSpotlight: true), FollowSpotlight: false));
        Assert.False(room.State.FollowSpotlight);
    }

    [Fact]
    public void Settings_need_something_to_change_and_an_expected_value_for_it() {
        var room = PokerHarness.Team();

        Rejected(room.Send(Bob, new PokerSettingsUpdate(new PokerSettingsValues())), NackCode.Invalid);
        Rejected(room.Send(Bob, new PokerSettingsUpdate(new PokerSettingsValues(), FollowSpotlight: false)), NackCode.Invalid);
        Rejected(room.Send(Outsider, new PokerSettingsUpdate(new PokerSettingsValues(FollowSpotlight: true), FollowSpotlight: false)), NackCode.Forbidden);
    }

    [Fact]
    public void Becoming_an_observer_deletes_votes_in_voting_rounds_only_and_re_arms_auto_reveal() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerVoteSet(R(1), 5, Lock: true));
        room.Accepted(Bob, new PokerRoundReveal(R(1)));
        room.Accepted(Bob, new PokerRoundCreate(R(2), ForceNew: true));
        room.Accepted(Bob, new PokerVoteSet(R(2), 8));
        var gen = room.Round(2).AutoRevealGen;

        var events = room.Accepted(Carol, new PokerObserverSet(Bob.Who.Id, true));

        Assert.Equal(["VoteChanged", "ObserverSet"], Kinds(events));
        Assert.Equal([Bob.Who.Id], Assert.Single(events.OfType<ObserverSet>()).Observers);
        Assert.True(room.Round(1).Votes.ContainsKey(Bob.Who.Id));
        Assert.False(room.Round(2).Votes.ContainsKey(Bob.Who.Id));
        Assert.Equal(gen + 1, room.Round(2).AutoRevealGen);
        NoOp(room.Send(Carol, new PokerObserverSet(Bob.Who.Id, true)));

        room.Accepted(Bob, new PokerObserverSet(Bob.Who.Id, false));
        Assert.Empty(room.State.Observers);
    }

    [Fact]
    public void Only_principals_known_to_the_session_can_be_observers() {
        var room = Quick();

        Rejected(room.Send(Bob, new PokerObserverSet(Carol.Who.Id, true)), NackCode.NotFound);
        room.Join(Carol);
        room.Accepted(Bob, new PokerObserverSet(Carol.Who.Id, true));
    }

    [Fact]
    public void Spotlight_take_asks_before_taking_over_and_force_takes_it() {
        var room = PokerHarness.Team();
        room.Join(Bob);
        room.Join(Carol);
        room.Accepted(Bob, new PokerSpotlightTake(R(1), false));

        var reject = Rejected(room.Send(Carol, new PokerSpotlightTake(R(1), false)), NackCode.SpotlightHeld);
        Assert.Equal(Bob.Who.Id.Value, reject.Data!.Value.GetProperty("holder").GetGuid());

        room.Accepted(Carol, new PokerSpotlightTake(R(1), true));
        Assert.Equal(Carol.Who.Id, room.State.Spotlight!.Holder);
        Assert.Equal(Carol.Client, room.State.Spotlight.HolderClient);
        NoOp(room.Send(Carol, new PokerSpotlightTake(R(1), false)));
        Rejected(room.Send(Carol, new PokerSpotlightTake(R(9), false)), NackCode.NotFound);
    }

    [Fact]
    public void Another_bound_tab_of_the_holder_also_asks_first() {
        var room = PokerHarness.Team();
        var bobTab2 = Viewer(2, "Bob", tab: 1);
        room.Join(Bob);
        room.Join(bobTab2);
        room.Accepted(Bob, new PokerSpotlightTake(R(1), false));

        Rejected(room.Send(bobTab2, new PokerSpotlightTake(R(1), false)), NackCode.SpotlightHeld);
        Rejected(room.Send(bobTab2, new PokerSpotlightMove(R(1))), NackCode.NotHolder);
        room.Accepted(bobTab2, new PokerSpotlightTake(R(1), true));
        Assert.Equal(bobTab2.Client, room.State.Spotlight!.HolderClient);
    }

    [Fact]
    public void Taking_over_discards_the_previous_holders_pending_rounds() {
        var room = PokerHarness.Team();
        room.Join(Bob);
        room.Join(Carol);
        room.Accepted(Bob, new PokerSpotlightTake(R(1), false));
        room.Accepted(Bob, new PokerRoundCreate(R(2), "ABC-2", Pending: true, ForceNew: true));

        var events = room.Accepted(Carol, new PokerSpotlightTake(R(1), true));

        Assert.Equal(["SpotlightChanged", "RoundDeleted", "PointerMoved"], Kinds(events));
        Assert.False(room.State.Rounds.ContainsKey(R(2)));
        Assert.Equal(R(1), room.State.CurrentRoundId);
    }

    [Fact]
    public void Move_is_ephemeral_and_binds_an_unbound_holder_tab() {
        var room = PokerHarness.Team();
        room.Join(Bob);
        room.Join(Carol);
        room.Accepted(Bob, new PokerRoundCreate(R(2), ForceNew: true));
        room.Accepted(Carol, new PokerSpotlightGive(Bob.Who.Id));
        Assert.Null(room.State.Spotlight!.HolderClient);

        Rejected(room.Send(Carol, new PokerSpotlightMove(R(1))), NackCode.NotHolder);
        var events = room.Accepted(Bob, new PokerSpotlightMove(R(1)));

        var moved = Assert.IsType<SpotlightMoved>(Assert.Single(events));
        Assert.Equal(EventDurability.Ephemeral, moved.Durability);
        Assert.Equal(Bob.Client, room.State.Spotlight.HolderClient);
        Assert.True(room.State.Spotlight.Dirty);
        NoOp(room.Send(Bob, new PokerSpotlightMove(R(1))));
    }

    [Fact]
    public void A_dirty_spotlight_settles_after_500_ms_or_rides_along_with_the_next_commit() {
        var room = PokerHarness.Team();
        room.Join(Bob);
        room.Accepted(Bob, new PokerRoundCreate(R(2), ForceNew: true));
        room.Accepted(Bob, new PokerSpotlightTake(R(2), false));
        room.Accepted(Bob, new PokerSpotlightMove(R(1)));

        var withVote = room.Accepted(Carol, new PokerVoteSet(R(1), 3));
        Assert.Equal(new SpotlightSettled(R(1), Bob.Client), withVote[^1]);
        Assert.False(room.State.Spotlight!.Dirty);

        room.Accepted(Bob, new PokerSpotlightMove(R(2)));
        var settle = Assert.Single(room.Timers(), x => x.Key == PokerReactions.SpotlightSettleTimer);
        Assert.Equal(room.Now + PokerTiming.SpotlightSettle, settle.DueAt);
        room.Advance(TimeSpan.FromSeconds(1));
        Assert.False(room.State.Spotlight.Dirty);
        Assert.IsType<SpotlightSettled>(room.Log[^1].Single());
        NoOp(room.Send(null, new SpotlightSettleDue()));
    }

    [Fact]
    public void An_ephemeral_decision_does_not_carry_the_settle() {
        var room = PokerHarness.Team();
        room.Join(Bob);
        room.Accepted(Bob, new PokerRoundCreate(R(2), ForceNew: true));
        room.Accepted(Bob, new PokerSpotlightTake(R(2), false));
        room.Accepted(Bob, new PokerSpotlightMove(R(1)));

        var events = room.Accepted(Bob, new PokerSpotlightMove(R(2)));

        Assert.IsType<SpotlightMoved>(Assert.Single(events));
    }

    [Fact]
    public void Give_goes_to_a_present_principal_from_the_holder_or_from_anyone_when_free() {
        var room = PokerHarness.Team();
        room.Join(Bob);
        room.Join(Carol);

        Rejected(room.Send(Bob, new PokerSpotlightGive(Dave.Who.Id)), NackCode.Invalid);
        room.Accepted(Bob, new PokerSpotlightGive(Carol.Who.Id));
        Assert.Equal(new SpotlightChanged(Carol.Who.Id, null, R(1)), room.Log[^1].Single());

        Rejected(room.Send(Bob, new PokerSpotlightGive(Bob.Who.Id)), NackCode.NotHolder);
        NoOp(room.Send(Carol, new PokerSpotlightGive(Carol.Who.Id)));
        room.Accepted(Carol, new PokerSpotlightGive(Bob.Who.Id));
        Assert.Equal(Bob.Who.Id, room.State.Spotlight!.Holder);
    }

    [Fact]
    public void Release_is_for_the_holder_and_discards_their_pending_rounds() {
        var room = PokerHarness.Team();
        room.Join(Bob);
        NoOp(room.Send(Bob, new PokerSpotlightRelease()));
        room.Accepted(Bob, new PokerSpotlightTake(R(1), false));
        room.Accepted(Bob, new PokerRoundCreate(R(2), "ABC-2", Pending: true, ForceNew: true));

        Rejected(room.Send(Carol, new PokerSpotlightRelease()), NackCode.NotHolder);
        var events = room.Accepted(Bob, new PokerSpotlightRelease());

        Assert.Equal(new SpotlightChanged(null, null, null), events[0]);
        Assert.Contains(events, x => x is RoundDeleted);
        Assert.Null(room.State.Spotlight);
        Assert.Single(room.State.Rounds);
    }

    [Fact]
    public void Submit_points_goes_pending_then_ok_persists_the_points() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerRoundSetTicket(R(1), "ABC-5", 1));
        room.Accepted(Bob, new PokerRoundReveal(R(1)));

        var started = room.Accepted(Bob, new PokerJiraSubmitPoints(R(1), 5));
        Assert.Equal(new JiraSubmitChanged(R(1), "ABC-5", 5, Bob.Who.Id, JiraSubmitStatus.Pending), Assert.Single(started));
        Rejected(room.Send(Carol, new PokerJiraSubmitPoints(R(1), 8)), NackCode.JiraPending);

        NoOp(room.Send(null, new JiraPointsResult(R(1), "ABC-5", 8, Bob.Who.Id, true)));
        var done = room.Accepted(null, new JiraPointsResult(R(1), "ABC-5", 5, Bob.Who.Id, true));

        var recorded = Assert.IsType<JiraPointsRecorded>(Assert.Single(done));
        Assert.Equal(new JiraPointsInfo(5, room.Now.ToUnixTimeMilliseconds(), Bob.Who.Id), recorded.Round.Jira);
        Assert.Equal(5, room.Round(1).Jira!.Points);
        Assert.Empty(room.State.JiraSubmits);
        room.Accepted(Carol, new PokerJiraSubmitPoints(R(1), 2.5m));
    }

    [Fact]
    public void A_failed_submit_keeps_the_code_until_the_next_try() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerRoundSetTicket(R(1), "ABC-5", 1));
        room.Accepted(Bob, new PokerRoundReveal(R(1)));
        room.Accepted(Bob, new PokerJiraSubmitPoints(R(1), 5));

        room.Accepted(null, new JiraPointsResult(R(1), "ABC-5", 5, Bob.Who.Id, false, "jira_forbidden"));

        Assert.Equal(new JiraSubmit("ABC-5", 5, Bob.Who.Id, JiraSubmitStatus.Failed, "jira_forbidden"), room.State.JiraSubmits[R(1)]);
        Assert.Null(room.Round(1).Jira);
        room.Accepted(Bob, new PokerJiraSubmitPoints(R(1), 5));
    }

    [Fact]
    public void Submit_points_rules() {
        var noJira = PokerHarness.Team(jira: false);
        noJira.Accepted(Bob, new PokerRoundReveal(R(1)));
        Rejected(noJira.Send(Bob, new PokerJiraSubmitPoints(R(1), 5)), NackCode.JiraNotConfigured);
        Rejected(Quick().Send(Bob, new PokerJiraSubmitPoints(R(1), 5)), NackCode.JiraNotConfigured);

        var room = PokerHarness.Team();
        Rejected(room.Send(Outsider, new PokerJiraSubmitPoints(R(1), 5)), NackCode.Forbidden);
        Rejected(room.Send(Bob, new PokerJiraSubmitPoints(R(9), 5)), NackCode.NotFound);
        Rejected(room.Send(Bob, new PokerJiraSubmitPoints(R(1), 5)), NackCode.Invalid);
        room.Accepted(Bob, new PokerRoundReveal(R(1)));
        Rejected(room.Send(Bob, new PokerJiraSubmitPoints(R(1), 5)), NackCode.Invalid);

        room.Accepted(Bob, new PokerRoundCreate(R(2), "ABC-2", ForceNew: true));
        room.Accepted(Bob, new PokerRoundReveal(R(2)));
        Rejected(room.Send(Bob, new PokerJiraSubmitPoints(R(2), 1.25m)), NackCode.Invalid);
        Rejected(room.Send(Bob, new PokerJiraSubmitPoints(R(2), -1)), NackCode.Invalid);
        Rejected(room.Send(Bob, new PokerJiraSubmitPoints(R(2), 1000.5m)), NackCode.Invalid);

        room.Send(null, new JiraSettingsChanged(new TeamJiraSettings(true, "ABC", false)));
        Rejected(room.Send(Bob, new PokerJiraSubmitPoints(R(2), 5)), NackCode.Forbidden);
        room.Accepted(Dave, new PokerJiraSubmitPoints(R(2), 5));
    }

    [Fact]
    public void A_result_for_a_ticket_the_round_no_longer_has_is_not_recorded() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerRoundSetTicket(R(1), "ABC-5", 1));
        room.Accepted(Bob, new PokerRoundReveal(R(1)));
        room.Accepted(Bob, new PokerJiraSubmitPoints(R(1), 5));
        room.Accepted(Bob, new PokerRoundReplay(R(1)));
        room.Accepted(Bob, new PokerRoundSetTicket(R(1), "ABC-6", 3));

        var events = room.Accepted(null, new JiraPointsResult(R(1), "ABC-5", 5, Bob.Who.Id, true));

        Assert.Equal(JiraSubmitStatus.Ok, Assert.IsType<JiraSubmitChanged>(Assert.Single(events)).Status);
        Assert.Null(room.Round(1).Jira);
        Assert.Empty(room.State.JiraSubmits);
    }

    [Fact]
    public void Jira_meta_persists_parent_keys_only_when_they_change() {
        var room = PokerHarness.Team();
        room.Accepted(Bob, new PokerRoundSetTicket(R(1), "ABC-5", 1));

        var first = room.Accepted(null, new JiraMetaLoaded([new JiraMeta("ABC-5", "Title", ParentKey: "ABC-1")]));
        Assert.Equal("ABC-1", Assert.IsType<RoundUpdated>(Assert.Single(first)).Round.ParentKey);
        Assert.Equal(2, room.Round(1).Version);

        NoOp(room.Send(null, new JiraMetaLoaded([new JiraMeta("ABC-5", "New title", ParentKey: "ABC-1")])));
        NoOp(room.Send(null, new JiraMetaLoaded([new JiraMeta("ABC-9", ParentKey: "ABC-2")])));
        room.Accepted(null, new JiraMetaLoaded([new JiraMeta("ABC-5", ParentKey: "<script>")]));
        Assert.Null(room.Round(1).ParentKey);
    }

    [Fact]
    public void Roster_and_point_description_changes_are_ephemeral_events() {
        var room = PokerHarness.Team();

        var roster = room.Accepted(null, new RosterChanged([Info(Bob), Info(Alice)]));
        Assert.Equal([Alice.Who.Id, Bob.Who.Id], Assert.IsType<RosterSet>(roster[0]).Seats.Select(x => x.Pid));
        Assert.All(roster, x => Assert.NotEqual(EventDurability.Committed, x.Durability));
        NoOp(room.Send(null, new RosterChanged([Info(Alice), Info(Bob)])));

        var descriptions = new Dictionary<string, string> { ["1"] = "an hour" };
        var settings = room.Accepted(null, new PointDescriptionsChanged(descriptions));
        Assert.Equal(EventDurability.Ephemeral, Assert.Single(settings).Durability);
        Assert.Same(descriptions, room.State.PointDescriptions);
    }

    [Fact]
    public void Delete_session_is_for_team_admins_or_the_quick_creator() {
        var team = PokerHarness.Team();
        Rejected(team.Send(Bob, new DeleteSession()), NackCode.Forbidden);
        Rejected(team.Send(null, new DeleteSession()), NackCode.Forbidden);
        Assert.IsType<SessionDeleted>(Assert.Single(team.Accepted(Dave, new DeleteSession())));
        Rejected(team.Send(Alice, new PokerVoteSet(R(1), 5)), NackCode.NotFound);

        var quick = Quick();
        Rejected(quick.Send(Bob, new DeleteSession()), NackCode.Forbidden);
        quick.Accepted(Alice, new DeleteSession());
    }
}
