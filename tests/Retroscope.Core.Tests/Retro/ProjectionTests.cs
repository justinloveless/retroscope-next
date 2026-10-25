using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using static Retroscope.Core.Tests.Retro.RetroFixture;

namespace Retroscope.Core.Tests.Retro;

[Trait("Category", "Retro")]
public sealed class ProjectionTests {
    [Fact]
    public void Every_viewer_gets_the_roster_and_endorsements() {
        var fx = new RetroFixture();
        fx.Do(Alice, new RetroEndorseGive(fx.NewEndorsementId(), Bob.Id, Helper));

        var snapshot = RetroProjection.Snapshot(fx.State, []);
        Assert.Equal(RetroFixture.Team, snapshot.TeamId);
        Assert.Equal([Alice.Id, Bob.Id, Dave.Id], snapshot.Members.Select(m => m.Pid));
        Assert.Single(snapshot.Endorsements);
        Assert.Equal(2, snapshot.EndorsementTypes.Count);
        Assert.Equal(3, snapshot.MaxEndorsements);
    }

    [Fact]
    public void Endorsement_events_celebrate_for_the_recipient_only() {
        var fx = new RetroFixture();
        var id = fx.NewEndorsementId();
        var events = fx.Do(Alice, new RetroEndorseGive(id, Bob.Id, Helper));

        var toBob = Assert.IsType<RetroEndorsementAdded>(Assert.Single(fx.Projection.ForViewer(fx.State, events, Bob.Id)));
        Assert.True(toBob.Celebrate);
        var toAlice = Assert.IsType<RetroEndorsementAdded>(Assert.Single(fx.Projection.ForViewer(fx.State, events, Alice.Id)));
        Assert.Null(toAlice.Celebrate);
        var toDave = Assert.IsType<RetroEndorsementAdded>(Assert.Single(fx.Projection.ForViewer(fx.State, events, Dave.Id)));
        Assert.Null(toDave.Celebrate);

        var settings = fx.Do(null, new RetroInput.TeamSettingsChanged(5));
        Assert.Equal([new TeamSettingsChanged(MaxEndorsements: 5)], fx.Projection.ForViewer(fx.State, settings, Dave.Id));
    }

    [Fact]
    public void Anonymous_cards_carry_no_author_and_mine_only_for_the_author() {
        var fx = new RetroFixture();
        var events = fx.Do(Alice, new RetroCardAdd(fx.NewCardId(), Good, "Psst", true));

        var toAlice = Assert.IsType<RetroCardAdded>(Assert.Single(fx.Projection.ForViewer(fx.State, events, Alice.Id)));
        Assert.Null(toAlice.Card.Author);
        Assert.True(toAlice.Card.Anonymous);
        Assert.True(toAlice.Card.Mine);
        var toBob = Assert.IsType<RetroCardAdded>(Assert.Single(fx.Projection.ForViewer(fx.State, events, Bob.Id)));
        Assert.Null(toBob.Card.Author);
        Assert.False(toBob.Card.Mine);

        var snapshot = RetroProjection.Snapshot(fx.State, []);
        Assert.Null(Assert.Single(snapshot.Cards).Author);
        Assert.Null(snapshot.Cards[0].Mine);
        Assert.Single(fx.Projection.Overlay(fx.State, Alice.Id).MineCards);
        Assert.Empty(fx.Projection.Overlay(fx.State, Bob.Id).MineCards);
    }

    [Fact]
    public void Nothing_sent_about_an_anonymous_post_names_its_author() {
        var fx = new RetroFixture();
        fx.Present = [Bob, Dave];
        var events = new List<RetroEvent>();
        var card = fx.NewCardId();
        events.AddRange(fx.Do(Alice, new RetroCardAdd(card, Actions, "Psst", true)));
        events.AddRange(fx.Do(Alice, new RetroCommentAdd(fx.NewCommentId(), card, "Me again", true)));
        events.AddRange(fx.Do(Alice, new RetroCardEdit(card, "Psst!", 1)));
        events.AddRange(fx.Do(Bob, new RetroConfigUpdate(new RetroConfigPatch(ShowAuthorNames: false), new RetroConfigPatch(ShowAuthorNames: true))));
        events.AddRange(fx.Do(Bob, new RetroConfigUpdate(new RetroConfigPatch(ShowAuthorNames: true), new RetroConfigPatch(ShowAuthorNames: false))));

        var alice = Alice.Id.ToString();
        foreach (var viewer in new[] { Bob.Id, Dave.Id }) {
            foreach (var wire in fx.Projection.ForViewer(fx.State, events, viewer)) {
                Assert.DoesNotContain(alice, Json(wire), StringComparison.OrdinalIgnoreCase);
            }
        }
        // The roster names every member; nothing else in the snapshot may name the anonymous author.
        var snapshot = RetroProjection.Snapshot(fx.State, []) with { Members = [] };
        Assert.DoesNotContain(alice, Json(snapshot), StringComparison.OrdinalIgnoreCase);
        Assert.Null(Assert.IsType<CardAdded>(events[0]).ActionItem!.CreatedBy);
    }

