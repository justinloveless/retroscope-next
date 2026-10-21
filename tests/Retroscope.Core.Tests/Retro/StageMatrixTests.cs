using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using static Retroscope.Core.Tests.Retro.RetroFixture;

namespace Retroscope.Core.Tests.Retro;

[Trait("Category", "Retro")]
public sealed class StageMatrixTests {
    public enum Action {
        AddCard,
        AddActionCard,
        EditCard,
        EditActionCard,
        DeleteCard,
        AddComment,
        AddActionComment,
        DeleteComment,
        Vote,
        Focus,
    }

    // DESIGN §c.4: rows are actions, columns thinking / voting / discussing / closed.
    public static TheoryData<Action, RetroStage, bool> Matrix() {
        var rows = new Dictionary<Action, bool[]> {
            [Action.AddCard] = [true, false, false, false],
            [Action.AddActionCard] = [true, false, true, false],
            [Action.EditCard] = [true, false, false, false],
            [Action.EditActionCard] = [true, false, true, false],
            [Action.DeleteCard] = [true, false, false, false],
            [Action.AddComment] = [true, false, false, false],
            [Action.AddActionComment] = [true, false, true, false],
            [Action.DeleteComment] = [true, false, false, false],
            [Action.Vote] = [false, true, false, false],
            [Action.Focus] = [false, false, true, false],
        };
        var data = new TheoryData<Action, RetroStage, bool>();
        foreach (var (action, allowed) in rows) {
            for (var stage = 0; stage < 4; stage++) {
                data.Add(action, (RetroStage)stage, allowed[stage]);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Stage_gates_follow_the_matrix(Action action, RetroStage stage, bool allowed) {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        var actionCard = fx.AddCard(Alice, Actions);
        var comment = fx.AddComment(Alice, card);
        fx.EnableStages(stage);

        RetroInput input = action switch {
            Action.AddCard => new RetroCardAdd(fx.NewCardId(), Good, "New", false),
            Action.AddActionCard => new RetroCardAdd(fx.NewCardId(), Actions, "New", false),
            Action.EditCard => new RetroCardEdit(card, "Changed", 1),
            Action.EditActionCard => new RetroCardEdit(actionCard, "Changed", 1),
            Action.DeleteCard => new RetroCardDelete(card),
            Action.AddComment => new RetroCommentAdd(fx.NewCommentId(), card, "New", false),
            Action.AddActionComment => new RetroCommentAdd(fx.NewCommentId(), actionCard, "New", false),
            Action.DeleteComment => new RetroCommentDelete(comment),
            Action.Vote => new RetroVoteSet(card, true),
            Action.Focus => new RetroFocusSet(card, null),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        if (allowed) {
            fx.Do(Alice, input);
        } else {
            fx.Rejects(Alice, input, NackCode.StageForbids);
        }
    }

    [Theory]
    [InlineData(Action.AddCard)]
    [InlineData(Action.Vote)]
    [InlineData(Action.Focus)]
    [InlineData(Action.DeleteComment)]
    public void With_stages_off_every_stage_allows_everything(Action action) {
        foreach (var stage in Enum.GetValues<RetroStage>()) {
            var fx = new RetroFixture();
            var card = fx.AddCard(Alice, Good);
            var comment = fx.AddComment(Alice, card);
            fx.State.Stage = stage;
            RetroInput input = action switch {
                Action.AddCard => new RetroCardAdd(fx.NewCardId(), Good, "New", false),
                Action.Vote => new RetroVoteSet(card, true),
                Action.Focus => new RetroFocusSet(card, null),
                _ => new RetroCommentDelete(comment),
            };
            fx.Do(Alice, input);
        }
    }

    [Theory]
    [InlineData(RetroStage.Thinking, false)]
    [InlineData(RetroStage.Voting, false)]
    [InlineData(RetroStage.Discussing, true)]
    [InlineData(RetroStage.Closed, true)]
    public void Voter_identities_show_from_discussing_on(RetroStage stage, bool shown) {
        var config = RetroDefaults.Config with { StagesEnabled = true };
        Assert.Equal(shown, StageRules.ShowsVoters(config, stage, archived: false));
        Assert.True(StageRules.ShowsVoters(config, stage, archived: true));
        Assert.True(StageRules.ShowsVoters(RetroDefaults.Config, stage, archived: false));
    }

    [Fact]
    public void Voting_disabled_refuses_votes_in_any_stage() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        fx.State.Config = fx.State.Config with { VotingEnabled = false };
        fx.Rejects(Bob, new RetroVoteSet(card, true), NackCode.Forbidden);

        fx.EnableStages(RetroStage.Voting);
        fx.Rejects(Bob, new RetroVoteSet(card, true), NackCode.Forbidden);
    }

    [Fact]
    public void An_archived_board_refuses_every_command() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        fx.Do(null, new RetroInput.ArchiveSet(true, Alice.Id));

        RetroCommand[] commands = [
            new RetroCardAdd(fx.NewCardId(), Good, "x", false),
            new RetroCardEdit(card, "x", 1),
            new RetroVoteSet(card, true),
            new RetroColumnAdd(fx.NewColumnId(), "x", ColumnColor.Blue),
            new RetroStageSet(RetroStage.Thinking, RetroStage.Voting),
            new RetroReadySet(RetroStage.Thinking, true),
            new RetroFocusSet(card, null),
            new RetroTimerStart(60_000, true, false, TimerState.Idle),
        ];
        foreach (var command in commands) {
            fx.Rejects(Alice, command, NackCode.BoardArchived);
        }
    }

    [Fact]
    public void Archiving_toggles_and_repeats_are_noops() {
        var fx = new RetroFixture();
        var events = fx.Do(null, new RetroInput.ArchiveSet(true, Alice.Id));
        Assert.Equal(new ArchivedChanged(true, Alice.Id, Start), Assert.Single(events));
        fx.NoOp(null, new RetroInput.ArchiveSet(true, Alice.Id));
        fx.Do(null, new RetroInput.ArchiveSet(false, Alice.Id));
        Assert.False(fx.State.Archived);
    }

    [Fact]
    public void Commands_without_a_caller_are_refused() {
        var fx = new RetroFixture();
        fx.Rejects(null, new RetroCardAdd(fx.NewCardId(), Good, "x", false), NackCode.Forbidden);
    }

    [Fact]
    public void Unknown_ids_are_not_found() {
        var fx = new RetroFixture();
        var missing = new CardId(Guid.NewGuid());
        fx.Rejects(Alice, new RetroCardAdd(fx.NewCardId(), new ColumnId(Guid.NewGuid()), "x", false), NackCode.NotFound);
        fx.Rejects(Alice, new RetroCardEdit(missing, "x", 1), NackCode.NotFound);
        fx.Rejects(Alice, new RetroCardDelete(missing), NackCode.NotFound);
        fx.Rejects(Alice, new RetroVoteSet(missing, true), NackCode.NotFound);
        fx.Rejects(Alice, new RetroCommentAdd(fx.NewCommentId(), missing, "x", false), NackCode.NotFound);
        fx.Rejects(Alice, new RetroCommentDelete(new CommentId(Guid.NewGuid())), NackCode.NotFound);
        fx.Rejects(Alice, new RetroFocusSet(missing, null), NackCode.NotFound);
    }
}
