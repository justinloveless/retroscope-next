using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using static Retroscope.Core.Tests.Retro.RetroFixture;

namespace Retroscope.Core.Tests.Retro;

/// <summary>A create resent under a new q after a cid_reuse renumbering must not apply twice.</summary>
[Trait("Category", "Retro")]
public sealed class IdempotentCreateTests {
    [Fact]
    public void A_repeated_card_acks_and_another_payload_is_refused() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good, "Hello", anonymous: true);

        fx.NoOp(Alice, new RetroCardAdd(card, Good, "Hello", true));

        Assert.Single(fx.State.Cards);
        fx.Rejects(Alice, new RetroCardAdd(card, Good, "Hello!", true), NackCode.Invalid);
        fx.Rejects(Alice, new RetroCardAdd(card, Bad, "Hello", true), NackCode.Invalid);
        fx.Rejects(Alice, new RetroCardAdd(card, Good, "Hello", false), NackCode.Invalid);
        fx.Rejects(Bob, new RetroCardAdd(card, Good, "Hello", true), NackCode.Invalid);
    }

    [Fact]
    public void A_repeated_card_acks_after_an_edit_and_after_the_stage_closed_adding() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good, "Hello");
        fx.Do(Alice, new RetroCardEdit(card, "Hello again", 1));
        fx.EnableStages(RetroStage.Discussing);

        fx.NoOp(Alice, new RetroCardAdd(card, Good, "Hello", false));
    }

    [Fact]
    public void A_repeated_action_item_card_creates_no_second_action_item() {
        var fx = new RetroFixture(team: true);
        var card = fx.AddCard(Alice, Actions, "Fix CI");

        fx.NoOp(Alice, new RetroCardAdd(card, Actions, "Fix CI", false));
    }

    [Fact]
    public void A_repeated_comment_acks_and_another_payload_is_refused() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        var comment = fx.AddComment(Bob, card, "Agreed");

        fx.NoOp(Bob, new RetroCommentAdd(comment, card, "Agreed", false));

        Assert.Single(fx.State.Comments);
        fx.Rejects(Bob, new RetroCommentAdd(comment, card, "Agreed!", false), NackCode.Invalid);
        fx.Rejects(Alice, new RetroCommentAdd(comment, card, "Agreed", false), NackCode.Invalid);
    }

    [Fact]
    public void A_repeated_action_item_comment_acks() {
        var fx = new RetroFixture(team: true);
        var card = fx.AddCard(Alice, Actions);
        var comment = fx.NewCommentId();
        fx.Do(Bob, new RetroInput.ActionItemCommentAdd(comment, card, "Done?"));

        fx.NoOp(Bob, new RetroInput.ActionItemCommentAdd(comment, card, "Done?"));

        fx.Rejects(Alice, new RetroInput.ActionItemCommentAdd(comment, card, "Done?"), NackCode.Invalid);
    }

    [Fact]
    public void A_repeated_column_acks_until_it_differs() {
        var fx = new RetroFixture();
        var column = fx.NewColumnId();
        fx.Do(Alice, new RetroColumnAdd(column, "Ideas", ColumnColor.Blue));
        var count = fx.State.Columns.Count;

        fx.NoOp(Alice, new RetroColumnAdd(column, "Ideas", ColumnColor.Blue));
        fx.NoOp(Bob, new RetroColumnAdd(column, "Ideas", ColumnColor.Blue));

        Assert.Equal(count, fx.State.Columns.Count);
        fx.Rejects(Alice, new RetroColumnAdd(column, "Ideas", ColumnColor.Red), NackCode.Invalid);
        fx.Rejects(Alice, new RetroColumnAdd(Good, "Ideas", ColumnColor.Blue), NackCode.Invalid);

        fx.Do(Alice, new RetroColumnUpdate(column, 1, Title: "Thoughts"));
        fx.NoOp(Alice, new RetroColumnAdd(column, "Ideas", ColumnColor.Blue));
    }

    [Fact]
    public void A_repeated_endorsement_acks_even_after_its_type_was_archived() {
        var fx = new RetroFixture(team: true);
        var id = fx.NewEndorsementId();
        fx.Do(Alice, new RetroEndorseGive(id, Bob.Id, Helper));
        fx.State.EndorsementTypes[0] = fx.State.EndorsementTypes[0] with { Archived = true };

        fx.NoOp(Alice, new RetroEndorseGive(id, Bob.Id, Helper));

        Assert.Single(fx.State.Endorsements);
        fx.Rejects(Bob, new RetroEndorseGive(id, Bob.Id, Helper), NackCode.Invalid);
    }
}
