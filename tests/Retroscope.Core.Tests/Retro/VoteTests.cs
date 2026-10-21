using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using static Retroscope.Core.Tests.Retro.RetroFixture;

namespace Retroscope.Core.Tests.Retro;

[Trait("Category", "Retro")]
public sealed class VoteTests {
    [Fact]
    public void Votes_are_set_semantics() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);

        Assert.Equal(new VoteSet(card, Bob.Id, true), Assert.Single(fx.Do(Bob, new RetroVoteSet(card, true))));
        fx.NoOp(Bob, new RetroVoteSet(card, true));
        fx.Do(Bob, new RetroVoteSet(card, false));
        fx.NoOp(Bob, new RetroVoteSet(card, false));
        Assert.Empty(fx.State.Cards[card].Voters);
    }

    [Fact]
    public void The_vote_limit_counts_every_card_on_the_board() {
        var fx = new RetroFixture();
        fx.State.Config = fx.State.Config with { MaxVotes = 2 };
        var cards = Enumerable.Range(0, 3).Select(_ => fx.AddCard(Alice, Good)).ToList();

        fx.Do(Bob, new RetroVoteSet(cards[0], true));
        fx.Do(Bob, new RetroVoteSet(cards[1], true));
        fx.Rejects(Bob, new RetroVoteSet(cards[2], true), NackCode.VoteLimit);

        fx.Do(Carol, new RetroVoteSet(cards[2], true));
        fx.Do(Bob, new RetroVoteSet(cards[0], false));
        fx.Do(Bob, new RetroVoteSet(cards[2], true));
    }

    [Fact]
    public void Removing_a_vote_is_open_at_the_limit() {
        var fx = new RetroFixture();
        fx.State.Config = fx.State.Config with { MaxVotes = 1 };
        var card = fx.AddCard(Alice, Good);
        fx.Do(Bob, new RetroVoteSet(card, true));
        fx.Do(Bob, new RetroVoteSet(card, false));
    }

    [Fact]
    public void Self_votes_are_refused_when_the_board_forbids_them() {
        var fx = new RetroFixture();
        fx.State.Config = fx.State.Config with { AllowSelfVotes = false };
        var card = fx.AddCard(Alice, Good);

        fx.Rejects(Alice, new RetroVoteSet(card, true), NackCode.SelfVote);
        fx.Do(Bob, new RetroVoteSet(card, true));
    }

    [Fact]
    public void Self_vote_rule_sees_through_anonymity_with_the_tag() {
        var fx = new RetroFixture();
        fx.State.Config = fx.State.Config with { AllowSelfVotes = false };
        var card = fx.AddCard(Alice, Good, anonymous: true);

        Assert.Null(fx.State.Cards[card].Authorship.Author);
        fx.Rejects(Alice, new RetroVoteSet(card, true), NackCode.SelfVote);
        fx.Do(Bob, new RetroVoteSet(card, true));
    }

    [Fact]
    public void Self_votes_are_open_by_default() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        fx.Do(Alice, new RetroVoteSet(card, true));
    }

    [Fact]
    public void Guests_vote_under_their_own_principal() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        fx.Do(Gus, new RetroVoteSet(card, true));
        Assert.Contains(Gus.Id, fx.State.Cards[card].Voters);
    }

    [Fact]
    public void Unlimited_means_no_limit() {
        var fx = new RetroFixture();
        for (var i = 0; i < 120; i++) {
            fx.Do(Bob, new RetroVoteSet(fx.AddCard(Alice, Good), true));
        }
        Assert.Equal(120, fx.State.VotesBy(Bob.Id));
    }
}
