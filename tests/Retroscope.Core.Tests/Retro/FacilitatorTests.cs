using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Core.Room;
using static Retroscope.Core.Tests.Retro.RetroFixture;

namespace Retroscope.Core.Tests.Retro;

[Trait("Category", "Retro")]
public sealed class FacilitatorTests {
    public static TheoryData<string> FacilitatorCommands() => [
        "column.add", "column.update", "column.delete", "column.reorder", "column.setActionItems",
        "board.rename", "config.update", "stage.set",
    ];

    private static RetroCommand Command(RetroFixture fx, string kind) {
        return kind switch {
            "column.add" => new RetroColumnAdd(fx.NewColumnId(), "New", ColumnColor.Purple),
            "column.update" => new RetroColumnUpdate(Good, 1, Title: "Great"),
            "column.delete" => new RetroColumnDelete(Bad),
            "column.reorder" => new RetroColumnReorder([Bad, Good, Actions]),
            "column.setActionItems" => new RetroColumnSetActionItems(Good),
            "board.rename" => new RetroBoardRename("Renamed", "Sprint 42 retro"),
            "config.update" => new RetroConfigUpdate(new RetroConfigPatch(VotingEnabled: false), new RetroConfigPatch(VotingEnabled: true)),
            "stage.set" => new RetroStageSet(RetroStage.Thinking, RetroStage.Voting),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    [Theory]
    [MemberData(nameof(FacilitatorCommands))]
    public void Team_boards_take_facilitation_from_members_only(string kind) {
        foreach (var visibility in Enum.GetValues<BoardVisibility>()) {
            var fx = new RetroFixture(team: true);
            fx.State.Visibility = visibility;
            fx.Rejects(Carol, Command(fx, kind), NackCode.Forbidden);
            fx.Rejects(Gus, Command(fx, kind), NackCode.Forbidden);
            fx.Do(Bob, Command(fx, kind));
        }
    }

    [Theory]
    [MemberData(nameof(FacilitatorCommands))]
    public void Quick_boards_take_facilitation_from_any_signed_in_principal(string kind) {
        var fx = new RetroFixture();
        fx.Rejects(Gus, Command(fx, kind), NackCode.Forbidden);
        fx.Do(Carol, Command(fx, kind));
    }

    [Fact]
    public void A_demoted_member_loses_facilitation_on_the_next_command() {
        var fx = new RetroFixture(team: true);
        fx.Do(Bob, new RetroColumnAdd(fx.NewColumnId(), "One", ColumnColor.Blue));

        fx.Access.Roles.Remove((RetroFixture.Team, Bob.Id));
        fx.Rejects(Bob, new RetroColumnAdd(fx.NewColumnId(), "Two", ColumnColor.Blue), NackCode.Forbidden);
    }

    [Fact]
    public void Non_members_on_a_link_team_board_add_edit_their_own_vote_and_comment() {
        var fx = new RetroFixture(team: true);
        fx.State.Visibility = BoardVisibility.Link;
        foreach (var who in new[] { Carol, Gus }) {
            var card = fx.AddCard(who, Good, "Mine");
            fx.Do(who, new RetroCardEdit(card, "Still mine", 1));
            fx.Do(who, new RetroVoteSet(card, true));
            var comment = fx.AddComment(who, card);
            fx.Do(who, new RetroCommentDelete(comment));
            fx.Do(who, new RetroCardDelete(card));
        }
    }

    [Fact]
    public void Only_the_author_edits_or_deletes_a_card() {
        var fx = new RetroFixture(team: true);
        var card = fx.AddCard(Carol, Good);

        fx.Rejects(Alice, new RetroCardEdit(card, "Hijack", 1), NackCode.Forbidden);
        fx.Rejects(Alice, new RetroCardDelete(card), NackCode.Forbidden);
        fx.Rejects(Gus, new RetroCardDelete(card), NackCode.Forbidden);
    }

    [Fact]
    public void Facilitators_delete_anyone_s_card_in_the_action_items_column_only() {
        var fx = new RetroFixture(team: true);
        var action = fx.AddCard(Carol, Actions);
        var good = fx.AddCard(Carol, Good);

        fx.Do(Bob, new RetroCardDelete(action));
        fx.Rejects(Bob, new RetroCardDelete(good), NackCode.Forbidden);

        var another = fx.AddCard(Bob, Actions);
        fx.Rejects(Carol, new RetroCardDelete(another), NackCode.Forbidden);
    }

    [Fact]
    public void Only_the_author_deletes_a_comment() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        var comment = fx.AddComment(Bob, card);
        fx.Rejects(Alice, new RetroCommentDelete(comment), NackCode.Forbidden);
        fx.Do(Bob, new RetroCommentDelete(comment));
    }

    [Fact]
    public void Guests_are_never_facilitators() {
        var principal = Gus;
        var fx = new RetroFixture(team: true);
        fx.Access.Roles[(RetroFixture.Team, principal.Id)] = TeamRole.Owner;
        Assert.False(RetroRoles.IsFacilitator(fx.State, principal, fx.Access));
    }
}
