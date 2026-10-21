using Retroscope.Core.Ids;
using Retroscope.Core.Markup;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using static Retroscope.Core.Tests.Retro.RetroFixture;

namespace Retroscope.Core.Tests.Retro;

[Trait("Category", "Retro")]
public sealed class CardTests {
    [Fact]
    public void A_card_records_its_author() {
        var fx = new RetroFixture();
        var id = fx.NewCardId();
        var added = Assert.IsType<CardAdded>(Assert.Single(fx.Do(Gus, new RetroCardAdd(id, Good, "Hello", false))));

        Assert.Equal(new AuthorInfo("Sneaky Penguin", Guest: true, Gus.Id), added.Authorship.Author);
        Assert.Null(added.Authorship.AnonTag);
        Assert.Equal(Start, added.CreatedAt);
        Assert.Null(added.ActionItem);
        Assert.Equal(1, fx.State.Cards[id].Version);
    }

    [Fact]
    public void Anonymous_cards_keep_only_the_tag() {
        var fx = new RetroFixture();
        var id = fx.NewCardId();
        var added = Assert.IsType<CardAdded>(Assert.Single(fx.Do(Alice, new RetroCardAdd(id, Good, "Psst", true))));

        Assert.Null(added.Authorship.Author);
        Assert.Equal(fx.Tagger.Tag(Board, Alice.Id), added.Authorship.AnonTag);
    }

    [Fact]
    public void Anonymous_posting_needs_a_signed_in_user_and_the_board_setting() {
        var fx = new RetroFixture();
        fx.Rejects(Gus, new RetroCardAdd(fx.NewCardId(), Good, "x", true), NackCode.Forbidden);
        var card = fx.AddCard(Alice, Good);
        fx.Rejects(Gus, new RetroCommentAdd(fx.NewCommentId(), card, "x", true), NackCode.Forbidden);

        fx.State.Config = fx.State.Config with { AllowAnonymous = false };
        fx.Rejects(Alice, new RetroCardAdd(fx.NewCardId(), Good, "x", true), NackCode.Forbidden);
        fx.Rejects(Alice, new RetroCommentAdd(fx.NewCommentId(), card, "x", true), NackCode.Forbidden);
    }

