using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using static Retroscope.Core.Tests.Retro.RetroFixture;

namespace Retroscope.Core.Tests.Retro;

[Trait("Category", "Retro")]
public sealed class ProjectionTests {
    [Fact]
    public void The_roster_and_endorsements_reach_team_members_only() {
        var fx = new RetroFixture(team: true);
        fx.Do(Alice, new RetroEndorseGive(fx.NewEndorsementId(), Bob.Id, Helper));

        var members = RetroProjection.Snapshot(fx.State, [], forMembers: true);
        Assert.Equal([Alice.Id, Bob.Id], members.Members!.Select(m => m.Pid));
        Assert.Single(members.Endorsements!);
        Assert.Equal(2, members.EndorsementTypes!.Count);
        Assert.Equal(3, members.MaxEndorsements);

        var others = RetroProjection.Snapshot(fx.State, [], forMembers: false);
        Assert.Null(others.Members);
        Assert.Null(others.Endorsements);
        Assert.Null(others.EndorsementTypes);
        Assert.Null(others.MaxEndorsements);
    }

    [Fact]
    public void Quick_boards_have_no_roster_for_anyone() {
        var fx = new RetroFixture();
        Assert.Null(RetroProjection.Snapshot(fx.State, [], forMembers: true).Members);
        Assert.False(RetroProjection.SeesRoster(fx.State, viewerIsMember: true));
    }

    [Fact]
    public void Endorsement_events_are_redacted_for_non_members_and_celebrate_for_the_recipient() {
        var fx = new RetroFixture(team: true);
        var id = fx.NewEndorsementId();
        var events = fx.Do(Alice, new RetroEndorseGive(id, Bob.Id, Helper));

        Assert.Empty(fx.Projection.ForViewer(fx.State, events, Carol.Id, viewerIsMember: false));
        var toBob = Assert.IsType<RetroEndorsementAdded>(Assert.Single(fx.Projection.ForViewer(fx.State, events, Bob.Id, true)));
        Assert.True(toBob.Celebrate);
        var toAlice = Assert.IsType<RetroEndorsementAdded>(Assert.Single(fx.Projection.ForViewer(fx.State, events, Alice.Id, true)));
        Assert.Null(toAlice.Celebrate);

        var settings = fx.Do(null, new RetroInput.TeamSettingsChanged(5));
        Assert.Empty(fx.Projection.ForViewer(fx.State, settings, Gus.Id, false));
        Assert.Equal([new TeamSettingsChanged(MaxEndorsements: 5)], fx.Projection.ForViewer(fx.State, settings, Bob.Id, true));
    }

    [Fact]
    public void Anonymous_cards_carry_no_author_and_mine_only_for_the_author() {
        var fx = new RetroFixture();
        var events = fx.Do(Alice, new RetroCardAdd(fx.NewCardId(), Good, "Psst", true));

        var toAlice = Assert.IsType<RetroCardAdded>(Assert.Single(fx.Projection.ForViewer(fx.State, events, Alice.Id, false)));
        Assert.Null(toAlice.Card.Author);
        Assert.True(toAlice.Card.Anonymous);
        Assert.True(toAlice.Card.Mine);
        var toBob = Assert.IsType<RetroCardAdded>(Assert.Single(fx.Projection.ForViewer(fx.State, events, Bob.Id, false)));
        Assert.Null(toBob.Card.Author);
        Assert.False(toBob.Card.Mine);

        var snapshot = RetroProjection.Snapshot(fx.State, [], forMembers: false);
        Assert.Null(Assert.Single(snapshot.Cards).Author);
        Assert.Null(snapshot.Cards[0].Mine);
        Assert.Single(fx.Projection.Overlay(fx.State, Alice.Id).MineCards);
        Assert.Empty(fx.Projection.Overlay(fx.State, Bob.Id).MineCards);
    }

    [Fact]
    public void Nothing_sent_about_an_anonymous_post_names_its_author() {
        var fx = new RetroFixture(team: true);
        fx.Present = [Bob, Carol];
        var events = new List<RetroEvent>();
        var card = fx.NewCardId();
        events.AddRange(fx.Do(Alice, new RetroCardAdd(card, Actions, "Psst", true)));
        events.AddRange(fx.Do(Alice, new RetroCommentAdd(fx.NewCommentId(), card, "Me again", true)));
        events.AddRange(fx.Do(Alice, new RetroCardEdit(card, "Psst!", 1)));
        events.AddRange(fx.Do(Bob, new RetroConfigUpdate(new RetroConfigPatch(ShowAuthorNames: false), new RetroConfigPatch(ShowAuthorNames: true))));
        events.AddRange(fx.Do(Bob, new RetroConfigUpdate(new RetroConfigPatch(ShowAuthorNames: true), new RetroConfigPatch(ShowAuthorNames: false))));

        var alice = Alice.Id.ToString();
        foreach (var (viewer, member) in new[] { (Bob.Id, true), (Carol.Id, false) }) {
            foreach (var wire in fx.Projection.ForViewer(fx.State, events, viewer, member)) {
                Assert.DoesNotContain(alice, Json(wire), StringComparison.OrdinalIgnoreCase);
            }
            var snapshot = RetroProjection.Snapshot(fx.State, [], member) with { Members = null };
            Assert.DoesNotContain(alice, Json(snapshot), StringComparison.OrdinalIgnoreCase);
        }
        Assert.Null(Assert.IsType<CardAdded>(events[0]).ActionItem!.CreatedBy);
    }

