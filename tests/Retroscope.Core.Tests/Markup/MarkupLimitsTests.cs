using Retroscope.Core.Markup;

namespace Retroscope.Core.Tests.Markup;

[Trait("Category", "Markup")]
public sealed class MarkupLimitsTests {
    private const string BlobImage = "![image](blob:6f1c2d3e-4b5a-4c6d-8e7f-901a2b3c4d5e)";

    [Theory]
    [InlineData(MarkupField.Card, 8000)]
    [InlineData(MarkupField.Comment, 4000)]
    [InlineData(MarkupField.ActionItem, 8000)]
    [InlineData(MarkupField.JiraText, 32000)]
    [InlineData(MarkupField.JiraDescription, 32000)]
    public void Accepts_bodies_up_to_the_field_limit_and_refuses_one_more(MarkupField field, int limit) {
        Assert.True(MarkupLimits.Check(new string('a', limit), field).IsValid);
        Assert.Equal(MarkupError.TooLong, MarkupLimits.Check(new string('a', limit + 1), field).Error);
    }

    [Fact]
    public void Counts_code_points_like_postgres_char_length() {
        var emoji = string.Concat(Enumerable.Repeat("😀", MarkupLimits.CommentLength));

        Assert.True(MarkupLimits.Check(emoji, MarkupField.Comment).IsValid);
        Assert.Equal(MarkupError.TooLong, MarkupLimits.Check(emoji + "a", MarkupField.Comment).Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void Refuses_empty_bodies(string body) {
        Assert.Equal(MarkupError.Empty, MarkupLimits.Check(body, MarkupField.Card).Error);
    }

    // Built in code: xunit's data serialisation would replace an unpaired surrogate.
    [Fact]
    public void Refuses_characters_a_stored_body_cannot_hold() {
        foreach (var c in new[] { '\0', '\u0007', '\u001f', '\ud83d', '\ude00' }) {
            Assert.Equal(MarkupError.InvalidCharacter, MarkupLimits.Check($"a{c}b", MarkupField.Card).Error);
        }
        Assert.Equal(MarkupError.InvalidCharacter, MarkupLimits.Check("a\ude00\ud83d", MarkupField.Card).Error);
    }

    [Fact]
    public void Allows_tabs_and_both_line_endings() {
        Assert.True(MarkupLimits.Check("a\tb\r\nc\rd\ne", MarkupField.Card).IsValid);
    }

    [Fact]
    public void Allows_eight_images_and_refuses_a_ninth() {
        var eight = string.Join(' ', Enumerable.Repeat(BlobImage, MarkupLimits.ImagesPerBody));

        Assert.True(MarkupLimits.Check(eight, MarkupField.Card).IsValid);
        Assert.Equal(MarkupError.TooManyImages, MarkupLimits.Check(eight + " " + BlobImage, MarkupField.Comment).Error);
    }

    [Fact]
    public void Returns_the_parsed_doc_in_the_field_dialect() {
        var check = MarkupLimits.Check("hi [[mention:0b0e7d4e-5a7c-4f5e-9a51-3a0d9c1e2f01:Ann]]", MarkupField.Comment);

        Assert.Equal([Guid.Parse("0b0e7d4e-5a7c-4f5e-9a51-3a0d9c1e2f01")], Mentions.Extract(check.Doc!));
    }

    [Theory]
    [InlineData(MarkupField.Card, MarkupDialect.Retro)]
    [InlineData(MarkupField.Comment, MarkupDialect.Retro)]
    [InlineData(MarkupField.ActionItem, MarkupDialect.Retro)]
    [InlineData(MarkupField.JiraText, MarkupDialect.Jira)]
    [InlineData(MarkupField.JiraDescription, MarkupDialect.JiraDescription)]
    public void Maps_each_field_to_its_dialect(MarkupField field, MarkupDialect dialect) {
        Assert.Equal(dialect, MarkupLimits.Dialect(field));
    }
}
