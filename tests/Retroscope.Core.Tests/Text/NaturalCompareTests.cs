using Retroscope.Core.Text;

namespace Retroscope.Core.Tests.Text;

[Trait("Category", "Text")]
public sealed class NaturalCompareTests {
    // The order of a.localeCompare(b, undefined, { numeric: true }) in Node 20 (ICU root collation), the
    // comparison the original used for sprint buckets. Strings in one group compare equal.
    private static readonly string[][] IcuOrder = [
        [""], ["_x"], ["2024-01", "2024-1"], ["99999999999999999999"], ["100000000000000000000"], ["a"], ["A"],
        ["a1b2"], ["a01b3"], ["a1b10"], ["abc"], ["Abc"], ["ABC"], ["b"], ["B"], ["item 007", "item 7"], ["item 8"],
        ["PI 3 Iteration 4"], ["PI 3 Iteration 12"], ["PI 12 Iteration 1"], ["Q1-Q2"], ["Q1/Q2"], ["Release 1.9"],
        ["Release 1.10"], ["Sprint"], ["Sprint 1"], ["Sprint 2", "Sprint 02"], ["Sprint 2a"], ["sprint 3"], ["SPRINT 3"],
        ["Sprint 3 - old"], ["Sprint 3 (old)"], ["sprint 5"], ["Sprint 9 "], ["Sprint 10"], ["Sprint 10.1"],
        ["Sprint 10.2"], ["Sprint 10.10"], ["sprint-5"], ["TEAM Sprint 9"], ["TEAM Sprint 10"], ["x"], ["x 1"], ["x_1"],
        ["x-1"], ["x.1"], ["x(1)"], ["x1"],
    ];

    [Fact]
    public void Every_pair_compares_like_icu_numeric_collation() {
        for (var i = 0; i < IcuOrder.Length; i++) {
            for (var j = 0; j < IcuOrder.Length; j++) {
                foreach (var a in IcuOrder[i]) {
                    foreach (var b in IcuOrder[j]) {
                        Assert.True(
                            Math.Sign(NaturalCompare.Compare(a, b)) == Math.Sign(i - j),
                            $"Compare(\"{a}\", \"{b}\") should be {Math.Sign(i - j)}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Sorting_a_shuffled_list_gives_the_icu_order() {
        var expected = IcuOrder.Select(x => x[0]).ToArray();
        var shuffled = expected.ToArray();
        new Random(7).Shuffle(shuffled);

        Array.Sort(shuffled, NaturalCompare.Instance);

        Assert.Equal(expected, shuffled);
    }

    [Fact]
    public void Nulls_sort_first() {
        Assert.Equal(0, NaturalCompare.Compare(null, null));
        Assert.True(NaturalCompare.Compare(null, "") < 0);
        Assert.True(NaturalCompare.Compare("", null) > 0);
    }

    [Theory]
    [InlineData("Sprint 3", "sprint 3", 1)]
    [InlineData("Sprint 3", "Sprint 3", 0)]
    [InlineData("Sprint 007a", "Sprint 7b", -1)]
    [InlineData("v1.2.10", "v1.2.9", 1)]
    public void Edge_cases(string a, string b, int expected) {
        Assert.Equal(expected, Math.Sign(NaturalCompare.Compare(a, b)));
        Assert.Equal(-expected, Math.Sign(NaturalCompare.Compare(b, a)));
    }
}