    [Fact]
    public void Turning_author_names_off_hides_them_and_resends_every_post() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        fx.AddComment(Bob, card);

        var events = fx.Do(Alice, new RetroConfigUpdate(new RetroConfigPatch(ShowAuthorNames: false), new RetroConfigPatch(ShowAuthorNames: true)));
        var wire = fx.Projection.ForViewer(fx.State, events, Alice.Id, false);

        Assert.IsType<RetroConfigChanged>(wire[0]);
        var resent = Assert.IsType<RetroCardAdded>(wire[1]);
        Assert.Null(resent.Card.Author);
        Assert.True(resent.Card.Mine);
        Assert.Null(Assert.IsType<RetroCommentAdded>(wire[2]).Comment.Author);
        Assert.Null(RetroProjection.Snapshot(fx.State, [], false).Cards[0].Author);
    }

    [Fact]
    public void Voters_stay_hidden_until_discussion_and_appear_with_the_stage_change() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        fx.EnableStages(RetroStage.Voting);

        var vote = fx.Do(Bob, new RetroVoteSet(card, true));
        var toCarol = Assert.IsType<RetroVoteChanged>(Assert.Single(fx.Projection.ForViewer(fx.State, vote, Carol.Id, false)));
        Assert.Equal(new RetroVoteChanged(card, 1, null, false), toCarol);
        var toBob = Assert.IsType<RetroVoteChanged>(Assert.Single(fx.Projection.ForViewer(fx.State, vote, Bob.Id, false)));
        Assert.True(toBob.Mine);
        Assert.Null(Assert.Single(RetroProjection.Snapshot(fx.State, [], false).Votes).Voters);

        var stage = fx.Do(Alice, new RetroStageSet(RetroStage.Voting, RetroStage.Discussing));
        var wire = fx.Projection.ForViewer(fx.State, stage, Carol.Id, false);
        AssertWire(new RetroVoteChanged(card, 1, [Bob.Id], false), wire[^1]);
        Assert.Equal([Bob.Id], Assert.Single(RetroProjection.Snapshot(fx.State, [], false).Votes).Voters);
    }

    [Fact]
    public void Archiving_shows_voters() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        fx.EnableStages(RetroStage.Voting);
        fx.Do(Bob, new RetroVoteSet(card, true));

        var events = fx.Do(null, new RetroInput.ArchiveSet(true, Alice.Id));
        var wire = fx.Projection.ForViewer(fx.State, events, Carol.Id, false);
        Assert.Equal(new RetroBoardMeta("Sprint 42 retro", BoardVisibility.Link, true), wire[0]);
        AssertWire(new RetroVoteChanged(card, 1, [Bob.Id], false), wire[1]);
    }

    [Fact]
    public void Access_changes_project_board_meta() {
        var fx = new RetroFixture(team: true);
        var events = fx.Do(null, new RetroInput.AccessSet(BoardVisibility.Password, Start));
        Assert.Equal([new RetroBoardMeta("Sprint 42 retro", BoardVisibility.Password, false)], fx.Projection.ForViewer(fx.State, events, Bob.Id, true));
        fx.NoOp(null, new RetroInput.AccessSet(BoardVisibility.Password, Start));
    }

    [Fact]
    public void Membership_updates_change_the_roster_without_a_wire_event() {
        var fx = new RetroFixture(team: true);
        var events = fx.Do(null, new RetroInput.MembersChanged([new MemberInfo(Bob.Id, "Bob", Room.TeamRole.Owner)]));
        Assert.Empty(fx.Projection.ForViewer(fx.State, events, Bob.Id, true));
        Assert.Equal([Bob.Id], RetroProjection.Snapshot(fx.State, [], true).Members!.Select(m => m.Pid));
    }

    [Fact]
    public void The_overlay_lists_the_viewer_s_cards_comments_and_votes() {
        var fx = new RetroFixture();
        var mine = fx.AddCard(Bob, Good);
        var theirs = fx.AddCard(Alice, Good);
        var comment = fx.AddComment(Bob, theirs, anonymous: true);
        fx.Do(Bob, new RetroVoteSet(theirs, true));

        Assert.Equal(new RetroOverlay([mine], [comment], [theirs]), fx.Projection.Overlay(fx.State, Bob.Id), OverlayComparer.Instance);
    }

    private sealed class OverlayComparer : IEqualityComparer<RetroOverlay> {
        public static readonly OverlayComparer Instance = new();

        public bool Equals(RetroOverlay? x, RetroOverlay? y) {
            return x!.MineCards.SequenceEqual(y!.MineCards)
                && x.MineComments.SequenceEqual(y.MineComments)
                && x.MyVotes.SequenceEqual(y.MyVotes);
        }

        public int GetHashCode(RetroOverlay obj) => 0;
    }
}
