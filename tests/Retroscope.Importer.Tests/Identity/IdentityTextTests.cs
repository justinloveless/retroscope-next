using Retroscope.Importer.Map;

namespace Retroscope.Importer.Tests.Identity;

[Trait("Category", "Identity")]
public sealed class IdentityTextTests {
    [Theory]
    [InlineData("bg-green-100 border-green-300", "green")]
    [InlineData("bg-Purple-100 border-purple-300", "purple")]
    [InlineData("bg-teal-100 border-teal-300", "gray")]
    [InlineData("border-red-300 bg-red-100", "red")]
    [InlineData("blue", "blue")]
    [InlineData("#ff0000", "gray")]
    [InlineData(null, "gray")]
    public void Tailwind_colours_map_to_the_palette(string? classes, string expected) =>
        Assert.Equal(expected, ColumnPalette.FromTailwind(classes));

    [Fact]
    public void Point_descriptions_keep_deck_keys_as_short_plain_text() {
        var json = """{"1": "tiny\nthing", "4": "x", "8": "  big  ", "13": 5, "21": "", "3": "LONG"}""".Replace("LONG", new string('y', 150), StringComparison.Ordinal);

        var descriptions = TeamsMap.PointDescriptions(json);

        Assert.Equal(["1", "3", "8"], descriptions.Select(x => x.Key));
        Assert.Equal("tiny thing", descriptions["1"]!.GetValue<string>());
        Assert.Equal(new string('y', 100), descriptions["3"]!.GetValue<string>());
        Assert.Equal("big", descriptions["8"]!.GetValue<string>());
        Assert.Empty(TeamsMap.PointDescriptions("not json"));
        Assert.Empty(TeamsMap.PointDescriptions("[1]"));
        Assert.Empty(TeamsMap.PointDescriptions(null));
    }

    [Fact]
    public void The_jira_associated_data_matches_the_servers_layout() =>
        Assert.Equal(
            "6a6972613a76313a00112233445566778899aabbccddeeff",
            Convert.ToHexStringLower(TeamsMap.JiraAssociatedData(new Guid("00112233-4455-6677-8899-aabbccddeeff"))));

    [Fact]
    public void The_invite_box_associated_data_is_the_invite_id_in_rfc_4122_order() =>
        Assert.Equal(
            "00112233445566778899aabbccddeeff",
            Convert.ToHexStringLower(InvitesMap.InviteAssociatedData(new Guid("00112233-4455-6677-8899-aabbccddeeff"))));

    [Theory]
    [InlineData("👍", "👍")]
    [InlineData(" 🎉 ", "🎉")]
    [InlineData("👨‍👩‍👧‍👦", "👨‍👩‍👧‍👦")]
    [InlineData("far too long for a vote emoji", "👍")]
    [InlineData("", "👍")]
    [InlineData(null, "👍")]
    public void Vote_emoji_fit_the_target_or_fall_back(string? emoji, string expected) =>
        Assert.Equal(expected, TemplatesMap.VoteEmoji(emoji));

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-3, null)]
    [InlineData(5, (short)5)]
    [InlineData(150, (short)99)]
    public void Vote_limits_fit_the_target(int? limit, short? expected) => Assert.Equal(expected, TemplatesMap.VoteLimit(limit));

    [Fact]
    public void Clipping_counts_characters_like_postgres_and_never_splits_a_pair() {
        Assert.Equal("ab", TargetRows.Clip("ab", 2));
        Assert.Equal("😀😀", TargetRows.Clip("😀😀😀", 2));
        Assert.Equal("a", TargetRows.Clip("a  b", 2));
        Assert.Equal(3, TargetRows.Length("😀a😀"));
    }
}
