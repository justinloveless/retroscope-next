using FsCheck.Xunit;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Team;

namespace Retroscope.Core.Tests.Team;

[Trait("Category", "Team")]
public sealed class ActionItemSyncTests {
    private readonly TeamFixture _team = new();

    [Fact]
    public void A_created_item_is_added() {
        var item = TeamFixture.NewItem(new CardId(Guid.CreateVersion7())) with { CreatedBy = null };

        var added = TeamFixture.Single<TeamEvent.ActionItemAdded>(_team.Run(new TeamInput.ActionItemCreated(item)));

        Assert.Equal(item, added.Item);
        Assert.Equal(item, _team.State.ActionItems[item.Id]);
    }

    [Fact]
    public void ActionItemCreated_is_idempotent_by_id() {
        var item = TeamFixture.NewItem(new CardId(Guid.CreateVersion7()));
        _team.Run(new TeamInput.ActionItemCreated(item));

        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.ActionItemCreated(item)));
        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.ActionItemCreated(item with { Text = "other" })));
    }

    [Fact]
    public void ActionItemCreated_after_a_reload_that_already_loaded_the_row_is_a_no_op() {
        // The board actor committed the row, the team actor was evicted and reloaded it, then the post arrived.
        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.ActionItemCreated(_team.Item)));
        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.ActionItemCreated(
            _team.Item with { Id = new ActionItemId(Guid.CreateVersion7()) })));
    }

    [Fact]
    public void Newer_card_versions_replace_the_text() {
        var card = _team.Item.SourceCardId!.Value;

        var synced = TeamFixture.Single<TeamEvent.ActionItemTextSynced>(
            _team.Run(new TeamInput.SyncActionText(card, "Fix it today", _team.Item.SourceVersion + 1)));

        Assert.Equal(new TeamEvent.ActionItemTextSynced(_team.Item.Id, "Fix it today", _team.Item.SourceVersion + 1), synced);
        Assert.Equal("Fix it today", _team.State.ActionItems[_team.Item.Id].Text);
        Assert.Equal(_team.Item.SourceVersion + 1, _team.State.ActionItems[_team.Item.Id].SourceVersion);
    }

    [Fact]
    public void Older_or_equal_card_versions_are_ignored() {
        var card = _team.Item.SourceCardId!.Value;
        _team.Run(new TeamInput.SyncActionText(card, "v5", 5));

        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.SyncActionText(card, "v5 again", 5)));
        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.SyncActionText(card, "v3", 3)));
        Assert.Equal("v5", _team.State.ActionItems[_team.Item.Id].Text);
    }

    [Fact]
    public void Text_for_an_unknown_card_is_ignored() {
        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.SyncActionText(new CardId(Guid.CreateVersion7()), "x", 9)));
    }

    [Property]
    public bool Text_sync_is_monotonic_whatever_the_delivery_order(byte[] raw) {
        var team = new TeamFixture();
        var card = team.Item.SourceCardId!.Value;
        var delivered = raw.Select(x => x % 40 + 1).ToList();
        var seen = new List<int>();
        foreach (var version in delivered) {
            team.Run(new TeamInput.SyncActionText(card, $"text {version}", version));
            seen.Add(team.State.ActionItems[team.Item.Id].SourceVersion);
        }
        var highest = delivered.Append(team.Item.SourceVersion).Max();
        var final = team.State.ActionItems[team.Item.Id];
        var expectedText = highest > team.Item.SourceVersion ? $"text {highest}" : team.Item.Text;
        return final.SourceVersion == highest
            && final.Text == expectedText
            && seen.Zip(seen.Skip(1)).All(x => x.First <= x.Second);
    }

    [Fact]
    public void Deleting_the_source_card_detaches_the_item_and_keeps_its_text() {
        var card = _team.Item.SourceCardId!.Value;

        var detached = TeamFixture.Single<TeamEvent.ActionItemSourceDetached>(_team.Run(new TeamInput.SourceCardDeleted(card)));

        Assert.Equal(_team.Item with { SourceCardId = null }, detached.Item);
        Assert.Equal(_team.Item.Text, _team.State.ActionItems[_team.Item.Id].Text);
        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.SourceCardDeleted(card)));
        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.SyncActionText(card, "late edit", 99)));
    }

    [Fact]
    public void Comment_counts_change_only_when_they_differ() {
        var counted = TeamFixture.Single<TeamEvent.ActionItemCommentsCounted>(
            _team.Run(new TeamInput.ActionItemCommentsChanged(_team.Item.Id, 2)));

        Assert.Equal(2, counted.Count);
        Assert.Equal(2, _team.State.ActionItems[_team.Item.Id].CommentCount);
        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.ActionItemCommentsChanged(_team.Item.Id, 2)));
        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.ActionItemCommentsChanged(new ActionItemId(Guid.CreateVersion7()), 1)));
        TeamFixture.Rejected(_team.Decide(new TeamInput.ActionItemCommentsChanged(_team.Item.Id, -1)), NackCode.Invalid);
    }

    [Fact]
    public void Board_and_poker_notices_pass_through() {
        var board = new BoardId(Guid.CreateVersion7());
        var session = new SessionId(Guid.CreateVersion7());

        Assert.Equal(board, TeamFixture.Single<TeamEvent.BoardsChanged>(_team.Decide(new TeamInput.BoardsChanged(board))).BoardId);
        Assert.Equal(
            new TeamEvent.PokerChanged(session, 3, 5),
            TeamFixture.Single<TeamEvent.PokerChanged>(_team.Decide(new TeamInput.PokerChanged(session, 3, 5))));
    }
}
