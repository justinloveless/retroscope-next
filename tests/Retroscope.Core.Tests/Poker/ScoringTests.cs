using System.Text.Json;
using Retroscope.Core.Poker;
using Retroscope.Core.Tests.Scaffold;

namespace Retroscope.Core.Tests.Poker;

[Trait("Category", "Poker")]
public sealed class ScoringTests {
    [Fact]
    public void Winner_matches_every_golden_vector_from_the_original() {
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot.Path, "tests/golden/poker-scoring.json")));
        var cases = golden.RootElement.GetProperty("cases");
        Assert.Equal(10_000, cases.GetArrayLength());

        var index = 0;
        foreach (var testCase in cases.EnumerateArray()) {
            // The original marks an abstain with points -1 and drops it before weighing.
            var participating = testCase.GetProperty("selections").EnumerateArray()
                .Select(x => (Points: x.GetProperty("points").GetInt32(), High: High(x)))
                .Where(x => x.Points != -1)
                .ToArray();
            var expected = testCase.GetProperty("expected");

            Assert.Equal("single", expected.GetProperty("kind").GetString());
            Assert.True(
                expected.GetProperty("points").GetInt32() == Scoring.Winner(participating),
                $"case {index}: {testCase.GetRawText()}");
            index++;
        }
    }

    [Theory]
    [InlineData(new short[0], 0)]
    [InlineData(new short[] { 5 }, 5)]
    [InlineData(new short[] { 3, 5 }, 5)]
    [InlineData(new short[] { 3, 3, 5 }, 3)]
    [InlineData(new short[] { 21, 1, 1 }, 1)]
    public void Plurality_wins_and_ties_go_to_the_higher_value(short[] points, short expected) {
        Assert.Equal(expected, Scoring.Winner(points.Select(x => new Vote(x, null, true, false))));
    }

    [Fact]
    public void A_between_vote_weighs_both_ends_and_abstains_do_not_count() {
        Vote[] votes = [new(3, 5, true, false), new(5, null, true, false), new(3, null, true, false), Vote.Abstain, Vote.Abstain];

        Assert.Equal(5, Scoring.Winner(votes));
        Assert.Equal(0, Scoring.Winner([Vote.Abstain]));
    }

    [Fact]
    public void Deck_next_gives_the_between_upper_end() {
        Assert.Equal([1, 2, 3, 5, 8, 13, 21], Deck.Values.Select(x => (int)x));
        Assert.Equal((short)2, Deck.Next(1));
        Assert.Equal((short)21, Deck.Next(13));
        Assert.Null(Deck.Next(21));
        Assert.Null(Deck.Next(4));
    }

    private static int? High(JsonElement selection) {
        return selection.TryGetProperty("betweenHighPoints", out var high) && high.ValueKind == JsonValueKind.Number
            ? high.GetInt32()
            : null;
    }
}
