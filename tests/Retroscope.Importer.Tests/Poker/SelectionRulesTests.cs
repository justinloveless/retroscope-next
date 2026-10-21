using Retroscope.Importer.Archive;
using Retroscope.Importer.Map;
using static Retroscope.Importer.Map.PokerMap;

namespace Retroscope.Importer.Tests.Poker;

[Trait("Category", "PokerMap")]
public sealed class SelectionRulesTests {
    private static readonly DateTime T0 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Both_selection_shapes_are_parsed() {
        var report = new ImportReport();

        var selections = Selection.Parse("""
            {"u1": {"points": 3, "betweenHighPoints": 5, "locked": true, "name": "Ann"},
             "slack_ab": {"points": 8, "display_name": "Bob"},
             "u2": {"points": "13", "locked": false},
             "u3": [1]}
            """, report).ToDictionary(x => x.Key);

        Assert.Equal(new Selection("u1", 3, 5, Locked: true, SlackShape: false, "Ann"), selections["u1"]);
        Assert.Equal(new Selection("slack_ab", 8, null, Locked: false, SlackShape: true, "Bob"), selections["slack_ab"]);
        Assert.Equal(new Selection("u2", 13, null, Locked: false, SlackShape: false, null), selections["u2"]);
        Assert.False(selections.ContainsKey("u3"));
        Assert.Equal(1, report.Counter("poker.droppedSelections.malformed"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData(null)]
    public void Selections_that_are_not_an_object_are_empty(string? json) =>
        Assert.Empty(Selection.Parse(json, new ImportReport()));

    [Theory]
    [InlineData(5, null, true, false, "5")]
    [InlineData(3, 5, true, false, "3-5")]
    [InlineData(3, 8, true, false, "3")]
    [InlineData(21, 34, true, false, "21")]
    [InlineData(-1, null, true, false, "abstained")]
    [InlineData(5, null, false, true, "5")]
    [InlineData(-1, null, false, true, "abstained")]
    [InlineData(1, null, false, false, null)]
    [InlineData(0, null, false, false, null)]
    [InlineData(0, null, true, false, null)]
    [InlineData(4, null, true, false, null)]
    [InlineData(2.5, null, true, false, null)]
    [InlineData(-1, null, false, false, null)]
    public void Unrevealed_rounds_keep_only_locked_deck_votes(double points, int? high, bool locked, bool slack, string? expected) =>
        Assert.Equal(expected, Describe(ToVote(Make(points, high, locked, slack), revealed: false)));

    [Theory]
    [InlineData(5, null, true, false, "5")]
    [InlineData(5, 8, true, false, "5-8")]
    [InlineData(5, 13, true, false, "5")]
    [InlineData(5, null, false, false, "abstained")]
    [InlineData(-1, null, false, false, "abstained")]
    [InlineData(0, null, false, false, "abstained")]
    [InlineData(4, null, true, false, "abstained")]
    [InlineData(13, null, false, true, "13")]
    public void Revealed_rounds_keep_every_entry(double points, int? high, bool locked, bool slack, string? expected) =>
        Assert.Equal(expected, Describe(ToVote(Make(points, high, locked, slack), revealed: true)));

    [Fact]
    public void Playing_rounds_and_legacy_inactive_rounds_with_a_result_are_revealed() {
        Selection[] abstained = [Make(-1, null, true, false), Make(-1, null, true, false)];
        Selection[] mixed = [Make(-1, null, true, false), Make(5, null, false, false)];

        Assert.True(IsRevealed("Playing", isActive: true, 0, []));
        Assert.True(IsRevealed("Playing", isActive: false, 0, []));
        Assert.True(IsRevealed("Selection", isActive: false, 0.5m, []));
        Assert.True(IsRevealed("Selection", isActive: false, 0, abstained));
        Assert.False(IsRevealed("Selection", isActive: false, 0, mixed));
        Assert.False(IsRevealed("Selection", isActive: false, 0, []));
        Assert.False(IsRevealed("Selection", isActive: true, 8, abstained));
    }

    [Fact]
    public void The_latest_duplicate_keeps_its_number_and_the_rest_follow_the_max_in_creation_order() {
        Guid a = Id(1), b = Id(2), c = Id(3), d = Id(4), e = Id(5);

        var numbers = Renumber([(a, 2, T0), (b, 2, T0.AddMinutes(5)), (c, 7, T0.AddMinutes(1)), (d, 0, T0.AddMinutes(2)), (e, 2, T0.AddMinutes(3))]);

        Assert.Equal(new Dictionary<Guid, int> { [b] = 2, [c] = 7, [a] = 8, [d] = 9, [e] = 10 }, numbers);
    }

    [Fact]
    public void Renumbering_unique_rounds_changes_nothing() {
        var numbers = Renumber([(Id(1), 1, T0), (Id(2), 3, T0)]);

        Assert.Equal(new Dictionary<Guid, int> { [Id(1)] = 1, [Id(2)] = 3 }, numbers);
    }

    private static Selection Make(double points, int? high, bool locked, bool slack) =>
        new("k", (decimal)points, high, locked, slack, null);

    private static string? Describe(VoteValue? vote) => vote switch {
        null => null,
        { Abstained: true } => "abstained",
        { High: { } high } value => $"{value.Points}-{high}",
        { } value => $"{value.Points}",
    };

    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");
}
