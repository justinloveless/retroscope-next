using Retroscope.Core.Poker;

namespace Retroscope.Core.Tests.Poker;

[Trait("Category", "Poker")]
public sealed class TicketKeysTests {
    [Theory]
    [InlineData("ABC-1", "ABC", "ABC-1")]
    [InlineData("  abc-12 ", null, "ABC-12")]
    [InlineData("a_b2-999999999", null, "A_B2-999999999")]
    [InlineData("42", "PROJ", "PROJ-42")]
    [InlineData("", "PROJ", null)]
    [InlineData("   ", null, null)]
    [InlineData(null, null, null)]
    public void Normalizes_keys_and_prefixes_bare_numbers(string? input, string? prefix, string? expected) {
        Assert.True(TicketKeys.TryNormalize(input, prefix, out var key));
        Assert.Equal(expected, key);
    }

    [Theory]
    [InlineData("42", null)]
    [InlineData("042", "ABC")]
    [InlineData("ABC-0", null)]
    [InlineData("ABC-1234567890", null)]
    [InlineData("1ABC-1", null)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU-1", null)]
    [InlineData("ABC 1", null)]
    [InlineData("round:1", null)]
    public void Refuses_what_the_schema_would_refuse(string input, string? prefix) {
        Assert.False(TicketKeys.TryNormalize(input, prefix, out var key));
        Assert.Null(key);
    }

    [Fact]
    public void Is_valid_is_anchored_at_the_very_end() {
        Assert.True(TicketKeys.IsValid("ABC-1"));
        Assert.False(TicketKeys.IsValid("ABC-1\n"));
        Assert.False(TicketKeys.IsValid(null));
    }
}
