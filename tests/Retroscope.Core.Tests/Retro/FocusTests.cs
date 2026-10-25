using System.Text.Json;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using static Retroscope.Core.Tests.Retro.RetroFixture;

namespace Retroscope.Core.Tests.Retro;

[Trait("Category", "Retro")]
public sealed class FocusTests {
    [Fact]
    public void Only_members_move_the_focus() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Dave, Good);
        fx.EnableStages(RetroStage.Discussing);

        fx.Rejects(Carol, new RetroFocusSet(card, null), NackCode.Forbidden);
        fx.Rejects(Gus, new RetroFocusSet(card, null), NackCode.Forbidden);
        fx.Do(Bob, new RetroFocusSet(card, null));
    }

    [Fact]
    public void Any_member_moves_the_focus_with_compare_and_set() {
        var fx = new RetroFixture();
        fx.EnableStages(RetroStage.Discussing);
        var first = fx.AddCard(Alice, Actions);
        var second = fx.AddCard(Alice, Actions);

        fx.Do(Dave, new RetroFocusSet(first, null));
        fx.Do(Bob, new RetroFocusSet(second, first));
        Assert.Equal(second, fx.State.Focus);
        fx.NoOp(Bob, new RetroFocusSet(second, second));

        var reject = fx.Rejects(Bob, new RetroFocusSet(null, first), NackCode.Stale);
        Assert.Equal(second.Value, reject.Data!.Value.GetProperty("current").GetGuid());

        fx.Do(Bob, new RetroFocusSet(null, second));
        var empty = fx.Rejects(Bob, new RetroFocusSet(first, second), NackCode.Stale);
        Assert.Equal(JsonValueKind.Null, empty.Data!.Value.GetProperty("current").ValueKind);
    }

    [Fact]
    public void Deleting_the_focused_card_clears_the_focus_in_the_same_transaction() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        fx.Do(Bob, new RetroFocusSet(card, null));

        var events = fx.Do(Alice, new RetroCardDelete(card));

        Assert.Equal(2, events.Count);
        Assert.IsType<CardDeleted>(events[0]);
        Assert.Equal(new FocusChanged(null), events[1]);
        Assert.Null(fx.State.Focus);
        var wire = fx.Projection.ForViewer(fx.State, events, Bob.Id);
        Assert.Equal([new RetroCardDeleted(card), new RetroFocus()], wire);
    }

    [Fact]
    public void Deleting_the_column_of_the_focused_card_clears_the_focus() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Bad);
        fx.Do(Bob, new RetroFocusSet(card, null));

        var events = fx.Do(Alice, new RetroColumnDelete(Bad));

        Assert.Equal(new FocusChanged(null), events[^1]);
        Assert.Null(fx.State.Focus);
    }

    [Fact]
    public void Deleting_other_cards_or_columns_keeps_the_focus() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        var other = fx.AddCard(Alice, Bad);
        fx.Do(Bob, new RetroFocusSet(card, null));

        Assert.Single(fx.Do(Alice, new RetroCardDelete(other)));
        Assert.Single(fx.Do(Alice, new RetroColumnDelete(Bad)));
        Assert.Equal(card, fx.State.Focus);
    }
}