    [Fact]
    public void Turning_author_names_off_hides_them_and_resends_every_post() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        fx.AddComment(Bob, card);

        var events = fx.Do(Alice, new RetroConfigUpdate(new RetroConfigPatch(ShowAuthorNames: false), new RetroConfigPatch(ShowAuthorNames: true)));
        var wire = fx.Projection.ForViewer(fx.State, events, Alice.Id);

        Assert.IsType<RetroConfigChanged>(wire[0]);
        var resent = Assert.IsType<RetroCardAdded>(wire[1]);
        Assert.Null(resent.Card.Author);
        Assert.True(resent.Card.Mine);
        Assert.Null(Assert.IsType<RetroCommentAdded>(wire[2]).Comment.Author);
        Assert.Null(RetroProjection.Snapshot(fx.State, []).Cards[0].Author);
    }

    [Fact]
    public void Voters_stay_hidden_until_discussion_and_appear_with_the_stage_change() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        fx.EnableStages(RetroStage.Voting);

        var vote = fx.Do(Bob, new RetroVoteSet(card, true));
        var toDave = Assert.IsType<RetroVoteChanged>(Assert.Single(fx.Projection.ForViewer(fx.State, vote, Dave.Id)));
        Assert.Equal(new RetroVoteChanged(card, 1, null, false), toDave);
        var toBob = Assert.IsType<RetroVoteChanged>(Assert.Single(fx.Projection.ForViewer(fx.State, vote, Bob.Id)));
        Assert.True(toBob.Mine);
        Assert.Null(Assert.Single(RetroProjection.Snapshot(fx.State, []).Votes).Voters);

        var stage = fx.Do(Alice, new RetroStageSet(RetroStage.Voting, RetroStage.Discussing));
        var wire = fx.Projection.ForViewer(fx.State, stage, Dave.Id);
        AssertWire(new RetroVoteChanged(card, 1, [Bob.Id], false), wire[^1]);
        Assert.Equal([Bob.Id], Assert.Single(RetroProjection.Snapshot(fx.State, []).Votes).Voters);
    }

    [Fact]
    public void Archiving_shows_voters() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        fx.EnableStages(RetroStage.Voting);
        fx.Do(Bob, new RetroVoteSet(card, true));

        var events = fx.Do(null, new RetroInput.ArchiveSet(true, Alice.Id));
        var wire = fx.Projection.ForViewer(fx.State, events, Dave.Id);
        Assert.Equal(new RetroBoardMeta("Sprint 42 retro", true), wire[0]);
        AssertWire(new RetroVoteChanged(card, 1, [Bob.Id], false), wire[1]);
    }

    [Fact]
    public void A_rename_projects_the_board_meta() {
        var fx = new RetroFixture();
        var events = fx.Do(Bob, new RetroBoardRename("Renamed", "Sprint 42 retro"));
        Assert.Equal([new RetroBoardMeta("Renamed", false)], fx.Projection.ForViewer(fx.State, events, Dave.Id));
    }

    [Fact]
    public void Membership_updates_reach_every_viewer_as_retro_members() {
        var fx = new RetroFixture();
        var events = fx.Do(null, new RetroInput.MembersChanged([new MemberInfo(Bob.Id, "Bob", Room.TeamRole.Owner)]));

        foreach (var viewer in new[] { Alice.Id, Bob.Id }) {
            var members = Assert.IsType<RetroMembers>(Assert.Single(fx.Projection.ForViewer(fx.State, events, viewer)));
            Assert.Equal(RetroProjection.Snapshot(fx.State, []).Members, members.Members);
            Assert.Equal([Bob.Id], members.Members.Select(m => m.Pid));
        }
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
