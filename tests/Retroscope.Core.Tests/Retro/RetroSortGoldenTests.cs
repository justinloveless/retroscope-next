using System.Globalization;
using System.Text.Json;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Core.Tests.Scaffold;

namespace Retroscope.Core.Tests.Retro;

[Trait("Category", "Retro")]
public sealed class RetroSortGoldenTests {
    private static readonly Dictionary<string, RetroSortKey> Keys = new() {
        ["votes-desc"] = RetroSortKey.VotesDesc,
        ["votes-asc"] = RetroSortKey.VotesAsc,
        ["time-asc"] = RetroSortKey.TimeAsc,
        ["time-desc"] = RetroSortKey.TimeDesc,
        ["author-asc"] = RetroSortKey.AuthorAsc,
        ["author-desc"] = RetroSortKey.AuthorDesc,
    };

    [Fact]
    public void Every_golden_case_sorts_like_the_original() {
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot.Path, "tests", "golden", "retro-sort.json")));
        var cases = golden.RootElement.GetProperty("cases");
        Assert.Equal(2000, cases.GetArrayLength());

        var index = 0;
        foreach (var @case in cases.EnumerateArray()) {
            var items = @case.GetProperty("items").EnumerateArray().Select(item => new RetroSortItem<string>(
                item.GetProperty("id").GetString()!,
                item.GetProperty("votes").GetInt32(),
                DateTimeOffset.Parse(item.GetProperty("created_at").GetString()!, CultureInfo.InvariantCulture),
                item.GetProperty("author").GetString())).ToList();

            foreach (var expected in @case.GetProperty("expected").EnumerateObject()) {
                var want = expected.Value.EnumerateArray().Select(id => id.GetString()!).ToList();
                var got = RetroSort.Sort(items, Keys[expected.Name]);
                Assert.True(want.SequenceEqual(got), $"case {index} {expected.Name}: want {string.Join(",", want)} got {string.Join(",", got)}");
            }
            index++;
        }
    }

    [Fact]
    public void The_default_follows_sort_chronologically() {
        Assert.Equal(RetroSortKey.VotesDesc, RetroSort.DefaultFor(RetroDefaults.Config));
        Assert.Equal(RetroSortKey.TimeAsc, RetroSort.DefaultFor(RetroDefaults.Config with { SortChronologically = true }));
    }

    [Fact]
    public void Card_ids_sort_through_the_same_rule() {
        var at = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
        RetroSortItem<int>[] items = [new(1, 0, at, "Zoe"), new(2, 2, at.AddSeconds(1), null), new(3, 2, at, "Ana")];
        Assert.Equal([3, 2, 1], RetroSort.Sort(items, RetroSortKey.VotesDesc));
        Assert.Equal([2, 3, 1], RetroSort.Sort(items, RetroSortKey.AuthorAsc));
    }
}
