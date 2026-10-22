using Retroscope.Core.Protocol;
using Retroscope.Core.Text;

namespace Retroscope.Core.Retro;

public enum RetroSortKey : byte {
    VotesDesc,
    VotesAsc,
    TimeAsc,
    TimeDesc,
    AuthorAsc,
    AuthorDesc,
}

/// <summary>One card as the column sort sees it; a null author sorts as an empty name.</summary>
public readonly record struct RetroSortItem<T>(T Item, int Votes, DateTimeOffset CreatedAt, string? Author);

/// <summary>
/// The per-viewer column sort, a port of <c>sortItems</c> (<c>RetroColumn.tsx:50-64</c>), checked against
/// <c>tests/golden/retro-sort.json</c>. The sort is stable, as <c>Array.prototype.sort</c> is, so ties keep
/// their input order. Times compare by the millisecond, as the client's <c>createdAt</c> does.
/// </summary>
public static class RetroSort {
    /// <summary>The column default before a viewer picks one (<c>sort_chronologically</c>).</summary>
    public static RetroSortKey DefaultFor(RetroConfig config) => config.SortChronologically ? RetroSortKey.TimeAsc : RetroSortKey.VotesDesc;

    public static IReadOnlyList<T> Sort<T>(IEnumerable<RetroSortItem<T>> items, RetroSortKey key) {
        var ordered = key switch {
            RetroSortKey.VotesDesc => items.OrderByDescending(i => i.Votes).ThenBy(i => i.CreatedAt.ToUnixTimeMilliseconds()),
            RetroSortKey.VotesAsc => items.OrderBy(i => i.Votes).ThenBy(i => i.CreatedAt.ToUnixTimeMilliseconds()),
            RetroSortKey.TimeAsc => items.OrderBy(i => i.CreatedAt.ToUnixTimeMilliseconds()),
            RetroSortKey.TimeDesc => items.OrderByDescending(i => i.CreatedAt.ToUnixTimeMilliseconds()),
            RetroSortKey.AuthorAsc => items.OrderBy(i => i.Author ?? "", NaturalCompare.Instance),
            RetroSortKey.AuthorDesc => items.OrderByDescending(i => i.Author ?? "", NaturalCompare.Instance),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
        return ordered.Select(i => i.Item).ToList();
    }
}
