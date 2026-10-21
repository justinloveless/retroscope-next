using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using static Retroscope.Core.Tests.Retro.RetroFixture;

namespace Retroscope.Core.Tests.Retro;

[Trait("Category", "Retro")]
public sealed class ColumnTests {
    [Fact]
    public void New_columns_go_after_the_highest_sort_key() {
        var fx = new RetroFixture();
        fx.Do(Alice, new RetroColumnDelete(Bad));
        var id = fx.NewColumnId();
        var added = Assert.IsType<ColumnAdded>(Assert.Single(fx.Do(Alice, new RetroColumnAdd(id, "Ideas", ColumnColor.Purple))));
        Assert.Equal(3, added.SortKey);
        Assert.Equal([Good, Actions, id], fx.State.Columns.Select(c => c.Id));
        Assert.Equal(1, fx.State.FindColumn(id)!.Version);
    }

    [Fact]
    public void Column_titles_are_checked() {
        var fx = new RetroFixture();
        fx.Rejects(Alice, new RetroColumnAdd(fx.NewColumnId(), "", ColumnColor.Blue), NackCode.Invalid);
        fx.Rejects(Alice, new RetroColumnAdd(fx.NewColumnId(), new string('x', 101), ColumnColor.Blue), NackCode.Invalid);
        fx.Rejects(Alice, new RetroColumnAdd(Good, "Dup", ColumnColor.Blue), NackCode.Invalid);
        fx.Rejects(Alice, new RetroColumnUpdate(Good, 1, Title: "\t"), NackCode.Invalid);
    }

    [Fact]
    public void Update_needs_the_base_version_and_bumps_it() {
        var fx = new RetroFixture();
        fx.Do(Alice, new RetroColumnUpdate(Good, 1, Title: "Went well"));
        fx.Do(Bob, new RetroColumnUpdate(Good, 2, Color: ColumnColor.Indigo));
        Assert.Equal(new ColumnInfo(Good, "Went well", ColumnColor.Indigo, false, 3), fx.State.FindColumn(Good)!.ToInfo());

        var reject = fx.Rejects(Alice, new RetroColumnUpdate(Good, 2, Title: "Late"), NackCode.Stale);
        var current = reject.Data!.Value.GetProperty("current");
        Assert.Equal("Went well", current.GetProperty("title").GetString());
        Assert.Equal(3, current.GetProperty("version").GetInt32());

        fx.NoOp(Alice, new RetroColumnUpdate(Good, 3, Title: "Went well", Color: ColumnColor.Indigo));
        fx.Rejects(Alice, new RetroColumnUpdate(new ColumnId(Guid.NewGuid()), 1, Title: "x"), NackCode.NotFound);
    }

    [Fact]
    public void Deleting_a_column_cascades_to_its_cards_and_comments() {
        var fx = new RetroFixture();
        var doomed = fx.AddCard(Alice, Bad);
        var doomedComment = fx.AddComment(Bob, doomed);
        var kept = fx.AddCard(Alice, Good);

        var deleted = Assert.IsType<ColumnDeleted>(Assert.Single(fx.Do(Alice, new RetroColumnDelete(Bad))));
        Assert.Equal([doomed], deleted.Cards);
        Assert.Equal([doomedComment], deleted.Comments);
        Assert.Equal([kept], fx.State.Cards.Keys);
        Assert.Empty(fx.State.Comments);
        Assert.Null(fx.State.FindColumn(Bad));
    }

    [Fact]
    public void Reorder_must_be_a_permutation_of_the_current_columns() {
        var fx = new RetroFixture();
        fx.Do(Alice, new RetroColumnReorder([Actions, Good, Bad]));
        Assert.Equal([Actions, Good, Bad], fx.State.Columns.Select(c => c.Id));
        Assert.Equal([0, 1, 2], fx.State.Columns.Select(c => c.SortKey));
        fx.NoOp(Alice, new RetroColumnReorder([Actions, Good, Bad]));

        ColumnId[][] bad = [[Actions, Good], [Actions, Good, Good], [Actions, Good, Bad, fx.NewColumnId()], [Actions, Good, fx.NewColumnId()]];
        foreach (var order in bad) {
            var reject = fx.Rejects(Alice, new RetroColumnReorder(order), NackCode.Stale);
            var current = reject.Data!.Value.GetProperty("current").EnumerateArray().Select(x => x.GetGuid()).ToList();
            Assert.Equal([Actions.Value, Good.Value, Bad.Value], current);
        }
    }

    [Fact]
    public void At_most_one_column_holds_action_items() {
        var fx = new RetroFixture();
        Assert.Equal(new ActionItemsColumnSet(Good), Assert.Single(fx.Do(Alice, new RetroColumnSetActionItems(Good))));
        Assert.Equal([Good], fx.State.Columns.Where(c => c.ActionItems).Select(c => c.Id));

        fx.NoOp(Alice, new RetroColumnSetActionItems(Good));
        fx.Do(Alice, new RetroColumnSetActionItems(null));
        Assert.Null(fx.State.ActionItemsColumn);
        fx.NoOp(Alice, new RetroColumnSetActionItems(null));
        fx.Rejects(Alice, new RetroColumnSetActionItems(fx.NewColumnId()), NackCode.NotFound);
    }
}
