using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using static Retroscope.Core.Tests.Retro.RetroFixture;

namespace Retroscope.Core.Tests.Retro;

[Trait("Category", "Retro")]
public sealed class ConfigTests {
    [Fact]
    public void Only_the_patched_fields_change() {
        var fx = new RetroFixture();
        fx.State.Config = fx.State.Config with { VoteEmoji = "🔥", MaxVotes = 5 };

        var events = fx.Do(Alice, new RetroConfigUpdate(
            new RetroConfigPatch(StagesEnabled: true),
            new RetroConfigPatch(StagesEnabled: false)));

        var changed = Assert.IsType<ConfigChanged>(Assert.Single(events));
        Assert.Equal(RetroDefaults.Config with { VoteEmoji = "🔥", MaxVotes = 5 }, changed.Previous);
        Assert.Equal(RetroDefaults.Config with { VoteEmoji = "🔥", MaxVotes = 5, StagesEnabled = true }, fx.State.Config);
    }

    [Fact]
    public void A_stale_expected_value_is_refused_with_the_fresh_config() {
        var fx = new RetroFixture();
        fx.State.Config = fx.State.Config with { AllowAnonymous = false };

        var reject = fx.Rejects(Alice, new RetroConfigUpdate(
            new RetroConfigPatch(AllowAnonymous: false, VotingEnabled: false),
            new RetroConfigPatch(AllowAnonymous: true, VotingEnabled: true)), NackCode.Stale);

        var current = reject.Data!.Value.GetProperty("current");
        Assert.False(current.GetProperty("allowAnonymous").GetBoolean());
        Assert.True(current.GetProperty("votingEnabled").GetBoolean());
        Assert.True(fx.State.Config.VotingEnabled);
    }

    [Fact]
    public void Expected_values_of_fields_not_being_patched_are_ignored() {
        var fx = new RetroFixture();
        fx.Do(Alice, new RetroConfigUpdate(
            new RetroConfigPatch(VoteEmoji: "🎉"),
            new RetroConfigPatch(VoteEmoji: "👍", AllowSelfVotes: false)));
        Assert.Equal("🎉", fx.State.Config.VoteEmoji);
    }

    [Fact]
    public void A_patched_field_without_an_expected_value_is_invalid() {
        var fx = new RetroFixture();
        fx.Rejects(Alice, new RetroConfigUpdate(new RetroConfigPatch(VotingEnabled: false), new RetroConfigPatch()), NackCode.Invalid);
    }

    [Fact]
    public void Max_votes_zero_means_unlimited_on_both_sides() {
        var fx = new RetroFixture();
        fx.Do(Alice, new RetroConfigUpdate(new RetroConfigPatch(MaxVotes: 3), new RetroConfigPatch(MaxVotes: 0)));
        Assert.Equal(3, fx.State.Config.MaxVotes);

        fx.Rejects(Alice, new RetroConfigUpdate(new RetroConfigPatch(MaxVotes: 0), new RetroConfigPatch(MaxVotes: 0)), NackCode.Stale);
        fx.Do(Alice, new RetroConfigUpdate(new RetroConfigPatch(MaxVotes: 0), new RetroConfigPatch(MaxVotes: 3)));
        Assert.Null(fx.State.Config.MaxVotes);
    }

    [Theory]
    [InlineData(-1, "👍")]
    [InlineData(100, "👍")]
    [InlineData(3, "")]
    [InlineData(3, "123456789")]
    [InlineData(3, "\u0000")]
    public void Out_of_range_values_are_invalid(int maxVotes, string emoji) {
        var fx = new RetroFixture();
        var patch = new RetroConfigPatch(MaxVotes: maxVotes, VoteEmoji: emoji);
        var expected = new RetroConfigPatch(MaxVotes: 0, VoteEmoji: "👍");
        fx.Rejects(Alice, new RetroConfigUpdate(patch, expected), NackCode.Invalid);
    }

    [Fact]
    public void Emoji_length_counts_code_points() {
        var fx = new RetroFixture();
        fx.Do(Alice, new RetroConfigUpdate(new RetroConfigPatch(VoteEmoji: "👍👍👍👍👍👍👍👍"), new RetroConfigPatch(VoteEmoji: "👍")));
    }

    [Fact]
    public void A_patch_that_changes_nothing_is_a_noop() {
        var fx = new RetroFixture();
        fx.NoOp(Alice, new RetroConfigUpdate(new RetroConfigPatch(AllowAnonymous: true), new RetroConfigPatch(AllowAnonymous: true)));
        fx.NoOp(Alice, new RetroConfigUpdate(new RetroConfigPatch(), new RetroConfigPatch()));
    }

    [Fact]
    public void Rename_is_compare_and_set_on_the_title() {
        var fx = new RetroFixture();
        var reject = fx.Rejects(Alice, new RetroBoardRename("New", "Old"), NackCode.Stale);
        Assert.Equal("Sprint 42 retro", reject.Data!.Value.GetProperty("current").GetString());

        fx.Do(Alice, new RetroBoardRename("New", "Sprint 42 retro"));
        Assert.Equal("New", fx.State.Title);
        fx.NoOp(Alice, new RetroBoardRename("New", "New"));
        fx.Rejects(Alice, new RetroBoardRename(" ", "New"), NackCode.Invalid);
        fx.Rejects(Alice, new RetroBoardRename(new string('x', 201), "New"), NackCode.Invalid);
    }
}
