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
    public void Every_member_facilitates_and_nobody_else_does(string kind) {
        foreach (var member in new[] { Alice, Bob }) {
            var fx = new RetroFixture();
            fx.Rejects(Carol, Command(fx, kind), NackCode.Forbidden);
            fx.Rejects(Gus, Command(fx, kind), NackCode.Forbidden);
            fx.Do(member, Command(fx, kind));
        }
    }

    [Fact]
    public void A_removed_member_loses_facilitation_on_the_next_command() {
        var fx = new RetroFixture();
        fx.Do(Bob, new RetroColumnAdd(fx.NewColumnId(), "One", ColumnColor.Blue));

        fx.Access.Roles.Remove((RetroFixture.Team, Bob.Id));
        fx.Rejects(Bob, new RetroColumnAdd(fx.NewColumnId(), "Two", ColumnColor.Blue), NackCode.Forbidden);
    }

    [Fact]
    public void Non_members_get_nothing_on_the_board() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        var comment = fx.AddComment(Alice, card);
        foreach (var who in new[] { Carol, Gus }) {
            fx.Rejects(who, new RetroCardAdd(fx.NewCardId(), Good, "Mine", false), NackCode.Forbidden);
            fx.Rejects(who, new RetroVoteSet(card, true), NackCode.Forbidden);
            fx.Rejects(who, new RetroCommentAdd(fx.NewCommentId(), card, "Hi", false), NackCode.Forbidden);
            fx.Rejects(who, new RetroCommentDelete(comment), NackCode.Forbidden);
            fx.Rejects(who, new RetroCardDelete(card), NackCode.Forbidden);
        }
    }

    [Fact]
    public void A_non_member_hears_forbidden_before_board_archived() {
        var fx = new RetroFixture();
        fx.Do(null, new RetroInput.ArchiveSet(true, Alice.Id));

        fx.Rejects(Carol, new RetroCardAdd(fx.NewCardId(), Good, "Late", false), NackCode.Forbidden);
        fx.Rejects(Bob, new RetroCardAdd(fx.NewCardId(), Good, "Late", false), NackCode.BoardArchived);
    }

    [Fact]
    public void Only_the_author_edits_or_deletes_a_card() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Dave, Good);

        fx.Rejects(Alice, new RetroCardEdit(card, "Hijack", 1), NackCode.Forbidden);
        fx.Rejects(Alice, new RetroCardDelete(card), NackCode.Forbidden);
        fx.Rejects(Gus, new RetroCardDelete(card), NackCode.Forbidden);
    }

    [Fact]
    public void Any_member_deletes_anyone_s_card_in_the_action_items_column_only() {
        var fx = new RetroFixture();
        var action = fx.AddCard(Dave, Actions);
        var good = fx.AddCard(Dave, Good);

        fx.Do(Bob, new RetroCardDelete(action));
        fx.Rejects(Bob, new RetroCardDelete(good), NackCode.Forbidden);

        var another = fx.AddCard(Bob, Actions);
        fx.Rejects(Carol, new RetroCardDelete(another), NackCode.Forbidden);
        fx.Do(Dave, new RetroCardDelete(another));
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
    public void Guests_are_never_members() {
        var fx = new RetroFixture();
        fx.Access.Roles[(RetroFixture.Team, Gus.Id)] = TeamRole.Owner;
        Assert.False(RetroRoles.IsMember(fx.State, Gus, fx.Access));
        fx.Rejects(Gus, new RetroColumnAdd(fx.NewColumnId(), "New", ColumnColor.Purple), NackCode.Forbidden);
    }
}
