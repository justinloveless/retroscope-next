using Retroscope.Server.Endpoints;

namespace Retroscope.Integration.Tests.AuthCore;

[Trait("Category", "AuthCore")]
public sealed class TextRulesTests {
    [Theory]
    [InlineData("Ada")]
    [InlineData("@ada")]
    [InlineData("Ada @ home")]
    [InlineData("Zoë 🎲")]
    [InlineData("")]
    public void Plain_text_is_accepted(string input) {
        Assert.True(TextRules.IsPlainText(input));
    }

    [Theory]
    [InlineData("Ada\u0000")]
    [InlineData("Ada\nLovelace")]
    [InlineData("Ada\tLovelace")]
    [InlineData("\u202Eada")]
    [InlineData("ada\u2066")]
    public void Control_characters_and_bidi_overrides_are_refused(string input) {
        Assert.False(TextRules.IsPlainText(input));
    }

    // Attribute strings are stored as UTF-8, which cannot hold a lone surrogate, so these are built at run time.
    [Fact]
    public void Lone_surrogates_are_refused() {
        Assert.False(TextRules.IsPlainText("Ada" + '\uD800'));
        Assert.False(TextRules.IsPlainText('\uDC00' + "Ada"));
        Assert.False(TextRules.IsPlainText("A" + '\uD800' + "da"));
        Assert.True(TextRules.IsPlainText("A" + '\uD83C' + '\uDFB2'));
    }
}
