using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using static Retroscope.Core.Tests.Retro.RetroFixture;

namespace Retroscope.Core.Tests.Retro;

[Trait("Category", "Retro")]
public sealed class ActionItemCommentTests {
    [Fact]
    public void A_member_comments_on_the_source_card_from_the_team_page() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Actions, "Fix the build");
        var id = fx.NewCommentId();

        var added = Assert.IsType<CommentAdded>(Assert.Single(fx.Do(Bob, new RetroInput.ActionItemCommentAdd(id, card, "On it"))));

        Assert.Equal(card, added.CardId);
        Assert.Equal(Bob.Id, added.Authorship.Author!.Pid);
        Assert.False(added.Authorship.Anonymous);
        Assert.Equal("On it", fx.State.Comments[id].Body);
        var wire = Assert.IsType<RetroCommentAdded>(Assert.Single(fx.Projection.ForViewer(fx.State, [added], Alice.Id)));
        Assert.Equal(id, wire.Comment.Id);
    }

    [Fact]
    public void Neither_the_stage_gate_nor_the_archive_applies() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Actions);
        fx.EnableStages(RetroStage.Closed);
        fx.Do(null, new RetroInput.ArchiveSet(true, Alice.Id));

        fx.Rejects(Bob, new RetroCommentAdd(fx.NewCommentId(), card, "Late", false), NackCode.BoardArchived);
        fx.Do(Bob, new RetroInput.ActionItemCommentAdd(fx.NewCommentId(), card, "Late"));
    }

    [Fact]
    public void Only_current_members_comment_and_the_usual_checks_apply() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Actions);
        var taken = fx.NewCommentId();
        fx.Do(Bob, new RetroInput.ActionItemCommentAdd(taken, card, "First"));

        fx.Rejects(null, new RetroInput.ActionItemCommentAdd(fx.NewCommentId(), card, "Hi"), NackCode.Forbidden);
        fx.Rejects(Carol, new RetroInput.ActionItemCommentAdd(fx.NewCommentId(), card, "Hi"), NackCode.Forbidden);
        fx.Rejects(Gus, new RetroInput.ActionItemCommentAdd(fx.NewCommentId(), card, "Hi"), NackCode.Forbidden);
        fx.Rejects(Bob, new RetroInput.ActionItemCommentAdd(taken, card, "Again"), NackCode.Invalid);
        fx.Rejects(Bob, new RetroInput.ActionItemCommentAdd(fx.NewCommentId(), fx.NewCardId(), "Hi"), NackCode.NotFound);
        fx.Rejects(Bob, new RetroInput.ActionItemCommentAdd(fx.NewCommentId(), card, "  "), NackCode.Invalid);

        fx.Access.Roles.Remove((RetroFixture.Team, Bob.Id));
        fx.Rejects(Bob, new RetroInput.ActionItemCommentAdd(fx.NewCommentId(), card, "Hi"), NackCode.Forbidden);
    }
}