    [Fact]
    public void The_anonymous_author_still_edits_and_deletes_through_the_tag() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good, anonymous: true);
        var comment = fx.AddComment(Alice, card, anonymous: true);

        fx.Rejects(Bob, new RetroCardEdit(card, "Not yours", 1), NackCode.Forbidden);
        var edited = Assert.IsType<CardEdited>(Assert.Single(fx.Do(Alice, new RetroCardEdit(card, "Edited", 1))));
        Assert.True(edited.Anonymous);
        fx.Do(Alice, new RetroCommentDelete(comment));
        fx.Do(Alice, new RetroCardDelete(card));
    }

    [Fact]
    public void Edits_carry_the_base_version_and_stale_returns_both_body_and_version() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good, "First");
        fx.Do(Alice, new RetroCardEdit(card, "Second", 1));
        Assert.Equal(2, fx.State.Cards[card].Version);

        var reject = fx.Rejects(Alice, new RetroCardEdit(card, "Lost update", 1), NackCode.Stale);
        var current = reject.Data!.Value.GetProperty("current");
        Assert.Equal("Second", current.GetProperty("body").GetString());
        Assert.Equal(2, current.GetProperty("version").GetInt32());

        fx.NoOp(Alice, new RetroCardEdit(card, "Second", 2));
    }

    public static TheoryData<string, NackCode> BadBodies() => new() {
        { "", NackCode.Invalid },
        { "  \n ", NackCode.Invalid },
        { new string('x', MarkupLimits.CardLength + 1), NackCode.TooLarge },
        { string.Concat(Enumerable.Repeat($"![i](blob:{Guid.NewGuid()}) ", 9)), NackCode.TooLarge },
        { "nul\u0000", NackCode.Invalid },
        { "lone \ud800", NackCode.Invalid },
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public void Bodies_are_validated_as_retro_markup(string body, NackCode code) {
        var fx = new RetroFixture();
        fx.Rejects(Alice, new RetroCardAdd(fx.NewCardId(), Good, body, false), code);
        var card = fx.AddCard(Alice, Good);
        fx.Rejects(Alice, new RetroCardEdit(card, body, 1), code);
        fx.Rejects(Alice, new RetroCommentAdd(fx.NewCommentId(), card, body, false), code);
    }

    [Fact]
    public void Comments_have_their_own_length_limit() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        var body = new string('x', MarkupLimits.CommentLength + 1);
        fx.Rejects(Alice, new RetroCommentAdd(fx.NewCommentId(), card, body, false), NackCode.TooLarge);
        fx.AddCard(Alice, Good, body);
    }

    [Theory]
    [InlineData(MarkupError.Empty, NackCode.Invalid)]
    [InlineData(MarkupError.InvalidCharacter, NackCode.Invalid)]
    [InlineData(MarkupError.TooLong, NackCode.TooLarge)]
    [InlineData(MarkupError.TooManyImages, NackCode.TooLarge)]
    public void Markup_errors_map_to_nack_codes(MarkupError error, NackCode code) {
        Assert.Equal(code, RetroDecider.ToNack(error));
    }

    [Fact]
    public void Mentions_keep_team_members_only() {
        var fx = new RetroFixture(team: true);
        var body = $"Thanks [[mention:{Bob.Id}:Bob]] and [[mention:{Carol.Id}:Carol]] and [[mention:{Bob.Id}:Bobby]]";
        var added = Assert.IsType<CardAdded>(Assert.Single(fx.Do(Alice, new RetroCardAdd(fx.NewCardId(), Good, body, false))));
        Assert.Equal([Bob.Id], added.Mentions);

        var card = added.CardId;
        var edited = Assert.IsType<CardEdited>(Assert.Single(fx.Do(Alice, new RetroCardEdit(card, $"Hey [[mention:{Alice.Id}:Me]]", 1))));
        Assert.Equal([Alice.Id], edited.Mentions);

        var comment = Assert.IsType<CommentAdded>(Assert.Single(fx.Do(Carol, new RetroCommentAdd(fx.NewCommentId(), card, body, false))));
        Assert.Equal([Bob.Id], comment.Mentions);
    }

    [Fact]
    public void Quick_boards_record_no_mentions() {
        var fx = new RetroFixture();
        var added = Assert.IsType<CardAdded>(Assert.Single(fx.Do(Alice, new RetroCardAdd(fx.NewCardId(), Good, $"[[mention:{Bob.Id}:Bob]]", false))));
        Assert.Empty(added.Mentions);
    }

    [Fact]
    public void Board_images_are_listed_once_for_attachment() {
        var fx = new RetroFixture();
        var blob = Guid.Parse("bb000000-0000-4000-8000-000000000001");
        var body = $"![a](blob:{blob}) and ![b](blob:{blob})";
        var added = Assert.IsType<CardAdded>(Assert.Single(fx.Do(Alice, new RetroCardAdd(fx.NewCardId(), Good, body, true))));
        Assert.Equal([new BlobId(blob)], added.Images);
        Assert.True(added.Authorship.Anonymous);
    }

    [Fact]
    public void Action_items_column_of_a_team_board_creates_the_item_in_place() {
        var fx = new RetroFixture(team: true);
        var card = fx.NewCardId();
        var added = Assert.IsType<CardAdded>(Assert.Single(fx.Do(Bob, new RetroCardAdd(card, Actions, "Fix CI", false))));

        var item = added.ActionItem!;
        Assert.Equal("Fix CI", item.Text);
        Assert.Equal(card, item.SourceCardId);
        Assert.Equal(Board, item.SourceBoardId);
        Assert.Equal(Bob.Id, item.CreatedBy);
        Assert.Equal(1, item.SourceVersion);
        Assert.Equal(Start.ToUnixTimeMilliseconds(), item.CreatedAt);

        var again = new RetroFixture(team: true);
        var sameId = Assert.IsType<CardAdded>(Assert.Single(again.Do(Bob, new RetroCardAdd(card, Actions, "Fix CI", false))));
        Assert.Equal(item.Id, sameId.ActionItem!.Id);
    }

    [Fact]
    public void Anonymous_action_items_have_no_creator_and_quick_boards_have_no_items() {
        var team = new RetroFixture(team: true);
        var anonymous = Assert.IsType<CardAdded>(Assert.Single(team.Do(Alice, new RetroCardAdd(team.NewCardId(), Actions, "x", true))));
        Assert.Null(anonymous.ActionItem!.CreatedBy);

        var quick = new RetroFixture();
        var added = Assert.IsType<CardAdded>(Assert.Single(quick.Do(Alice, new RetroCardAdd(quick.NewCardId(), Actions, "x", false))));
        Assert.Null(added.ActionItem);
    }

    [Fact]
    public void Taken_ids_are_invalid() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        var comment = fx.AddComment(Alice, card);
        fx.Rejects(Bob, new RetroCardAdd(card, Bad, "x", false), NackCode.Invalid);
        fx.Rejects(Bob, new RetroCommentAdd(comment, card, "x", false), NackCode.Invalid);
    }

    [Fact]
    public void Deleting_a_card_takes_its_comments_and_votes() {
        var fx = new RetroFixture();
        var card = fx.AddCard(Alice, Good);
        var comment = fx.AddComment(Bob, card);
        var other = fx.AddComment(Bob, fx.AddCard(Alice, Good));
        fx.Do(Bob, new RetroVoteSet(card, true));

        var deleted = Assert.IsType<CardDeleted>(Assert.Single(fx.Do(Alice, new RetroCardDelete(card))));
        Assert.Equal([comment], deleted.Comments);
        Assert.False(fx.State.Cards.ContainsKey(card));
        Assert.False(fx.State.Comments.ContainsKey(comment));
        Assert.True(fx.State.Comments.ContainsKey(other));
        Assert.Equal(0, fx.State.VotesBy(Bob.Id));
    }
}
